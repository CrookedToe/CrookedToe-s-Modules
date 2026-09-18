using System.Diagnostics;
using CrookedToe.Modules.Compatibility;
using CrookedToe.Modules.Diagnostics;
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

    private ModuleDiagnostics? _diagnostics;
    private DiagnosticProbe? _updateProbe;
    private DiagnosticProbe? _settingsProbe;
    private DiagnosticProbe? _openVrProbe;
    private DiagnosticProbe? _playerInputProbe;
    private DiagnosticProbe? _oscInputProbe;

    private bool _wasLeashEngaged;
    private bool _isStopping;
    private long _lastUpdateTimestamp;
    private long _nextVrRetryTimestamp;
    private long _nextPlayerPublishTimestamp;
    private long _neutralRepairUntilTimestamp;
    private long _lastVrWarningTimestamp;
    private long _lastPlayerWarningTimestamp;
    private long _lastPlayerSuccessTimestamp;
    private long _lastHealthLogTimestamp;
    private int _consecutivePlayerFailures;
    private bool _inputWasStale;
    private bool _motionInputWasSilent;
    private bool? _lastObservedGrabbed;
    private bool? _lastObservedEnable;
    private bool? _lastObservedDisable;
    private long _playerPublicationsSinceHealth;
    private long _neutralPublicationsSinceHealth;
    private PoseUpdateResult _lastLoggedVrResult = PoseUpdateResult.NoChange;
    private LeashSettings _settings;

    private new void Log(string message) => RealtimeModuleLog.Write(this, message);
    private new void LogDebug(string message) => RealtimeModuleLog.Write(this, message, debug: true);

    protected override void OnPreLoad()
    {
        CreateSettings();
        RegisterParameters();
        CreateSettingsGroups();
    }

    protected override Task<bool> OnModuleStart()
    {
        StartDiagnostics();
        _isStopping = false;
        ResetLeashState();
        ConfigureAvatarParameters(GetClient().Avatar);
        BeginNeutralRepair(Stopwatch.GetTimestamp());
        TryNeutralizePlayerInput(LeashDefaults.StopNeutralRepetitions);
        Log("OSC Leash module started");
        return Task.FromResult(true);
    }

    protected override Task OnModuleStop()
    {
        _isStopping = true;
        TryNeutralizePlayerInput(LeashDefaults.StopNeutralRepetitions);
        PoseUpdateResult cleanupResult = _openVr.RemoveOwnOffset();
        if (cleanupResult is not PoseUpdateResult.Success and not PoseUpdateResult.NoChange)
            Log($"OSC Leash height cleanup: {cleanupResult}");

        ClearState(clearOpenVrOwnership: cleanupResult is PoseUpdateResult.Success or PoseUpdateResult.NoChange);
        Log("OSC Leash module stopped");
        StopDiagnostics();
        return Task.CompletedTask;
    }

    protected override void OnAvatarChange(Avatar? avatar)
    {
        ResetLeashState();
        ConfigureAvatarParameters(avatar);
        BeginNeutralRepair(Stopwatch.GetTimestamp());
        TryNeutralizePlayerInput(LeashDefaults.StopNeutralRepetitions);

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
        BeginNeutralRepair(Stopwatch.GetTimestamp());
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
        RegisterParameter<bool>(OSCLeashParameter.LeashDisable, "leash_disable", ParameterMode.Read, "Leash Disable", "Disables leash motion when true (preferred optional gate)");

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

        using DiagnosticScope updateMeasurement = _updateProbe?.Measure() ?? default;
        int patchedObservers = VrcOscUiDispatchWorkaround.ApplyIfDue();
        if (patchedObservers > 0)
            _diagnostics?.Event("vrcosc_dispatch_workaround", $"patchedObservers={patchedObservers}");

        long now = Stopwatch.GetTimestamp();
        float deltaTime = GetDeltaTime(now);
        using (_settingsProbe?.Measure() ?? default)
            RefreshSettings();
        ObserveInputFreshness(now);

        LeashInputSnapshot snapshot = _input.Snapshot;
        bool leashEngaged = snapshot.LeashEngaged;
        bool motionActive = snapshot.GrabbedForMotion;
        bool justGrabbed = leashEngaged && !_wasLeashEngaged;
        bool justReleased = !leashEngaged && _wasLeashEngaged;
        _wasLeashEngaged = leashEngaged;
        if (justReleased)
            BeginNeutralRepair(now);

        LeashIntent intent = _motion.Resolve(snapshot.Signal, _settings, motionActive);

        using (_openVrProbe?.Measure() ?? default)
        {
            MaintainOpenVrConnection(now);
            UpdateVerticalMotion(intent, leashEngaged, motionActive, justGrabbed, justReleased, deltaTime, now);
        }

        // Native pose work can stall. Do not publish the direction or grab state that
        // was sampled before that call; input callbacks continue while it is blocked.
        now = Stopwatch.GetTimestamp();
        ObserveInputFreshness(now);
        snapshot = _input.Snapshot;
        intent = _motion.Resolve(snapshot.Signal, _settings, snapshot.GrabbedForMotion);
        UpdatePlayerMovement(intent, snapshot.GrabbedForMotion, justGrabbed || justReleased ||
            (motionActive && !snapshot.GrabbedForMotion), now);

        LogHealthIfDue(Stopwatch.GetTimestamp());
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
        bool leashEngaged,
        bool motionActive,
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
            if (refreshResult == PoseUpdateResult.ExternalWriterActive)
            {
                SuspendForExternalWriter(now);
                return;
            }
            if (refreshResult is not PoseUpdateResult.Success and not PoseUpdateResult.NoChange)
                return;

            if (refreshResult == PoseUpdateResult.Success)
                _verticalMotion.Reset();
            else
                _verticalMotion.Rebase(_openVr.LastAppliedOffset);
            _poseRecovery.Reset();
            _diagnostics?.Event("height_grab_baseline",
                $"source=active_tracking_origin;referenceHeight={_openVr.ReferenceHeight:F3};ownedOffset={_openVr.LastAppliedOffset:F3}");
            LogDebug(
                $"Leash grabbed at OpenVR height {_openVr.ReferenceHeight:F3} " +
                $"with owned offset {_openVr.LastAppliedOffset:F3}");
        }

        if (justReleased)
        {
            _verticalMotion.Rebase(_openVr.LastAppliedOffset);
            _diagnostics?.Event("height_release",
                $"referenceHeight={_openVr.ReferenceHeight:F3};ownedOffset={_verticalMotion.Offset:F3}");
            LogDebug($"Leash released at height offset {_verticalMotion.Offset:F3}");
        }

        if (_poseRecovery.Suspended && !TryResumeAfterExternalPoseSettles(leashEngaged, now))
            return;

        bool changed = _verticalMotion.Constrain(_settings.MaximumVerticalOffset);
        if (leashEngaged)
        {
            if (!motionActive || !intent.VerticalModeActive)
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
        bool willRetryAutomatically = _poseRecovery.Suspend(_wasLeashEngaged, TimestampSeconds(now));
        _diagnostics?.Event("external_pose_writer", willRetryAutomatically ? "waiting_to_resume" : "locked_until_regrab");
        if (!willRetryAutomatically)
        {
            Log("Warning: another application repeatedly changed the OpenVR standing pose. " +
                "OSC Leash height drag is suspended until the leash is released and grabbed again.");
            return;
        }

        Log("OpenVR standing pose changed externally. OSC Leash is waiting for it to settle before resuming height drag.");
    }

    private bool TryResumeAfterExternalPoseSettles(bool leashEngaged, long now)
    {
        if (!leashEngaged || _poseRecovery.LockedUntilRegrab)
            return false;

        PoseUpdateResult result = _openVr.ObserveExternalPose(out bool changed);
        if (result is not PoseUpdateResult.Success and not PoseUpdateResult.ExternalWriterActive)
        {
            HandleVrResult(result, "observe external pose", now);
            return false;
        }

        bool resumed = _poseRecovery.Observe(
            leashEngaged,
            changed,
            TimestampSeconds(now),
            LeashDefaults.ExternalPoseQuietSeconds);
        if (!resumed)
            return false;

        _verticalMotion.Reset();
        _diagnostics?.Event("external_pose_writer_recovered");
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
            BeginNeutralRepair(now);
            if (!_inputWasStale)
            {
                _diagnostics?.Event("osc_input_stale", $"timeoutSeconds={LeashDefaults.InputFreshnessSeconds:F0}");
                Log($"OSC Leash input stopped for more than {LeashDefaults.InputFreshnessSeconds:F0}s. " +
                    "Movement was neutralized; release and re-grab the leash before continuing.");
            }

            _inputWasStale = true;
            return;
        }

        bool motionSilenced = _input.SuppressMotionIfSilent(
            now,
            (long)(LeashDefaults.MotionSilenceSeconds * Stopwatch.Frequency));
        if (motionSilenced)
        {
            _motion.Reset();
            BeginNeutralRepair(now);
            _motionInputWasSilent = true;
            _diagnostics?.Event(
                "osc_motion_silenced",
                $"timeoutMilliseconds={LeashDefaults.MotionSilenceSeconds * 1000f:F0}");
        }

        if (_motionInputWasSilent && !_input.MotionSuppressed)
        {
            _motionInputWasSilent = false;
            _diagnostics?.Event("osc_motion_recovered");
        }

        if (!_inputWasStale || !_input.HasReceivedInput || _input.RequiresGrabRelease)
            return;

        _inputWasStale = false;
        _diagnostics?.Event("osc_input_recovered");
        Log("OSC Leash input recovered.");
    }

    private void UpdatePlayerMovement(LeashIntent intent, bool grabbedForMotion, bool forcePublish, long now)
    {
        bool repairingNeutral = !grabbedForMotion && now < _neutralRepairUntilTimestamp;
        if (!grabbedForMotion && !repairingNeutral && !forcePublish)
            return;
        if (!forcePublish && now < _nextPlayerPublishTimestamp)
            return;

        _nextPlayerPublishTimestamp = AddSeconds(now, LeashDefaults.PlayerPublishIntervalSeconds);
        using DiagnosticScope publicationMeasurement = _playerInputProbe?.Measure() ?? default;

        Player? player;
        try
        {
            player = GetClient().Player;
        }
        catch (Exception ex)
        {
            HandlePlayerInputResult(success: false, ex, slowCommand: false, slowDurationMilliseconds: 0d, now);
            return;
        }

        if (player is null)
            return;

        bool success = _playerInput.Apply(new VrcPlayerInputSink(player), intent, grabbedForMotion, _input.CanContinueMotion);
        _playerPublicationsSinceHealth++;
        if (!grabbedForMotion)
            _neutralPublicationsSinceHealth++;
        HandlePlayerInputResult(
            success,
            _playerInput.LastFailure,
            _playerInput.LastCommandWasSlow,
            _playerInput.LastCommandDurationMilliseconds,
            Stopwatch.GetTimestamp());
    }

    private void TryNeutralizePlayerInput(int repetitions = 1)
    {
        Player? player;
        try
        {
            player = GetClient().Player;
        }
        catch (Exception ex)
        {
            HandlePlayerInputResult(
                success: false,
                ex,
                slowCommand: false,
                slowDurationMilliseconds: 0d,
                Stopwatch.GetTimestamp());
            return;
        }

        if (player is null)
            return;

        var sink = new VrcPlayerInputSink(player);
        for (int i = 0; i < repetitions; i++)
        {
            bool success = _playerInput.TryNeutralize(sink);
            _playerPublicationsSinceHealth++;
            _neutralPublicationsSinceHealth++;
            HandlePlayerInputResult(
                success,
                _playerInput.LastFailure,
                _playerInput.LastCommandWasSlow,
                _playerInput.LastCommandDurationMilliseconds,
                Stopwatch.GetTimestamp());
        }
    }

    private void HandlePlayerInputResult(
        bool success,
        Exception? failure,
        bool slowCommand,
        double slowDurationMilliseconds,
        long now)
    {
        if (slowCommand && SecondsSince(_lastPlayerWarningTimestamp, now) >= 5f)
        {
            _diagnostics?.Event("player_input_slow", $"durationMs={slowDurationMilliseconds:F1}");
            Log($"Warning: a complete OSC Leash state took a slow player-input send " +
                $"({slowDurationMilliseconds:F0}ms max command). All remaining channels were still attempted.");
            _lastPlayerWarningTimestamp = now;
        }

        if (success)
        {
            _lastPlayerSuccessTimestamp = now;
            if (_consecutivePlayerFailures > 0)
            {
                _diagnostics?.Event("player_input_recovered", $"failedBatches={_consecutivePlayerFailures}");
                Log($"OSC Leash player input recovered after {_consecutivePlayerFailures} failed batches.");
            }
            _consecutivePlayerFailures = 0;
            return;
        }

        _consecutivePlayerFailures++;
        if (SecondsSince(_lastPlayerWarningTimestamp, now) < 5f)
            return;

        string detail = failure is null ? string.Empty : $" {failure.GetType().Name}: {failure.Message}";
        _diagnostics?.Event("player_input_failure",
            $"consecutive={_consecutivePlayerFailures};exception={detail.Trim()}");
        Log($"Warning: VRChat rejected part of an OSC Leash state " +
            $"({_consecutivePlayerFailures} consecutive failed publications). Every channel was attempted and the complete state will be repeated.{detail}");
        _lastPlayerWarningTimestamp = now;
    }

    private void BeginNeutralRepair(long now)
    {
        _neutralRepairUntilTimestamp = AddSeconds(now, LeashDefaults.NeutralRepairSeconds);
        _nextPlayerPublishTimestamp = 0;
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
        long publications = Interlocked.Exchange(ref _playerPublicationsSinceHealth, 0);
        long neutralPublications = Interlocked.Exchange(ref _neutralPublicationsSinceHealth, 0);
        LeashInputSnapshot snapshot = _input.Snapshot;
        string state =
            $"grabbed={snapshot.IsGrabbed};enabled={snapshot.LeashEnabled};" +
            $"enablePresent={snapshot.HasLeashEnableParameter};disablePresent={snapshot.HasLeashDisableParameter};" +
            $"releaseRequired={snapshot.RequiresGrabRelease};motionSuppressed={snapshot.MotionSuppressed};" +
            $"stretch={snapshot.Stretch:F3};x={snapshot.NetX:F3};y={snapshot.NetY:F3};z={snapshot.NetZ:F3};" +
            $"inputAge={inputAge};stale={_inputWasStale};" +
            $"playerPublications={publications};neutralPublications={neutralPublications}";
        _diagnostics?.Event("leash_state", state);
        LogDebug(
            $"OSC Leash health: inputAge={inputAge}, stale={_inputWasStale}, " +
            $"grabbed={snapshot.IsGrabbed}, enabled={snapshot.LeashEnabled}, " +
            $"enablePresent={snapshot.HasLeashEnableParameter}, disablePresent={snapshot.HasLeashDisableParameter}, " +
            $"releaseRequired={snapshot.RequiresGrabRelease}, motionSuppressed={snapshot.MotionSuppressed}, " +
            $"signal=({snapshot.NetX:F2},{snapshot.NetY:F2},{snapshot.NetZ:F2}) stretch={snapshot.Stretch:F2}, " +
            $"playerSuccessAge={playerAge}, " +
            $"playerFailures={_consecutivePlayerFailures}, publications={publications}, " +
            $"neutralPublications={neutralPublications}, neutralRepair={now < _neutralRepairUntilTimestamp}, " +
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
        _diagnostics?.Event("openvr_failure", $"operation={operation};result={result}");
        _lastLoggedVrResult = result;
        _lastVrWarningTimestamp = now;
    }

    private void ResetLeashState()
    {
        _input.Reset();
        _motion.Reset();
        _wasLeashEngaged = false;
        _poseRecovery.Reset();
        _lastUpdateTimestamp = 0;
        _nextVrRetryTimestamp = 0;
        _nextPlayerPublishTimestamp = 0;
        _neutralRepairUntilTimestamp = 0;
        _verticalMotion.Reset();
        _inputWasStale = false;
        _motionInputWasSilent = false;
        _lastObservedGrabbed = null;
        _lastObservedEnable = null;
        _lastObservedDisable = null;
        _playerPublicationsSinceHealth = 0;
        _neutralPublicationsSinceHealth = 0;
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
        => earlier == 0 ? float.MaxValue : Math.Max(0f, (float)((now - earlier) / (double)Stopwatch.Frequency));

    private static double TimestampSeconds(long timestamp)
        => timestamp / (double)Stopwatch.Frequency;

    protected override void OnRegisteredParameterReceived(RegisteredParameter parameter)
    {
        if (_isStopping)
            return;

        using DiagnosticScope inputMeasurement = _oscInputProbe?.Measure() ?? default;

        OSCLeashParameter key = (OSCLeashParameter)parameter.Lookup;
        switch (key)
        {
            case OSCLeashParameter.IsGrabbed:
                ObserveBooleanInput(key, parameter.GetValue<bool>(), ref _lastObservedGrabbed);
                break;
            case OSCLeashParameter.LeashEnable:
                ObserveBooleanInput(key, parameter.GetValue<bool>(), ref _lastObservedEnable);
                break;
            case OSCLeashParameter.LeashDisable:
                ObserveBooleanInput(key, parameter.GetValue<bool>(), ref _lastObservedDisable);
                break;
            default:
                _input.Set(key, parameter.GetValue<float>());
                break;
        }
    }

    private void ConfigureAvatarParameters(Avatar? avatar)
    {
        bool hasOptionalEnable = avatar?.Parameters.Any(parameter =>
            string.Equals(parameter.Name, "leash_enable", StringComparison.Ordinal)) == true;
        bool hasOptionalDisable = avatar?.Parameters.Any(parameter =>
            string.Equals(parameter.Name, "leash_disable", StringComparison.Ordinal)) == true;
        _input.ConfigureOptionalGates(hasOptionalEnable, hasOptionalDisable);
        _diagnostics?.Event(
            "avatar_parameter_capabilities",
            $"avatar={avatar?.Id ?? "none"};leashEnablePresent={hasOptionalEnable};" +
            $"leashDisablePresent={hasOptionalDisable}");
    }

    private void ObserveBooleanInput(OSCLeashParameter key, bool value, ref bool? previous)
    {
        _input.Set(key, value);
        if (previous == value)
            return;

        previous = value;
        LeashInputSnapshot snapshot = _input.Snapshot;
        _diagnostics?.Event(
            "leash_gate_change",
            $"parameter={key};value={value};active={_input.GrabbedForMotion};" +
            $"enablePresent={snapshot.HasLeashEnableParameter};disablePresent={snapshot.HasLeashDisableParameter};" +
            $"releaseRequired={snapshot.RequiresGrabRelease}");
    }

    private void StartDiagnostics()
    {
        StopDiagnostics();
        _diagnostics = BoundedDiagnostics.StartModule("OSCLeash");
        _updateProbe = _diagnostics.CreateProbe("control_loop", LeashDefaults.UpdateIntervalMilliseconds);
        _settingsProbe = _diagnostics.CreateProbe("settings_refresh");
        _openVrProbe = _diagnostics.CreateProbe("openvr_stage");
        _playerInputProbe = _diagnostics.CreateProbe("player_state_publication");
        _oscInputProbe = _diagnostics.CreateProbe("osc_input_callback");
        VrcOscUiDispatchWorkaround.ApplyIfDue(force: true);
        _diagnostics.Event("vrcosc_dispatch_workaround", VrcOscUiDispatchWorkaround.Status);
        Log($"Performance diagnostics: {_diagnostics.LogDirectory} (bounded to {BoundedDiagnostics.RetainedFileCount} x {BoundedDiagnostics.MaxFileBytes / 1024 / 1024} MiB files)");
    }

    private void StopDiagnostics()
    {
        _diagnostics?.Dispose();
        _diagnostics = null;
        _updateProbe = null;
        _settingsProbe = null;
        _openVrProbe = null;
        _playerInputProbe = null;
        _oscInputProbe = null;
    }
}
