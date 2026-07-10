using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class LeashMotionEngineTests
{
    [TestMethod]
    public void DiagonalMovementNeverExceedsUnitInput()
    {
        var engine = new LeashMotionEngine();

        LeashIntent intent = engine.Resolve(
            LeashSignal.From(1f, 0f, 1f, 1f),
            Settings(),
            grabbedForMotion: true,
            LeashDefaults.BaseDeltaTimeSeconds);

        float magnitude = MathF.Sqrt((intent.MoveX * intent.MoveX) + (intent.MoveZ * intent.MoveZ));
        Assert.IsTrue(magnitude <= 1f + 0.0001f);
        Assert.AreEqual(intent.MoveX, intent.MoveZ, 0.0001f);
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
