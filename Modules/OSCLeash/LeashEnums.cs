namespace CrookedToe.Modules.OSCLeash;

/// <summary>Leash direction relative to avatar</summary>
public enum LeashDirection
{
    /// <summary>Front-facing (default) - pull back to turn</summary>
    North,
    /// <summary>Back-facing - pull forward to turn</summary>
    South,
    /// <summary>Right-facing - pull left to turn</summary>
    East,
    /// <summary>Left-facing - pull right to turn</summary>
    West
}

/// <summary>OSC parameters for leash control</summary>
public enum OSCLeashParameter
{
    ZPositive, ZNegative, XPositive, XNegative, 
    YPositive, YNegative, IsGrabbed, Stretch
}

/// <summary>Configuration settings for leash behavior</summary>
public enum OSCLeashSetting
{
    // Basic Movement
    LeashDirection, WalkDeadzone, RunDeadzone, StrengthMultiplier,
    UpDownDeadzone, UpDownCompensation,
    
    // Turning
    TurningEnabled, TurningMultiplier, TurningDeadzone, TurningGoal,
    
    // Vertical Movement
    VerticalMovementEnabled, VerticalMovementMultiplier, VerticalMovementDeadzone,
    VerticalMovementSmoothing, VerticalHorizontalCompensation, GrabBasedGravity,
    
    // Advanced Gravity Settings
    GravityStrength, TerminalVelocity
} 