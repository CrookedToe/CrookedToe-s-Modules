using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class LeashSettingsTests
{
    [TestMethod]
    public void RunThresholdCannotBeLowerThanMoveThreshold()
    {
        LeashSettings settings = Settings() with { WalkDeadzone = 0.8f, RunDeadzone = 0.2f };

        LeashSettings sanitized = settings.Sanitize();

        Assert.AreEqual(0.8f, sanitized.WalkDeadzone);
        Assert.AreEqual(0.8f, sanitized.RunDeadzone);
    }

    [TestMethod]
    public void NonFiniteSettingsFallBackToSafeValues()
    {
        LeashSettings sanitized = (Settings() with
        {
            WalkDeadzone = float.NaN,
            RunDeadzone = float.PositiveInfinity,
            StrengthMultiplier = float.NaN,
            VerticalMultiplier = float.NegativeInfinity,
            MaximumVerticalOffset = float.NaN
        }).Sanitize();

        Assert.IsTrue(float.IsFinite(sanitized.WalkDeadzone));
        Assert.IsTrue(sanitized.RunDeadzone >= sanitized.WalkDeadzone);
        Assert.IsTrue(float.IsFinite(sanitized.StrengthMultiplier));
        Assert.IsTrue(float.IsFinite(sanitized.VerticalMultiplier));
        Assert.IsTrue(float.IsFinite(sanitized.MaximumVerticalOffset));
    }

    private static LeashSettings Settings()
        => new(
            WalkDeadzone: 0.15f,
            RunDeadzone: 0.7f,
            StrengthMultiplier: 1.2f,
            Direction: LeashDirection.North,
            TurningEnabled: false,
            TurningMultiplier: 0.8f,
            VerticalEnabled: false,
            ReturnHeightOnRelease: false,
            VerticalMultiplier: 1f,
            MaximumVerticalOffset: 3f);
}
