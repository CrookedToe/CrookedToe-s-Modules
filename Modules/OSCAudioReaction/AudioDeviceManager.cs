using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace CrookedToe.Modules.OSCAudioReaction;

internal sealed record AudioCaptureStoppedEventArgs(Exception? Exception, bool Expected);

public sealed class SimpleAudioDeviceManager : IDisposable, IMMNotificationClient
{
    private readonly OSCAudioReactionModule _module;
    private readonly object _deviceLock = new();

    private MMDeviceEnumerator? _deviceEnumerator;
    private WasapiLoopbackCapture? _audioCapture;
    private MMDevice? _currentDevice;
    private EventHandler<WaveInEventArgs>? _dataAvailableHandlers;
    private volatile bool _isInitialized;
    private volatile bool _isCapturing;
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private bool _notificationRegistered;

    public SimpleAudioDeviceManager(OSCAudioReactionModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));

        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceEnumerator.RegisterEndpointNotificationCallback(this);
            _notificationRegistered = true;
        }
        catch (Exception ex)
        {
            _module.Log($"Failed to initialize audio device manager: {ex.Message}");
            Dispose();
            throw;
        }
    }

    public bool IsInitialized => _isInitialized && !_disposed;

    public bool IsCapturing
    {
        get
        {
            var capture = _audioCapture;
            if (!_isCapturing || _disposed || capture == null)
                return false;

            try
            {
                return capture.CaptureState is CaptureState.Starting or CaptureState.Capturing;
            }
            catch
            {
                return false;
            }
        }
    }

    public string? CurrentDeviceName
    {
        get
        {
            if (!IsInitialized || _currentDevice == null)
                return null;
            try { return _currentDevice.FriendlyName; }
            catch { return "[Device Name Error]"; }
        }
    }

    public WaveFormat? CurrentWaveFormat
    {
        get
        {
            var capture = _audioCapture;
            if (_disposed || capture == null)
                return null;
            try { return capture.WaveFormat; }
            catch { return null; }
        }
    }

    public WasapiLoopbackCapture? AudioCapture => _disposed ? null : _audioCapture;

    internal event EventHandler<AudioCaptureStoppedEventArgs>? CaptureStopped;
    internal event EventHandler? DefaultRenderDeviceChanged;

    public bool InitializeDefaultDevice()
    {
        if (_disposed || _deviceEnumerator == null)
            return false;

        MMDevice? defaultDevice = null;
        try
        {
            defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null)
            {
                _module.Log("No default audio output device found");
                return false;
            }

            return InitializeDevice(defaultDevice);
        }
        catch (Exception ex)
        {
            try { defaultDevice?.Dispose(); } catch { }
            _module.Log($"Failed to get default audio device: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StartCaptureAsync()
    {
        if (_disposed || !_isInitialized || _audioCapture == null)
            return false;
        if (IsCapturing)
            return true;

        string deviceName = CurrentDeviceName ?? "Unknown";
        bool success = await TryStartCaptureWithRetryAsync().ConfigureAwait(false);
        _isCapturing = success;

        if (success)
            _module.Log($"Audio capture started: {deviceName}");
        else
            _module.Log($"Failed to start audio capture after all retry attempts: {deviceName}");

        return success;
    }

    internal async Task<bool> RestartDefaultCaptureAsync()
    {
        if (_disposed)
            return false;

        StopCapture(log: false);
        if (!InitializeDefaultDevice())
            return false;

        return await StartCaptureAsync().ConfigureAwait(false);
    }

    public void StopCapture() => StopCapture(log: true);

    private void StopCapture(bool log)
    {
        var capture = _audioCapture;
        bool wasCapturing = IsCapturing;
        _stopRequested = true;
        _isCapturing = false;

        if (capture == null)
            return;

        lock (_deviceLock)
        {
            try { capture.StopRecording(); } catch { }
        }

        if (log && wasCapturing)
            _module.Log($"Audio capture stopped: {CurrentDeviceName ?? "Unknown"}");
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add
        {
            if (value == null)
                return;

            lock (_deviceLock)
            {
                if (_disposed)
                    return;

                _dataAvailableHandlers += value;
                if (_audioCapture != null)
                    _audioCapture.DataAvailable += value;
            }
        }
        remove
        {
            if (value == null)
                return;

            lock (_deviceLock)
            {
                _dataAvailableHandlers -= value;
                if (_audioCapture != null)
                    _audioCapture.DataAvailable -= value;
            }
        }
    }

    private bool InitializeDevice(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_disposed)
        {
            device.Dispose();
            return false;
        }

        lock (_deviceLock)
        {
            try
            {
                string deviceName = device.FriendlyName;
                DisposeAudioCapture();
                DisposeCurrentDevice();

                var capture = new WasapiLoopbackCapture(device);
                AttachHandlers(capture);

                _audioCapture = capture;
                _currentDevice = device;
                _isInitialized = true;
                _isCapturing = false;
                _stopRequested = false;
                _module.Log($"Audio capture initialized: {deviceName}");
                return true;
            }
            catch (Exception ex)
            {
                try { device.Dispose(); } catch { }
                DisposeAudioCapture();
                _isInitialized = false;
                _module.Log($"Failed to initialize audio capture: {ex.Message}");
                return false;
            }
        }
    }

    private async Task<bool> TryStartCaptureWithRetryAsync()
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (_disposed)
                return false;

            var capture = _audioCapture;
            if (capture == null)
                return false;

            try
            {
                _stopRequested = false;
                capture.StartRecording();
                if (await WaitForCaptureStartAsync(capture).ConfigureAwait(false))
                    return true;
            }
            catch (Exception ex)
            {
                _module.LogDebug($"Audio capture start attempt {attempt} failed: {ex.Message}");
            }

            _isCapturing = false;
            if (attempt < maxRetries)
                await Task.Delay(150 * attempt).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitForCaptureStartAsync(WasapiLoopbackCapture capture)
    {
        const int attempts = 25;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            CaptureState state;
            try { state = capture.CaptureState; }
            catch { return false; }

            if (state == CaptureState.Capturing)
                return true;
            if (state == CaptureState.Stopped)
                return false;

            await Task.Delay(20).ConfigureAwait(false);
        }

        return capture.CaptureState == CaptureState.Capturing;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool expected;
        lock (_deviceLock)
        {
            if (!ReferenceEquals(sender, _audioCapture))
                return;

            expected = _stopRequested || _disposed;
            _isCapturing = false;
        }

        CaptureStopped?.Invoke(this, new AudioCaptureStoppedEventArgs(e.Exception, expected));
    }

    private void AttachHandlers(WasapiLoopbackCapture capture)
    {
        capture.RecordingStopped += OnRecordingStopped;
        if (_dataAvailableHandlers == null)
            return;

        foreach (var handler in _dataAvailableHandlers.GetInvocationList().Cast<EventHandler<WaveInEventArgs>>())
            capture.DataAvailable += handler;
    }

    private void DetachHandlers(WasapiLoopbackCapture capture)
    {
        try { capture.RecordingStopped -= OnRecordingStopped; } catch { }
        if (_dataAvailableHandlers == null)
            return;

        try
        {
            foreach (var handler in _dataAvailableHandlers.GetInvocationList().Cast<EventHandler<WaveInEventArgs>>())
                capture.DataAvailable -= handler;
        }
        catch { }
    }

    private void DisposeAudioCapture()
    {
        var capture = _audioCapture;
        if (capture == null)
            return;

        _stopRequested = true;
        _isCapturing = false;
        try { capture.StopRecording(); } catch { }
        DetachHandlers(capture);
        try { capture.Dispose(); } catch { }
        if (ReferenceEquals(_audioCapture, capture))
            _audioCapture = null;
    }

    private void DisposeCurrentDevice()
    {
        try { _currentDevice?.Dispose(); } catch { }
        _currentDevice = null;
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (_disposed || flow != DataFlow.Render || role != Role.Multimedia)
            return;

        DefaultRenderDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (_disposed || newState == DeviceState.Active)
            return;

        string? currentId;
        try { currentId = _currentDevice?.ID; }
        catch { return; }

        if (string.Equals(deviceId, currentId, StringComparison.OrdinalIgnoreCase))
            DefaultRenderDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    public void OnDeviceRemoved(string deviceId) => OnDeviceStateChanged(deviceId, DeviceState.NotPresent);

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_deviceLock)
        {
            DisposeAudioCapture();

            if (_notificationRegistered && _deviceEnumerator != null)
            {
                try { _deviceEnumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                _notificationRegistered = false;
            }

            try { _deviceEnumerator?.Dispose(); } catch { }
            _deviceEnumerator = null;
            DisposeCurrentDevice();
            _isInitialized = false;
        }
    }
}
