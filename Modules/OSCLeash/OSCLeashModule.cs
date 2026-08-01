using System.Diagnostics;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;

namespace CrookedToe.Modules.OSCLeash;

[ModuleTitle("OSC Leash")]
[ModuleDescription("Controls avatar movement from leash parameters, with optional OpenVR height drag")]
[ModuleType(ModuleType.Generic)]
[ModulePrefab("OSCLeash", "https://github.com/CrookedToe/CrookedToe-s-Modules/releases/latest")]
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

    private readonly LeashInputState _input = new();
    private readonly LeashMotionEngine _motion = new();
    private readonly PlayerInputController _playerInput = new();
    private readonly VerticalMotionState _verticalMotion = new();
    private readonly ExternalPoseRecoveryState _poseRecovery = new();
    private readonly OpenVrPoseCoordinator _openVr = new();

    private bool _wasGrabbedForMotion;
    private bool _isStopping;
    private long _lastUpdateTimestamp;
    private long _nextVrRetryTimestamp;
    private long _nextPlayerRetryTimestamp;
    private long _lastVrWarningTimestamp;
    private long _lastPlayerWarningTimestamp;
    private long _lastPlayerSuccessTimestamp;
    private long _lastHealthLogTimestamp;
    private int _consecutivePlayerFailures;
    private bool _inputWasStale;
    private PoseUpdateResult _lastLoggedVrResult = PoseUpdateResult.NoChange;
    private LeashSettings _settings;

    protected override void OnPreLoad()
    {
        CreateSettings();
        RegisterParameters();
        CreateSettingsGroups();
    }

    protected override Task<bool> OnModuleStart()
    {
        _isStopping = false;
        ResetLeashState();
        TryNeutralizePlayerInput(forceAll: true);
        Log("OSC Leash module started");
        return Task.FromResult(true);
    }

    protected override Task OnModuleStop()
    {
        _isStopping = true;
        TryNeutralizePlayerInput(forceAll: true);
        PoseUpdateResult cleanupResult = _openVr.RemoveOwnOffset();
        if (cleanupResult is not PoseUpdateResult.Success and not PoseUpdateResult.NoChange)
            Log($"OSC Leash height cleanup: {cleanupResult}");

        ClearState(clearOpenVrOwnership: cleanupResult is PoseUpdateResult.Success or PoseUpdateResult.NoChange);
        Log("OSC Leash module stopped");
        return Task.CompletedTask;
    }

    protected override void OnAvatarChange(Avatar? avatar)
    {
        ResetLeashState();
        TryNeutralizePlayerInput(forceAll: true);

        PoseUpdateResult cleanupResult = _openVr.RemoveOwnOffset();
        HandleVrResult(cleanupResult, "clean up after avatar change", Stopwatch.GetTimestamp());
        if (cleanupResult is PoseUpdateResult.Success or PoseUpdateResult.NoChange)
            _openVr.Clear();
        else
            _openVr.Disconnect();
    }

    protected override void OnPlayerUpdate()
    {
        if (GetClient().Player is not null)
            return;

        ResetLeashState();
        _playerInput.RequestFullNeutral();
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
        ObserveInputFreshness(now);

        bool grabbedForMotion = _input.GrabbedForMotion;
        bool justGrabbed = grabbedForMotion && !_wasGrabbedForMotion;
        bool justReleased = !grabbedForMotion && _wasGrabbedForMotion;
        _wasGrabbedForMotion = grabbedForMotion;

        LeashIntent intent = _motion.Resolve(_input.Signal, _settings, grabbedForMotion);

        MaintainOpenVrConnection(now);
        UpdateVerticalMotion(intent, grabbedForMotion, justGrabbed, justReleased, deltaTime, now);

        UpdatePlayerMovement(intent, grabbedForMotion, now);

        LogHealthIfDue(now);
    }

    private float GetDeltaTime(long now)
    {
        if (_lastUpdateTimestamp == 0)
        {
            _lastUpdateTimestamp = now;
            return LeashDefaults.UpdateIntervalSeconds;
        }

        float elapsed = (float)((now - _lastUpdateTimestamp) / (double)Stopwatch.Frequency);
        _lastUpdateTimestamp = now;
        return Math.Clamp(elapsed, 0f, LeashDefaults.MaxDeltaTimeSeconds);
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
            MaximumVerticalOffset: GetSettingValue<float>(OSCLeashSetting.MaximumVerticalOffset))
            .Sanitize();
    }

    private void MaintainOpenVrConnection(long now)
    {
        if (!_settings.VerticalEnabled)
        {
            if (_openVr.OwnsPose)
            {
                PoseUpdateResult cleanupResult = _openVr.RemoveOwnOffset();
                HandleVrResult(cleanupResult, "disable height drag", now);
            }

            _poseRecovery.Reset();
            _verticalMotion.Reset();
            _nextVrRetryTimestamp = 0;
            _openVr.Disconnect();
            return;
        }

        if (_openVr.Connected || now < _nextVrRetryTimestamp)
            return;

        PoseUpdateResult result = GetOpenVRManager() is null
            ? PoseUpdateResult.OpenVrUnavailable
            : _openVr.TryConnect();

        if (result == PoseUpdateResult.Success)
        {
            _verticalMotion.Rebase(_openVr.LastAppliedOffset);
            LogDebug($"OpenVR height drag ready at reference height {_openVr.ReferenceHeight:F3}");
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
            LogDebug($"Leash grabbed at OpenVR height {_openVr.ReferenceHeight:F3}");
        }

        if (justReleased)
        {
            _verticalMotion.Rebase(_openVr.LastAppliedOffset);
            LogDebug($"Leash released at height offset {_verticalMotion.Offset:F3}");
        }

        if (_poseRecovery.Suspended && !TryResumeAfterExternalPoseSettles(grabbedForMotion, now))
            return;

        bool changed = _verticalMotion.Constrain(_settings.MaximumVerticalOffset);
        if (grabbedForMotion)
        {
            if (!intent.VerticalModeActive)
            {
                _verticalMotion.Rebase(_openVr.LastAppliedOffset);
                changed = _verticalMotion.Constrain(_settings.MaximumVerticalOffset);
                if (!changed)
                    return;
            }
            else
            {
                changed |= _verticalMotion.ApplyPull(
                    intent.VerticalTargetVelocity,
                    deltaTime,
                    _settings.MaximumVerticalOffset);
            }
        }
        else if (_settings.ReturnHeightOnRelease)
        {
            changed |= _verticalMotion.ReturnToOrigin(
                LeashDefaults.ReturnAcceleration,
                _settings.VerticalMultiplier,
                deltaTime);
        }
        else if (!changed)
        {
            return;
        }

        if (!changed)
            return;

        bool finalReturn = _verticalMotion.Offset == 0f &&
                           MathF.Abs(_openVr.LastAppliedOffset) > LeashDefaults.NormalizeEpsilon;

        PoseUpdateResult result = _openVr.ApplyOffset(_verticalMotion.Offset);
        if (result == PoseUpdateResult.Success)
        {
            if (finalReturn)
                _openVr.ReleaseZeroOffsetOwnership();
            return;
        }

        if (result == PoseUpdateResult.ExternalWriterActive)
        {
            SuspendForExternalWriter(now);
            return;
        }

        _verticalMotion.Rebase(_openVr.LastAppliedOffset);
        _nextVrRetryTimestamp = AddSeconds(now, LeashDefaults.VrRetryIntervalSeconds);
        HandleVrResult(result, "write height preview", now);
    }

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

        Log("OpenVR standing pose changed externally. OSC Leash is waiting for it to settle before resuming height drag.");
    }

    private bool TryResumeAfterExternalPoseSettles(bool grabbedForMotion, long now)
    {
        if (!grabbedForMotion || _poseRecovery.LockedUntilRegrab)
            return false;

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
        Log("OpenVR standing pose is stable; OSC Leash height drag resumed from the new baseline.");
        return true;
    }

    private void ObserveInputFreshness(long now)
    {
        bool expired = _input.ExpireIfStale(
            now,
            (long)(LeashDefaults.InputFreshnessSeconds * Stopwatch.Frequency));
        if (expired)
        {
            _motion.Reset();
            _playerInput.RequestFullNeutral();
            if (!_inputWasStale)
            {
                Log($"OSC Leash input stopped for more than {LeashDefaults.InputFreshnessSeconds:F0}s. " +
                    "Movement was neutralized; release and re-grab the leash before continuing.");
            }

            _inputWasStale = true;
            return;
        }

        if (!_inputWasStale || !_input.HasReceivedInput || _input.RequiresGrabRelease)
            return;

        _inputWasStale = false;
        Log("OSC Leash input recovered.");
    }

    private void UpdatePlayerMovement(LeashIntent intent, bool grabbedForMotion, long now)
    {
        if (now < _nextPlayerRetryTimestamp)
            return;

        Player? player;
        try
        {
            player = GetClient().Player;
        }
        catch (Exception ex)
        {
            HandlePlayerInputResult(success: false, ex, now, scheduleRetry: true);
            return;
        }

        if (player is null)
            return;

        bool success = _playerInput.Apply(new VrcPlayerInputSink(player), intent, grabbedForMotion);
        HandlePlayerInputResult(success, _playerInput.LastFailure, now, scheduleRetry: true);
    }

    private void TryNeutralizePlayerInput(bool forceAll = false)
    {
        if (forceAll)
            _playerInput.RequestFullNeutral();

        Player? player;
        try
        {
            player = GetClient().Player;
        }
        catch (Exception ex)
        {
            HandlePlayerInputResult(success: false, ex, Stopwatch.GetTimestamp(), scheduleRetry: false);
            return;
        }

        if (player is null)
            return;

        bool success = _playerInput.TryNeutralize(new VrcPlayerInputSink(player));
        HandlePlayerInputResult(
            success,
            _playerInput.LastFailure,
            Stopwatch.GetTimestamp(),
            scheduleRetry: false);
    }

    private void HandlePlayerInputResult(
        bool success,
        Exception? failure,
        long now,
        bool scheduleRetry)
    {
        if (success)
        {
            _lastPlayerSuccessTimestamp = now;
            _nextPlayerRetryTimestamp = 0;
            if (_consecutivePlayerFailures > 0)
                Log($"OSC Leash player input recovered after {_consecutivePlayerFailures} failed batches.");
            _consecutivePlayerFailures = 0;
            return;
        }

        _consecutivePlayerFailures++;
        if (scheduleRetry)
        {
            int exponent = Math.Min(_consecutivePlayerFailures - 1, 5);
            float delaySeconds = Math.Min(
                LeashDefaults.PlayerRetryInitialSeconds * (1 << exponent),
                LeashDefaults.PlayerRetryMaximumSeconds);
            _nextPlayerRetryTimestamp = AddSeconds(now, delaySeconds);
        }

        if (SecondsSince(_lastPlayerWarningTimestamp, now) < 5f)
            return;

        string detail = failure is null ? string.Empty : $" {failure.GetType().Name}: {failure.Message}";
        Log($"Warning: VRChat rejected an OSC Leash player-input batch " +
            $"({_consecutivePlayerFailures} consecutive). Remaining commands were skipped and neutral cleanup remains pending.{detail}");
        _lastPlayerWarningTimestamp = now;
    }

    private void LogHealthIfDue(long now)
    {
        if (SecondsSince(_lastHealthLogTimestamp, now) < LeashDefaults.HealthLogIntervalSeconds)
            return;

        string inputAge = _input.HasReceivedInput
            ? $"{_input.InputAgeSeconds(now):F1}s"
            : "none";
        string playerAge = _lastPlayerSuccessTimestamp == 0
            ? "none"
            : $"{SecondsSince(_lastPlayerSuccessTimestamp, now):F1}s";
        LogDebug(
            $"OSC Leash health: inputAge={inputAge}, stale={_inputWasStale}, " +
            $"releaseRequired={_input.RequiresGrabRelease}, playerSuccessAge={playerAge}, " +
            $"playerFailures={_consecutivePlayerFailures}, neutralPending={_playerInput.HasPendingNeutral}, " +
            $"openVrConnected={_openVr.Connected}");
        _lastHealthLogTimestamp = now;
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

    private void ResetLeashState()
    {
        _input.Reset();
        _motion.Reset();
        _wasGrabbedForMotion = false;
        _poseRecovery.Reset();
        _lastUpdateTimestamp = 0;
        _nextVrRetryTimestamp = 0;
        _nextPlayerRetryTimestamp = 0;
        _verticalMotion.Reset();
        _inputWasStale = false;
    }

    private void ClearState(bool clearOpenVrOwnership)
    {
        ResetLeashState();
        _lastVrWarningTimestamp = 0;
        _lastPlayerWarningTimestamp = 0;
        _lastPlayerSuccessTimestamp = 0;
        _lastHealthLogTimestamp = 0;
        _consecutivePlayerFailures = 0;
        _lastLoggedVrResult = PoseUpdateResult.NoChange;
        if (clearOpenVrOwnership)
            _openVr.Clear();
        else
            _openVr.Disconnect();
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

        OSCLeashParameter key = (OSCLeashParameter)parameter.Lookup;
        switch (key)
        {
            case OSCLeashParameter.IsGrabbed:
            case OSCLeashParameter.LeashEnable:
                _input.Set(key, parameter.GetValue<bool>());
                break;
            default:
                _input.Set(key, parameter.GetValue<float>());
                break;
        }
    }
}
