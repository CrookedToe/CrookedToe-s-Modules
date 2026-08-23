using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class PlayerInputControllerTests
{
    [TestMethod]
    public void FailedActiveChannelDoesNotPreventTheOtherChannels()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink { FailHorizontal = true };

        bool applied = controller.Apply(sink, MovingIntent(), active: true);

        Assert.IsFalse(applied);
        Assert.AreEqual(1, sink.VerticalCommands.Count);
        Assert.AreEqual(1, sink.HorizontalAttempts);
        Assert.AreEqual(1, sink.TurnCommands.Count);
    }

    [TestMethod]
    public void CompleteNeutralCanBeRepeatedUntilVrChatReceivesIt()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();
        Assert.IsTrue(controller.Apply(sink, MovingIntent(), active: true));

        sink.FailVertical = true;
        Assert.IsFalse(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.AreEqual(0f, sink.HorizontalCommands[^1]);
        Assert.AreEqual(0f, sink.TurnCommands[^1]);

        sink.FailVertical = false;
        Assert.IsTrue(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.AreEqual(0f, sink.VerticalCommands[^1]);
        Assert.AreEqual(3, sink.StopRunAttempts);
    }

    [TestMethod]
    public void FailedChannelNeverStopsTheRemainingStatePublication()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink { FailVertical = true };

        Assert.IsFalse(controller.Apply(sink, MovingIntent(), active: true));

        Assert.AreEqual(1, sink.HorizontalCommands.Count);
        Assert.AreEqual(1, sink.TurnCommands.Count);
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
        Assert.AreEqual(0f, sink.VerticalCommands[^1]);
        Assert.AreEqual(0f, sink.HorizontalCommands[^1]);

        sink.FailStopRun = false;
        Assert.IsTrue(controller.Apply(sink, LeashIntent.Idle, active: false));
        Assert.AreEqual(2, sink.StopRunAttempts);
    }

    [TestMethod]
    public void FullNeutralCleansUnknownInputLeftByAPreviousProcess()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();

        Assert.IsTrue(controller.TryNeutralize(sink));

        Assert.AreEqual(1, sink.StopRunAttempts);
        Assert.AreEqual(0f, sink.VerticalCommands.Single());
        Assert.AreEqual(0f, sink.HorizontalCommands.Single());
        Assert.AreEqual(0f, sink.TurnCommands.Single());
    }

    [TestMethod]
    public void HealthyActiveStateIsSentRedundantlyForPacketLossRepair()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();

        Assert.IsTrue(controller.Apply(sink, MovingIntent(), active: true));
        Assert.IsTrue(controller.Apply(sink, MovingIntent(), active: true));

        Assert.AreEqual(2, sink.VerticalCommands.Count);
        Assert.AreEqual(2, sink.HorizontalCommands.Count);
        Assert.AreEqual(2, sink.TurnCommands.Count);
    }

    [TestMethod]
    public void NeutralStateIsAlsoSentRedundantlyForPacketLossRepair()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink();

        Assert.IsTrue(controller.TryNeutralize(sink));
        Assert.IsTrue(controller.TryNeutralize(sink));

        CollectionAssert.AreEqual(new[] { 0f, 0f }, sink.VerticalCommands);
        CollectionAssert.AreEqual(new[] { 0f, 0f }, sink.HorizontalCommands);
        CollectionAssert.AreEqual(new[] { 0f, 0f }, sink.TurnCommands);
        Assert.AreEqual(2, sink.StopRunAttempts);
    }

    [TestMethod]
    public void SlowCommandDoesNotCreateAPartialRemoteState()
    {
        var controller = new PlayerInputController();
        var sink = new FakePlayerInputSink { SlowVerticalMilliseconds = 60 };

        Assert.IsTrue(controller.Apply(sink, MovingIntent(), active: true));

        Assert.IsTrue(controller.LastCommandWasSlow);
        Assert.IsTrue(controller.LastCommandDurationMilliseconds >= PlayerInputController.SlowCommandThresholdMilliseconds);
        Assert.AreEqual(1, sink.HorizontalCommands.Count);
        Assert.AreEqual(1, sink.TurnCommands.Count);
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
        public int SlowVerticalMilliseconds { get; set; }
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
            if (SlowVerticalMilliseconds > 0)
                Thread.Sleep(SlowVerticalMilliseconds);
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
