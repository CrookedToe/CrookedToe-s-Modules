using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CrookedToe.Modules.OSCAudioReaction;

/// <summary>
/// Audio device manager for handling Windows audio output capture
/// </summary>
public sealed class SimpleAudioDeviceManager : IDisposable
{
    private readonly OSCAudioReactionModule _module;
    private readonly object _deviceLock = new();
    
    private MMDeviceEnumerator? _deviceEnumerator;
    private WasapiLoopbackCapture? _audioCapture;
    private MMDevice? _currentDevice;
    private bool _isInitialized;
    private bool _isCapturing;
    private bool _disposed;
    private DateTime _lastDeviceCheck = DateTime.Now;
    private int _deviceChangeCount = 0;

    public SimpleAudioDeviceManager(OSCAudioReactionModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _module.LogDebug("Audio device enumerator initialized");
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to initialize audio device manager: {ex.Message}");
            throw;
        }
    }

    #region Properties

    public bool IsInitialized => _isInitialized && !_disposed;

    public bool IsCapturing => _isCapturing && !_disposed && _audioCapture != null;

    public string? CurrentDeviceName
    {
        get
        {
            if (!IsInitialized || _currentDevice == null)
                return null;
                
            try
            {
                return _currentDevice.FriendlyName;
            }
            catch (Exception ex)
            {
                _module.LogDebug($"Failed to get device name: {ex.Message}");
                return "[Device Name Error]";
            }
        }
    }

    public WaveFormat? CurrentWaveFormat
    {
        get
        {
            if (!IsCapturing || _audioCapture == null)
                return null;
                
            try
            {
                return _audioCapture.WaveFormat;
            }
            catch (Exception ex)
            {
                _module.LogDebug($"Failed to get wave format: {ex.Message}");
                return null;
            }
        }
    }

    public WasapiLoopbackCapture? AudioCapture
    {
        get
        {
            if (_disposed)
                return null;
                
            return _audioCapture;
        }
    }

    #endregion

    #region Device Management

    public List<MMDevice> GetAvailableDevices()
    {
        if (_disposed || _deviceEnumerator == null)
            return new List<MMDevice>();

        try
        {
            var devices = _deviceEnumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();

            _module.LogDebug($"Found {devices.Count} active audio output devices");
            return devices;
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to enumerate audio devices: {ex.Message}");
            return new List<MMDevice>();
        }
    }

    public MMDevice? GetDefaultDevice()
    {
        if (_disposed || _deviceEnumerator == null)
            return null;

        try
        {
            var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            
            if (defaultDevice == null)
            {
                _module.Log("No default audio output device found");
                return null;
            }

            _module.LogDebug($"Default audio device: {defaultDevice.FriendlyName}");
            return defaultDevice;
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to get default audio device: {ex.Message}");
            return null;
        }
    }

    public MMDevice? GetDeviceById(string deviceId)
    {
        if (_disposed || string.IsNullOrEmpty(deviceId) || _deviceEnumerator == null)
            return null;

        try
        {
            var device = _deviceEnumerator.GetDevice(deviceId);
            
            if (device == null)
            {
                _module.Log($"Audio device not found: {deviceId}");
                return null;
            }

            _module.LogDebug($"Retrieved audio device: {device.FriendlyName}");
            return device;
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to get audio device by ID '{deviceId}': {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Audio Capture

    public async Task<bool> InitializeDefaultDevice()
    {
        if (_disposed)
            return false;
        
        var defaultDevice = GetDefaultDevice();
        if (defaultDevice == null)
        {
            _module.Log("Cannot initialize: no default audio output device available");
            return false;
        }

        return await InitializeDevice(defaultDevice);
    }

    public async Task<bool> InitializeDeviceById(string deviceId)
    {
        if (_disposed)
            return false;
        
        var device = GetDeviceById(deviceId);
        if (device == null)
        {
            _module.Log($"Cannot initialize: audio device not found: {deviceId}");
            return false;
        }

        return await InitializeDevice(device);
    }

    public async Task<bool> InitializeDevice(MMDevice device)
    {
        if (device == null)
            throw new ArgumentNullException(nameof(device));
            
        if (_disposed)
            return false;

        lock (_deviceLock)
        {
            try
            {
                var deviceName = device.FriendlyName;
                _module.LogDebug($"Initializing audio capture for device: {deviceName}");

                DisposeAudioCapture();

                _audioCapture = new WasapiLoopbackCapture(device);
                _currentDevice = device;
                _isInitialized = true;
                _deviceChangeCount++;

                _module.Log($"Audio capture initialized: {deviceName}");
                LogDeviceDetails(device);
                
                return true;
            }
            catch (Exception ex)
            {
                _module.Log($"Failed to initialize audio capture for device '{device.FriendlyName}': {ex.Message}");
                _module.LogDebug($"Device initialization error details: {ex}");
                
                DisposeAudioCapture();
                _isInitialized = false;
                return false;
            }
        }
    }

    public void StartCapture()
    {
        if (_disposed || !_isInitialized || _audioCapture == null)
        {
            _module.LogDebug("Cannot start capture - device not initialized");
            return;
        }

        if (_isCapturing)
        {
            _module.LogDebug("Audio capture already active");
            return;
        }

        lock (_deviceLock)
        {
            try
            {
                var deviceName = CurrentDeviceName ?? "Unknown";
                _module.LogDebug($"Starting audio capture for device: {deviceName}");
                
                // Add a small delay to let the device settle after initialization
                System.Threading.Thread.Sleep(100);
                
                // Try starting capture with retry logic
                bool success = TryStartCaptureWithRetry();
                
                if (success)
                {
                    _isCapturing = true;
                    _module.Log($"Audio capture started: {deviceName}");
                    LogCaptureStatus();
                }
                else
                {
                    _module.Log($"Failed to start audio capture after all retry attempts: {deviceName}");
                    _isCapturing = false;
                }
            }
            catch (Exception ex)
            {
                _module.Log($"Failed to start audio capture: {ex.Message}");
                _module.LogDebug($"Capture start error details: {ex}");
                _isCapturing = false;
            }
        }
    }

    private bool TryStartCaptureWithRetry()
    {
        const int maxRetries = 3;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _module.LogDebug($"Audio capture start attempt {attempt}/{maxRetries}");
                
                // Ensure the capture is in a clean state by attempting to stop any existing recording
                try
                {
                    _audioCapture?.StopRecording();
                    System.Threading.Thread.Sleep(200); // Give it time to stop
                }
                catch
                {
                    // Ignore errors when stopping - it may not be recording
                }
                
                _audioCapture?.StartRecording();
                _module.LogDebug($"Audio capture started successfully on attempt {attempt}");
                return true;
            }
            catch (System.OutOfMemoryException ex)
            {
                _module.LogDebug($"WASAPI memory error on attempt {attempt}: {ex.Message}");
                
                if (attempt < maxRetries)
                {
                    // Try reinitializing the capture device
                    if (TryReinitializeCapture())
                    {
                        _module.LogDebug("Audio capture reinitialized, retrying...");
                        System.Threading.Thread.Sleep(500 * attempt); // Increasing delay
                        continue;
                    }
                }
                
                _module.Log($"WASAPI initialization failed - this often indicates audio driver issues or device conflicts");
                LogAudioSystemDiagnostics();
            }
            catch (Exception ex)
            {
                _module.LogDebug($"Audio capture start attempt {attempt} failed: {ex.Message}");
                
                if (attempt < maxRetries)
                {
                    System.Threading.Thread.Sleep(300 * attempt); // Increasing delay
                }
            }
        }
        
        return false;
    }

    private bool TryReinitializeCapture()
    {
        try
        {
            if (_currentDevice == null)
                return false;
                
            _module.LogDebug("Reinitializing audio capture device...");
            
            // Dispose current capture
            DisposeAudioCapture();
            
            // Small delay to let resources clean up
            System.Threading.Thread.Sleep(200);
            
            // Create new capture with the same device
            _audioCapture = new WasapiLoopbackCapture(_currentDevice);
            _module.LogDebug("Audio capture reinitialized successfully");
            
            return true;
        }
        catch (Exception ex)
        {
            _module.LogDebug($"Failed to reinitialize audio capture: {ex.Message}");
            return false;
        }
    }

    public void StopCapture()
    {
        if (!_isCapturing || _audioCapture == null)
            return;

        lock (_deviceLock)
        {
            try
            {
                var deviceName = CurrentDeviceName ?? "Unknown";
                _audioCapture.StopRecording();
                _isCapturing = false;
                _module.Log($"Audio capture stopped: {deviceName}");
            }
            catch (Exception ex)
            {
                _module.Log($"Error stopping audio capture: {ex.Message}");
                _module.LogDebug($"Capture stop error details: {ex}");
                _isCapturing = false;
            }
        }
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add
        {
            if (_audioCapture != null && value != null)
            {
                _audioCapture.DataAvailable += value;
                _module.LogDebug("Audio data handler registered");
            }
        }
        remove
        {
            if (_audioCapture != null && value != null)
            {
                _audioCapture.DataAvailable -= value;
                _module.LogDebug("Audio data handler unregistered");
            }
        }
    }

    #endregion

    #region Device Status and Monitoring

    public bool CheckDeviceHealth()
    {
        if (!IsInitialized || _currentDevice == null)
            return false;

        try
        {
            // Check if device is still available and active
            var deviceState = _currentDevice.State;
            var isHealthy = deviceState == DeviceState.Active;

            if (!isHealthy)
            {
                _module.Log($"Audio device unhealthy: {deviceState}");
            }

            return isHealthy;
        }
        catch (Exception ex)
        {
            _module.LogDebug($"Device health check failed: {ex.Message}");
            return false;
        }
    }

    public void LogDeviceStatus()
    {
        if (!IsInitialized)
        {
            _module.LogDebug("Device status: Not initialized");
            return;
        }

        var deviceName = CurrentDeviceName ?? "Unknown";
        var captureStatus = IsCapturing ? "Active" : "Inactive";
        var waveFormat = CurrentWaveFormat;
        var formatInfo = waveFormat != null 
            ? $"{waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels}ch"
            : "Unknown";

        _module.LogDebug($"Device status: {deviceName} | Capture: {captureStatus} | Format: {formatInfo} | Changes: {_deviceChangeCount}");
    }

    #endregion

    #region Helper Methods

    private void DisposeAudioCapture()
    {
        try
        {
            if (_audioCapture != null)
            {
                if (_isCapturing)
                {
                    _audioCapture.StopRecording();
                    _isCapturing = false;
                }
                
                _audioCapture.Dispose();
                _audioCapture = null;
                _module.LogDebug("Audio capture disposed");
            }
        }
        catch (Exception ex)
        {
            _module.LogDebug($"Error disposing audio capture: {ex.Message}");
        }
    }

    private void LogDeviceDetails(MMDevice device)
    {
        try
        {
            var deviceInfo = $"Device: {device.FriendlyName} | " +
                           $"ID: {device.ID} | " +
                           $"State: {device.State} | " +
                           $"DataFlow: {device.DataFlow}";
            
            _module.LogDebug(deviceInfo);
        }
        catch (Exception ex)
        {
            _module.LogDebug($"Failed to log device details: {ex.Message}");
        }
    }

    private void LogCaptureStatus()
    {
        try
        {
            var waveFormat = CurrentWaveFormat;
            if (waveFormat != null)
            {
                var formatInfo = $"Capture format: {waveFormat.SampleRate}Hz, " +
                               $"{waveFormat.BitsPerSample}-bit, " +
                               $"{waveFormat.Channels} channels, " +
                               $"Encoding: {waveFormat.Encoding}";
                
                _module.LogDebug(formatInfo);
            }
        }
        catch (Exception ex)
        {
            _module.LogDebug($"Failed to log capture status: {ex.Message}");
        }
    }

    private void LogAudioSystemDiagnostics()
    {
        try
        {
            _module.LogDebug("=== Audio System Diagnostics ===");
            
            var availableDevices = GetAvailableDevices();
            _module.LogDebug($"Total active audio devices: {availableDevices.Count}");
            
            foreach (var device in availableDevices.Take(5)) // Log first 5 devices
            {
                try
                {
                    _module.LogDebug($"Device: {device.FriendlyName} | State: {device.State} | DataFlow: {device.DataFlow}");
                }
                catch (Exception ex)
                {
                    _module.LogDebug($"Device info error: {ex.Message}");
                }
            }
            
            if (_currentDevice != null)
            {
                try
                {
                    _module.LogDebug($"Current device state: {_currentDevice.State}");
                    _module.LogDebug($"Device changes since start: {_deviceChangeCount}");
                }
                catch (Exception ex)
                {
                    _module.LogDebug($"Current device check failed: {ex.Message}");
                }
            }
            
            _module.LogDebug("=== End Diagnostics ===");
        }
        catch (Exception ex)
        {
            _module.LogDebug($"Failed to log audio diagnostics: {ex.Message}");
        }
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        if (_disposed)
            return;

        _module.LogDebug("Disposing audio device manager...");
        
        lock (_deviceLock)
        {
            try
            {
                DisposeAudioCapture();
                
                _deviceEnumerator?.Dispose();
                _deviceEnumerator = null;
                _currentDevice = null;
                _isInitialized = false;
                
                _module.LogDebug("Audio device manager disposed");
            }
            catch (Exception ex)
            {
                _module.Log($"Error during device manager disposal: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    #endregion
} 