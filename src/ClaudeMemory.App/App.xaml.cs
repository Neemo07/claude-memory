using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ClaudeMemory.App.Services;

namespace ClaudeMemory.App;

/// <summary>
/// Application entry point. Owns the single-instance gate and the command-line route, and keeps
/// fatal errors in <c>claude-memory-error.log</c> beside the executable (PRD §6, §8: no transcript
/// content is logged).
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "claude-memory";

    private SingleInstance? _instance;
    private MainWindow? _window;

    /// <summary>Crash log path, next to the executable (PRD §6).</summary>
    public static string ErrorLogPath
        => Path.Combine(AppContext.BaseDirectory, "claude-memory-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var route = StartupRouting.Parse(e.Args);
            _instance = new SingleInstance(InstanceName);

            if (!_instance.IsFirstInstance)
            {
                // PRD FR-23: hand the path to the running window and leave.
                if (route.Action == StartupAction.OpenFile) _instance.TrySend(route.Path);
                Shutdown(0);
                return;
            }

            _instance.StartListening(OnSecondLaunch);

            var window = new MainWindow();
            _window = window;
            MainWindow = window;
            window.Show();

            if (route.Action == StartupAction.OpenFile) window.OpenFromShell(route.Path);
            else if (route.Message is not null) window.ShowStartupMessage(route.Message);
        }
        catch (Exception error)
        {
            Report(error, fatal: true);
            Shutdown(1);
        }
    }

    /// <summary>Called on the listener thread; the window must be touched on the UI thread.</summary>
    private void OnSecondLaunch(string path)
        => Dispatcher.BeginInvoke(() =>
        {
            if (_window is not null) _window.OpenFromShell(path);
        });

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _instance = null;
        _window = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A recoverable failure inside a handler should not take the window down; keep the app
        // usable and record what happened.
        Report(e.Exception, fatal: false);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception error) Report(error, fatal: true);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, fatal: false);
        e.SetObserved();
    }

    /// <summary>Appends the error to the log and, when fatal, tells the user where it is.</summary>
    private static void Report(Exception error, bool fatal)
    {
        TryLog(error);

        if (!fatal) return;

        try
        {
            MessageBox.Show(
                "claude-memory hit an unexpected error and has to close.\n\n"
                + error.GetType().Name + ": " + error.Message + "\n\n"
                + "Details were written to:\n" + ErrorLogPath,
                "claude-memory",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Nothing better to do while the app is coming down.
        }
    }

    private static void TryLog(Exception error)
    {
        try
        {
            var entry = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("u"))
                .Append("  ")
                .Append(error.GetType().FullName)
                .Append(": ")
                .Append(error.Message)
                .Append('\n')
                .Append(error.StackTrace)
                .Append("\n\n")
                .ToString();

            File.AppendAllText(
                ErrorLogPath, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            // A log failure must never mask the original error.
        }
    }
}
