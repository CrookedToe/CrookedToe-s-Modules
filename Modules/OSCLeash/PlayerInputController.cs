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
    private bool _runOwned;
    private bool _verticalOwned;
    private bool _horizontalOwned;
    private bool _turnOwned;

    public bool HasPendingNeutral => _runOwned || _verticalOwned || _horizontalOwned || _turnOwned;
    public Exception? LastFailure { get; private set; }

    public void RequestFullNeutral()
    {
        _runOwned = true;
        _verticalOwned = true;
        _horizontalOwned = true;
        _turnOwned = true;
    }

    public bool Apply(IPlayerInputSink sink, LeashIntent intent, bool active)
    {
        LastFailure = null;
        return active ? ApplyActive(sink, intent) : TryNeutralizeCore(sink);
    }

    public bool TryNeutralize(IPlayerInputSink sink)
    {
        LastFailure = null;
        return TryNeutralizeCore(sink);
    }

    private bool ApplyActive(IPlayerInputSink sink, LeashIntent intent)
    {
        if (!ApplyButton(sink, intent.ShouldRun))
            return false;
        if (!ApplyAxis(ref _verticalOwned, intent.MoveZ, sink.MoveVertical))
            return false;
        if (!ApplyAxis(ref _horizontalOwned, intent.MoveX, sink.MoveHorizontal))
            return false;
        return ApplyAxis(
            ref _turnOwned,
            intent.HasTurnInput ? intent.TurnValue : 0f,
            sink.LookHorizontal);
    }

    private bool TryNeutralizeCore(IPlayerInputSink sink)
    {
        if (!TryRelease(ref _runOwned, sink.StopRun))
            return false;
        if (!TryRelease(ref _verticalOwned, () => sink.MoveVertical(0f)))
            return false;
        if (!TryRelease(ref _horizontalOwned, () => sink.MoveHorizontal(0f)))
            return false;
        return TryRelease(ref _turnOwned, () => sink.LookHorizontal(0f));
    }

    private bool ApplyButton(IPlayerInputSink sink, bool pressed)
    {
        if (!pressed)
            return TryRelease(ref _runOwned, sink.StopRun);

        _runOwned = true;
        return Try(sink.Run);
    }

    private bool ApplyAxis(ref bool owned, float value, Action<float> command)
    {
        value = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
        owned = true;
        if (!Try(() => command(value)))
            return false;

        if (MathF.Abs(value) <= LeashDefaults.NormalizeEpsilon)
            owned = false;
        return true;
    }

    private bool TryRelease(ref bool owned, Action command)
    {
        if (!owned)
            return true;
        if (!Try(command))
            return false;

        owned = false;
        return true;
    }

    private bool Try(Action command)
    {
        try
        {
            command();
            return true;
        }
        catch (Exception ex)
        {
            LastFailure ??= ex;
            return false;
        }
    }
}
