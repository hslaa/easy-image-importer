using System.IO.Pipes;

namespace EasyImageImporter.App.Startup;

/// <summary>
/// Only one copy of the app may run: two copiers on one card would fight over the same
/// session. Starting it again (e.g. from the Start menu while it sits in the tray) just
/// brings the running window forward.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Name = "EasyImageImporter";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Returns the guard if this is the first instance. Otherwise asks the running instance to
    /// show itself and returns null; the caller should then exit.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, $"Local\\{Name}");
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed; the lock is ours now.
            owned = true;
        }

        if (owned) return new SingleInstance(mutex);

        mutex.Dispose();
        SignalRunningInstance();
        return null;
    }

    /// <summary>Calls <paramref name="onShowRequested"/> (on a background thread) whenever another launch asks for the window.</summary>
    public void Listen(Action onShowRequested) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                onShowRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away mid-handshake; keep listening.
            }
        }
    });

    private static void SignalRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // The other instance is starting up or shutting down; nothing more we can do.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
