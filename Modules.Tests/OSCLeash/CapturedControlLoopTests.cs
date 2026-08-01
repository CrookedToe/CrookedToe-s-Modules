using System.Text.Json;
using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class CapturedControlLoopTests
{
    private const float Epsilon = 0.0001f;

    [TestMethod]
    public void CapturedSequencesRespectControlLoopSafetyInvariants()
    {
        TraceFixture fixture = LoadFixture();
        int replayedFrames = 0;
        int directReversals = 0;
        int zeroAxisFrames = 0;
        int overUnitInputFrames = 0;
        int verticalModeFrames = 0;

        foreach (TraceSequence sequence in fixture.Sequences)
        {
            var engine = new LeashMotionEngine();
            var verticalMotion = new VerticalMotionState();
            LeashSettings settings = CreateSettings(sequence);
            ReplayFrame? previous = null;

            foreach (float[] row in sequence.Frames)
            {
                ReplayFrame frame = ReplayFrame.From(row);
                for (int repeat = 0; repeat < frame.Repeat; repeat++)
                {
                    LeashSignal signal = LeashSignal.From(frame.NetX, frame.NetY, frame.NetZ, frame.Stretch);
                    LeashIntent intent = engine.Resolve(signal, settings, frame.Grabbed);
                    string context = $"{sequence.Name} ({sequence.Source}), replay frame {replayedFrames}";

                    Assert.IsTrue(float.IsFinite(intent.MoveX), $"Non-finite X output: {context}");
                    Assert.IsTrue(float.IsFinite(intent.MoveZ), $"Non-finite Z output: {context}");
                    Assert.IsTrue(float.IsFinite(intent.TurnValue), $"Non-finite turn output: {context}");
                    Assert.IsTrue(float.IsFinite(intent.VerticalTargetVelocity), $"Non-finite height output: {context}");

                    AssertAxisIsSafe(intent.MoveX, signal.NetX, signal.Stretch, settings, context, "X");
                    AssertAxisIsSafe(intent.MoveZ, signal.NetZ, signal.Stretch, settings, context, "Z");

                    float movementMagnitude = MathF.Sqrt((intent.MoveX * intent.MoveX) + (intent.MoveZ * intent.MoveZ));
                    Assert.IsTrue(movementMagnitude <= 1f + Epsilon, $"Combined movement exceeded 1: {context}");

                    if (!frame.Grabbed || signal.Stretch <= settings.WalkDeadzone)
                    {
                        Assert.AreEqual(0f, intent.MoveX, Epsilon, $"X did not stop: {context}");
                        Assert.AreEqual(0f, intent.MoveZ, Epsilon, $"Z did not stop: {context}");
                        Assert.IsFalse(intent.VerticalModeActive, $"Height mode ignored stretch/release: {context}");
                        Assert.AreEqual(0f, intent.VerticalTargetVelocity, Epsilon, $"Height did not stop: {context}");
                    }

                    if (frame.Grabbed)
                        Assert.AreEqual(
                            movementMagnitude > Epsilon && signal.Stretch > settings.RunDeadzone,
                            intent.ShouldRun,
                            $"Run threshold mismatch: {context}");
                    else
                        Assert.AreEqual(LeashIntent.Idle, intent, $"Released leash was not idle: {context}");

                    if (MathF.Abs(signal.NetX) <= Epsilon || MathF.Abs(signal.NetZ) <= Epsilon)
                        zeroAxisFrames++;

                    float requestedMagnitude = signal.HorizontalMagnitude * signal.Stretch * settings.StrengthMultiplier;
                    if (requestedMagnitude > 1f + Epsilon)
                        overUnitInputFrames++;

                    if (intent.VerticalModeActive)
                    {
                        verticalModeFrames++;
                        Assert.IsFalse(intent.HasTurnInput, $"Vertical pull also commanded turning: {context}");
                        Assert.IsTrue(intent.VerticalTargetVelocity * signal.NetY >= -Epsilon, $"Height direction mismatch: {context}");
                        verticalMotion.ApplyPull(
                            intent.VerticalTargetVelocity,
                            frame.DeltaSeconds,
                            settings.MaximumVerticalOffset);
                    }

                    Assert.IsTrue(
                        MathF.Abs(verticalMotion.Offset) <= settings.MaximumVerticalOffset + Epsilon,
                        $"Integrated height exceeded its limit: {context}");

                    if (!settings.TurningEnabled)
                        Assert.IsFalse(intent.HasTurnInput, $"Turning was disabled: {context}");

                    if (previous is not null && IsDirectReversal(previous.Value, frame, settings))
                    {
                        directReversals++;
                        if (AxisReversed(previous.Value.NetX, frame.NetX))
                            Assert.IsTrue(intent.MoveX * frame.NetX > 0f, $"X reversal was not applied immediately: {context}");
                        if (AxisReversed(previous.Value.NetZ, frame.NetZ))
                            Assert.IsTrue(intent.MoveZ * frame.NetZ > 0f, $"Z reversal was not applied immediately: {context}");
                    }

                    previous = frame;
                    replayedFrames++;
                }
            }
        }

        Assert.IsTrue(replayedFrames >= 240, "The fixture no longer contains the intended trace coverage.");
        Assert.IsTrue(directReversals > 0, "No direct reversal was replayed.");
        Assert.IsTrue(zeroAxisFrames > 0, "No axis-drop frame was replayed.");
        Assert.IsTrue(overUnitInputFrames > 0, "No diagonal over-unit input was replayed.");
        Assert.IsTrue(verticalModeFrames > 0, "No vertical-mode frame was replayed.");
    }

    private static void AssertAxisIsSafe(
        float output,
        float input,
        float stretch,
        LeashSettings settings,
        string context,
        string axis)
    {
        if (MathF.Abs(output) <= Epsilon)
            return;

        Assert.IsTrue(output * input > 0f, $"{axis} output opposed the current pull: {context}");
        float requestedAxis = MathF.Abs(input * stretch * settings.StrengthMultiplier);
        Assert.IsTrue(MathF.Abs(output) <= requestedAxis + Epsilon, $"{axis} output exceeded the current pull: {context}");
    }

    private static bool IsDirectReversal(ReplayFrame previous, ReplayFrame current, LeashSettings settings)
    {
        if (!previous.Grabbed || !current.Grabbed ||
            previous.Stretch <= settings.WalkDeadzone || current.Stretch <= settings.WalkDeadzone)
            return false;

        return AxisReversed(previous.NetX, current.NetX) || AxisReversed(previous.NetZ, current.NetZ);
    }

    private static bool AxisReversed(float previous, float current)
        => MathF.Abs(previous) > Epsilon && MathF.Abs(current) > Epsilon &&
           MathF.Sign(previous) != MathF.Sign(current);

    private static LeashSettings CreateSettings(TraceSequence sequence)
        => new(
            WalkDeadzone: 0.15f,
            RunDeadzone: 0.7f,
            StrengthMultiplier: 1.2f,
            Direction: LeashDirection.North,
            TurningEnabled: sequence.TurningEnabled,
            TurningMultiplier: 0.8f,
            VerticalEnabled: sequence.VerticalEnabled,
            ReturnHeightOnRelease: true,
            VerticalMultiplier: 1.5f,
            MaximumVerticalOffset: 3f);

    private static TraceFixture LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "OSCLeash", "Fixtures", "control-loop-traces.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TraceFixture>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Control-loop fixture could not be loaded.");
    }

    private sealed record TraceFixture(TraceSequence[] Sequences);

    private sealed record TraceSequence(
        string Name,
        string Source,
        bool TurningEnabled,
        bool VerticalEnabled,
        float[][] Frames);

    private readonly record struct ReplayFrame(
        int Repeat,
        bool Grabbed,
        float DeltaSeconds,
        float Stretch,
        float NetX,
        float NetY,
        float NetZ)
    {
        public static ReplayFrame From(float[] row)
        {
            Assert.AreEqual(7, row.Length, "Unexpected control-loop fixture row width.");
            return new ReplayFrame((int)row[0], row[1] != 0f, row[2], row[3], row[4], row[5], row[6]);
        }
    }
}
