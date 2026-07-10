namespace CrookedToe.Modules.OSCLeash;

internal static class LeashDefaults
{
    public const int UpdateIntervalMilliseconds = 8;
    public const float BaseDeltaTimeSeconds = 0.008f;
    public const float MaxDeltaTimeSeconds = 0.05f;
    public const float VrWriteIntervalSeconds = 1f / 30f;
    public const float ExternalPoseQuietSeconds = 0.25f;
    public const float VrRetryIntervalSeconds = 2f;
    public const float VerticalCooldownSeconds = 1f;
    public const float StopThreshold = 0.01f;
    public const float VelocityStopThreshold = 0.1f;
    public const float TurnEpsilon = 0.0001f;
    public const float NormalizeEpsilon = 0.0001f;
    public const float DirectionChangeHoldSeconds = 0.12f;
    public const float AngleHysteresisDegrees = 5f;
    public const float DeadzoneHysteresisFactor = 0.8f;
}

internal readonly record struct LeashSettings(
    float WalkDeadzone,
    float RunDeadzone,
    float StrengthMultiplier,
    float UpDownDeadzone,
    float UpDownCompensation,
    float MovementSmoothing,
    LeashDirection Direction,
    bool TurningEnabled,
    float TurningMultiplier,
    float TurningDeadzone,
    float TurningGoal,
    float TurningVerticalAngleLimit,
    bool VerticalEnabled,
    bool ReturnHeightOnRelease,
    float VerticalMultiplier,
    float VerticalDeadzone,
    float VerticalSmoothing,
    float VerticalAngle,
    float GravityStrength,
    float TerminalVelocity,
    float MaximumVerticalOffset,
    bool DebugTraceEnabled);

internal readonly record struct LeashSignal(
    float NetX,
    float NetY,
    float NetZ,
    float Stretch,
    float HorizontalMagnitude,
    float VerticalMagnitude,
    float VerticalAngle,
    bool HasHorizontalDirection,
    float NormalizedX,
    float NormalizedZ)
{
    public static LeashSignal From(float netX, float netY, float netZ, float stretch)
    {
        netX = ClampFinite(netX, -1f, 1f);
        netY = ClampFinite(netY, -1f, 1f);
        netZ = ClampFinite(netZ, -1f, 1f);
        stretch = ClampFinite(stretch, 0f, 1f);

        float horizontalMagnitude = MathF.Sqrt((netX * netX) + (netZ * netZ));
        float verticalMagnitude = MathF.Abs(netY);
        float verticalAngle = MathF.Atan2(verticalMagnitude, horizontalMagnitude) * (180f / MathF.PI);
        bool hasHorizontalDirection = horizontalMagnitude > LeashDefaults.NormalizeEpsilon;

        return new LeashSignal(
            netX,
            netY,
            netZ,
            stretch,
            horizontalMagnitude,
            verticalMagnitude,
            verticalAngle,
            hasHorizontalDirection,
            hasHorizontalDirection ? netX / horizontalMagnitude : 0f,
            hasHorizontalDirection ? netZ / horizontalMagnitude : 0f);
    }

    private static float ClampFinite(float value, float min, float max)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : 0f;
}

internal readonly record struct LeashIntent(
    float MoveX,
    float MoveZ,
    bool ShouldRun,
    float TurnValue,
    bool HasTurnInput,
    bool VerticalModeActive,
    bool TurnSuppressedByVertical,
    bool ShouldApplyVerticalPull,
    float VerticalTargetVelocity,
    float PullVerticalAngle)
{
    public static readonly LeashIntent Idle = new(0f, 0f, false, 0f, false, false, false, false, 0f, 0f);
}

internal readonly record struct MovementCommand(
    bool Run,
    float MoveVertical,
    float MoveHorizontal,
    bool TurnWritten,
    float TurnHorizontal)
{
    public static readonly MovementCommand None = new(false, 0f, 0f, false, 0f);
}

internal sealed class LeashMotionEngine
{
    private readonly NonOvershootingAxisFilter _moveX = new();
    private readonly NonOvershootingAxisFilter _moveZ = new();
    private bool _verticalModeActive;
    private bool _turnModeActive;

    public void Reset()
    {
        _moveX.Reset();
        _moveZ.Reset();
        _verticalModeActive = false;
        _turnModeActive = false;
    }

    public LeashIntent Resolve(LeashSignal signal, LeashSettings settings, bool grabbedForMotion, float deltaTime)
    {
        if (!grabbedForMotion)
        {
            Reset();
            return LeashIntent.Idle;
        }

        bool verticalModeActive = ResolveVerticalMode(signal, settings);
        (float moveX, float moveZ) = ResolveHorizontalMovement(signal, settings, deltaTime);
        bool turningAllowed = ResolveTurnMode(signal, settings, verticalModeActive);
        float turnValue = turningAllowed ? CalculateTurning(signal, settings) : 0f;
        bool hasTurnInput = MathF.Abs(turnValue) > LeashDefaults.TurnEpsilon;

        return new LeashIntent(
            moveX,
            moveZ,
            signal.Stretch > settings.RunDeadzone,
            hasTurnInput ? turnValue : 0f,
            hasTurnInput,
            verticalModeActive,
            settings.TurningEnabled && verticalModeActive,
            verticalModeActive,
            verticalModeActive ? signal.NetY * settings.VerticalMultiplier : 0f,
            signal.VerticalAngle);
    }

    private (float X, float Z) ResolveHorizontalMovement(LeashSignal signal, LeashSettings settings, float deltaTime)
    {
        if (signal.Stretch <= settings.WalkDeadzone)
        {
            _moveX.Reset();
            _moveZ.Reset();
            return (0f, 0f);
        }

        float netX = signal.NetX;
        float netZ = signal.NetZ;
        if (signal.VerticalMagnitude >= settings.UpDownDeadzone && settings.UpDownCompensation > 0f)
        {
            float compensation = Math.Clamp(
                1f - (signal.VerticalMagnitude * settings.UpDownCompensation * 0.5f),
                0.1f,
                1f);
            netX *= compensation;
            netZ *= compensation;
        }

        float strength = signal.Stretch * settings.StrengthMultiplier;
        float targetX = Math.Clamp(netX * strength, -1f, 1f);
        float targetZ = Math.Clamp(netZ * strength, -1f, 1f);

        return (
            _moveX.Update(targetX, settings.MovementSmoothing, deltaTime),
            _moveZ.Update(targetZ, settings.MovementSmoothing, deltaTime));
    }

    private bool ResolveVerticalMode(LeashSignal signal, LeashSettings settings)
    {
        if (!settings.VerticalEnabled)
        {
            _verticalModeActive = false;
            return false;
        }

        float exitAngle = Math.Max(0f, settings.VerticalAngle - LeashDefaults.AngleHysteresisDegrees);
        float exitDeadzone = settings.VerticalDeadzone * LeashDefaults.DeadzoneHysteresisFactor;
        _verticalModeActive = _verticalModeActive
            ? signal.VerticalAngle >= exitAngle && signal.VerticalMagnitude >= exitDeadzone
            : signal.VerticalAngle >= settings.VerticalAngle && signal.VerticalMagnitude >= settings.VerticalDeadzone;

        return _verticalModeActive;
    }

    private bool ResolveTurnMode(LeashSignal signal, LeashSettings settings, bool verticalModeActive)
    {
        if (!settings.TurningEnabled || verticalModeActive || !signal.HasHorizontalDirection)
        {
            _turnModeActive = false;
            return false;
        }

        float exitAngle = Math.Min(90f, settings.TurningVerticalAngleLimit + LeashDefaults.AngleHysteresisDegrees);
        float exitDeadzone = settings.TurningDeadzone * LeashDefaults.DeadzoneHysteresisFactor;
        _turnModeActive = _turnModeActive
            ? signal.Stretch > exitDeadzone && signal.VerticalAngle <= exitAngle
            : signal.Stretch > settings.TurningDeadzone && signal.VerticalAngle <= settings.TurningVerticalAngleLimit;

        return _turnModeActive;
    }

    private static float CalculateTurning(LeashSignal signal, LeashSettings settings)
    {
        ((float X, float Z) forward, (float X, float Z) side) = settings.Direction switch
        {
            LeashDirection.North => ((0f, 1f), (1f, 0f)),
            LeashDirection.South => ((0f, -1f), (-1f, 0f)),
            LeashDirection.East => ((1f, 0f), (0f, 1f)),
            LeashDirection.West => ((-1f, 0f), (0f, -1f)),
            _ => ((0f, 1f), (1f, 0f))
        };

        float forwardDot = (signal.NormalizedX * forward.X) + (signal.NormalizedZ * forward.Z);
        float sideDot = (signal.NormalizedX * side.X) + (signal.NormalizedZ * side.Z);
        if (MathF.Abs(sideDot) < LeashDefaults.NormalizeEpsilon)
            return 0f;

        float pullAngle = MathF.Abs(MathF.Atan2(sideDot, forwardDot) * (180f / MathF.PI));
        if (pullAngle < settings.TurningGoal)
            return 0f;

        float strength = (pullAngle - settings.TurningGoal) / Math.Max(1f, 180f - settings.TurningGoal);
        return Math.Clamp(
            MathF.Sign(sideDot) * strength * signal.Stretch * settings.TurningMultiplier,
            -1f,
            1f);
    }

    internal static float SmoothingAlpha(float smoothing, float deltaTime)
    {
        if (smoothing <= 0f)
            return 1f;
        if (smoothing >= 1f)
            return 0f;

        return 1f - MathF.Pow(smoothing, deltaTime / LeashDefaults.BaseDeltaTimeSeconds);
    }
}

internal sealed class NonOvershootingAxisFilter
{
    private float _output;
    private int _acceptedDirection;
    private int _pendingDirection;
    private float _pendingDuration;
    private float _neutralDuration;

    public void Reset()
    {
        _output = 0f;
        _acceptedDirection = 0;
        _pendingDirection = 0;
        _pendingDuration = 0f;
        _neutralDuration = 0f;
    }

    public float Update(float target, float smoothing, float deltaTime)
    {
        target = float.IsFinite(target) ? Math.Clamp(target, -1f, 1f) : 0f;
        int targetDirection = MathF.Abs(target) <= LeashDefaults.NormalizeEpsilon ? 0 : Math.Sign(target);

        if (targetDirection == 0)
        {
            _output = 0f;
            _pendingDirection = 0;
            _pendingDuration = 0f;
            _neutralDuration += deltaTime;
            if (_neutralDuration >= LeashDefaults.DirectionChangeHoldSeconds)
                _acceptedDirection = 0;
            return 0f;
        }

        _neutralDuration = 0f;
        if (_acceptedDirection == 0)
        {
            _acceptedDirection = targetDirection;
            _pendingDirection = 0;
            _pendingDuration = 0f;
        }
        else if (targetDirection != _acceptedDirection)
        {
            _output = 0f;
            if (_pendingDirection != targetDirection)
            {
                _pendingDirection = targetDirection;
                _pendingDuration = 0f;
            }

            _pendingDuration += deltaTime;
            if (_pendingDuration < LeashDefaults.DirectionChangeHoldSeconds)
                return 0f;

            _acceptedDirection = targetDirection;
            _pendingDirection = 0;
            _pendingDuration = 0f;
        }
        else
        {
            _pendingDirection = 0;
            _pendingDuration = 0f;
        }

        float alpha = LeashMotionEngine.SmoothingAlpha(smoothing, deltaTime);
        float next = _output + ((target - _output) * alpha);

        // Smoothing is attack-only. When the pull weakens, brake immediately instead
        // of continuing to command more movement than the current leash signal asks for.
        if (MathF.Abs(next) > MathF.Abs(target))
            next = target;
        if (Math.Sign(next) != targetDirection)
            next = 0f;

        _output = next;
        return _output;
    }
}

internal sealed class VerticalMotionState
{
    public float Offset { get; private set; }
    public float Velocity { get; private set; }

    public void Reset()
    {
        Offset = 0f;
        Velocity = 0f;
    }

    public void Stop() => Velocity = 0f;

    public void Rebase(float offset)
    {
        Offset = offset;
        Velocity = 0f;
    }

    public bool ApplyPull(float targetVelocity, float smoothing, float deltaTime, float maximumOffset)
    {
        float alpha = LeashMotionEngine.SmoothingAlpha(smoothing, deltaTime);
        Velocity += (targetVelocity - Velocity) * alpha;

        float previousOffset = Offset;
        Offset = Math.Clamp(Offset + (Velocity * deltaTime), -maximumOffset, maximumOffset);
        if (MathF.Abs(Offset) >= maximumOffset && MathF.Sign(Velocity) == MathF.Sign(Offset))
            Velocity = 0f;

        return Offset != previousOffset;
    }

    public bool ReturnToOrigin(float gravityStrength, float terminalVelocity, float deltaTime)
    {
        if (MathF.Abs(Offset) < LeashDefaults.StopThreshold &&
            MathF.Abs(Velocity) < LeashDefaults.VelocityStopThreshold)
        {
            bool changed = Offset != 0f || Velocity != 0f;
            Reset();
            return changed;
        }

        float direction = -MathF.Sign(Offset);
        if (direction == 0f)
        {
            Reset();
            return true;
        }

        Velocity = Math.Clamp(Velocity + (gravityStrength * direction * deltaTime), -terminalVelocity, terminalVelocity);
        float nextOffset = Offset + (Velocity * deltaTime);
        if (MathF.Sign(nextOffset) != MathF.Sign(Offset))
        {
            Reset();
            return true;
        }

        Offset = nextOffset;
        return true;
    }
}

internal sealed class ExternalPoseRecoveryState
{
    private double _stableSinceSeconds;
    private int _automaticResumeAttempts;

    public bool Suspended { get; private set; }
    public bool LockedUntilRegrab { get; private set; }
    public int AutomaticResumeAttempts => _automaticResumeAttempts;

    public void Reset()
    {
        Suspended = false;
        LockedUntilRegrab = false;
        _stableSinceSeconds = 0d;
        _automaticResumeAttempts = 0;
    }

    public bool Suspend(bool grabbedForMotion, double nowSeconds)
    {
        Suspended = true;
        if (!grabbedForMotion || _automaticResumeAttempts > 0)
        {
            LockedUntilRegrab = true;
            return false;
        }

        _stableSinceSeconds = nowSeconds;
        return true;
    }

    public bool Observe(bool grabbedForMotion, bool poseChanged, double nowSeconds, double quietSeconds)
    {
        if (!Suspended || !grabbedForMotion || LockedUntilRegrab)
            return false;

        if (poseChanged)
        {
            _stableSinceSeconds = nowSeconds;
            return false;
        }

        if (nowSeconds - _stableSinceSeconds < quietSeconds)
            return false;

        Suspended = false;
        _automaticResumeAttempts++;
        return true;
    }
}
