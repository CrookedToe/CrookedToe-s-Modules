using System.Diagnostics;

namespace CrookedToe.Modules.OSCLeash;

internal sealed class LeashInputState
{
    private readonly object _gate = new();
    private bool _isGrabbed;
    private bool _leashEnabled = true;
    private bool _leashDisabled;
    private bool _hasLeashEnableParameter;
    private bool _hasLeashDisableParameter;
    private bool _requiresGrabRelease;
    private bool _motionSuppressed;
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
                return IsLeashEngagedUnsafe() && !_motionSuppressed;
        }
    }

    public bool LeashEngaged
    {
        get
        {
            lock (_gate)
                return IsLeashEngagedUnsafe();
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

    public LeashInputSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new LeashInputSnapshot(
                    _isGrabbed,
                    _leashEnabled && !_leashDisabled,
                    _hasLeashEnableParameter,
                    _hasLeashDisableParameter,
                    _requiresGrabRelease,
                    _motionSuppressed,
                    _stretch,
                    _xPositive - _xNegative,
                    _yNegative - _yPositive,
                    _zPositive - _zNegative);
            }
        }
    }

    public void ConfigureOptionalGates(bool hasLeashEnableParameter, bool hasLeashDisableParameter)
    {
        lock (_gate)
        {
            _hasLeashEnableParameter = hasLeashEnableParameter;
            _hasLeashDisableParameter = hasLeashDisableParameter;
            if (!hasLeashEnableParameter)
                _leashEnabled = true;
            if (!hasLeashDisableParameter)
                _leashDisabled = false;
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
            PrepareForFreshInput();
            _lastInputTimestamp = timestamp;
            switch (parameter)
            {
                case OSCLeashParameter.IsGrabbed:
                    if (!value)
                        _requiresGrabRelease = false;
                    _isGrabbed = value && !_requiresGrabRelease;
                    break;
                case OSCLeashParameter.LeashEnable:
                    // Registered parameters that are absent from the current avatar can be
                    // surfaced by VRCOSC with their type's default value. leash_enable is
                    // optional, so an absent parameter must retain the enabled default.
                    if (_hasLeashEnableParameter)
                        _leashEnabled = value;
                    break;
                case OSCLeashParameter.LeashDisable:
                    // false is deliberately the safe default, including for avatars that do
                    // not declare the parameter. This is the preferred gate for new prefabs.
                    _leashDisabled = value;
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
            PrepareForFreshInput();
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

    public bool SuppressMotionIfSilent(long now, long timeoutTicks)
    {
        lock (_gate)
        {
            if (_motionSuppressed || _lastInputTimestamp == 0 || now - _lastInputTimestamp <= timeoutTicks)
                return false;

            _motionSuppressed = true;
            return true;
        }
    }

    public bool MotionSuppressed
    {
        get
        {
            lock (_gate)
                return _motionSuppressed;
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
        _leashDisabled = false;
        _motionSuppressed = false;
        ResetSignalValues();
    }

    private void PrepareForFreshInput()
    {
        if (!_motionSuppressed)
            return;

        // A transport gap may have lost any subset of the component updates. Clear
        // the old vector before accepting the recovered stream so old and new axes
        // cannot combine into a false direction. Stretch starts at zero, making the
        // partially rebuilt state fail safe until its own fresh value arrives.
        ResetSignalValues();
        _motionSuppressed = false;
    }

    private void ResetSignalValues()
    {
        _stretch = 0f;
        _xPositive = _xNegative = _yPositive = _yNegative = _zPositive = _zNegative = 0f;
    }

    private bool IsLeashEngagedUnsafe()
        => _isGrabbed && _leashEnabled && !_leashDisabled && !_requiresGrabRelease;
}

internal readonly record struct LeashInputSnapshot(
    bool IsGrabbed,
    bool LeashEnabled,
    bool HasLeashEnableParameter,
    bool HasLeashDisableParameter,
    bool RequiresGrabRelease,
    bool MotionSuppressed,
    float Stretch,
    float NetX,
    float NetY,
    float NetZ);
