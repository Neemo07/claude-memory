using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ClaudeMemory.App.Services;

/// <summary>
/// One window per session (PRD FR-23). The first launch owns a named mutex and listens on a
/// named pipe; every later launch hands its file argument to that pipe and exits.
/// </summary>
/// <remarks>
/// The pipe name is injectable so tests can use a loopback name and never talk to a real
/// instance. Both objects are created in the caller's session, which is what per-user means here.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly bool _isFirstInstance;
    private readonly object _gate = new();
    private bool _disposed;
    private CancellationTokenSource? _stop;
    private Task? _listener;

    public SingleInstance(string name)
    {
        _pipeName = name;

        // Ownership is deliberately not taken: ReleaseMutex is thread-affine, and a window can
        // be disposed on a different thread than the one that created it. The kernel object's
        // own lifetime is the gate instead - it disappears when the last handle closes.
        _mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out var createdNew);
        _isFirstInstance = createdNew;
    }

    /// <summary>True when this process is the one that should show the window.</summary>
    public bool IsFirstInstance => _isFirstInstance;

    /// <summary>Starts the listener. Requests arrive on a background thread.</summary>
    public void StartListening(Action<string> onRequest)
    {
        lock (_gate)
        {
            if (_disposed || !_isFirstInstance || _listener is not null) return;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _listener = Task.Run(() => ListenAsync(onRequest, token), token);
    }

    /// <summary>Sends one line to the running instance. False when nobody answers in time.</summary>
    public bool TrySend(string request, int timeoutMilliseconds = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        var firstAttempt = true;

        while (Environment.TickCount64 <= deadline)
        {
            if (!firstAttempt)
            {
                // A refused connection returns immediately; without this pause the deadline
                // burns CPU in a tight loop for three seconds.
                Thread.Sleep(75);
            }

            firstAttempt = false;
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(250);

                using var writer = new StreamWriter(
                    client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                writer.WriteLine(request);
                return true;
            }
            catch (TimeoutException)
            {
                // The owner is still starting; try again until the deadline.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private async Task ListenAsync(Action<string> onRequest, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(
                    server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line)) onRequest(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The other side hung up mid-send; keep serving.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            _stop?.Cancel();
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception error) when (error is AggregateException or ObjectDisposedException)
        {
            // Shutdown must not throw.
        }
        finally
        {
            _stop?.Dispose();
            _mutex.Dispose();
        }
    }
}
