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
    LeashDirection = 0,
    WalkDeadzone = 1,
    RunDeadzone = 2,
    StrengthMultiplier = 3,
    TurningEnabled = 7,
    TurningMultiplier = 8,
    VerticalMovementEnabled = 12,
    VerticalMovementMultiplier = 13,
    GrabBasedGravity = 17,
    MaximumVerticalOffset = 21
}
