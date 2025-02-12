using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;
using Valve.VR;
using System;
using System.Threading.Tasks;
using CrookedToe.Modules.OSCLeash;

namespace CrookedToe.Modules.OSCLeash;

[ModuleTitle("OSC Leash")]
[ModuleDescription("Allows for controlling avatar movement with parameters, including vertical movement via OpenVR")]
[ModuleType(ModuleType.Generic)]
[ModulePrefab("OSCLeash", "https://github.com/CrookedToe/OSCLeash/tree/main/Unity")]
[ModuleInfo("https://github.com/CrookedToe/CrookedToe-s-Modules")]
public class OSCLeashModule : Module
{
    // Constants for movement thresholds and update timing
    private const float UPDATE_THRESHOLD = 0.025f;  // Minimum movement delta before applying updates
    private const int FRAME_SKIP = 1;  // Skip frames to reduce update frequency (30Hz target)
    
    // Core components for movement and VR handling
    private readonly MovementState _state = new();
    private readonly OpenVRService _ovrService;
    
    // State tracking for movement updates
    private int _frameCounter;  // Tracks frames for update timing
    private Vector3 _lastMovement;  // Previous movement vector for change detection
    private bool _needsMovementUpdate;  // Flag indicating parameter changes
    private bool _hasShownLeashWarning;  // Track if we've shown the leash warning
    
    public OSCLeashModule()
    {
        _ovrService = new OpenVRService(Log);
    }
    
    protected override void OnPreLoad()
    {
        // Basic Movement Settings
        CreateSlider(OSCLeashSetting.WalkDeadzone, "Walk Deadzone", 
            "Minimum stretch (0-1) required to start walking. Below this: no movement, above this: start walking. " +
            "Lower values make it more sensitive to small movements.", 
            0.15f, 0.0f, 1.0f);
            
        CreateSlider(OSCLeashSetting.RunDeadzone, "Run Deadzone", 
            "Stretch threshold (0-1) for running. Below WalkDeadzone: no movement, between WalkDeadzone and this: walking, " +
            "above this: running. Higher values require more stretch to start running.", 
            0.70f, 0.0f, 1.0f);
            
        CreateSlider(OSCLeashSetting.StrengthMultiplier, "Movement Strength", 
            "Overall speed multiplier (0.1-5.0) affecting all movement. " +
            "Higher values make both walking and running faster.", 
            1.2f, 0.1f, 5.0f);
            
        CreateSlider(OSCLeashSetting.UpDownDeadzone, "Up/Down Deadzone",
            "Vertical movement threshold (0-1) that stops horizontal movement. " +
            "When vertical pulling exceeds this, horizontal movement stops completely.",
            0.5f, 0.0f, 1.0f);
            
        CreateSlider(OSCLeashSetting.UpDownCompensation, "Up/Down Compensation",
            "How much vertical pulling affects horizontal speed (0-1). At 0: no effect, " +
            "at 1: strong vertical pulling completely stops horizontal movement.",
            0.5f, 0.0f, 1.0f);
            
        CreateDropdown(OSCLeashSetting.LeashDirection, "Leash Direction", 
            "Which direction the leash faces, affecting turning controls:\n" +
            "- North (default): Pull back + left/right to turn\n" +
            "- South: Pull forward + left/right to turn\n" +
            "- East: Pull left + forward/back to turn\n" +
            "- West: Pull right + forward/back to turn", 
            LeashDirection.North);

        // Turning Controls
        CreateToggle(OSCLeashSetting.TurningEnabled, "Enable Turning", 
            "Enables avatar rotation control. When enabled, pulling in specific directions (based on LeashDirection) " +
            "will rotate your avatar.", 
            false);
            
        CreateSlider(OSCLeashSetting.TurningMultiplier, "Turn Speed", 
            "Rotation speed multiplier (0.1-2.0). Higher values make the avatar turn faster " +
            "when pulling in turning directions.", 
            0.80f, 0.1f, 2.0f);
            
        CreateSlider(OSCLeashSetting.TurningDeadzone, "Turn Deadzone", 
            "Minimum stretch (0-1) required before turning starts. Must exceed this before any rotation occurs. " +
            "Higher values require stronger pulling to start turning.", 
            0.15f, 0.0f, 1.0f);
            
        CreateSlider(OSCLeashSetting.TurningGoal, "Maximum Turn Angle", 
            "Maximum rotation angle in degrees (0-180). Limits how far the avatar can turn when pulling. " +
            "Higher values allow more rotation before stopping.", 
            90f, 0.0f, 180.0f);

        // Vertical Movement Settings
        CreateToggle(OSCLeashSetting.VerticalMovementEnabled, "Enable Vertical Movement", 
            "Enables OpenVR height control. When enabled, pulling up/down changes your real VR height. " +
            "Requires SteamVR to be running.", 
            false);
            
        CreateSlider(OSCLeashSetting.VerticalMovementMultiplier, "Vertical Speed", 
            "Vertical movement speed multiplier (0.1-5.0). Higher values make height changes faster " +
            "when pulling up or down.", 
            1.0f, 0.1f, 5.0f);
            
        CreateSlider(OSCLeashSetting.VerticalMovementDeadzone, "Vertical Deadzone", 
            "Minimum vertical pull (0-1) needed for height changes. Must exceed this before moving up/down. " +
            "Higher values require stronger vertical pulling.", 
            0.15f, 0.0f, 1.0f, 0.05f);
            
        CreateSlider(OSCLeashSetting.VerticalMovementSmoothing, "Vertical Smoothing", 
            "Smoothing factor for height changes (0-1). At 0: immediate changes but may be jittery, " +
            "at 1: very smooth but more delayed.", 
            0.95f, 0.0f, 1.0f);
            
        CreateSlider(OSCLeashSetting.VerticalHorizontalCompensation, "Vertical Angle", 
            "Required angle from horizontal for height changes (15-75°). Lower angles (15°) make vertical movement " +
            "easier to trigger, higher angles (75°) require more vertical pulling.", 
            45f, 15f, 75f);

        // Create logical groups
        CreateGroup("Basic Movement", 
            OSCLeashSetting.WalkDeadzone,
            OSCLeashSetting.RunDeadzone,
            OSCLeashSetting.StrengthMultiplier,
            OSCLeashSetting.UpDownDeadzone,
            OSCLeashSetting.UpDownCompensation,
            OSCLeashSetting.LeashDirection);

        CreateGroup("Turning Controls",
            OSCLeashSetting.TurningEnabled,
            OSCLeashSetting.TurningMultiplier,
            OSCLeashSetting.TurningDeadzone,
            OSCLeashSetting.TurningGoal);

        CreateGroup("Vertical Movement",
            OSCLeashSetting.VerticalMovementEnabled,
            OSCLeashSetting.VerticalMovementMultiplier,
            OSCLeashSetting.VerticalMovementDeadzone,
            OSCLeashSetting.VerticalMovementSmoothing,
            OSCLeashSetting.VerticalHorizontalCompensation);

        // Register parameters
        RegisterParameter<bool>(OSCLeashParameter.IsGrabbed, "Leash_IsGrabbed", 
            ParameterMode.Read, "Leash Grabbed", "Whether the leash is currently being held");
            
        RegisterParameter<float>(OSCLeashParameter.Stretch, "Leash_Stretch", 
            ParameterMode.Read, "Leash Stretch", "How far the leash is stretched");
            
        RegisterParameter<float>(OSCLeashParameter.ZPositive, "Leash_Z+", 
            ParameterMode.Read, "Forward Pull", "Forward movement value", false);
            
        RegisterParameter<float>(OSCLeashParameter.ZNegative, "Leash_Z-", 
            ParameterMode.Read, "Backward Pull", "Backward movement value", false);
            
        RegisterParameter<float>(OSCLeashParameter.XPositive, "Leash_X+", 
            ParameterMode.Read, "Right Pull", "Rightward movement value", false);
            
        RegisterParameter<float>(OSCLeashParameter.XNegative, "Leash_X-", 
            ParameterMode.Read, "Left Pull", "Leftward movement value", false);
            
        RegisterParameter<float>(OSCLeashParameter.YPositive, "Leash_Y+", 
            ParameterMode.Read, "Upward Pull", "Upward movement value", false);
            
        RegisterParameter<float>(OSCLeashParameter.YNegative, "Leash_Y-", 
            ParameterMode.Read, "Downward Pull", "Downward movement value", false);
    }
    
    protected override Task<bool> OnModuleStart()
    {
        var verticalEnabled = GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled);
        if (verticalEnabled)
        {
            _ovrService.Enable();
            _ovrService.Initialize(GetOVRClient());
        }
        else
        {
            _ovrService.Disable();
        }
        return Task.FromResult(true);
    }
    
    protected override Task OnModuleStop()
    {
        _ovrService.Disable();
        return Task.CompletedTask;
    }
    
    [ModuleUpdate(ModuleUpdateMode.Custom, true, 33)]
    private void UpdateMovement()
    {
        if (_frameCounter++ % (FRAME_SKIP + 1) != 0)
            return;
            
        var ovrClient = GetOVRClient();
        if (ovrClient == null)
            return;
            
        // Handle OpenVR state changes
        var verticalEnabled = GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled);
        if (verticalEnabled)
        {
            _ovrService.Enable();
            if (!_ovrService.IsInitialized && ovrClient.HasInitialised)
            {
                _ovrService.Initialize(ovrClient);
            }
        }
        else
        {
            _ovrService.Disable();
        }
        
        // Check if any leash parameters have been received
        if (!_hasShownLeashWarning && !_state.IsGrabbed && _state.Stretch == 0 && 
            _state.XPositive == 0 && _state.XNegative == 0 &&
            _state.YPositive == 0 && _state.YNegative == 0 &&
            _state.ZPositive == 0 && _state.ZNegative == 0)
        {
            Log("Warning: No leash parameters detected. Please verify your avatar has the OSCLeash parameters set up correctly.");
            _hasShownLeashWarning = true;
            return;
        }
        
        // Reset warning flag if we detect parameters
        if (_state.IsGrabbed || _state.Stretch != 0 || 
            _state.XPositive != 0 || _state.XNegative != 0 ||
            _state.YPositive != 0 || _state.YNegative != 0 ||
            _state.ZPositive != 0 || _state.ZNegative != 0)
        {
            _hasShownLeashWarning = false;
        }
        
        var deltaTime = 1f / 30f;
        UpdateVerticalMovementIfNeeded(deltaTime);
        UpdateHorizontalMovementIfNeeded(deltaTime);
    }
    
    private void UpdateVerticalMovementIfNeeded(float deltaTime)
    {
        bool isEnabled = GetSettingValue<bool>(OSCLeashSetting.VerticalMovementEnabled);
        if (!isEnabled || !_ovrService.IsInitialized)
            return;
            
        // Sync grabbed state with OpenVRService
        _ovrService.IsGrabbed = _state.IsGrabbed;
        
        try
        {
            float verticalDeadzone = GetSettingValue<float>(OSCLeashSetting.VerticalMovementDeadzone);
            float angleThreshold = GetSettingValue<float>(OSCLeashSetting.VerticalHorizontalCompensation);
            float verticalMultiplier = GetSettingValue<float>(OSCLeashSetting.VerticalMovementMultiplier);
            float smoothing = GetSettingValue<float>(OSCLeashSetting.VerticalMovementSmoothing);
            
            float newOffset = _ovrService.UpdateVerticalMovement(deltaTime, _state, verticalDeadzone, 
                angleThreshold, verticalMultiplier, smoothing);
                
            _ovrService.ApplyOffset(newOffset);
        }
        catch (Exception ex)
        {
            Log($"Error updating vertical movement: {ex.Message}");
        }
    }
    
    private void UpdateHorizontalMovementIfNeeded(float deltaTime)
    {
        var player = GetPlayer();
        if (player == null)
            return;
            
        var newMovement = CalculateMovement();
        
        // Always process when not grabbed to ensure proper reset
        if (!_state.IsGrabbed)
        {
            ResetMovement(player);
            _lastMovement = Vector3.Zero;
            _needsMovementUpdate = false;
            return;
        }
        
        // Only apply threshold check when grabbed
        if (!_needsMovementUpdate && _lastMovement.DistanceTo(newMovement) < UPDATE_THRESHOLD)
        {
            return;
        }
        
        ApplyMovement(player, newMovement);
        _lastMovement = newMovement;
        _needsMovementUpdate = false;
    }
    
    private Vector3 CalculateMovement()
    {
        if (!_state.IsGrabbed)
            return Vector3.Zero;
            
        float walkDeadzone = GetSettingValue<float>(OSCLeashSetting.WalkDeadzone);
        float runDeadzone = GetSettingValue<float>(OSCLeashSetting.RunDeadzone);
        float strengthMultiplier = GetSettingValue<float>(OSCLeashSetting.StrengthMultiplier);
        float upDownDeadzone = GetSettingValue<float>(OSCLeashSetting.UpDownDeadzone);
        float upDownCompensation = GetSettingValue<float>(OSCLeashSetting.UpDownCompensation);
        
        _state.UpdateState(walkDeadzone, runDeadzone);
        return _state.CalculateMovement(strengthMultiplier, upDownDeadzone, upDownCompensation);
    }
    
    private void ResetMovement(Player player)
    {
        player.StopRun();
        player.MoveVertical(0);
        player.MoveHorizontal(0);
        player.LookHorizontal(0);
        _state.Reset();
    }
    
    private void ApplyMovement(Player player, Vector3 movement)
    {
        if (!_state.IsGrabbed || movement == Vector3.Zero)
        {
            ResetMovement(player);
            return;
        }
        
        // Apply running state based on current state
        if (_state.CurrentState == MovementStateType.Running)
        {
            player.Run();
        }
        else
        {
            player.StopRun();
        }
        
        // Apply movement
        player.MoveVertical(movement.Z);
        player.MoveHorizontal(movement.X);
        
        // Apply turning if enabled and in a movement state
        if (_state.CurrentState != MovementStateType.Idle)
        {
            UpdateTurning(player, movement);
        }
        else
        {
            player.LookHorizontal(0);
        }
    }
    
    private void UpdateTurning(Player player, Vector3 movement)
    {
        var turningEnabled = GetSettingValue<bool>(OSCLeashSetting.TurningEnabled);
        var turningDeadzone = GetSettingValue<float>(OSCLeashSetting.TurningDeadzone);
        
        if (!turningEnabled || _state.Stretch <= turningDeadzone)
        {
            player.LookHorizontal(0);
            return;
        }
        
        player.LookHorizontal(CalculateTurningOutput(movement));
    }
    
    private float CalculateTurningOutput(Vector3 movement)
    {
        var turningMultiplier = GetSettingValue<float>(OSCLeashSetting.TurningMultiplier);
        var turningGoal = Math.Max(0f, GetSettingValue<float>(OSCLeashSetting.TurningGoal)) / 180f;
        var direction = GetSettingValue<LeashDirection>(OSCLeashSetting.LeashDirection);
        float turningOutput = 0f;
        
        switch (direction)
        {
            case LeashDirection.North:
                if (movement.Z < turningGoal)
                {
                    turningOutput = movement.X * turningMultiplier;
                    turningOutput += (movement.X > 0) ? -movement.X : movement.X;
                }
                break;
            case LeashDirection.South:
                if (-movement.Z < turningGoal)
                {
                    turningOutput = -movement.X * turningMultiplier;
                    turningOutput += (movement.X > 0) ? -movement.X : movement.X;
                }
                break;
            case LeashDirection.East:
                if (movement.X < turningGoal)
                {
                    turningOutput = movement.Z * turningMultiplier;
                    turningOutput += (movement.Z > 0) ? -movement.Z : movement.Z;
                }
                break;
            case LeashDirection.West:
                if (-movement.X < turningGoal)
                {
                    turningOutput = -movement.Z * turningMultiplier;
                    turningOutput += (movement.Z > 0) ? -movement.Z : movement.Z;
                }
                break;
        }
        
        return Math.Clamp(turningOutput, -1f, 1f);
    }
    
    protected override void OnRegisteredParameterReceived(RegisteredParameter parameter)
    {
        _needsMovementUpdate = true;
        switch (parameter.Lookup)
        {
            case OSCLeashParameter.IsGrabbed:
                _state.IsGrabbed = parameter.GetValue<bool>();
                break;
            case OSCLeashParameter.Stretch:
                _state.Stretch = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.ZPositive:
                _state.ZPositive = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.ZNegative:
                _state.ZNegative = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.XPositive:
                _state.XPositive = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.XNegative:
                _state.XNegative = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.YPositive:
                _state.YPositive = parameter.GetValue<float>();
                break;
            case OSCLeashParameter.YNegative:
                _state.YNegative = parameter.GetValue<float>();
                break;
        }
    }
} 