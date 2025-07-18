using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;
using Valve.VR;
using System;
using System.Threading.Tasks;

namespace CrookedToe.Modules.OSCLeash;

/// <summary>
/// Configuration constants for OpenVR service
/// </summary>
internal static class OpenVRConfig
{
    public const float GRAVITY = 9.81f;
    public const float TERMINAL_VELOCITY = 15.0f;
    public const float VERTICAL_SMOOTHING = 0.95f;
    public const float GRAB_DELAY = 0f;
    public const float MAX_VERTICAL_OFFSET = 2.0f;
    public const float MOVEMENT_DEADZONE = 0.05f;
    public const float STOP_THRESHOLD = 0.01f;
    public const float VELOCITY_STOP_THRESHOLD = 0.1f;
    public const float MIN_HEIGHT_CHANGE = 0.001f; // 1mm minimum change to reduce jitter
}

/// <summary>
/// OSC Leash module for VRCOSC - Enables avatar movement control via leash parameters
/// with support for horizontal movement, turning, and vertical movement via OpenVR
/// </summary>
[ModuleTitle("OSC Leash")]
[ModuleDescription("Allows for controlling avatar movement with parameters, including vertical movement via OpenVR")]
[ModuleType(ModuleType.Generic)]
[ModulePrefab("OSCLeash", "https://github.com/CrookedToe/OSCLeash/tree/main/Unity")]
[ModuleInfo("https://github.com/CrookedToe/CrookedToe-s-Modules")]
public class OSCLeashModule : Module
{
    #region State Variables
    // Movement state
    private bool _isGrabbed;
    private float _stretch;
    private float _xPos, _xNeg, _yPos, _yNeg, _zPos, _zNeg;
    private float _lastMoveX, _lastMoveY, _lastMoveZ;
    private bool _isWalking, _isRunning;
    
    // VR state with reference height system (based on working old implementation)
    private bool _vrEnabled;
    private float _vrHeight;
    private float _vrVelocity;
    private float _referenceHeight;
    private float _currentVerticalOffset;
    private float _lastAppliedHeight;
    private HmdMatrix34_t _standingZeroPose;  // Working copy like old implementation
    private bool _vrInitialized;
    
    // Smoothing state
    private float _smoothMoveX, _smoothMoveZ;
    private bool _wasGrabbed;
    #endregion

    #region Module Lifecycle
    protected override void OnPreLoad()
    {
        Log("Initializing OSC Leash module configuration...");
        
        CreateMovementSettings();
        CreateTurningSettings();
        CreateVerticalSettings();
        RegisterParameters();
        CreateSettingsGroups();
        
        LogDebug("Module configuration initialized successfully");
    }

    protected override Task<bool> OnModuleStart()
    {
        Log("Starting OSC Leash module...");
        
        _vrEnabled = GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled);
        
        if (_vrEnabled)
        {
            Log("Vertical movement enabled, initializing VR system...");
            InitializeVR();
        }
        else
        {
            LogDebug("Vertical movement disabled, skipping VR initialization");
        }
        
        Log("OSC Leash module started successfully");
        return Task.FromResult(true);
    }

    protected override Task OnModuleStop()
    {
        Log("Stopping OSC Leash module...");
        
        CleanupVRResources();
        ResetParametersToSafeValues();
        ClearModuleState();
        
        Log("OSC Leash module stopped successfully");
        return Task.CompletedTask;
    }
    #endregion

    #region Configuration Setup
    private void CreateMovementSettings()
    {
        CreateSlider(OSCLeashSetting.WalkDeadzone, "Walk Deadzone", "Minimum stretch to start walking", 0.15f, 0.0f, 1.0f);
        CreateSlider(OSCLeashSetting.RunDeadzone, "Run Deadzone", "Stretch threshold for running", 0.70f, 0.0f, 1.0f);
        CreateSlider(OSCLeashSetting.StrengthMultiplier, "Movement Strength", "Overall speed multiplier", 1.2f, 0.1f, 5.0f);
        CreateSlider(OSCLeashSetting.UpDownDeadzone, "Up/Down Deadzone", "Vertical movement threshold", 0.5f, 0.0f, 1.0f);
        CreateSlider(OSCLeashSetting.UpDownCompensation, "Up/Down Compensation", "Vertical effect on horizontal speed", 0.5f, 0.0f, 1.0f);
        CreateDropdown(OSCLeashSetting.LeashDirection, "Leash Direction", "Direction the leash faces", LeashDirection.North);
    }

    private void CreateTurningSettings()
    {
        CreateToggle(OSCLeashSetting.TurningEnabled, "Enable Turning", "Enables avatar rotation control", false);
        CreateSlider(OSCLeashSetting.TurningMultiplier, "Turn Speed", "Rotation speed multiplier", 0.80f, 0.1f, 2.0f);
        CreateSlider(OSCLeashSetting.TurningDeadzone, "Turn Deadzone", "Minimum stretch for turning", 0.15f, 0.0f, 1.0f);
        CreateSlider(OSCLeashSetting.TurningGoal, "Maximum Turn Angle", "Maximum rotation angle", 90f, 0.0f, 180.0f);
    }

    private void CreateVerticalSettings()
    {
        CreateToggle(OSCLeashSetting.VerticalMovementEnabled, "Enable Vertical Movement", "Enables OpenVR height control", false);
        CreateToggle(OSCLeashSetting.GrabBasedGravity, "Enable Gravity", "Return to grab height when released", false);
        CreateSlider(OSCLeashSetting.VerticalMovementMultiplier, "Vertical Speed", "Vertical movement speed multiplier", 1.0f, 0.1f, 5.0f);
        CreateSlider(OSCLeashSetting.VerticalMovementDeadzone, "Vertical Deadzone", "Minimum vertical pull needed", 0.15f, 0.0f, 1.0f);
        CreateSlider(OSCLeashSetting.VerticalMovementSmoothing, "Vertical Smoothing", "Smoothing factor for height changes", 0.8f, 0.0f, 1.0f);
        CreateSlider(OSCLeashSetting.VerticalHorizontalCompensation, "Vertical Angle", "Required angle from horizontal", 45f, 15f, 75f);
        CreateSlider(OSCLeashSetting.GravityStrength, "Gravity Strength", "Gravity acceleration", 9.81f, 0.1f, 50.0f);
        CreateSlider(OSCLeashSetting.TerminalVelocity, "Terminal Velocity", "Maximum falling speed", 15.0f, 1.0f, 50.0f);
    }

    private void RegisterParameters()
    {
        RegisterParameter<bool>(OSCLeashParameter.IsGrabbed, "Leash_IsGrabbed", ParameterMode.Read, "Leash Grabbed", "Whether the leash is being held");
        RegisterParameter<float>(OSCLeashParameter.Stretch, "Leash_Stretch", ParameterMode.Read, "Leash Stretch", "How far the leash is stretched");
        RegisterParameter<float>(OSCLeashParameter.ZPositive, "Leash_Z+", ParameterMode.Read, "Forward Pull", "Forward movement value");
        RegisterParameter<float>(OSCLeashParameter.ZNegative, "Leash_Z-", ParameterMode.Read, "Backward Pull", "Backward movement value");
        RegisterParameter<float>(OSCLeashParameter.XPositive, "Leash_X+", ParameterMode.Read, "Right Pull", "Rightward movement value");
        RegisterParameter<float>(OSCLeashParameter.XNegative, "Leash_X-", ParameterMode.Read, "Left Pull", "Leftward movement value");
        RegisterParameter<float>(OSCLeashParameter.YPositive, "Leash_Y+", ParameterMode.Read, "Upward Pull", "Upward movement value");
        RegisterParameter<float>(OSCLeashParameter.YNegative, "Leash_Y-", ParameterMode.Read, "Downward Pull", "Downward movement value");
    }

    private void CreateSettingsGroups()
    {
        CreateGroup("Basic Movement", "Core movement and speed settings",
            OSCLeashSetting.WalkDeadzone, OSCLeashSetting.RunDeadzone, OSCLeashSetting.StrengthMultiplier,
            OSCLeashSetting.UpDownDeadzone, OSCLeashSetting.UpDownCompensation, OSCLeashSetting.LeashDirection);
        CreateGroup("Turning Controls", "Avatar rotation and turning behavior",
            OSCLeashSetting.TurningEnabled, OSCLeashSetting.TurningMultiplier,
            OSCLeashSetting.TurningDeadzone, OSCLeashSetting.TurningGoal);
        CreateGroup("Vertical Movement", "OpenVR height control and gravity settings",
            OSCLeashSetting.VerticalMovementEnabled, OSCLeashSetting.GrabBasedGravity,
            OSCLeashSetting.VerticalMovementMultiplier, OSCLeashSetting.VerticalMovementDeadzone,
            OSCLeashSetting.VerticalMovementSmoothing, OSCLeashSetting.VerticalHorizontalCompensation,
            OSCLeashSetting.GravityStrength, OSCLeashSetting.TerminalVelocity);
    }
    #endregion

    #region Cleanup Methods
    private void CleanupVRResources()
    {
        if (!_vrEnabled || !_vrInitialized) return;
        
        try
        {
            LogDebug("Cleaning up VR resources...");
            
            var setup = OpenVR.ChaperoneSetup;
            if (setup != null)
            {
                setup.SetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
                setup.CommitWorkingCopy(EChaperoneConfigFile.Live);
                LogDebug($"VR baseline restored to original height: {_standingZeroPose.m7:F3}");
            }
            
            _vrInitialized = false;
            LogDebug("VR resources cleaned up successfully");
        }
        catch (Exception ex)
        {
            Log($"Error during VR resource cleanup: {ex.Message}");
            _vrInitialized = false;
        }
    }

    private void ResetParametersToSafeValues()
    {
        try
        {
            LogDebug("Resetting avatar movement to safe values...");
            
            var player = GetPlayer();
            if (player != null)
            {
                player.StopRun();
                player.MoveVertical(0);
                player.MoveHorizontal(0);
                // Note: Rotation is never reset to preserve user control
            }
        }
        catch (Exception ex)
        {
            Log($"Error resetting parameters: {ex.Message}");
        }
    }

    private void ClearModuleState()
    {
        LogDebug("Clearing module state variables...");
        
        // Clear movement state
        _isGrabbed = false;
        _stretch = 0f;
        _xPos = _xNeg = _yPos = _yNeg = _zPos = _zNeg = 0f;
        _lastMoveX = _lastMoveY = _lastMoveZ = 0f;
        _isWalking = _isRunning = false;
        
        // Clear VR state
        _vrHeight = 0f;
        _vrVelocity = 0f;
        _referenceHeight = 0f;
        _currentVerticalOffset = 0f;
        _lastAppliedHeight = 0f;
        _vrInitialized = false;
        
        // Clear smoothing state
        _smoothMoveX = _smoothMoveZ = 0f;
        _wasGrabbed = false;
    }
    #endregion

    #region VR System Management
    private void InitializeVR()
    {
        var ovrClient = GetOVRClient();
        if (ovrClient?.HasInitialised != true)
        {
            Log("Warning: OpenVR client not initialized, vertical movement disabled");
            _vrEnabled = false;
            return;
        }

        try
        {
            var setup = OpenVR.ChaperoneSetup;
            if (setup != null)
            {
                // Initialize like the working old implementation
                _standingZeroPose = new HmdMatrix34_t();
                setup.GetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
                _vrHeight = _standingZeroPose.m7;
                _referenceHeight = _standingZeroPose.m7;
                _currentVerticalOffset = 0;
                _vrVelocity = 0;
                _lastAppliedHeight = _standingZeroPose.m7;
                _vrInitialized = true;
                
                Log($"VR initialized successfully at reference height: {_referenceHeight:F3}");
            }
            else
            {
                Log("Warning: OpenVR ChaperoneSetup not available, vertical movement disabled");
                _vrEnabled = false;
            }
        }
        catch (Exception ex)
        {
            Log($"VR initialization failed: {ex.Message}");
            _vrEnabled = false;
                }
    }

    /// <summary>
    /// Updates reference height to current position - based on working old implementation
    /// Called when leash is grabbed to handle space drag properly
    /// </summary>
    private void UpdateReferenceHeight()
    {
        var ovrClient = GetOVRClient();
        if (ovrClient?.HasInitialised != true)
            return;

        var chaperoneSetup = OpenVR.ChaperoneSetup;
        if (chaperoneSetup != null)
        {
            chaperoneSetup.GetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
            _referenceHeight = _standingZeroPose.m7;
            _currentVerticalOffset = 0;
            _vrVelocity = 0;
            _lastAppliedHeight = _referenceHeight;
            LogDebug($"Updated reference height to {_referenceHeight:F3}");
        }
    }

    /// <summary>
    /// Apply vertical offset - based on working old implementation
    /// </summary>
    private void ApplyOffset(float newOffset)
    {
        if (!CanUpdatePlayspace())
            return;

        try
        {
            var chaperoneSetup = OpenVR.ChaperoneSetup;
            if (chaperoneSetup == null)
                return;

            _currentVerticalOffset = newOffset;
            float absoluteHeight = _referenceHeight + newOffset;
            _standingZeroPose.m7 = absoluteHeight;

            chaperoneSetup.SetWorkingStandingZeroPoseToRawTrackingPose(ref _standingZeroPose);
            chaperoneSetup.CommitWorkingCopy(EChaperoneConfigFile.Live);
            _lastAppliedHeight = absoluteHeight;
        }
        catch (Exception ex)
        {
            LogDebug($"Error applying offset: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if we can update the playspace - based on working old implementation
    /// </summary>
    private bool CanUpdatePlayspace()
    {
        var ovrClient = GetOVRClient();
        if (!_vrEnabled || !_vrInitialized || ovrClient?.HasInitialised != true)
            return false;

        // Always allow updates when grabbed or when gravity is pulling us back
        return _isGrabbed || (GetSettingValue<bool>(OSCLeashSetting.GrabBasedGravity) && 
               (Math.Abs(_currentVerticalOffset) > OpenVRConfig.STOP_THRESHOLD || 
                Math.Abs(_vrVelocity) > OpenVRConfig.VELOCITY_STOP_THRESHOLD));
    }

    #endregion

    #region Movement Processing
    [ModuleUpdate(ModuleUpdateMode.Custom, true, 8)] // 120 FPS
    private void UpdateMovement()
    {
        var player = GetPlayer();
        if (player == null) return;

        try
        {
            UpdateMovementState();
            var movement = CalculateMovement();
            ApplyMovement(player, movement);
            
            if (_vrEnabled)
            {
                UpdateVR();
            }
        }
        catch (Exception ex)
        {
            Log($"Movement update error: {ex.Message}");
        }
    }

    private void UpdateMovementState()
    {
        var walkThreshold = GetSettingValue<float>(OSCLeashSetting.WalkDeadzone);
        var runThreshold = GetSettingValue<float>(OSCLeashSetting.RunDeadzone);
        
        bool wasWalking = _isWalking;
        bool wasRunning = _isRunning;
        
        if (_isGrabbed)
        {
            _isWalking = _stretch > walkThreshold;
            _isRunning = _stretch > runThreshold;
        }
        else
        {
            _isWalking = false;
            _isRunning = false;
        }
        
        // Log significant state changes
        if (_isRunning && !wasRunning)
            LogDebug($"Started running (stretch: {_stretch:F2})");
        else if (!_isRunning && wasRunning)
            LogDebug("Stopped running");
        else if (_isWalking && !wasWalking)
            LogDebug($"Started walking (stretch: {_stretch:F2})");
        else if (!_isWalking && wasWalking)
            LogDebug("Stopped walking");
    }

    private (float x, float y, float z) CalculateMovement()
    {
        if (!_isGrabbed)
        {
            // Decay movement when not grabbed
            _smoothMoveX *= 0.7f;
            _smoothMoveZ *= 0.7f;
            return (Math.Abs(_smoothMoveX) < 0.01f ? 0 : _smoothMoveX, 0, Math.Abs(_smoothMoveZ) < 0.01f ? 0 : _smoothMoveZ);
        }

        // Calculate net movement
        float netX = _xPos - _xNeg;
        float netY = _yNeg - _yPos; // Inverted for VR
        float netZ = _zPos - _zNeg;

        // Apply strength multiplier
        float strength = _stretch * GetSettingValue<float>(OSCLeashSetting.StrengthMultiplier);
        
        // Apply vertical compensation
        float verticalStretch = Math.Abs(netY);
        float upDownDeadzone = GetSettingValue<float>(OSCLeashSetting.UpDownDeadzone);
        float compensation = GetSettingValue<float>(OSCLeashSetting.UpDownCompensation);
        
        if (verticalStretch >= upDownDeadzone && compensation > 0)
        {
            float compensationFactor = 1.0f - (verticalStretch * compensation * 0.5f);
            compensationFactor = Math.Max(0.1f, Math.Min(1.0f, compensationFactor));
            netX *= compensationFactor;
            netZ *= compensationFactor;
        }

        // Apply final multiplier and smoothing
        netX *= strength;
        netZ *= strength;
        
        _smoothMoveX = _smoothMoveX * 0.7f + netX * 0.3f;
        _smoothMoveZ = _smoothMoveZ * 0.7f + netZ * 0.3f;

        return (_smoothMoveX, netY, _smoothMoveZ);
    }

    private void ApplyMovement(Player player, (float x, float y, float z) movement)
    {
        if (!_isGrabbed)
        {
            player.StopRun();
            player.MoveVertical(0);
            player.MoveHorizontal(0);
            return;
        }

        // Apply running state
        if (_isRunning)
            player.Run();
        else
            player.StopRun();

        // Apply movement
        player.MoveVertical(movement.z);
        player.MoveHorizontal(movement.x);

        // Handle turning feature
        ApplyTurningFeature(player, movement);
    }

    private void ApplyTurningFeature(Player player, (float x, float y, float z) movement)
    {
        if (GetSettingValue<bool>(OSCLeashSetting.TurningEnabled) && 
            _stretch > GetSettingValue<float>(OSCLeashSetting.TurningDeadzone))
        {
            float turnValue = CalculateTurning(movement.x, movement.z);
            player.LookHorizontal(turnValue);
        }
    }

    private float CalculateTurning(float moveX, float moveZ)
    {
        var multiplier = GetSettingValue<float>(OSCLeashSetting.TurningMultiplier);
        var goal = GetSettingValue<float>(OSCLeashSetting.TurningGoal) / 180f;
        var direction = GetSettingValue<LeashDirection>(OSCLeashSetting.LeashDirection);

        float output = direction switch
        {
            LeashDirection.North => moveZ < goal ? moveX * multiplier : 0,
            LeashDirection.South => -moveZ < goal ? -moveX * multiplier : 0,
            LeashDirection.East => moveX < goal ? moveZ * multiplier : 0,
            LeashDirection.West => -moveX < goal ? -moveZ * multiplier : 0,
            _ => 0
        };

        return Math.Max(-1f, Math.Min(1f, output));
    }
    #endregion

    #region VR Height Control
    // VR Height Control Logic:
    // 1. Reference height is set once during initialization from OpenVR baseline
    // 2. OVRAdvancedSettings controls the actual chaperone height through OpenVR's built-in system
    // 3. OSCLeash applies vertical movement as offsets from the baseline reference height
    // 4. When leash is grabbed: maintain current vertical offset, apply movement as additional offset
    // 5. When falling (gravity enabled): return to offset 0 (baseline reference height)
    
    private void UpdateVR()
    {
        if (!_vrInitialized) return;

        try
        {
            var setup = OpenVR.ChaperoneSetup;
            if (setup == null) return;

            var gravityEnabled = GetSettingValue<bool>(OSCLeashSetting.GrabBasedGravity);
            var verticalDeadzone = GetSettingValue<float>(OSCLeashSetting.VerticalMovementDeadzone);
            var angleThreshold = GetSettingValue<float>(OSCLeashSetting.VerticalHorizontalCompensation);
            var verticalMultiplier = GetSettingValue<float>(OSCLeashSetting.VerticalMovementMultiplier);
            var smoothing = GetSettingValue<float>(OSCLeashSetting.VerticalMovementSmoothing);

            bool justGrabbed = _isGrabbed && !_wasGrabbed;
            bool justReleased = !_isGrabbed && _wasGrabbed;
            _wasGrabbed = _isGrabbed;

            bool shouldUpdateChaperone = false;
            bool isGravityActive = false;

            // Check if gravity would be active
            if (!_isGrabbed && gravityEnabled)
            {
                isGravityActive = Math.Abs(_currentVerticalOffset) > OpenVRConfig.STOP_THRESHOLD || 
                                Math.Abs(_vrVelocity) > OpenVRConfig.VELOCITY_STOP_THRESHOLD;
            }

            // Handle grab state changes
            if (justGrabbed)
            {
                // CRITICAL: Update reference height to current position (handles space drag)
                UpdateReferenceHeight();
                LogDebug($"Leash grabbed - updated reference height to current position: {_referenceHeight:F3}");
            }
            else if (justReleased)
            {
                LogDebug($"Leash released - stopping vertical movement (offset: {_currentVerticalOffset:F3})");
                _vrVelocity = 0f;
            }

            if (_isGrabbed)
            {
                shouldUpdateChaperone = HandleGrabbedVerticalMovement(verticalDeadzone, angleThreshold, verticalMultiplier, smoothing);
            }
            else if (gravityEnabled)
            {
                shouldUpdateChaperone = ApplyGravity();
            }

            // Apply chaperone changes only when needed using working implementation method
            if (shouldUpdateChaperone)
            {
                ApplyOffset(_currentVerticalOffset);
            }
        }
        catch (Exception ex)
        {
            Log($"VR update error: {ex.Message}");
        }
    }

    private bool HandleGrabbedVerticalMovement(float verticalDeadzone, float angleThreshold, float verticalMultiplier, float smoothing)
    {
        float netY = _yNeg - _yPos;
        float horizontalMag = (float)Math.Sqrt((_xPos - _xNeg) * (_xPos - _xNeg) + (_zPos - _zNeg) * (_zPos - _zNeg));
        float pullAngle = (float)(Math.Atan2(Math.Abs(netY), horizontalMag) * 180.0 / Math.PI);

        if (pullAngle >= angleThreshold && Math.Abs(netY) >= verticalDeadzone)
        {
            float targetVelocity = netY * verticalMultiplier;
            _vrVelocity = _vrVelocity * smoothing + targetVelocity * (1 - smoothing);
            
            float newOffset = _currentVerticalOffset + _vrVelocity * 0.016f;
            _currentVerticalOffset = newOffset;
            _vrHeight = _referenceHeight + _currentVerticalOffset;
            
            return true; // Should update chaperone
        }
        
        return false;
    }

    private bool ApplyGravity()
    {
        var gravityStrength = GetSettingValue<float>(OSCLeashSetting.GravityStrength);
        var terminalVelocity = GetSettingValue<float>(OSCLeashSetting.TerminalVelocity);
        
        // Check if we're at rest at reference height (offset 0)
        if (Math.Abs(_currentVerticalOffset) < OpenVRConfig.STOP_THRESHOLD && 
            Math.Abs(_vrVelocity) < OpenVRConfig.VELOCITY_STOP_THRESHOLD)
        {
            if (_currentVerticalOffset != 0f || _vrVelocity != 0f)
            {
                LogDebug("Gravity simulation complete - returned to reference height");
                _vrVelocity = 0f;
                _currentVerticalOffset = 0f;
                _vrHeight = _referenceHeight;
                return true; // Final update to ensure we're exactly at reference
            }
            // At rest at reference height - no chaperone updates needed
            return false;
        }

        // Apply gravity towards reference height (offset 0)
        float gravityDirection = -Math.Sign(_currentVerticalOffset);
        _vrVelocity += gravityStrength * gravityDirection * 0.016f;
        
        // Clamp velocity to terminal velocity
        _vrVelocity = gravityDirection > 0
            ? Math.Min(_vrVelocity, terminalVelocity)
            : Math.Max(_vrVelocity, -terminalVelocity);

        float newOffset = _currentVerticalOffset + _vrVelocity * 0.016f;

        // Check if we've crossed reference height
        if ((_currentVerticalOffset > 0f && newOffset < 0f) ||
            (_currentVerticalOffset < 0f && newOffset > 0f))
        {
            LogDebug($"Gravity crossed reference height - stopping at reference (was {_currentVerticalOffset:F3})");
            _vrVelocity = 0f;
            _currentVerticalOffset = 0f;
            _vrHeight = _referenceHeight;
            return true;
        }

        _currentVerticalOffset = newOffset;
        _vrHeight = _referenceHeight + _currentVerticalOffset;
        return true;
    }

    // REMOVED: ApplyChaperoneHeight() - replaced with ApplyOffset() method from working implementation
    #endregion

    #region Parameter Handling
    protected override void OnRegisteredParameterReceived(RegisteredParameter parameter)
    {
        try
        {
            var paramType = (OSCLeashParameter)parameter.Lookup;
            bool wasGrabbed = _isGrabbed;
            
            switch (paramType)
            {
                case OSCLeashParameter.IsGrabbed:
                    _isGrabbed = parameter.GetValue<bool>();
                    if (_isGrabbed != wasGrabbed)
                        LogDebug($"Leash {(_isGrabbed ? "grabbed" : "released")}");
                    break;
                case OSCLeashParameter.Stretch:
                    _stretch = parameter.GetValue<float>();
                    break;
                case OSCLeashParameter.ZPositive:
                    _zPos = parameter.GetValue<float>();
                    break;
                case OSCLeashParameter.ZNegative:
                    _zNeg = parameter.GetValue<float>();
                    break;
                case OSCLeashParameter.XPositive:
                    _xPos = parameter.GetValue<float>();
                    break;
                case OSCLeashParameter.XNegative:
                    _xNeg = parameter.GetValue<float>();
                    break;
                case OSCLeashParameter.YPositive:
                    _yPos = parameter.GetValue<float>();
                    break;
                case OSCLeashParameter.YNegative:
                    _yNeg = parameter.GetValue<float>();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Parameter processing error: {ex.Message}");
        }
    }
    #endregion
} 