using System.IO.Pipes;
using System.Text;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\CodexDesktopUsageSwitcher.Windows";
    private const string PipeName = "CodexDesktopUsageSwitcher.Windows.Activate";
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Mutex _mutex;
    private Task? _listener;

    private SingleInstanceCoordinator(Mutex mutex)
    {
        _mutex = mutex;
    }

    public event EventHandler? ActivationRequested;

    public static bool TryCreatePrimary(out SingleInstanceCoordinator? coordinator)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            coordinator = null;
            return false;
        }

        coordinator = new SingleInstanceCoordinator(mutex);
        return true;
    }

    public static void SignalPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(900);
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            writer.WriteLine("show");
        }
        catch (Exception)
        {
            // Best effort: if the original instance is still starting, the second
            // launch should simply exit instead of showing duplicate tray icons.
        }
    }

    public void Start()
    {
        _listener = Task.Run(ListenLoopAsync);
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        // Let the listener observe cancellation and stop using the token before the
        // source is disposed, otherwise it can throw ObjectDisposedException mid-flight.
        try
        {
            _listener?.Wait(ShutdownWait);
        }
        catch (AggregateException)
        {
            // The listener faulted or was cancelled during shutdown; nothing to recover.
        }

        _cancellation.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                await Task.Delay(250, _cancellation.Token).ConfigureAwait(false);
            }
        }
    }
}
