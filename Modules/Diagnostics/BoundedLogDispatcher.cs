using System.Collections.Concurrent;
using VRCOSC.App.SDK.Modules;

namespace CrookedToe.Modules.Diagnostics;

/// <summary>
/// The host's Log/LogDebug calls synchronously dispatch to WPF and write files. Share one
/// bounded worker for the process so neither a stalled UI nor module restarts accumulate work.
/// </summary>
internal static class RealtimeModuleLog
{
    private static readonly BoundedLogDispatcher Dispatcher = new(128);

    public static void Write(Module module, string message, bool debug = false)
    {
        Dispatcher.TryPost(() =>
        {
            if (debug)
                module.LogDebug(message);
            else
                module.Log(message);
        });
    }

    public static long DroppedMessages => Dispatcher.DroppedMessages;
}

internal sealed class BoundedLogDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _pending;
    private readonly Thread _worker;
    private long _droppedMessages;
    private long _failedMessages;

    public BoundedLogDispatcher(int capacity)
    {
        _pending = new(capacity);
        _worker = new Thread(Drain) { IsBackground = true, Name = "CrookedToe module log delivery" };
        _worker.Start();
    }

    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);
    public long FailedMessages => Interlocked.Read(ref _failedMessages);
    internal int PendingCount => _pending.Count;

    public bool TryPost(Action callback)
    {
        try
        {
            if (_pending.TryAdd(callback))
                return true;
        }
        catch (InvalidOperationException)
        {
            // Shutdown raced with this producer. Never fall back to synchronous logging.
        }
        Interlocked.Increment(ref _droppedMessages);
        return false;
    }

    private void Drain()
    {
        foreach (Action callback in _pending.GetConsumingEnumerable())
        {
            try { callback(); }
            catch (Exception) { Interlocked.Increment(ref _failedMessages); }
        }
    }

    // Do not join: a host UI callback may be waiting for the thread calling Dispose.
    // The process-wide production dispatcher lives for the process, across module restarts.
    public void Dispose() => _pending.CompleteAdding();
}
