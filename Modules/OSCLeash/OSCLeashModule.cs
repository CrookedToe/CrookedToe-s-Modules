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
        (OSCLeashSetting.WalkDeadzone, "Walk Deadzone", "Minimum leash stretch required for movement", 0.15f, 0f, 1f),
        (OSCLeashSetting.RunDeadzone, "Run Deadzone", "Leash stretch required for running", 0.70f, 0f, 1f),
        (OSCLeashSetting.StrengthMultiplier, "Movement Sensitivity", "How strongly leash pull maps to VRChat movement", 1.2f, 0.1f, 5f),
        (OSCLeashSetting.UpDownDeadzone, "Vertical Compensation Deadzone", "Vertical pull required before horizontal movement is reduced", 0.5f, 0f, 1f),
        (OSCLeashSetting.UpDownCompensation, "Vertical Compensation", "How much vertical pull reduces horizontal movement", 0.5f, 0f, 1f),
        (OSCLeashSetting.MovementSmoothing, "Movement Smoothing", "Horizontal input smoothing", 0.7f, 0f, 0.95f),
        (OSCLeashSetting.TurningMultiplier, "Turn Sensitivity", "How strongly side pull maps to VRChat turning", 0.8f, 0.1f, 2f),
        (OSCLeashSetting.TurningDeadzone, "Turn Deadzone", "Minimum leash stretch required for turning", 0.15f, 0f, 1f),
        (OSCLeashSetting.TurningGoal, "Minimum Turn Angle", "Minimum angle away from forward before turning starts", 20f, 0f, 90f),
        (OSCLeashSetting.TurningVerticalAngleLimit, "Turn Vertical Limit", "Maximum vertical pull angle that still permits turning", 45f, 0f, 90f),
        (OSCLeashSetting.VerticalMovementMultiplier, "Height Sensitivity", "Maximum height-drag speed in meters per second", 1f, 0.1f, 5f),
        (OSCLeashSetting.VerticalMovementDeadzone, "Height Deadzone", "Minimum vertical pull required for height drag", 0.15f, 0f, 1f),
        (OSCLeashSetting.VerticalMovementSmoothing, "Height Smoothing", "Height-drag velocity smoothing", 0.8f, 0f, 0.99f),
        (OSCLeashSetting.VerticalHorizontalCompensation, "Height Pull Angle", "Minimum vertical angle required for height drag", 45f, 15f, 75f),
        (OSCLeashSetting.GravityStrength, "Return Acceleration", "Acceleration used when returning to the grab height", 9.81f, 0.1f, 50f),
        (OSCLeashSetting.TerminalVelocity, "Return Terminal Speed", "Maximum return speed", 15f, 1f, 50f),
        (OSCLeashSetting.MaximumVerticalOffset, "Maximum Height Distance", "Maximum distance height drag may move from the grab height", 3f, 0.25f, 20f)
    ];

    private static readonly (OSCLeashSetting Key, string Name, string Description, bool Default)[] ToggleSettings =
    [
        (OSCLeashSetting.TurningEnabled, "Enable Turning", "Enables avatar rotation control", false),
        (OSCLeashSetting.VerticalMovementEnabled, "Enable Height Drag", "Enables OpenVR playspace height control", false),
        (OSCLeashSetting.GrabBasedGravity, "Return Height On Release", "Returns to the grab height when the leash is released", false),
        (OSCLeashSetting.DebugTraceEnabled, "Record Debug Trace", "Records a sampled JSONL trace for troubleshooting", false)
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
    private readonly LeashTraceRecorder _trace = new();

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
        _trace.Dispose();

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

        CreateDropdown(OSCLeashSetting.LeashDirection, "Leash Direction", "Direction the leash faces", LeashDirection.North);
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
            "Core movement behavior",
            OSCLeashSetting.LeashDirection,
            OSCLeashSetting.WalkDeadzone,
            OSCLeashSetting.RunDeadzone,
            OSCLeashSetting.StrengthMultiplier,
            OSCLeashSetting.UpDownDeadzone,
            OSCLeashSetting.UpDownCompensation,
            OSCLeashSetting.MovementSmoothing);

        CreateGroup(
            "Turning",
            "Avatar rotation behavior",
            OSCLeashSetting.TurningEnabled,
            OSCLeashSetting.TurningMultiplier,
            OSCLeashSetting.TurningDeadzone,
            OSCLeashSetting.TurningGoal,
            OSCLeashSetting.TurningVerticalAngleLimit);

        CreateGroup(
            "Height Drag",
            "OpenVR height behavior",
            OSCLeashSetting.VerticalMovementEnabled,
            OSCLeashSetting.VerticalMovementMultiplier,
            OSCLeashSetting.VerticalMovementDeadzone,
            OSCLeashSetting.VerticalMovementSmoothing,
            OSCLeashSetting.VerticalHorizontalCompensation,
            OSCLeashSetting.MaximumVerticalOffset,
            OSCLeashSetting.GrabBasedGravity,
            OSCLeashSetting.GravityStrength,
            OSCLeashSetting.TerminalVelocity);

        CreateGroup("Debug", "Troubleshooting", OSCLeashSetting.DebugTraceEnabled);
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, LeashDefaults.UpdateIntervalMilliseconds)]
    private void UpdateMovement()
    {
        if (_isStopping)
            return;

        long now = Stopwatch.GetTimestamp();
        float deltaTime = GetDeltaTime(now);
        RefreshSettings();
        _trace.SetEnabled(_settings.DebugTraceEnabled, Log);

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
        RecordTrace(signal, intent, grabbedForMotion);
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
            GetSettingValue<float>(OSCLeashSetting.WalkDeadzone),
            GetSettingValue<float>(OSCLeashSetting.RunDeadzone),
            GetSettingValue<float>(OSCLeashSetting.StrengthMultiplier),
            GetSettingValue<float>(OSCLeashSetting.UpDownDeadzone),
            GetSettingValue<float>(OSCLeashSetting.UpDownCompensation),
            GetSettingValue<float>(OSCLeashSetting.MovementSmoothing),
            GetSettingValue<LeashDirection>(OSCLeashSetting.LeashDirection),
            GetSettingValue<bool>(OSCLeashSetting.TurningEnabled),
            GetSettingValue<float>(OSCLeashSetting.TurningMultiplier),
            GetSettingValue<float>(OSCLeashSetting.TurningDeadzone),
            GetSettingValue<float>(OSCLeashSetting.TurningGoal),
            GetSettingValue<float>(OSCLeashSetting.TurningVerticalAngleLimit),
            GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled),
            GetSettingValue<bool>(OSCLeashSetting.GrabBasedGravity),
            GetSettingValue<float>(OSCLeashSetting.VerticalMovementMultiplier),
            GetSettingValue<float>(OSCLeashSetting.VerticalMovementDeadzone),
            GetSettingValue<float>(OSCLeashSetting.VerticalMovementSmoothing),
            GetSettingValue<float>(OSCLeashSetting.VerticalHorizontalCompensation),
            GetSettingValue<float>(OSCLeashSetting.GravityStrength),
            GetSettingValue<float>(OSCLeashSetting.TerminalVelocity),
            GetSettingValue<float>(OSCLeashSetting.MaximumVerticalOffset),
            GetSettingValue<bool>(OSCLeashSetting.DebugTraceEnabled));
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
                _settings.VerticalSmoothing,
                deltaTime,
                _settings.MaximumVerticalOffset);
        }
        else if (_settings.ReturnHeightOnRelease)
        {
            changed = _verticalMotion.ReturnToOrigin(
                _settings.GravityStrength,
                _settings.TerminalVelocity,
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

    private void RecordTrace(LeashSignal signal, LeashIntent intent, bool grabbedForMotion)
    {
        if (!_settings.DebugTraceEnabled)
            return;

        bool returningHeight = !grabbedForMotion &&
                               _settings.ReturnHeightOnRelease &&
                               (MathF.Abs(_verticalMotion.Offset) > LeashDefaults.NormalizeEpsilon ||
                                MathF.Abs(_verticalMotion.Velocity) > LeashDefaults.NormalizeEpsilon);
        if (!_trace.ShouldSample(grabbedForMotion || returningHeight))
            return;

        _trace.Record(new
        {
            TimestampUtc = DateTime.UtcNow,
            Settings = _settings,
            Input = new
            {
                Grabbed = _isGrabbed,
                Enabled = _leashEnabled,
                GrabbedForMotion = grabbedForMotion,
                Stretch = _stretch,
                XPositive = _xPositive,
                XNegative = _xNegative,
                YPositive = _yPositive,
                YNegative = _yNegative,
                ZPositive = _zPositive,
                ZNegative = _zNegative
            },
            Signal = signal,
            Intent = intent,
            Vertical = new
            {
                _verticalMotion.Offset,
                _verticalMotion.Velocity,
                SuspendedForExternalWriter = _poseRecovery.Suspended,
                LockedUntilRegrab = _poseRecovery.LockedUntilRegrab,
                _poseRecovery.AutomaticResumeAttempts
            },
            OpenVr = _openVr.Snapshot()
        }, Log);
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
