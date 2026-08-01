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
            grabbedForMotion: true);

        float magnitude = MathF.Sqrt((intent.MoveX * intent.MoveX) + (intent.MoveZ * intent.MoveZ));
        Assert.IsTrue(magnitude <= 1f + 0.0001f);
        Assert.AreEqual(intent.MoveX, intent.MoveZ, 0.0001f);
    }

    [TestMethod]
    public void CrossingTheTargetCommandsCorrectionImmediately()
    {
        LeashSettings settings = Settings();
        var engine = new LeashMotionEngine();
        LeashSignal positive = LeashSignal.From(1f, 0f, 0f, 1f);
        LeashSignal negative = LeashSignal.From(-1f, 0f, 0f, 1f);

        LeashIntent beforeCrossing = engine.Resolve(positive, settings, grabbedForMotion: true);
        LeashIntent correction = engine.Resolve(negative, settings, grabbedForMotion: true);

        Assert.IsTrue(beforeCrossing.MoveX > 0f);
        Assert.IsTrue(correction.MoveX < 0f);
    }

    [TestMethod]
    public void DroppingBelowDeadzoneStopsImmediately()
    {
        LeashSettings settings = Settings();
        var engine = new LeashMotionEngine();

        LeashIntent moving = engine.Resolve(
            LeashSignal.From(1f, 0f, 0f, 1f),
            settings,
            grabbedForMotion: true);
        LeashIntent stopped = engine.Resolve(
            LeashSignal.From(1f, 0f, 0f, settings.WalkDeadzone),
            settings,
            grabbedForMotion: true);

        Assert.IsTrue(moving.MoveX > 0f);
        Assert.AreEqual(0f, stopped.MoveX);
        Assert.AreEqual(0f, stopped.MoveZ);
    }

    [TestMethod]
    public void ReleasedLeashAlwaysProducesIdleIntent()
    {
        LeashSettings settings = Settings() with
        {
            TurningEnabled = true,
            VerticalEnabled = true
        };

        var engine = new LeashMotionEngine();
        LeashIntent intent = engine.Resolve(
            LeashSignal.From(1f, 1f, 1f, 1f),
            settings,
            grabbedForMotion: false);

        Assert.AreEqual(LeashIntent.Idle, intent);
    }

    [TestMethod]
    public void VerticalPullUsesCurrentSignalAndSuppressesTurning()
    {
        LeashSettings settings = Settings() with
        {
            TurningEnabled = true,
            VerticalEnabled = true,
            VerticalMultiplier = 2f
        };

        var engine = new LeashMotionEngine();
        LeashIntent intent = engine.Resolve(
            LeashSignal.From(0.1f, -0.5f, 0.1f, 1f),
            settings,
            grabbedForMotion: true);

        Assert.IsTrue(intent.VerticalModeActive);
        Assert.AreEqual(-1f, intent.VerticalTargetVelocity, 0.0001f);
        Assert.IsFalse(intent.HasTurnInput);
    }

    [TestMethod]
    public void UnstretchedVerticalDirectionCannotMoveHeight()
    {
        var engine = new LeashMotionEngine();
        LeashSettings settings = Settings() with { VerticalEnabled = true, VerticalMultiplier = 2f };

        LeashIntent intent = engine.Resolve(
            LeashSignal.From(0f, 1f, 0f, stretch: 0f),
            settings,
            grabbedForMotion: true);

        Assert.IsFalse(intent.VerticalModeActive);
        Assert.AreEqual(0f, intent.VerticalTargetVelocity);
    }

    [TestMethod]
    public void VerticalSpeedIsScaledByStretch()
    {
        var engine = new LeashMotionEngine();
        LeashSettings settings = Settings() with { VerticalEnabled = true, VerticalMultiplier = 2f };

        LeashIntent intent = engine.Resolve(
            LeashSignal.From(0f, -1f, 0f, stretch: 0.5f),
            settings,
            grabbedForMotion: true);

        Assert.IsTrue(intent.VerticalModeActive);
        Assert.AreEqual(-1f, intent.VerticalTargetVelocity, 0.0001f);
    }

    [TestMethod]
    public void RunRequiresActualHorizontalMovement()
    {
        var engine = new LeashMotionEngine();
        LeashSettings settings = Settings() with
        {
            WalkDeadzone = 0.15f,
            RunDeadzone = 0.2f,
            VerticalEnabled = true
        };

        LeashIntent intent = engine.Resolve(
            LeashSignal.From(0f, 1f, 0f, stretch: 0.5f),
            settings,
            grabbedForMotion: true);

        Assert.IsFalse(intent.ShouldRun);
        Assert.AreEqual(0f, intent.MoveX);
        Assert.AreEqual(0f, intent.MoveZ);
    }

    [TestMethod]
    public void VerticalModeUsesOneSmallExitHysteresis()
    {
        var engine = new LeashMotionEngine();
        LeashSettings settings = Settings() with { VerticalEnabled = true };

        LeashIntent entered = engine.Resolve(
            SignalAtVerticalAngle(46f),
            settings,
            grabbedForMotion: true);
        LeashIntent noisyBoundary = engine.Resolve(
            SignalAtVerticalAngle(43f),
            settings,
            grabbedForMotion: true);
        LeashIntent exited = engine.Resolve(
            SignalAtVerticalAngle(39f),
            settings,
            grabbedForMotion: true);

        Assert.IsTrue(entered.VerticalModeActive);
        Assert.IsTrue(noisyBoundary.VerticalModeActive);
        Assert.IsFalse(exited.VerticalModeActive);
    }

    [TestMethod]
    public void RandomizedSignalsAlwaysRespectSafetyInvariants()
    {
        var random = new Random(0x1EA5);
        var engine = new LeashMotionEngine();

        for (int i = 0; i < 5_000; i++)
        {
            LeashSettings settings = (Settings() with
            {
                WalkDeadzone = NextFloat(0f, 1f),
                RunDeadzone = NextFloat(0f, 1f),
                StrengthMultiplier = NextFloat(0.1f, 5f),
                TurningEnabled = random.Next(2) == 1,
                TurningMultiplier = NextFloat(0.1f, 2f),
                VerticalEnabled = random.Next(2) == 1,
                VerticalMultiplier = NextFloat(0.1f, 5f),
                MaximumVerticalOffset = NextFloat(0.25f, 20f)
            }).Sanitize();
            LeashSignal signal = LeashSignal.From(
                NextFloat(-2f, 2f),
                NextFloat(-2f, 2f),
                NextFloat(-2f, 2f),
                NextFloat(-0.5f, 1.5f));
            bool grabbed = random.Next(4) != 0;

            LeashIntent intent = engine.Resolve(signal, settings, grabbed);
            float movementMagnitude = MathF.Sqrt((intent.MoveX * intent.MoveX) + (intent.MoveZ * intent.MoveZ));

            Assert.IsTrue(float.IsFinite(intent.MoveX));
            Assert.IsTrue(float.IsFinite(intent.MoveZ));
            Assert.IsTrue(float.IsFinite(intent.TurnValue));
            Assert.IsTrue(float.IsFinite(intent.VerticalTargetVelocity));
            Assert.IsTrue(movementMagnitude <= 1.0001f);
            Assert.IsFalse(intent.ShouldRun && movementMagnitude <= LeashDefaults.NormalizeEpsilon);
            Assert.IsTrue(
                MathF.Abs(intent.VerticalTargetVelocity) <=
                (settings.VerticalMultiplier * signal.Stretch) + 0.0001f);

            if (!grabbed)
                Assert.AreEqual(LeashIntent.Idle, intent);
            if (signal.Stretch <= settings.WalkDeadzone)
            {
                Assert.AreEqual(0f, intent.MoveX);
                Assert.AreEqual(0f, intent.MoveZ);
                Assert.IsFalse(intent.VerticalModeActive);
                Assert.AreEqual(0f, intent.VerticalTargetVelocity);
            }
        }

        float NextFloat(float min, float max)
            => min + ((float)random.NextDouble() * (max - min));
    }

    private static LeashSignal SignalAtVerticalAngle(float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return LeashSignal.From(MathF.Cos(radians), MathF.Sin(radians), 0f, stretch: 1f);
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
