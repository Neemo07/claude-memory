using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeMemory.App;

/// <summary>
/// Application entry point. Fatal errors are written to <c>claude-memory-error.log</c> beside
/// the executable and shown in a dialog (PRD §6, §8: no transcript content is logged).
/// </summary>
public partial class App : Application
{
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
            new MainWindow().Show();
        }
        catch (Exception error)
        {
            Report(error, fatal: true);
            Shutdown(1);
        }
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
