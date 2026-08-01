using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class LeashControlPipelineTests
{
    [TestMethod]
    public void CapturedZeroStretchVerticalGrabProducesNoMovementAtAnyLayer()
    {
        var input = new LeashInputState();
        var motion = new LeashMotionEngine();
        var height = new VerticalMotionState();
        var playerInput = new PlayerInputController();
        var sink = new CapturingPlayerInputSink();
        LeashSettings settings = Settings() with { VerticalEnabled = true, VerticalMultiplier = 1.5f };

        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.Stretch, 0f);
        input.Set(OSCLeashParameter.XNegative, 0.013f);
        input.Set(OSCLeashParameter.YNegative, 0.988f);
        input.Set(OSCLeashParameter.ZPositive, 0.024f);

        LeashIntent intent = motion.Resolve(input.Signal, settings, input.GrabbedForMotion);
        bool heightChanged = height.ApplyPull(intent.VerticalTargetVelocity, 0.01f, settings.MaximumVerticalOffset);
        Assert.IsTrue(playerInput.Apply(sink, intent, input.GrabbedForMotion));

        Assert.AreEqual(LeashIntent.Idle.VerticalTargetVelocity, intent.VerticalTargetVelocity);
        Assert.IsFalse(intent.VerticalModeActive);
        Assert.IsFalse(heightChanged);
        Assert.AreEqual(0f, height.Offset);
        Assert.AreEqual(0f, sink.Vertical[^1]);
        Assert.AreEqual(0f, sink.Horizontal[^1]);
    }

    [TestMethod]
    public void AvatarResetNeutralizesPriorMovementAndClearsTheOldDirection()
    {
        var input = new LeashInputState();
        var motion = new LeashMotionEngine();
        var playerInput = new PlayerInputController();
        var sink = new CapturingPlayerInputSink();
        LeashSettings settings = Settings();

        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.Stretch, 1f);
        input.Set(OSCLeashParameter.XPositive, 1f);
        LeashIntent moving = motion.Resolve(input.Signal, settings, input.GrabbedForMotion);
        Assert.IsTrue(playerInput.Apply(sink, moving, input.GrabbedForMotion));
        Assert.IsTrue(sink.Horizontal[^1] > 0f);

        input.Reset();
        motion.Reset();
        LeashIntent afterAvatarChange = motion.Resolve(input.Signal, settings, input.GrabbedForMotion);
        Assert.IsTrue(playerInput.Apply(sink, afterAvatarChange, input.GrabbedForMotion));

        Assert.AreEqual(LeashIntent.Idle, afterAvatarChange);
        Assert.AreEqual(0f, sink.Horizontal[^1]);
        Assert.IsFalse(playerInput.HasPendingNeutral);
    }

    [TestMethod]
    public void LateralReversalAndReleaseReachThePlayerWithoutOvershoot()
    {
        var input = new LeashInputState();
        var motion = new LeashMotionEngine();
        var playerInput = new PlayerInputController();
        var sink = new CapturingPlayerInputSink();
        LeashSettings settings = Settings();

        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.Stretch, 1f);
        input.Set(OSCLeashParameter.XPositive, 1f);
        ApplyFrame();
        float positive = sink.Horizontal[^1];

        input.Set(OSCLeashParameter.XPositive, 0f);
        input.Set(OSCLeashParameter.XNegative, 1f);
        ApplyFrame();
        float correction = sink.Horizontal[^1];

        input.Set(OSCLeashParameter.IsGrabbed, false);
        ApplyFrame();

        Assert.IsTrue(positive > 0f);
        Assert.IsTrue(correction < 0f);
        Assert.AreEqual(0f, sink.Horizontal[^1]);

        void ApplyFrame()
        {
            LeashIntent intent = motion.Resolve(input.Signal, settings, input.GrabbedForMotion);
            Assert.IsTrue(playerInput.Apply(sink, intent, input.GrabbedForMotion));
        }
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

    private sealed class CapturingPlayerInputSink : IPlayerInputSink
    {
        public List<float> Vertical { get; } = [];
        public List<float> Horizontal { get; } = [];

        public void Run()
        {
        }

        public void StopRun()
        {
        }

        public void MoveVertical(float value) => Vertical.Add(value);
        public void MoveHorizontal(float value) => Horizontal.Add(value);
        public void LookHorizontal(float value)
        {
        }
    }
}
