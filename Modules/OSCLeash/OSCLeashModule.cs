using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;
using Valve.VR;

namespace CrookedToe.Modules.OSCLeash;

internal static class LeashConfig
{
    public const float DELTA_TIME = 0.008f;
    public const float VERTICAL_COOLDOWN = 1.0f;
    public const float STOP_THRESHOLD = 0.01f;
    public const float VELOCITY_STOP_THRESHOLD = 0.1f;
    public const float TURN_EPSILON = 0.0001f;
    public const float NORMALIZE_EPSILON = 1e-4f;
}

internal struct CachedSettings
{
    public float WalkDeadzone, RunDeadzone, StrengthMultiplier;
    public float UpDownDeadzone, UpDownCompensation, MovementSmoothing;
    public LeashDirection Direction;
    public bool TurningEnabled;
    public float TurningMultiplier, TurningDeadzone, TurningGoal;
    public bool VerticalEnabled, GravityEnabled;
    public float VerticalMultiplier, VerticalDeadzone, VerticalSmoothing;
    public float VerticalAngle, GravityStrength, TerminalVelocity;
}

[ModuleTitle("OSC Leash")]
[ModuleDescription("Allows for controlling avatar movement with parameters, including vertical movement via OpenVR")]
[ModuleType(ModuleType.Generic)]
[ModulePrefab("OSCLeash", "https://github.com/CrookedToe/OSCLeash/tree/main/Unity")]
[ModuleInfo("https://github.com/CrookedToe/CrookedToe-s-Modules")]
public class OSCLeashModule : Module
{
    private static readonly (OSCLeashSetting Key, string Name, string Desc, float Default, float Min, float Max)[] SliderSettings =
    [
        (OSCLeashSetting.WalkDeadzone, "Walk Deadzone", "Minimum stretch to start walking", 0.15f, 0.0f, 1.0f),
        (OSCLeashSetting.RunDeadzone, "Run Deadzone", "Stretch threshold for running", 0.70f, 0.0f, 1.0f),
        (OSCLeashSetting.StrengthMultiplier, "Movement Strength", "Overall speed multiplier", 1.2f, 0.1f, 5.0f),
        (OSCLeashSetting.UpDownDeadzone, "Up/Down Deadzone", "Vertical movement threshold", 0.5f, 0.0f, 1.0f),
        (OSCLeashSetting.UpDownCompensation, "Up/Down Compensation", "Vertical effect on horizontal speed", 0.5f, 0.0f, 1.0f),
        (OSCLeashSetting.MovementSmoothing, "Movement Smoothing", "Smoothing factor for horizontal movement", 0.7f, 0.0f, 0.95f),
        (OSCLeashSetting.TurningMultiplier, "Turn Speed", "Rotation speed multiplier", 0.80f, 0.1f, 2.0f),
        (OSCLeashSetting.TurningDeadzone, "Turn Deadzone", "Minimum stretch for turning", 0.15f, 0.0f, 1.0f),
        (OSCLeashSetting.TurningGoal, "Minimum Turn Angle", "Minimum angle away from forward before turning starts", 20f, 0.0f, 90.0f),
        (OSCLeashSetting.VerticalMovementMultiplier, "Vertical Speed", "Vertical movement speed multiplier", 1.0f, 0.1f, 5.0f),
        (OSCLeashSetting.VerticalMovementDeadzone, "Vertical Deadzone", "Minimum vertical pull needed", 0.15f, 0.0f, 1.0f),
        (OSCLeashSetting.VerticalMovementSmoothing, "Vertical Smoothing", "Smoothing factor for height changes", 0.8f, 0.0f, 1.0f),
        (OSCLeashSetting.VerticalHorizontalCompensation, "Vertical Angle", "Required angle from horizontal", 45f, 15f, 75f),
        (OSCLeashSetting.GravityStrength, "Gravity Strength", "Gravity acceleration", 9.81f, 0.1f, 50.0f),
        (OSCLeashSetting.TerminalVelocity, "Terminal Velocity", "Maximum falling speed", 15.0f, 1.0f, 50.0f),
    ];

    private static readonly (OSCLeashSetting Key, string Name, string Desc, bool Default)[] ToggleSettings =
    [
        (OSCLeashSetting.TurningEnabled, "Enable Turning", "Enables avatar rotation control", false),
        (OSCLeashSetting.VerticalMovementEnabled, "Enable Vertical Movement", "Enables OpenVR height control", false),
        (OSCLeashSetting.GrabBasedGravity, "Enable Gravity", "Return to grab height when released", false),
    ];

    private static readonly (OSCLeashParameter Key, string Address, string Name, string Desc)[] FloatParameters =
    [
        (OSCLeashParameter.Stretch, "Leash_Stretch", "Leash Stretch", "How far the leash is stretched"),
        (OSCLeashParameter.ZPositive, "Leash_Z+", "Forward Pull", "Forward movement value"),
        (OSCLeashParameter.ZNegative, "Leash_Z-", "Backward Pull", "Backward movement value"),
        (OSCLeashParameter.XPositive, "Leash_X+", "Right Pull", "Rightward movement value"),
        (OSCLeashParameter.XNegative, "Leash_X-", "Left Pull", "Leftward movement value"),
        (OSCLeashParameter.YPositive, "Leash_Y+", "Upward Pull", "Upward movement value"),
        (OSCLeashParameter.YNegative, "Leash_Y-", "Downward Pull", "Downward movement value"),
    ];

    private bool _isGrabbed;
    private float _stretch;
    private float _xPos, _xNeg, _yPos, _yNeg, _zPos, _zNeg;
    private bool _isWalking, _isRunning;
    
    private float _vrVelocity;
    private float _referenceHeight;
    private float _currentVerticalOffset;
    private HmdMatrix34_t _standingZeroPose;
    private HmdMatrix34_t _initialStandingZeroPose;
    private DateTime? _grabbedAt;
    private bool _vrInitialized;
    private bool _vrInitFailed;
    
    private float _smoothMoveX, _smoothMoveZ;
    private bool _wasGrabbed;
    
    private CachedSettings _settings;
    private Dictionary<OSCLeashParameter, Action<RegisteredParameter>>? _parameterHandlers;

    private float NetX => _xPos - _xNeg;
    private float NetY => _yNeg - _yPos;
    private float NetZ => _zPos - _zNeg;

    protected override void OnPreLoad()
    {
        Log("Initializing OSC Leash module...");
        CreateSettings();
        RegisterAllParameters();
        CreateSettingsGroups();
        InitializeParameterHandlers();
    }

    protected override Task<bool> OnModuleStart()
    {
        Log("OSC Leash module started");
        return Task.FromResult(true);
    }

    protected override Task OnModuleStop()
    {
        Log("Stopping OSC Leash module...");
        CleanupVRResources();
        ResetParametersToSafeValues();
        ClearModuleState();
        Log("OSC Leash module stopped");
        return Task.CompletedTask;
    }

    private void CreateSettings()
    {
        foreach (var (key, name, desc, def, min, max) in SliderSettings)
            CreateSlider(key, name, desc, def, min, max);
        
        foreach (var (key, name, desc, def) in ToggleSettings)
            CreateToggle(key, name, desc, def);
        
        CreateDropdown(OSCLeashSetting.LeashDirection, "Leash Direction", "Direction the leash faces", LeashDirection.North);
    }

    private void RegisterAllParameters()
    {
        RegisterParameter<bool>(OSCLeashParameter.IsGrabbed, "Leash_IsGrabbed", ParameterMode.Read, "Leash Grabbed", "Whether the leash is being held");
        
        foreach (var (key, address, name, desc) in FloatParameters)
            RegisterParameter<float>(key, address, ParameterMode.Read, name, desc);
    }

    private void CreateSettingsGroups()
    {
        CreateGroup("Basic Movement", "Core movement and speed settings",
            OSCLeashSetting.WalkDeadzone, OSCLeashSetting.RunDeadzone, OSCLeashSetting.StrengthMultiplier,
            OSCLeashSetting.UpDownDeadzone, OSCLeashSetting.UpDownCompensation, OSCLeashSetting.MovementSmoothing,
            OSCLeashSetting.LeashDirection);
        CreateGroup("Turning Controls", "Avatar rotation and turning behavior",
            OSCLeashSetting.TurningEnabled, OSCLeashSetting.TurningMultiplier,
            OSCLeashSetting.TurningDeadzone, OSCLeashSetting.TurningGoal);
        CreateGroup("Vertical Movement", "OpenVR height control and gravity settings",
            OSCLeashSetting.VerticalMovementEnabled, OSCLeashSetting.GrabBasedGravity,
            OSCLeashSetting.VerticalMovementMultiplier, OSCLeashSetting.VerticalMovementDeadzone,
            OSCLeashSetting.VerticalMovementSmoothing, OSCLeashSetting.VerticalHorizontalCompensation,
            OSCLeashSetting.GravityStrength, OSCLeashSetting.TerminalVelocity);
    }

    private void InitializeParameterHandlers()
    {
        _parameterHandlers = new Dictionary<OSCLeashParameter, Action<RegisteredParameter>>
        {
            [OSCLeashParameter.IsGrabbed] = p => _isGrabbed = p.GetValue<bool>(),
            [OSCLeashParameter.Stretch] = p => _stretch = p.GetValue<float>(),
            [OSCLeashParameter.ZPositive] = p => _zPos = p.GetValue<float>(),
            [OSCLeashParameter.ZNegative] = p => _zNeg = p.GetValue<float>(),
            [OSCLeashParameter.XPositive] = p => _xPos = p.GetValue<float>(),
            [OSCLeashParameter.XNegative] = p => _xNeg = p.GetValue<float>(),
            [OSCLeashParameter.YPositive] = p => _yPos = p.GetValue<float>(),
            [OSCLeashParameter.YNegative] = p => _yNeg = p.GetValue<float>(),
        };
    }

    private void CleanupVRResources()
    {
        if (!_vrInitialized) return;
        
        try
        {
            var setup = OpenVR.ChaperoneSetup;
            if (setup != null)
            {
                setup.SetWorkingStandingZeroPoseToRawTrackingPose(ref _initialStandingZeroPose);
                setup.CommitWorkingCopy(EChaperoneConfigFile.Live);
            }
        }
        catch { }
        
        _vrInitialized = false;
    }

    private void ResetParametersToSafeValues()
    {
        var player = GetPlayer();
        if (player == null) return;
        
        try { player.StopRun(); } catch { }
        try { player.MoveVertical(0); } catch { }
        try { player.MoveHorizontal(0); } catch { }
        try { player.LookHorizontal(0); } catch { }
    }

    private void ClearModuleState()
    {
        _isGrabbed = false;
        _stretch = 0f;
        _xPos = _xNeg = _yPos = _yNeg = _zPos = _zNeg = 0f;
        _isWalking = _isRunning = false;
        _vrVelocity = 0f;
        _referenceHeight = 0f;
        _currentVerticalOffset = 0f;
        _vrInitialized = false;
        _vrInitFailed = false;
        _grabbedAt = null;
        _smoothMoveX = _smoothMoveZ = 0f;
        _wasGrabbed = false;
    }

    private void InitializeVR()
    {
        if (_vrInitFailed) return;
        
        var ovrClient = GetOpenVRManager();
        if (ovrClient is null)
        {
            Log("Warning: OpenVR manager not available, vertical movement disabled");
            _vrInitFailed = true;
            return;
        }

        try
        {
            var setup = OpenVR.ChaperoneSetup;
            if (setup != null)
            {
                _standingZeroPose = new HmdMatrix34_t();
                setup.GetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
                _initialStandingZeroPose = _standingZeroPose;
                _referenceHeight = _standingZeroPose.m7;
                _currentVerticalOffset = 0;
                _vrVelocity = 0;
                _vrInitialized = true;
                Log($"VR initialized at reference height: {_referenceHeight:F3}");
            }
            else
            {
                Log("Warning: OpenVR ChaperoneSetup not available");
                _vrInitFailed = true;
            }
        }
        catch (Exception ex)
        {
            Log($"Warning: OpenVR not available ({ex.Message})");
            _vrInitFailed = true;
        }
    }

    private void UpdateReferenceHeight()
    {
        if (!_vrInitialized) return;
        
        var ovrClient = GetOpenVRManager();
        if (ovrClient is null) return;

        try
        {
            var chaperoneSetup = OpenVR.ChaperoneSetup;
            if (chaperoneSetup != null)
            {
                chaperoneSetup.GetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
                _referenceHeight = _standingZeroPose.m7;
                _currentVerticalOffset = 0;
                _vrVelocity = 0;
            }
        }
        catch { }
    }

    private void ApplyOffset(float newOffset)
    {
        if (!CanUpdatePlayspace()) return;

        try
        {
            var chaperoneSetup = OpenVR.ChaperoneSetup;
            if (chaperoneSetup == null) return;

            _currentVerticalOffset = newOffset;
            _standingZeroPose.m7 = _referenceHeight + newOffset;

            chaperoneSetup.SetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
            chaperoneSetup.CommitWorkingCopy(EChaperoneConfigFile.Live);
        }
        catch
        {
            _vrInitialized = false;
        }
    }

    private bool CanUpdatePlayspace()
    {
        var ovrClient = GetOpenVRManager();
        if (!_settings.VerticalEnabled || !_vrInitialized || ovrClient is null)
            return false;

        return _isGrabbed || (_settings.GravityEnabled && 
               (MathF.Abs(_currentVerticalOffset) > LeashConfig.STOP_THRESHOLD || 
                MathF.Abs(_vrVelocity) > LeashConfig.VELOCITY_STOP_THRESHOLD));
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, 8)]
    private void UpdateMovement()
    {
        var player = GetPlayer();
        if (player == null) return;

        RefreshSettings();
        
        bool justGrabbed = _isGrabbed && !_wasGrabbed;
        bool justReleased = !_isGrabbed && _wasGrabbed;
        _wasGrabbed = _isGrabbed;

        UpdateMovementState();
        var movement = CalculateMovement();
        ApplyMovement(player, movement);
        HandleVRState();
        
        if (_settings.VerticalEnabled && _vrInitialized)
            UpdateVR(justGrabbed, justReleased);
    }

    private void RefreshSettings()
    {
        _settings = new CachedSettings
        {
            WalkDeadzone = GetSettingValue<float>(OSCLeashSetting.WalkDeadzone),
            RunDeadzone = GetSettingValue<float>(OSCLeashSetting.RunDeadzone),
            StrengthMultiplier = GetSettingValue<float>(OSCLeashSetting.StrengthMultiplier),
            UpDownDeadzone = GetSettingValue<float>(OSCLeashSetting.UpDownDeadzone),
            UpDownCompensation = GetSettingValue<float>(OSCLeashSetting.UpDownCompensation),
            MovementSmoothing = GetSettingValue<float>(OSCLeashSetting.MovementSmoothing),
            Direction = GetSettingValue<LeashDirection>(OSCLeashSetting.LeashDirection),
            TurningEnabled = GetSettingValue<bool>(OSCLeashSetting.TurningEnabled),
            TurningMultiplier = GetSettingValue<float>(OSCLeashSetting.TurningMultiplier),
            TurningDeadzone = GetSettingValue<float>(OSCLeashSetting.TurningDeadzone),
            TurningGoal = GetSettingValue<float>(OSCLeashSetting.TurningGoal),
            VerticalEnabled = GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled),
            GravityEnabled = GetSettingValue<bool>(OSCLeashSetting.GrabBasedGravity),
            VerticalMultiplier = GetSettingValue<float>(OSCLeashSetting.VerticalMovementMultiplier),
            VerticalDeadzone = GetSettingValue<float>(OSCLeashSetting.VerticalMovementDeadzone),
            VerticalSmoothing = GetSettingValue<float>(OSCLeashSetting.VerticalMovementSmoothing),
            VerticalAngle = GetSettingValue<float>(OSCLeashSetting.VerticalHorizontalCompensation),
            GravityStrength = GetSettingValue<float>(OSCLeashSetting.GravityStrength),
            TerminalVelocity = GetSettingValue<float>(OSCLeashSetting.TerminalVelocity),
        };
    }

    private void HandleVRState()
    {
        if (_settings.VerticalEnabled && !_vrInitialized && !_vrInitFailed)
            InitializeVR();
        else if (!_settings.VerticalEnabled && _vrInitialized)
            CleanupVRResources();
        else if (!_settings.VerticalEnabled && _vrInitFailed)
            _vrInitFailed = false;
    }

    private void UpdateMovementState()
    {
        if (_isGrabbed)
        {
            _isWalking = _stretch > _settings.WalkDeadzone;
            _isRunning = _stretch > _settings.RunDeadzone;
        }
        else
        {
            _isWalking = false;
            _isRunning = false;
        }
    }

    private (float x, float y, float z) CalculateMovement()
    {
        if (!_isGrabbed)
        {
            _smoothMoveX = 0f;
            _smoothMoveZ = 0f;
            return (0f, 0f, 0f);
        }

        float netX = NetX;
        float netY = NetY;
        float netZ = NetZ;

        float strength = _stretch * _settings.StrengthMultiplier;
        float verticalStretch = MathF.Abs(netY);
        
        if (verticalStretch >= _settings.UpDownDeadzone && _settings.UpDownCompensation > 0)
        {
            float compensationFactor = Math.Clamp(1.0f - (verticalStretch * _settings.UpDownCompensation * 0.5f), 0.1f, 1.0f);
            netX *= compensationFactor;
            netZ *= compensationFactor;
        }

        netX *= strength;
        netZ *= strength;
        
        float smoothing = _settings.MovementSmoothing;
        _smoothMoveX = _smoothMoveX * smoothing + netX * (1f - smoothing);
        _smoothMoveZ = _smoothMoveZ * smoothing + netZ * (1f - smoothing);

        return (_smoothMoveX, netY, _smoothMoveZ);
    }

    private void ApplyMovement(Player player, (float x, float y, float z) movement)
    {
        if (!_isGrabbed)
        {
            player.StopRun();
            player.MoveVertical(0);
            player.MoveHorizontal(0);
            player.LookHorizontal(0);
            return;
        }

        if (_isRunning) player.Run();
        else player.StopRun();

        player.MoveVertical(movement.z);
        player.MoveHorizontal(movement.x);
        ApplyTurning(player, movement);
    }

    private void ApplyTurning(Player player, (float x, float y, float z) movement)
    {
        if (!_settings.TurningEnabled || _stretch <= _settings.TurningDeadzone)
            return;

        float turnValue = CalculateTurning(movement.x, movement.z);
        if (MathF.Abs(turnValue) > LeashConfig.TURN_EPSILON)
            player.LookHorizontal(turnValue);
    }

    private float CalculateTurning(float moveX, float moveZ)
    {
        float absX = MathF.Abs(moveX);
        float absZ = MathF.Abs(moveZ);
        float maxMag = MathF.Max(absX, absZ);
        
        if (maxMag < LeashConfig.NORMALIZE_EPSILON) return 0f;

        float normX = moveX / maxMag;
        float normZ = moveZ / maxMag;

        float forwardComponent = _settings.Direction switch
        {
            LeashDirection.North or LeashDirection.South => normZ,
            LeashDirection.East or LeashDirection.West => normX,
            _ => 0f
        };

        float sideComponent = _settings.Direction switch
        {
            LeashDirection.North or LeashDirection.South => normX,
            LeashDirection.East or LeashDirection.West => normZ,
            _ => 0f
        };

        float sideMag = MathF.Abs(sideComponent);
        float fwdMag = MathF.Abs(forwardComponent);

        if (sideMag < LeashConfig.NORMALIZE_EPSILON && fwdMag < LeashConfig.NORMALIZE_EPSILON)
            return 0f;

        float pullAngleDeg = MathF.Atan2(sideMag, fwdMag) * (180f / MathF.PI);
        if (pullAngleDeg < _settings.TurningGoal) return 0f;

        float baseTurn = sideComponent * _settings.TurningMultiplier;
        float orientedTurn = _settings.Direction switch
        {
            LeashDirection.North => baseTurn,
            LeashDirection.South => -baseTurn,
            LeashDirection.East => -baseTurn,
            LeashDirection.West => baseTurn,
            _ => 0f
        };

        return Math.Clamp(orientedTurn, -1f, 1f);
    }

    private void UpdateVR(bool justGrabbed, bool justReleased)
    {
        if (!_vrInitialized) return;

        bool shouldUpdateChaperone = false;
        
        if (justGrabbed)
        {
            UpdateReferenceHeight();
            _grabbedAt = DateTime.UtcNow;
        }
        else if (justReleased)
        {
            _vrVelocity = 0f;
            _grabbedAt = null;
        }

        if (_isGrabbed)
            shouldUpdateChaperone = HandleGrabbedVerticalMovement();
        else if (_settings.GravityEnabled)
            shouldUpdateChaperone = ApplyGravity();

        if (shouldUpdateChaperone)
            ApplyOffset(_currentVerticalOffset);
    }

    private bool HandleGrabbedVerticalMovement()
    {
        if (_grabbedAt.HasValue)
        {
            var elapsed = (DateTime.UtcNow - _grabbedAt.Value).TotalSeconds;
            if (elapsed < LeashConfig.VERTICAL_COOLDOWN)
                return false;
        }

        float netY = NetY;
        float horizontalMag = MathF.Sqrt(NetX * NetX + NetZ * NetZ);
        float pullAngle = MathF.Atan2(MathF.Abs(netY), horizontalMag) * (180f / MathF.PI);

        if (pullAngle >= _settings.VerticalAngle && MathF.Abs(netY) >= _settings.VerticalDeadzone)
        {
            float targetVelocity = netY * _settings.VerticalMultiplier;
            _vrVelocity = _vrVelocity * _settings.VerticalSmoothing + targetVelocity * (1f - _settings.VerticalSmoothing);
            _currentVerticalOffset += _vrVelocity * LeashConfig.DELTA_TIME;
            return true;
        }
        
        return false;
    }

    private bool ApplyGravity()
    {
        if (MathF.Abs(_currentVerticalOffset) < LeashConfig.STOP_THRESHOLD && 
            MathF.Abs(_vrVelocity) < LeashConfig.VELOCITY_STOP_THRESHOLD)
        {
            if (_currentVerticalOffset != 0f || _vrVelocity != 0f)
            {
                _vrVelocity = 0f;
                _currentVerticalOffset = 0f;
                return true;
            }
            return false;
        }

        float gravityDirection = -MathF.Sign(_currentVerticalOffset);
        _vrVelocity += _settings.GravityStrength * gravityDirection * LeashConfig.DELTA_TIME;
        
        _vrVelocity = gravityDirection > 0
            ? MathF.Min(_vrVelocity, _settings.TerminalVelocity)
            : MathF.Max(_vrVelocity, -_settings.TerminalVelocity);

        float newOffset = _currentVerticalOffset + _vrVelocity * LeashConfig.DELTA_TIME;

        if ((_currentVerticalOffset > 0f && newOffset < 0f) ||
            (_currentVerticalOffset < 0f && newOffset > 0f))
        {
            _vrVelocity = 0f;
            _currentVerticalOffset = 0f;
            return true;
        }

        _currentVerticalOffset = newOffset;
        return true;
    }

    protected override void OnRegisteredParameterReceived(RegisteredParameter parameter)
    {
        var paramType = (OSCLeashParameter)parameter.Lookup;
        if (_parameterHandlers?.TryGetValue(paramType, out var handler) == true)
            handler(parameter);
    }
}

