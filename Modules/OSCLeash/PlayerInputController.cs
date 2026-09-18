using System.Diagnostics;
using VRCOSC.App.SDK.VRChat;

namespace CrookedToe.Modules.OSCLeash;

internal interface IPlayerInputSink
{
    void Run();
    void StopRun();
    void MoveVertical(float value);
    void MoveHorizontal(float value);
    void LookHorizontal(float value);
}

internal sealed class VrcPlayerInputSink(Player player) : IPlayerInputSink
{
    public void Run() => player.Run();
    public void StopRun() => player.StopRun();
    public void MoveVertical(float value) => player.MoveVertical(value);
    public void MoveHorizontal(float value) => player.MoveHorizontal(value);
    public void LookHorizontal(float value) => player.LookHorizontal(value);
}

internal sealed class PlayerInputController
{
    internal const double SlowCommandThresholdMilliseconds = 50d;
    public Exception? LastFailure { get; private set; }
    public bool LastCommandWasSlow { get; private set; }
    public double LastCommandDurationMilliseconds { get; private set; }

    public bool Apply(IPlayerInputSink sink, LeashIntent intent, bool active, Func<bool>? canContinueMotion = null)
    {
        ResetAttemptState();
        bool succeeded = true;

        // OSC is unacknowledged UDP. Always publish a complete snapshot, including
        // zeroes, and attempt every channel even when an earlier send fails or stalls.
        // Local "ownership" cannot prove that VRChat received a previous packet.
        long publicationStarted = Stopwatch.GetTimestamp();
        bool startedActive = active;
        succeeded &= Try(ContinueMotion() && intent.ShouldRun ? sink.Run : sink.StopRun);
        succeeded &= Try(() => sink.MoveVertical(ContinueMotion() ? Sanitize(intent.MoveZ) : 0f));
        succeeded &= Try(() => sink.MoveHorizontal(ContinueMotion() ? Sanitize(intent.MoveX) : 0f));
        succeeded &= Try(() => sink.LookHorizontal(ContinueMotion() && intent.HasTurnInput ? Sanitize(intent.TurnValue) : 0f));

        // A release, transport gap, or slow send invalidates this captured intent.
        // Repair channels already sent too, without resetting failure/latency evidence.
        if (startedActive && !ContinueMotion())
        {
            succeeded &= Try(sink.StopRun);
            succeeded &= Try(() => sink.MoveVertical(0f));
            succeeded &= Try(() => sink.MoveHorizontal(0f));
            succeeded &= Try(() => sink.LookHorizontal(0f));
        }
        return succeeded;

        bool ContinueMotion()
        {
            active = active && !LastCommandWasSlow &&
                Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds < SlowCommandThresholdMilliseconds &&
                (canContinueMotion?.Invoke() ?? true);
            return active;
        }
    }

    public bool TryNeutralize(IPlayerInputSink sink) => Apply(sink, LeashIntent.Idle, active: false);

    private bool Try(Action command)
    {
        long started = Stopwatch.GetTimestamp();
        bool succeeded = true;
        try
        {
            command();
        }
        catch (Exception ex)
        {
            LastFailure ??= ex;
            succeeded = false;
        }

        double duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LastCommandDurationMilliseconds = Math.Max(LastCommandDurationMilliseconds, duration);
        if (duration >= SlowCommandThresholdMilliseconds)
            LastCommandWasSlow = true;
        return succeeded;
    }

    private static float Sanitize(float value)
        => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;

    private void ResetAttemptState()
    {
        LastFailure = null;
        LastCommandWasSlow = false;
        LastCommandDurationMilliseconds = 0d;
    }
}
