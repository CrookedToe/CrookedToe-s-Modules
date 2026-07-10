namespace CrookedToe.Modules.OSCLeash;

public enum LeashDirection
{
    North,
    South,
    East,
    West
}

public enum OSCLeashParameter
{
    ZPositive, ZNegative, XPositive, XNegative, 
    YPositive, YNegative, IsGrabbed, Stretch,
    LeashEnable
}

public enum OSCLeashSetting
{
    LeashDirection, WalkDeadzone, RunDeadzone, StrengthMultiplier,
    UpDownDeadzone, UpDownCompensation, MovementSmoothing,
    TurningEnabled, TurningMultiplier, TurningDeadzone, TurningGoal, TurningVerticalAngleLimit,
    VerticalMovementEnabled, VerticalMovementMultiplier, VerticalMovementDeadzone,
    VerticalMovementSmoothing, VerticalHorizontalCompensation, GrabBasedGravity,
    GravityStrength, TerminalVelocity, DebugTraceEnabled, MaximumVerticalOffset
}
