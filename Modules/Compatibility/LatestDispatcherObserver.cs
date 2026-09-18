using System.Windows.Threading;

namespace CrookedToe.Modules.Compatibility;

/// <summary>One pending UI operation and one latest value, without owning the view's lifetime.</summary>
internal sealed class LatestDispatcherObserver<TTarget, TMessage>
    where TTarget : DispatcherObject
    where TMessage : class
{
    private readonly object _gate = new();
    private readonly WeakReference<TTarget> _target;
    private readonly Dispatcher _dispatcher;
    private readonly Action<TTarget, TMessage> _callback;
    private TMessage? _latest;
    private bool _scheduled;

    public LatestDispatcherObserver(TTarget target, Action<TTarget, TMessage> callback)
    {
        _target = new(target);
        _dispatcher = target.Dispatcher;
        _callback = callback;
    }

    public bool IsAlive => !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished && _target.TryGetTarget(out _);

    public void Publish(TMessage message)
    {
        lock (_gate)
        {
            if (!IsAlive)
                return;
            _latest = message;
            if (_scheduled)
                return;
            _scheduled = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Deliver));
        }
    }

    private void Deliver()
    {
        TMessage? message;
        lock (_gate)
        {
            message = _latest;
            _latest = null;
            _scheduled = false;
        }
        if (message is not null && _target.TryGetTarget(out TTarget? target))
            _callback(target, message);
    }
}
