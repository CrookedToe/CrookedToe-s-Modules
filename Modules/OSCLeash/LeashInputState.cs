using System.Diagnostics;

namespace CrookedToe.Modules.OSCLeash;

internal sealed class LeashInputState
{
    private readonly object _gate = new();
    private bool _isGrabbed;
    private bool _leashEnabled = true;
    private bool _requiresGrabRelease;
    private long _lastInputTimestamp;
    private float _stretch;
    private float _xPositive;
    private float _xNegative;
    private float _yPositive;
    private float _yNegative;
    private float _zPositive;
    private float _zNegative;

    public bool GrabbedForMotion
    {
        get
        {
            lock (_gate)
                return _isGrabbed && _leashEnabled && !_requiresGrabRelease;
        }
    }

    public LeashSignal Signal
    {
        get
        {
            lock (_gate)
            {
                return LeashSignal.From(
                    _xPositive - _xNegative,
                    _yNegative - _yPositive,
                    _zPositive - _zNegative,
                    _stretch);
            }
        }
    }

    public bool HasReceivedInput
    {
        get
        {
            lock (_gate)
                return _lastInputTimestamp != 0;
        }
    }

    public bool RequiresGrabRelease
    {
        get
        {
            lock (_gate)
                return _requiresGrabRelease;
        }
    }

    public float InputAgeSeconds(long now)
    {
        lock (_gate)
        {
            return _lastInputTimestamp == 0
                ? float.MaxValue
                : (float)((now - _lastInputTimestamp) / (double)Stopwatch.Frequency);
        }
    }

    public void Set(OSCLeashParameter parameter, bool value)
        => Set(parameter, value, Stopwatch.GetTimestamp());

    internal void Set(OSCLeashParameter parameter, bool value, long timestamp)
    {
        lock (_gate)
        {
            _lastInputTimestamp = timestamp;
            switch (parameter)
            {
                case OSCLeashParameter.IsGrabbed:
                    if (!value)
                        _requiresGrabRelease = false;
                    _isGrabbed = value && !_requiresGrabRelease;
                    break;
                case OSCLeashParameter.LeashEnable:
                    _leashEnabled = value;
                    break;
            }
        }
    }

    public void Set(OSCLeashParameter parameter, float value)
        => Set(parameter, value, Stopwatch.GetTimestamp());

    internal void Set(OSCLeashParameter parameter, float value, long timestamp)
    {
        value = float.IsFinite(value) ? value : 0f;
        lock (_gate)
        {
            _lastInputTimestamp = timestamp;
            switch (parameter)
            {
                case OSCLeashParameter.Stretch:
                    _stretch = value;
                    break;
                case OSCLeashParameter.XPositive:
                    _xPositive = value;
                    break;
                case OSCLeashParameter.XNegative:
                    _xNegative = value;
                    break;
                case OSCLeashParameter.YPositive:
                    _yPositive = value;
                    break;
                case OSCLeashParameter.YNegative:
                    _yNegative = value;
                    break;
                case OSCLeashParameter.ZPositive:
                    _zPositive = value;
                    break;
                case OSCLeashParameter.ZNegative:
                    _zNegative = value;
                    break;
            }
        }
    }

    public bool ExpireIfStale(long now, long timeoutTicks)
    {
        lock (_gate)
        {
            if (_lastInputTimestamp == 0 || now - _lastInputTimestamp <= timeoutTicks)
                return false;

            _requiresGrabRelease |= _isGrabbed;
            ResetValues();
            _lastInputTimestamp = 0;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetValues();
            _requiresGrabRelease = false;
            _lastInputTimestamp = 0;
        }
    }

    private void ResetValues()
    {
        _isGrabbed = false;
        _leashEnabled = true;
        _stretch = 0f;
        _xPositive = _xNegative = _yPositive = _yNegative = _zPositive = _zNegative = 0f;
    }
}
