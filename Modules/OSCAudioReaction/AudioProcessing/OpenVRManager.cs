using System;
using Valve.VR;
using VRCOSC.App.SDK.OVR;
using VRCOSC.App.SDK.Modules;

namespace CrookedToe.Modules.OSCAudioReaction.AudioProcessing;

/// <summary>
/// Manages OpenVR integration for audio direction enhancement
/// </summary>
public class OpenVRManager : IDisposable
{
    private readonly Action<string> _logCallback;
    private OVRClient? _ovrClient;
    private bool _isInitialized;
    private bool _isEnabled;
    private bool _isDisposed;
    
    // Head orientation tracking
    private float _yaw;   // Horizontal rotation (left/right)
    private float _pitch; // Vertical rotation (up/down)
    private float _roll;  // Roll rotation (tilt)
    
    // Smoothing parameters
    private float _smoothingFactor = 0.7f;
    private float _smoothedYaw;
    private float _smoothedPitch;
    private float _smoothedRoll;

    public OpenVRManager(Action<string> logCallback)
    {
        _logCallback = logCallback ?? throw new ArgumentNullException(nameof(logCallback));
    }

    public bool Initialize(OVRClient? client)
    {
        ThrowIfDisposed();
        
        if (!_isEnabled || client?.HasInitialised != true)
        {
            Reset();
            return false;
        }

        if (_isInitialized)
            return true;

        try
        {
            var compositor = OpenVR.Compositor;
            
            if (compositor == null)
            {
                Reset();
                return false;
            }

            _ovrClient = client;
            _isInitialized = true;
            _logCallback("OpenVR initialized for audio direction enhancement");
            return true;
        }
        catch (Exception ex)
        {
            _logCallback($"Failed to initialize OpenVR: {ex.Message}");
            Reset();
            return false;
        }
    }

    public void UpdateHeadOrientation()
    {
        ThrowIfDisposed();
        
        if (!_isInitialized || !_isEnabled || _ovrClient?.HasInitialised != true)
            return;

        try
        {
            var compositor = OpenVR.Compositor;
            if (compositor == null)
                return;

            // Get head pose
            TrackedDevicePose_t[] poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
            compositor.GetLastPoses(poses, null);

            // Find the HMD (head) device
            int hmdIndex = (int)OpenVR.k_unTrackedDeviceIndex_Hmd;
            
            if (poses[hmdIndex].bPoseIsValid)
            {
                // Convert SteamVR matrix to orientation angles
                var pose = poses[hmdIndex].mDeviceToAbsoluteTracking;
                ExtractOrientationFromMatrix(ref pose, out _yaw, out _pitch, out _roll);
                
                // Apply smoothing
                _smoothedYaw = _smoothedYaw * _smoothingFactor + _yaw * (1 - _smoothingFactor);
                _smoothedPitch = _smoothedPitch * _smoothingFactor + _pitch * (1 - _smoothingFactor);
                _smoothedRoll = _smoothedRoll * _smoothingFactor + _roll * (1 - _smoothingFactor);
            }
        }
        catch (Exception ex)
        {
            _logCallback($"Error updating head orientation: {ex.Message}");
        }
    }

    private void ExtractOrientationFromMatrix(ref HmdMatrix34_t matrix, out float yaw, out float pitch, out float roll)
    {
        // Extract rotation angles from the rotation matrix
        // SteamVR uses a right-handed coordinate system where:
        // x is right, y is up, z is backward (away from the user)
        
        // First convert the 3x4 matrix to a 3x3 rotation matrix
        float m11 = matrix.m0, m12 = matrix.m4, m13 = matrix.m8;
        float m21 = matrix.m1, m22 = matrix.m5, m23 = matrix.m9;
        float m31 = matrix.m2, m32 = matrix.m6, m33 = matrix.m10;

        // Extract Euler angles
        if (m31 > 0.99999f) 
        {
            // Singularity at north pole
            yaw = (float)Math.Atan2(m13, m33);
            pitch = (float)Math.PI / 2;
            roll = 0;
        } 
        else if (m31 < -0.99999f) 
        {
            // Singularity at south pole
            yaw = (float)Math.Atan2(m13, m33);
            pitch = -(float)Math.PI / 2;
            roll = 0;
        } 
        else 
        {
            yaw = (float)Math.Atan2(-m13, m11);
            pitch = (float)Math.Asin(m12);
            roll = (float)Math.Atan2(-m32, m22);
        }

        // Convert to degrees for easier use
        yaw = yaw * 180.0f / (float)Math.PI;
        pitch = pitch * 180.0f / (float)Math.PI;
        roll = roll * 180.0f / (float)Math.PI;
    }

    public float GetNormalizedAudioDirection(float rawDirection)
    {
        if (!_isInitialized || !_isEnabled)
            return rawDirection;

        // Convert raw direction (0-1) to angle (-90 to 90 degrees)
        float rawAngle = (rawDirection - 0.5f) * 180f;
        
        // Adjust by head yaw to get direction relative to where user is looking
        float adjustedAngle = rawAngle - _smoothedYaw;
        
        // Normalize back to 0-1 range
        float adjustedDirection = (adjustedAngle / 180f) + 0.5f;
        return Math.Clamp(adjustedDirection, 0f, 1f);
    }

    public void SetSmoothingFactor(float factor)
    {
        _smoothingFactor = Math.Clamp(factor, 0f, 0.95f);
    }

    public void Enable() => _isEnabled = true;

    public void Disable()
    {
        _isEnabled = false;
        Reset();
    }

    private void Reset()
    {
        _isInitialized = false;
        _yaw = 0;
        _pitch = 0;
        _roll = 0;
        _smoothedYaw = 0;
        _smoothedPitch = 0;
        _smoothedRoll = 0;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(OpenVRManager));
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        Reset();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public bool IsInitialized => _isInitialized;
    public float Yaw => _smoothedYaw;
    public float Pitch => _smoothedPitch;
    public float Roll => _smoothedRoll;
} 