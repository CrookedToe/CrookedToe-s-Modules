using System;
using Valve.VR;
using VRCOSC.App.SDK.OVR;
using VRCOSC.App.SDK.Modules;

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
    public const float STOP_THRESHOLD = 0.005f;   // Reduced for more precise stopping
    public const float VELOCITY_STOP_THRESHOLD = 0.05f; // Reduced for more precise stopping
}

/// <summary>
/// Manages OpenVR integration and vertical movement calculations
/// </summary>
public sealed class OpenVRService : IDisposable
{
    private readonly Action<string> logCallback;
    private bool isInitialized;
    private bool isEnabled;
    private float currentVerticalOffset;
    private float verticalVelocity;
    private float referencePosition;  // Store the position when we grab
    private HmdMatrix34_t standingZeroPose;  // Removed readonly as it needs to be modified
    private bool isDisposed;
    private OVRClient? ovrClient;
    private ETrackingUniverseOrigin originalTrackingOrigin;

    public OpenVRService(Action<string> logCallback)
    {
        this.logCallback = logCallback ?? throw new ArgumentNullException(nameof(logCallback));
        standingZeroPose = new HmdMatrix34_t();
    }

    /// <summary>
    /// Initializes the OpenVR service with the provided client
    /// </summary>
    public bool Initialize(OVRClient? client)
    {
        ThrowIfDisposed();
        
        if (!isEnabled || client?.HasInitialised != true)
        {
            if (!isEnabled)
                return false;
                
            logCallback("OpenVR is not initialized. Vertical movement will be disabled until OpenVR is available.");
            return false;
        }

        ovrClient = client;  // Restored assignment

        if (isInitialized)
            return true;

        try
        {
            var compositor = OpenVR.Compositor;
            var chaperoneSetup = OpenVR.ChaperoneSetup;
            
            if (compositor == null || chaperoneSetup == null)
            {
                logCallback("Failed to initialize OpenVR: Required components not available");
                return false;
            }

            originalTrackingOrigin = compositor.GetTrackingSpace();
            isInitialized = true;
            UpdateOffset();
            return true;
        }
        catch (Exception ex)
        {
            logCallback($"Failed to initialize OpenVR: {ex.Message}");
            return false;
        }
    }

    public void Enable()
    {
        isEnabled = true;
    }

    public void Disable()
    {
        isEnabled = false;
        IsGrabbed = false;
        verticalVelocity = 0;
    }

    /// <summary>
    /// Resets the service state without releasing resources
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        
        ResetOpenVRState();
        ResetInternalState();
    }

    private void ResetOpenVRState()
    {
        // Don't modify OpenVR state when resetting
        isInitialized = false;
        verticalVelocity = 0;
        IsGrabbed = false;
    }

    private void ResetInternalState()
    {
        isInitialized = false;
        verticalVelocity = 0;
        currentVerticalOffset = 0;
        referencePosition = 0;
        IsGrabbed = false;
    }

    private void UpdateOffset()
    {
        if (ovrClient?.HasInitialised == true)
        {
            var chaperoneSetup = OpenVR.ChaperoneSetup;
            if (chaperoneSetup != null)
            {
                chaperoneSetup.GetWorkingStandingZeroPoseToRawTrackingPose(ref standingZeroPose);
                currentVerticalOffset = standingZeroPose.m7;
            }
        }
    }

    /// <summary>
    /// Applies a vertical offset to the player's position
    /// </summary>
    public void ApplyOffset(float newOffset)
    {
        ThrowIfDisposed();
        
        // Allow position changes when grabbed OR when we have active velocity (for return movement)
        if (!isEnabled || !isInitialized || ovrClient?.HasInitialised != true || 
            (!IsGrabbed && Math.Abs(verticalVelocity) < OpenVRConfig.VELOCITY_STOP_THRESHOLD))
            return;

        try
        {
            var chaperoneSetup = OpenVR.ChaperoneSetup;
            if (chaperoneSetup == null)
                return;

            currentVerticalOffset = newOffset;
            standingZeroPose.m7 = newOffset;

            chaperoneSetup.SetWorkingStandingZeroPoseToRawTrackingPose(ref standingZeroPose);
            chaperoneSetup.CommitWorkingCopy(EChaperoneConfigFile.Live);
        }
        catch (Exception ex)
        {
            logCallback($"Error applying offset: {ex.Message}");
            IsGrabbed = false;
            verticalVelocity = 0;
        }
    }

    /// <summary>
    /// Updates vertical movement based on the current state and parameters
    /// </summary>
    public float UpdateVerticalMovement(float deltaTime, MovementState state, float verticalDeadzone,
        float angleThreshold, float verticalMultiplier, float smoothing)
    {
        ThrowIfDisposed();
        
        if (!isInitialized || !isEnabled)
            return currentVerticalOffset;

        // Always update our current position from OpenVR
        UpdateOffset();

        // Handle grab state change
        if (state.IsGrabbed != IsGrabbed)
        {
            if (state.IsGrabbed)
            {
                // Just grabbed - store current position as reference
                referencePosition = currentVerticalOffset;
                verticalVelocity = 0f;
            }
            IsGrabbed = state.IsGrabbed;
        }

        // If not grabbed and close to reference with no significant velocity, remain passive
        float distanceToReference = Math.Abs(currentVerticalOffset - referencePosition);
        if (!IsGrabbed && 
            Math.Abs(verticalVelocity) < OpenVRConfig.VELOCITY_STOP_THRESHOLD &&
            distanceToReference < OpenVRConfig.STOP_THRESHOLD)
        {
            return currentVerticalOffset;
        }

        // If not grabbed but have velocity or not at reference position, handle return
        if (!IsGrabbed)
        {
            return ApplyGravity(deltaTime);
        }

        // Handle active movement when grabbed
        float stepSize = CalculateStepSize(state, verticalDeadzone, angleThreshold);
        if (Math.Abs(stepSize) < OpenVRConfig.MOVEMENT_DEADZONE)
        {
            verticalVelocity = 0f;
            return currentVerticalOffset;
        }

        return ApplyMovement(deltaTime, stepSize, verticalMultiplier, smoothing);
    }

    private float CalculateStepSize(MovementState state, float verticalDeadzone, float angleThreshold)
    {
        Vector3 movement = state.GetMovementVector();
        
        // Calculate horizontal magnitude for angle check
        float horizontalX = movement.X;
        float horizontalZ = movement.Z;
        float horizontalMagnitude = MathF.Sqrt(horizontalX * horizontalX + horizontalZ * horizontalZ);

        // Convert the angle threshold from degrees to the actual angle check
        float pullAngle = MathF.Atan2(MathF.Abs(movement.Y), horizontalMagnitude) * (180f / MathF.PI);
        if (pullAngle < angleThreshold)
            return 0f;

        return movement.Y;
    }

    private float ApplyGravity(float deltaTime)
    {
        float distanceToReference = currentVerticalOffset - referencePosition;
        
        // Only stop if we're both close to reference AND moving slowly
        if (Math.Abs(distanceToReference) < OpenVRConfig.STOP_THRESHOLD && 
            Math.Abs(verticalVelocity) < OpenVRConfig.VELOCITY_STOP_THRESHOLD)
        {
            verticalVelocity = 0f;
            IsGrabbed = false;
            ApplyOffset(referencePosition);  // Ensure we land exactly at reference
            return referencePosition;
        }

        // Calculate gravity force based on distance
        float gravityDirection = -Math.Sign(distanceToReference);
        float distanceMultiplier = Math.Min(Math.Abs(distanceToReference), 1f) + 1f; // Scale force with distance, capped at 2x
        
        // Apply gravity with distance scaling
        verticalVelocity += OpenVRConfig.GRAVITY * gravityDirection * distanceMultiplier * deltaTime;
        
        // Apply additional damping when moving away from reference
        if (Math.Sign(verticalVelocity) != gravityDirection)
        {
            verticalVelocity *= 0.9f; // Dampen velocity when moving away from target
        }
        
        // Clamp velocity with scaling based on distance
        float maxVelocity = OpenVRConfig.TERMINAL_VELOCITY * distanceMultiplier;
        if (gravityDirection > 0)
        {
            verticalVelocity = Math.Min(verticalVelocity, maxVelocity);
        }
        else
        {
            verticalVelocity = Math.Max(verticalVelocity, -maxVelocity);
        }

        float newOffset = currentVerticalOffset + verticalVelocity * deltaTime;

        // If we've crossed reference position, snap to reference
        if ((currentVerticalOffset > referencePosition && newOffset < referencePosition) ||
            (currentVerticalOffset < referencePosition && newOffset > referencePosition))
        {
            verticalVelocity = 0f;
            IsGrabbed = false;
            ApplyOffset(referencePosition);
            return referencePosition;
        }

        ApplyOffset(newOffset);
        return newOffset;
    }

    private float ApplyMovement(float deltaTime, float stepSize, float verticalMultiplier, float smoothing)
    {
        // Calculate target velocity based on input
        float targetVelocity = stepSize * verticalMultiplier;
        
        // Smoothly interpolate to target velocity using the smoothing parameter
        verticalVelocity = verticalVelocity * smoothing + targetVelocity * (1f - smoothing);
        
        // Update position with velocity
        float newOffset = currentVerticalOffset + verticalVelocity * deltaTime;
        float clampedOffset = Math.Clamp(newOffset, -OpenVRConfig.MAX_VERTICAL_OFFSET, OpenVRConfig.MAX_VERTICAL_OFFSET);
        
        ApplyOffset(clampedOffset);
        return clampedOffset;
    }

    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(OpenVRService));
        }
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            isInitialized = false;
            ovrClient = null;
            isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }

    public bool IsInitialized => isInitialized;
    public float CurrentVerticalOffset => currentVerticalOffset;
    public bool IsGrabbed { get; set; }
} 