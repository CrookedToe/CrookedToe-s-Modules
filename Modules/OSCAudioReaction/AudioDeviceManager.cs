using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CrookedToe.Modules.OSCAudioReaction;

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
    private EventHandler<WaveInEventArgs>? _dataAvailableHandlers;
    private int _deviceChangeCount;

    public SimpleAudioDeviceManager(OSCAudioReactionModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to initialize audio device manager: {ex.Message}");
            throw;
        }
    }

    public bool IsInitialized => _isInitialized && !_disposed;
    public bool IsCapturing => _isCapturing && !_disposed && _audioCapture != null;

    public string? CurrentDeviceName
    {
        get
        {
            if (!IsInitialized || _currentDevice == null) return null;
            try { return _currentDevice.FriendlyName; }
            catch { return "[Device Name Error]"; }
        }
    }

    public WaveFormat? CurrentWaveFormat
    {
        get
        {
            if (!IsCapturing || _audioCapture == null) return null;
            try { return _audioCapture.WaveFormat; }
            catch { return null; }
        }
    }

    public WasapiLoopbackCapture? AudioCapture => _disposed ? null : _audioCapture;

    public List<MMDevice> GetAvailableDevices()
    {
        if (_disposed || _deviceEnumerator == null) return [];

        try
        {
            return _deviceEnumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to enumerate audio devices: {ex.Message}");
            return [];
        }
    }

    public MMDevice? GetDefaultDevice()
    {
        if (_disposed || _deviceEnumerator == null) return null;

        try
        {
            var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null)
            {
                _module.Log("No default audio output device found");
                return null;
            }
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
        if (_disposed || string.IsNullOrEmpty(deviceId) || _deviceEnumerator == null) return null;

        try
        {
            var device = _deviceEnumerator.GetDevice(deviceId);
            if (device == null)
            {
                _module.Log($"Audio device not found: {deviceId}");
                return null;
            }
            return device;
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to get audio device by ID '{deviceId}': {ex.Message}");
            return null;
        }
    }

    public async Task<bool> InitializeDefaultDevice()
    {
        if (_disposed) return false;
        
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
        if (_disposed) return false;
        
        var device = GetDeviceById(deviceId);
        if (device == null)
        {
            _module.Log($"Cannot initialize: audio device not found: {deviceId}");
            return false;
        }

        return await InitializeDevice(device);
    }

    public Task<bool> InitializeDevice(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_disposed) return Task.FromResult(false);

        lock (_deviceLock)
        {
            try
            {
                var deviceName = device.FriendlyName;
                DisposeAudioCapture();

                _audioCapture = new WasapiLoopbackCapture(device);

                if (_dataAvailableHandlers != null)
                {
                    foreach (var handler in _dataAvailableHandlers.GetInvocationList()
                                 .Cast<EventHandler<WaveInEventArgs>>())
                    {
                        _audioCapture.DataAvailable += handler;
                    }
                }
                
                _currentDevice = device;
                _isInitialized = true;
                _deviceChangeCount++;

                _module.Log($"Audio capture initialized: {deviceName}");
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _module.Log($"Failed to initialize audio capture for device '{device.FriendlyName}': {ex.Message}");
                DisposeAudioCapture();
                _isInitialized = false;
                return Task.FromResult(false);
            }
        }
    }

    public async Task StartCaptureAsync()
    {
        if (_disposed || !_isInitialized || _audioCapture == null) return;
        if (_isCapturing) return;

        var deviceName = CurrentDeviceName ?? "Unknown";
        await Task.Delay(50);
        
        bool success = await TryStartCaptureWithRetryAsync();
        
        lock (_deviceLock)
        {
            if (success)
            {
                _isCapturing = true;
                _module.Log($"Audio capture started: {deviceName}");
            }
            else
            {
                _module.Log($"Failed to start audio capture after all retry attempts: {deviceName}");
                _isCapturing = false;
            }
        }
    }

    public void StartCapture() => StartCaptureAsync().GetAwaiter().GetResult();

    private async Task<bool> TryStartCaptureWithRetryAsync()
    {
        const int maxRetries = 3;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                try
                {
                    _audioCapture?.StopRecording();
                    await Task.Delay(100);
                }
                catch { }
                
                _audioCapture?.StartRecording();
                return true;
            }
            catch (OutOfMemoryException)
            {
                if (attempt < maxRetries && TryReinitializeCapture())
                {
                    await Task.Delay(200 * attempt);
                    continue;
                }
                
                _module.Log("WASAPI initialization failed - audio driver issues or device conflicts likely");
            }
            catch (Exception ex)
            {
                _module.LogDebug($"Audio capture start attempt {attempt} failed: {ex.Message}");
                if (attempt < maxRetries)
                    await Task.Delay(150 * attempt);
            }
        }
        
        return false;
    }

    private bool TryReinitializeCapture()
    {
        if (_currentDevice == null) return false;
            
        try
        {
            DisposeAudioCapture();
            _audioCapture = new WasapiLoopbackCapture(_currentDevice);

            if (_dataAvailableHandlers != null)
            {
                foreach (var handler in _dataAvailableHandlers.GetInvocationList()
                             .Cast<EventHandler<WaveInEventArgs>>())
                {
                    _audioCapture.DataAvailable += handler;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StopCapture()
    {
        var wasCapturing = _isCapturing;
        _isCapturing = false;
        
        if (!wasCapturing || _audioCapture == null) return;

        lock (_deviceLock)
        {
            var deviceName = CurrentDeviceName ?? "Unknown";
            try { _audioCapture?.StopRecording(); } catch { }
            _module.Log($"Audio capture stopped: {deviceName}");
        }
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add
        {
            if (value == null) return;
            _dataAvailableHandlers += value;
            if (_audioCapture != null)
                _audioCapture.DataAvailable += value;
        }
        remove
        {
            if (value == null) return;
            _dataAvailableHandlers -= value;
            if (_audioCapture != null)
                _audioCapture.DataAvailable -= value;
        }
    }

    public bool CheckDeviceHealth()
    {
        if (!IsInitialized || _currentDevice == null) return false;

        try
        {
            var deviceState = _currentDevice.State;
            var isHealthy = deviceState == DeviceState.Active;
            if (!isHealthy)
                _module.Log($"Audio device unhealthy: {deviceState}");
            return isHealthy;
        }
        catch
        {
            return false;
        }
    }

    private void DisposeAudioCapture()
    {
        var capture = _audioCapture;
        if (capture == null) return;
        
        if (_isCapturing)
        {
            try { capture.StopRecording(); } catch { }
            _isCapturing = false;
        }

        if (_dataAvailableHandlers != null)
        {
            try
            {
                foreach (var handler in _dataAvailableHandlers.GetInvocationList()
                             .Cast<EventHandler<WaveInEventArgs>>())
                {
                    capture.DataAvailable -= handler;
                }
            }
            catch { }
        }

        try { capture.Dispose(); } catch { }
        _audioCapture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        lock (_deviceLock)
        {
            DisposeAudioCapture();
            try { _deviceEnumerator?.Dispose(); } catch { }
            _deviceEnumerator = null;
            try { _currentDevice?.Dispose(); } catch { }
            _currentDevice = null;
            _isInitialized = false;
        }
    }
}
