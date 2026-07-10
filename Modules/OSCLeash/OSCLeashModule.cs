using System.Diagnostics;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;

namespace CrookedToe.Modules.OSCLeash;

[ModuleTitle("OSC Leash")]
[ModuleDescription("Controls avatar movement from leash parameters, with optional OpenVR height drag")]
[ModuleType(ModuleType.Generic)]
[ModulePrefab("OSCLeash", "https://github.com/CrookedToe/OSCLeash/tree/main/Unity")]
[ModuleInfo("https://github.com/CrookedToe/CrookedToe-s-Modules")]
public class OSCLeashModule : Module
{
    private static readonly (OSCLeashSetting Key, string Name, string Description, float Default, float Min, float Max)[] SliderSettings =
    [
        (OSCLeashSetting.WalkDeadzone, "Move Start", "How far the leash must stretch before movement starts", 0.15f, 0f, 1f),
        (OSCLeashSetting.RunDeadzone, "Run Start", "How far the leash must stretch before running starts", 0.70f, 0f, 1f),
        (OSCLeashSetting.StrengthMultiplier, "Pull Strength", "How strongly the leash controls movement speed", 1.2f, 0.1f, 5f),
        (OSCLeashSetting.TurningMultiplier, "Turn Strength", "How strongly a side pull turns the avatar", 0.8f, 0.1f, 2f),
        (OSCLeashSetting.VerticalMovementMultiplier, "Height Speed", "Maximum height-drag speed in meters per second", 1f, 0.1f, 5f),
        (OSCLeashSetting.MaximumVerticalOffset, "Height Limit", "Maximum height distance from the position where the leash was grabbed", 3f, 0.25f, 20f)
    ];

    private static readonly (OSCLeashSetting Key, string Name, string Description, bool Default)[] ToggleSettings =
    [
        (OSCLeashSetting.TurningEnabled, "Allow Turning", "Allows side pulls to turn the avatar", false),
        (OSCLeashSetting.VerticalMovementEnabled, "Allow Height Drag", "Allows vertical pulls to move the OpenVR playspace", false),
        (OSCLeashSetting.GrabBasedGravity, "Return Height on Release", "Returns to the original grab height after the leash is released", false)
    ];

    private static readonly (OSCLeashParameter Key, string Address, string Name, string Description)[] FloatParameters =
    [
        (OSCLeashParameter.Stretch, "Leash_Stretch", "Leash Stretch", "How far the leash is stretched"),
        (OSCLeashParameter.ZPositive, "Leash_Z+", "Forward Pull", "Forward movement value"),
        (OSCLeashParameter.ZNegative, "Leash_Z-", "Backward Pull", "Backward movement value"),
        (OSCLeashParameter.XPositive, "Leash_X+", "Right Pull", "Rightward movement value"),
        (OSCLeashParameter.XNegative, "Leash_X-", "Left Pull", "Leftward movement value"),
        (OSCLeashParameter.YPositive, "Leash_Y+", "Upward Pull", "Upward movement value"),
        (OSCLeashParameter.YNegative, "Leash_Y-", "Downward Pull", "Downward movement value")
    ];

    private readonly LeashMotionEngine _motion = new();
    private readonly VerticalMotionState _verticalMotion = new();
    private readonly ExternalPoseRecoveryState _poseRecovery = new();
    private readonly OpenVrPoseCoordinator _openVr = new();

    private bool _isGrabbed;
    private bool _leashEnabled = true;
    private float _stretch;
    private float _xPositive;
    private float _xNegative;
    private float _yPositive;
    private float _yNegative;
    private float _zPositive;
    private float _zNegative;

    private bool _wasGrabbedForMotion;
    private bool _turnInputActive;
    private bool _heightNeedsCooldownAfterConnect;
    private bool _verticalSettingWasEnabled;
    private bool _isStopping;
    private long _lastUpdateTimestamp;
    private long _grabbedAtTimestamp;
    private long _lastVrWriteTimestamp;
    private long _lastExternalPoseObservationTimestamp;
    private long _nextVrRetryTimestamp;
    private long _lastVrWarningTimestamp;
    private PoseUpdateResult _lastLoggedVrResult = PoseUpdateResult.NoChange;
    private LeashSettings _settings;

    private float NetX => _xPositive - _xNegative;
    private float NetY => _yNegative - _yPositive;
    private float NetZ => _zPositive - _zNegative;

    protected override void OnPreLoad()
    {
        CreateSettings();
        RegisterParameters();
        CreateSettingsGroups();
    }

    protected override Task<bool> OnModuleStart()
    {
        _isStopping = false;
        Log("OSC Leash module started");
        return Task.FromResult(true);
    }

    protected override Task OnModuleStop()
    {
        _isStopping = true;
        ResetPlayerInput();
        PoseUpdateResult cleanupResult = RemoveOwnOffsetWithRetry();
        if (cleanupResult is not PoseUpdateResult.Success and not PoseUpdateResult.NoChange)
            Log($"OSC Leash height cleanup: {cleanupResult}");

        ClearState();
        Log("OSC Leash module stopped");
        return Task.CompletedTask;
    }

    private PoseUpdateResult RemoveOwnOffsetWithRetry()
    {
        PoseUpdateResult result = PoseUpdateResult.NoChange;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            result = _openVr.RemoveOwnOffset();
            if (result is not PoseUpdateResult.ReadFailed and not PoseUpdateResult.WriteFailed)
                break;
        }

        return result;
    }

    private void CreateSettings()
    {
        foreach (var (key, name, description, defaultValue, min, max) in SliderSettings)
            CreateSlider(key, name, description, defaultValue, min, max);

        foreach (var (key, name, description, defaultValue) in ToggleSettings)
            CreateToggle(key, name, description, defaultValue);

        CreateDropdown(
            OSCLeashSetting.LeashDirection,
            "Leash Forward",
            "Prefab forward axis used for turning; most prefabs use North (+Z)",
            LeashDirection.North);
    }

    private void RegisterParameters()
    {
        RegisterParameter<bool>(OSCLeashParameter.IsGrabbed, "Leash_IsGrabbed", ParameterMode.Read, "Leash Grabbed", "Whether the leash is being held");
        RegisterParameter<bool>(OSCLeashParameter.LeashEnable, "leash_enable", ParameterMode.Read, "Leash Enable", "Enables or disables leash motion");

        foreach (var (key, address, name, description) in FloatParameters)
            RegisterParameter<float>(key, address, ParameterMode.Read, name, description);
    }

    private void CreateSettingsGroups()
    {
        CreateGroup(
            "Movement",
            "When movement starts and how strongly it responds",
            OSCLeashSetting.WalkDeadzone,
            OSCLeashSetting.RunDeadzone,
            OSCLeashSetting.StrengthMultiplier);

        CreateGroup(
            "Turning",
            "Optional turning from side pulls",
            OSCLeashSetting.TurningEnabled,
            OSCLeashSetting.LeashDirection,
            OSCLeashSetting.TurningMultiplier);

        CreateGroup(
            "Height Drag",
            "Optional OpenVR playspace height control",
            OSCLeashSetting.VerticalMovementEnabled,
            OSCLeashSetting.VerticalMovementMultiplier,
            OSCLeashSetting.MaximumVerticalOffset,
            OSCLeashSetting.GrabBasedGravity);
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, LeashDefaults.UpdateIntervalMilliseconds)]
    private void UpdateMovement()
    {
        if (_isStopping)
            return;

        long now = Stopwatch.GetTimestamp();
        float deltaTime = GetDeltaTime(now);
        RefreshSettings();

        bool grabbedForMotion = _isGrabbed && _leashEnabled;
        bool justGrabbed = grabbedForMotion && !_wasGrabbedForMotion;
        bool justReleased = !grabbedForMotion && _wasGrabbedForMotion;
        _wasGrabbedForMotion = grabbedForMotion;
        if (justGrabbed && _settings.VerticalEnabled && !_openVr.Connected)
            _heightNeedsCooldownAfterConnect = true;

        LeashSignal signal = LeashSignal.From(NetX, NetY, NetZ, _stretch);
        LeashIntent intent = _motion.Resolve(signal, _settings, grabbedForMotion, deltaTime);

        MaintainOpenVrConnection(now);
        UpdateVerticalMotion(intent, grabbedForMotion, justGrabbed, justReleased, deltaTime, now);

        Player? player = GetClient().Player;
        if (player is not null)
            ApplyMovement(player, intent, grabbedForMotion);
    }

    private float GetDeltaTime(long now)
    {
        if (_lastUpdateTimestamp == 0)
        {
            _lastUpdateTimestamp = now;
            return LeashDefaults.BaseDeltaTimeSeconds;
        }

        float elapsed = (float)((now - _lastUpdateTimestamp) / (double)Stopwatch.Frequency);
        _lastUpdateTimestamp = now;
        return Math.Clamp(elapsed, LeashDefaults.BaseDeltaTimeSeconds, LeashDefaults.MaxDeltaTimeSeconds);
    }

    private void RefreshSettings()
    {
        _settings = new LeashSettings(
            WalkDeadzone: GetSettingValue<float>(OSCLeashSetting.WalkDeadzone),
            RunDeadzone: GetSettingValue<float>(OSCLeashSetting.RunDeadzone),
            StrengthMultiplier: GetSettingValue<float>(OSCLeashSetting.StrengthMultiplier),
            Direction: GetSettingValue<LeashDirection>(OSCLeashSetting.LeashDirection),
            TurningEnabled: GetSettingValue<bool>(OSCLeashSetting.TurningEnabled),
            TurningMultiplier: GetSettingValue<float>(OSCLeashSetting.TurningMultiplier),
            VerticalEnabled: GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled),
            ReturnHeightOnRelease: GetSettingValue<bool>(OSCLeashSetting.GrabBasedGravity),
            VerticalMultiplier: GetSettingValue<float>(OSCLeashSetting.VerticalMovementMultiplier),
            MaximumVerticalOffset: GetSettingValue<float>(OSCLeashSetting.MaximumVerticalOffset));
    }

    private void MaintainOpenVrConnection(long now)
    {
        if (!_settings.VerticalEnabled)
        {
            if (_verticalSettingWasEnabled || _openVr.OwnsPose)
            {
                PoseUpdateResult cleanupResult = _openVr.RemoveOwnOffset();
                HandleVrResult(cleanupResult, "disable height drag", now);
            }

            _verticalSettingWasEnabled = false;
            _poseRecovery.Reset();
            _heightNeedsCooldownAfterConnect = false;
            _verticalMotion.Reset();
            _openVr.Disconnect();
            return;
        }

        if (!_verticalSettingWasEnabled)
            _heightNeedsCooldownAfterConnect = true;
        _verticalSettingWasEnabled = true;
        if (_openVr.Connected || now < _nextVrRetryTimestamp)
            return;

        PoseUpdateResult result = GetOpenVRManager() is null
            ? PoseUpdateResult.OpenVrUnavailable
            : _openVr.TryConnect();

        if (result == PoseUpdateResult.Success)
        {
            LogDebug($"OpenVR height drag ready at reference height {_openVr.ReferenceHeight:F3}");
            if (_heightNeedsCooldownAfterConnect && _wasGrabbedForMotion)
            {
                _verticalMotion.Reset();
                _grabbedAtTimestamp = now;
            }
            _heightNeedsCooldownAfterConnect = false;
            _nextVrRetryTimestamp = 0;
            return;
        }

        if (result == PoseUpdateResult.ExternalWriterActive)
        {
            SuspendForExternalWriter(now);
            return;
        }

        _nextVrRetryTimestamp = AddSeconds(now, LeashDefaults.VrRetryIntervalSeconds);
        HandleVrResult(result, "connect", now);
    }

    private void UpdateVerticalMotion(
        LeashIntent intent,
        bool grabbedForMotion,
        bool justGrabbed,
        bool justReleased,
        float deltaTime,
        long now)
    {
        if (!_settings.VerticalEnabled || !_openVr.Connected)
            return;

        if (justGrabbed)
        {
            PoseUpdateResult refreshResult = _openVr.RefreshBaseline();
            HandleVrResult(refreshResult, "capture grab height", now);
            if (refreshResult != PoseUpdateResult.Success)
                return;

            _verticalMotion.Reset();
            _poseRecovery.Reset();
            _grabbedAtTimestamp = now;
            LogDebug($"Leash grabbed at OpenVR height {_openVr.ReferenceHeight:F3}");
        }

        if (justReleased)
        {
            _verticalMotion.Rebase(_openVr.LastAppliedOffset);
            _verticalMotion.Stop();
            _grabbedAtTimestamp = 0;
            LogDebug($"Leash released at height offset {_verticalMotion.Offset:F3}");
        }

        if (_poseRecovery.Suspended && !TryResumeAfterExternalPoseSettles(grabbedForMotion, now))
            return;

        bool changed;
        if (grabbedForMotion)
        {
            if (SecondsSince(_grabbedAtTimestamp, now) < LeashDefaults.VerticalCooldownSeconds)
                return;

            if (!intent.VerticalModeActive)
            {
                _verticalMotion.Rebase(_openVr.LastAppliedOffset);
                _verticalMotion.Stop();
                return;
            }

            changed = _verticalMotion.ApplyPull(
                intent.VerticalTargetVelocity,
                LeashDefaults.HeightSmoothing,
                deltaTime,
                _settings.MaximumVerticalOffset);
        }
        else if (_settings.ReturnHeightOnRelease)
        {
            changed = _verticalMotion.ReturnToOrigin(
                LeashDefaults.ReturnAcceleration,
                LeashDefaults.ReturnMaximumSpeed,
                deltaTime);
        }
        else
        {
            return;
        }

        if (!changed)
            return;

        bool finalReturn = _verticalMotion.Offset == 0f &&
                           MathF.Abs(_openVr.LastAppliedOffset) > LeashDefaults.NormalizeEpsilon;
        if (!finalReturn && !VrWriteIsDue(now))
            return;

        PoseUpdateResult result = _openVr.ApplyOffset(_verticalMotion.Offset);
        if (result == PoseUpdateResult.Success)
        {
            _lastVrWriteTimestamp = now;
            if (finalReturn)
                _openVr.ReleaseZeroOffsetOwnership();
            return;
        }

        if (result == PoseUpdateResult.ExternalWriterActive)
        {
            SuspendForExternalWriter(now);
            return;
        }

        _verticalMotion.Stop();
        _nextVrRetryTimestamp = AddSeconds(now, LeashDefaults.VrRetryIntervalSeconds);
        HandleVrResult(result, "write height preview", now);
    }

    private bool VrWriteIsDue(long now)
        => _lastVrWriteTimestamp == 0 || SecondsSince(_lastVrWriteTimestamp, now) >= LeashDefaults.VrWriteIntervalSeconds;

    private void SuspendForExternalWriter(long now)
    {
        _verticalMotion.Reset();
        bool willRetryAutomatically = _poseRecovery.Suspend(_wasGrabbedForMotion, TimestampSeconds(now));
        if (!willRetryAutomatically)
        {
            Log("Warning: another application repeatedly changed the OpenVR standing pose. " +
                "OSC Leash height drag is suspended until the leash is released and grabbed again.");
            return;
        }

        _lastExternalPoseObservationTimestamp = 0;
        Log("OpenVR standing pose changed externally. OSC Leash is waiting for it to settle before resuming height drag.");
    }

    private bool TryResumeAfterExternalPoseSettles(bool grabbedForMotion, long now)
    {
        if (!grabbedForMotion || _poseRecovery.LockedUntilRegrab)
            return false;
        if (_lastExternalPoseObservationTimestamp != 0 &&
            SecondsSince(_lastExternalPoseObservationTimestamp, now) < LeashDefaults.VrWriteIntervalSeconds)
            return false;

        _lastExternalPoseObservationTimestamp = now;
        PoseUpdateResult result = _openVr.ObserveExternalPose(out bool changed);
        if (result is not PoseUpdateResult.Success and not PoseUpdateResult.ExternalWriterActive)
        {
            HandleVrResult(result, "observe external pose", now);
            return false;
        }

        bool resumed = _poseRecovery.Observe(
            grabbedForMotion,
            changed,
            TimestampSeconds(now),
            LeashDefaults.ExternalPoseQuietSeconds);
        if (!resumed)
            return false;

        _verticalMotion.Reset();
        _lastVrWriteTimestamp = 0;
        Log("OpenVR standing pose is stable; OSC Leash height drag resumed from the new baseline.");
        return true;
    }

    private void ApplyMovement(Player player, LeashIntent intent, bool grabbedForMotion)
    {
        if (!grabbedForMotion)
        {
            player.StopRun();
            player.MoveVertical(0f);
            player.MoveHorizontal(0f);
            ReleaseTurnInput(player);
            return;
        }

        if (intent.ShouldRun)
            player.Run();
        else
            player.StopRun();

        player.MoveVertical(intent.MoveZ);
        player.MoveHorizontal(intent.MoveX);

        if (intent.HasTurnInput)
        {
            player.LookHorizontal(intent.TurnValue);
            _turnInputActive = true;
            return;
        }

        ReleaseTurnInput(player);
    }

    private void ReleaseTurnInput(Player player)
    {
        if (!_turnInputActive)
            return;

        player.LookHorizontal(0f);
        _turnInputActive = false;
    }

    private void ResetPlayerInput()
    {
        Player? player = GetClient().Player;
        if (player is null)
            return;

        try { player.StopRun(); } catch { }
        try { player.MoveVertical(0f); } catch { }
        try { player.MoveHorizontal(0f); } catch { }
        try { player.LookHorizontal(0f); } catch { }
        _turnInputActive = false;
    }

    private void HandleVrResult(PoseUpdateResult result, string operation, long now)
    {
        if (result is PoseUpdateResult.Success or PoseUpdateResult.NoChange or PoseUpdateResult.ExternalWriterActive)
            return;

        bool changed = result != _lastLoggedVrResult;
        if (!changed && SecondsSince(_lastVrWarningTimestamp, now) < 5f)
            return;

        Log($"Warning: OpenVR could not {operation}: {result}");
        _lastLoggedVrResult = result;
        _lastVrWarningTimestamp = now;
    }

    private void ClearState()
    {
        _isGrabbed = false;
        _leashEnabled = true;
        _stretch = 0f;
        _xPositive = _xNegative = _yPositive = _yNegative = _zPositive = _zNegative = 0f;
        _wasGrabbedForMotion = false;
        _turnInputActive = false;
        _poseRecovery.Reset();
        _heightNeedsCooldownAfterConnect = false;
        _verticalSettingWasEnabled = false;
        _lastUpdateTimestamp = 0;
        _grabbedAtTimestamp = 0;
        _lastVrWriteTimestamp = 0;
        _lastExternalPoseObservationTimestamp = 0;
        _nextVrRetryTimestamp = 0;
        _lastVrWarningTimestamp = 0;
        _lastLoggedVrResult = PoseUpdateResult.NoChange;
        _motion.Reset();
        _verticalMotion.Reset();
        _openVr.Clear();
    }

    private static long AddSeconds(long timestamp, float seconds)
        => timestamp + (long)(seconds * Stopwatch.Frequency);

    private static float SecondsSince(long earlier, long now)
        => earlier == 0 ? float.MaxValue : (float)((now - earlier) / (double)Stopwatch.Frequency);

    private static double TimestampSeconds(long timestamp)
        => timestamp / (double)Stopwatch.Frequency;

    protected override void OnRegisteredParameterReceived(RegisteredParameter parameter)
    {
        if (_isStopping)
            return;

        switch ((OSCLeashParameter)parameter.Lookup)
        {
            case OSCLeashParameter.IsGrabbed:
                _isGrabbed = parameter.GetValue<bool>();
                break;
            case OSCLeashParameter.LeashEnable:
                _leashEnabled = parameter.GetValue<bool>();
                break;
            case OSCLeashParameter.Stretch:
                _stretch = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.XPositive:
                _xPositive = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.XNegative:
                _xNegative = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.YPositive:
                _yPositive = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.YNegative:
                _yNegative = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.ZPositive:
                _zPositive = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.ZNegative:
                _zNegative = parameter.GetValue<float>();
                break;
        }
    }
}
