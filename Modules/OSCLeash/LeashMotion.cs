namespace CrookedToe.Modules.OSCLeash;

internal static class LeashDefaults
{
    public const int UpdateIntervalMilliseconds = 16;
    public const float UpdateIntervalSeconds = UpdateIntervalMilliseconds / 1000f;
    public const float MaxDeltaTimeSeconds = 0.05f;
    public const float ExternalPoseQuietSeconds = 0.25f;
    public const float VrRetryIntervalSeconds = 2f;
    public const float MotionSilenceSeconds = 0.3f;
    public const float InputFreshnessSeconds = 2f;
    public const float PlayerPublishIntervalSeconds = 0.05f;
    public const float NeutralRepairSeconds = 2f;
    public const int StopNeutralRepetitions = 3;
    public const float HealthLogIntervalSeconds = 60f;
    public const float TurnEpsilon = 0.0001f;
    public const float NormalizeEpsilon = 0.0001f;
    public const float VerticalCompensationThreshold = 0.5f;
    public const float VerticalCompensationStrength = 0.5f;
    public const float TurnDeadzone = 0.15f;
    public const float TurnStartAngleDegrees = 20f;
    public const float TurnVerticalLimitDegrees = 45f;
    public const float HeightDeadzone = 0.15f;
    public const float HeightActivationAngleDegrees = 45f;
    public const float HeightExitAngleDegrees = 40f;
    public const float ReturnAcceleration = 9.81f;
}

internal readonly record struct LeashSettings(
    float WalkDeadzone,
    float RunDeadzone,
    float StrengthMultiplier,
    LeashDirection Direction,
    bool TurningEnabled,
    float TurningMultiplier,
    bool VerticalEnabled,
    bool ReturnHeightOnRelease,
    float VerticalMultiplier,
    float MaximumVerticalOffset)
{
    public LeashSettings Sanitize()
    {
        float walkDeadzone = ClampFinite(WalkDeadzone, 0f, 1f, 0.15f);
        return this with
        {
            WalkDeadzone = walkDeadzone,
            RunDeadzone = ClampFinite(RunDeadzone, walkDeadzone, 1f, MathF.Max(0.7f, walkDeadzone)),
            StrengthMultiplier = ClampFinite(StrengthMultiplier, 0.1f, 5f, 1.2f),
            TurningMultiplier = ClampFinite(TurningMultiplier, 0.1f, 2f, 0.8f),
            VerticalMultiplier = ClampFinite(VerticalMultiplier, 0.1f, 5f, 1f),
            MaximumVerticalOffset = ClampFinite(MaximumVerticalOffset, 0.25f, 20f, 3f)
        };
    }

    private static float ClampFinite(float value, float min, float max, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

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
    float VerticalTargetVelocity)
{
    public static readonly LeashIntent Idle = new(0f, 0f, false, 0f, false, false, 0f);
}

internal sealed class LeashMotionEngine
{
    private bool _verticalModeActive;

    public void Reset() => _verticalModeActive = false;

    public LeashIntent Resolve(LeashSignal signal, LeashSettings settings, bool grabbedForMotion)
    {
        settings = settings.Sanitize();
        if (!grabbedForMotion)
        {
            Reset();
            return LeashIntent.Idle;
        }

        bool verticalModeActive = ResolveVerticalMode(signal, settings);
        (float moveX, float moveZ) = ResolveHorizontalMovement(signal, settings);
        bool hasMovement = MathF.Sqrt((moveX * moveX) + (moveZ * moveZ)) > LeashDefaults.NormalizeEpsilon;
        bool turningAllowed = settings.TurningEnabled &&
                              !verticalModeActive &&
                              signal.HasHorizontalDirection &&
                              signal.Stretch > LeashDefaults.TurnDeadzone &&
                              signal.VerticalAngle <= LeashDefaults.TurnVerticalLimitDegrees;
        float turnValue = turningAllowed ? CalculateTurning(signal, settings) : 0f;
        bool hasTurnInput = MathF.Abs(turnValue) > LeashDefaults.TurnEpsilon;

        return new LeashIntent(
            moveX,
            moveZ,
            hasMovement && signal.Stretch > settings.RunDeadzone,
            hasTurnInput ? turnValue : 0f,
            hasTurnInput,
            verticalModeActive,
            verticalModeActive
                ? signal.NetY * signal.Stretch * settings.VerticalMultiplier
                : 0f);
    }

    private static (float X, float Z) ResolveHorizontalMovement(LeashSignal signal, LeashSettings settings)
    {
        if (signal.Stretch <= settings.WalkDeadzone)
            return (0f, 0f);

        float netX = signal.NetX;
        float netZ = signal.NetZ;
        if (signal.VerticalMagnitude >= LeashDefaults.VerticalCompensationThreshold)
        {
            float compensation = Math.Clamp(
                1f - (signal.VerticalMagnitude * LeashDefaults.VerticalCompensationStrength * 0.5f),
                0.1f,
                1f);
            netX *= compensation;
            netZ *= compensation;
        }

        float strength = signal.Stretch * settings.StrengthMultiplier;
        return ClampToUnitCircle(netX * strength, netZ * strength);
    }

    private static (float X, float Z) ClampToUnitCircle(float x, float z)
    {
        float magnitude = MathF.Sqrt((x * x) + (z * z));
        if (!float.IsFinite(magnitude) || magnitude <= LeashDefaults.NormalizeEpsilon)
            return (0f, 0f);
        if (magnitude <= 1f)
            return (x, z);

        float scale = 1f / magnitude;
        return (x * scale, z * scale);
    }

    private bool ResolveVerticalMode(LeashSignal signal, LeashSettings settings)
    {
        if (!settings.VerticalEnabled ||
            signal.Stretch <= settings.WalkDeadzone ||
            signal.VerticalMagnitude < LeashDefaults.HeightDeadzone)
        {
            _verticalModeActive = false;
            return false;
        }

        float minimumAngle = _verticalModeActive
            ? LeashDefaults.HeightExitAngleDegrees
            : LeashDefaults.HeightActivationAngleDegrees;
        _verticalModeActive = signal.VerticalAngle >= minimumAngle;
        return _verticalModeActive;
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
        if (pullAngle < LeashDefaults.TurnStartAngleDegrees)
            return 0f;

        float strength = (pullAngle - LeashDefaults.TurnStartAngleDegrees) /
                         (180f - LeashDefaults.TurnStartAngleDegrees);
        return Math.Clamp(
            MathF.Sign(sideDot) * strength * signal.Stretch * settings.TurningMultiplier,
            -1f,
            1f);
    }
}

internal sealed class VerticalMotionState
{
    private float _returnSpeed;

    public float Offset { get; private set; }

    public void Reset()
    {
        Offset = 0f;
        _returnSpeed = 0f;
    }

    public void Rebase(float offset)
    {
        Offset = float.IsFinite(offset) ? offset : 0f;
        _returnSpeed = 0f;
    }

    public bool ApplyPull(float targetVelocity, float deltaTime, float maximumOffset)
    {
        targetVelocity = float.IsFinite(targetVelocity) ? targetVelocity : 0f;
        deltaTime = ClampDeltaTime(deltaTime);
        maximumOffset = float.IsFinite(maximumOffset) ? MathF.Max(0f, maximumOffset) : 0f;

        float previousOffset = Offset;
        Offset = Math.Clamp(Offset + (targetVelocity * deltaTime), -maximumOffset, maximumOffset);
        return Offset != previousOffset;
    }

    public bool Constrain(float maximumOffset)
    {
        maximumOffset = float.IsFinite(maximumOffset) ? MathF.Max(0f, maximumOffset) : 0f;
        float constrained = Math.Clamp(Offset, -maximumOffset, maximumOffset);
        if (constrained == Offset)
            return false;

        Offset = constrained;
        _returnSpeed = 0f;
        return true;
    }

    public bool ReturnToOrigin(float acceleration, float maximumSpeed, float deltaTime)
    {
        acceleration = float.IsFinite(acceleration) ? MathF.Max(0f, acceleration) : 0f;
        maximumSpeed = float.IsFinite(maximumSpeed) ? MathF.Max(0f, maximumSpeed) : 0f;
        deltaTime = ClampDeltaTime(deltaTime);
        if (Offset == 0f || acceleration == 0f || maximumSpeed == 0f || deltaTime == 0f)
            return false;

        _returnSpeed = MathF.Min(
            _returnSpeed + (acceleration * deltaTime),
            maximumSpeed);
        float step = _returnSpeed * deltaTime;
        if (MathF.Abs(Offset) <= step)
        {
            Reset();
            return true;
        }

        Offset -= MathF.Sign(Offset) * step;
        return true;
    }

    private static float ClampDeltaTime(float deltaTime)
        => float.IsFinite(deltaTime)
            ? Math.Clamp(deltaTime, 0f, LeashDefaults.MaxDeltaTimeSeconds)
            : 0f;
}

internal sealed class ExternalPoseRecoveryState
{
    private double _stableSinceSeconds;
    private int _automaticResumeAttempts;

    public bool Suspended { get; private set; }
    public bool LockedUntilRegrab { get; private set; }
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
