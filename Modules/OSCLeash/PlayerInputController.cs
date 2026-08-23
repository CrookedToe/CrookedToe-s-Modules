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

    public bool Apply(IPlayerInputSink sink, LeashIntent intent, bool active)
    {
        ResetAttemptState();
        bool succeeded = true;

        // OSC is unacknowledged UDP. Always publish a complete snapshot, including
        // zeroes, and attempt every channel even when an earlier send fails or stalls.
        // Local "ownership" cannot prove that VRChat received a previous packet.
        succeeded &= Try(active && intent.ShouldRun ? sink.Run : sink.StopRun);
        succeeded &= Try(() => sink.MoveVertical(active ? Sanitize(intent.MoveZ) : 0f));
        succeeded &= Try(() => sink.MoveHorizontal(active ? Sanitize(intent.MoveX) : 0f));
        succeeded &= Try(() => sink.LookHorizontal(active && intent.HasTurnInput ? Sanitize(intent.TurnValue) : 0f));
        return succeeded;
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
