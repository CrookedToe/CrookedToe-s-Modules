using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class PlayerInputControllerTests
{
    [TestMethod]
    public void FailedActiveCommandStillLeavesNeutralCleanupPending()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink { FailHorizontal = true };

        bool applied = controller.Apply(sink, MovingIntent(), active: true);

        Assert.IsFalse(applied);
        Assert.IsTrue(controller.HasPendingNeutral);
        Assert.AreEqual(1, sink.VerticalCommands.Count);
        Assert.AreEqual(1, sink.HorizontalAttempts);

        sink.FailHorizontal = false;
        Assert.IsTrue(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.IsFalse(controller.HasPendingNeutral);
        Assert.AreEqual(0f, sink.HorizontalCommands[^1]);
    }

    [TestMethod]
    public void FailedNeutralCommandIsRetriedUntilItSucceeds()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();
        Assert.IsTrue(controller.Apply(sink, MovingIntent(), active: true));

        sink.FailVertical = true;
        Assert.IsFalse(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.IsTrue(controller.HasPendingNeutral);

        sink.FailVertical = false;
        Assert.IsTrue(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.IsFalse(controller.HasPendingNeutral);
        Assert.AreEqual(0f, sink.VerticalCommands[^1]);
    }

    [TestMethod]
    public void FailedChannelStopsTheRemainingSharedTransportBatch()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink { FailVertical = true };

        Assert.IsFalse(controller.Apply(sink, MovingIntent(), active: true));

        Assert.AreEqual(0, sink.HorizontalCommands.Count);
        Assert.AreEqual(0, sink.TurnCommands.Count);
        Assert.IsInstanceOfType<InvalidOperationException>(controller.LastFailure);
    }

    [TestMethod]
    public void RunIsReleasedAndRetriedIndependently()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();
        Assert.IsTrue(controller.Apply(sink, MovingIntent() with { ShouldRun = true }, active: true));

        sink.FailStopRun = true;
        Assert.IsFalse(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.IsTrue(controller.HasPendingNeutral);

        sink.FailStopRun = false;
        Assert.IsTrue(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.AreEqual(2, sink.StopRunAttempts);
        Assert.IsFalse(controller.HasPendingNeutral);
    }

    [TestMethod]
    public void FullNeutralCleansUnknownInputLeftByAPreviousProcess()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();

        controller.RequestFullNeutral();
        Assert.IsTrue(controller.TryNeutralize(sink));

        Assert.AreEqual(1, sink.StopRunAttempts);
        Assert.AreEqual(0f, sink.VerticalCommands.Single());
        Assert.AreEqual(0f, sink.HorizontalCommands.Single());
        Assert.AreEqual(0f, sink.TurnCommands.Single());
        Assert.IsFalse(controller.HasPendingNeutral);
    }

    private static LeashIntent MovingIntent()
        => new(
            MoveX: 0.4f,
            MoveZ: -0.6f,
            ShouldRun: false,
            TurnValue: 0.2f,
            HasTurnInput: true,
            VerticalModeActive: false,
            VerticalTargetVelocity: 0f);

    private sealed class FakePlayerInputSink : IPlayerInputSink
    {
        public bool FailVertical { get; set; }
        public bool FailHorizontal { get; set; }
        public bool FailStopRun { get; set; }
        public int HorizontalAttempts { get; private set; }
        public int StopRunAttempts { get; private set; }
        public List<float> VerticalCommands { get; } = [];
        public List<float> HorizontalCommands { get; } = [];
        public List<float> TurnCommands { get; } = [];

        public void Run()
        {
        }

        public void StopRun()
        {
            StopRunAttempts++;
            if (FailStopRun)
                throw new InvalidOperationException("Injected StopRun failure.");
        }

        public void MoveVertical(float value)
        {
            if (FailVertical)
                throw new InvalidOperationException("Injected vertical failure.");
            VerticalCommands.Add(value);
        }

        public void MoveHorizontal(float value)
        {
            HorizontalAttempts++;
            if (FailHorizontal)
                throw new InvalidOperationException("Injected horizontal failure.");
            HorizontalCommands.Add(value);
        }

        public void LookHorizontal(float value) => TurnCommands.Add(value);
    }
}
