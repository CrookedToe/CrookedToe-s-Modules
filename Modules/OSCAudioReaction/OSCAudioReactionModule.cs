using System.Diagnostics;
using CrookedToe.Modules.Compatibility;
using CrookedToe.Modules.Diagnostics;
using NAudio.Wave;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;

namespace CrookedToe.Modules.OSCAudioReaction;

[ModuleTitle("OSC Audio Reaction")]
[ModuleDescription("Comprehensive audio analysis with direction, volume, frequency bands, and intelligent spike detection")]
[ModuleType(ModuleType.Generic)]
public class OSCAudioReactionModule : Module
{
    private enum AudioParameter
    {
        AudioDirection, AudioVolume, AudioSpike,
        SubBassVolume, BassVolume, LowMidVolume, MidVolume,
        UpperMidVolume, PresenceVolume, BrillianceVolume
    }

    private enum AudioSetting
    {
        Gain, EnableAGC, Smoothing, DirectionThreshold, ScaleFrequencyWithVolume,
        SpikeThreshold, SpikeHoldDuration, EnableSubBass, EnableBass, EnableLowMid,
        EnableMid, EnableUpperMid, EnablePresence, EnableBrilliance, FrequencySmoothing,
        EnableDirectionalPause, DirectionalPauseFactor,
        HabituationIncrease, HabituationDecayRate, HabituationThreshold
    }

    private static readonly (AudioParameter Parameter, AudioSetting Setting)[] FrequencyBandMappings =
    [
        (AudioParameter.SubBassVolume, AudioSetting.EnableSubBass),
        (AudioParameter.BassVolume, AudioSetting.EnableBass),
        (AudioParameter.LowMidVolume, AudioSetting.EnableLowMid),
        (AudioParameter.MidVolume, AudioSetting.EnableMid),
        (AudioParameter.UpperMidVolume, AudioSetting.EnableUpperMid),
        (AudioParameter.PresenceVolume, AudioSetting.EnablePresence),
        (AudioParameter.BrillianceVolume, AudioSetting.EnableBrilliance)
    ];

    private const int UpdateIntervalMilliseconds = 50;
    private const int SettingsRefreshUpdates = 50;
    private const float InitialCaptureRetrySeconds = 2f;
    private const float MaximumCaptureRetrySeconds = 30f;
    private const float InitialPublicationRetrySeconds = 0.1f;
    private const float MaximumPublicationRetrySeconds = 2f;
    private const double SlowPublicationCommandMilliseconds = 50d;
    private static readonly long FailureLogCooldown = Stopwatch.Frequency * 5;
    private static readonly long HealthLogInterval = Stopwatch.Frequency * 60;

    private readonly object _captureRecoveryLock = new();
    private readonly float[] _currentBands = new float[AudioBandDefinitions.Count];

    private AudioProcessor? _audioProcessor;
    private SimpleAudioDeviceManager? _audioDeviceManager;
    private LatestAudioFrameBuffer? _audioFrames;
    private byte[]? _processingBuffer;
    private AudioSettings? _cachedSettings;
    private Task<bool>? _captureRecoveryTask;
    private string _captureRecoveryReason = string.Empty;

    private int _cachedSampleRate;
    private int _cachedChannels;
    private int _framesSinceSettingsRefresh;
    private int _captureRecoveryRequested;
    private int _safeOutputRequested;
    private int _captureRetryCount;
    private int _consecutiveProcessingFailures;
    private int _consecutivePublicationFailures;

    private long _lastCallbackTimestamp;
    private long _lastProcessedTimestamp;
    private long _lastSuccessfulPublicationTimestamp;
    private long _lastProcessingFailureLogTimestamp;
    private long _lastPublicationFailureLogTimestamp;
    private long _lastHealthLogTimestamp;
    private long _nextCaptureRetryTimestamp;
    private long _nextPublicationTimestamp;
    private long _processedFrames;

    private float _currentVolume;
    private float _currentDirection = 0.5f;
    private bool _currentSpike;
    private bool _settingsNeedRefresh = true;
    private volatile bool _isStopping;
    private ModuleDiagnostics? _diagnostics;
    private DiagnosticProbe? _updateProbe;
    private DiagnosticProbe? _captureCallbackProbe;
    private DiagnosticProbe? _processingProbe;
    private DiagnosticProbe? _publicationProbe;

    private new void Log(string message) => RealtimeModuleLog.Write(this, message);
    private new void LogDebug(string message) => RealtimeModuleLog.Write(this, message, debug: true);

    protected override void OnPreLoad()
    {
        RegisterAudioParameters();
        CreateAudioSettings();
        CreateAudioSettingsGroups();
    }

    protected override void OnPostLoad()
    {
    }

    protected override async Task<bool> OnModuleStart()
    {
        StartDiagnostics();
        _isStopping = false;
        ResetRuntimeState();
        Log("Starting OSC Audio Reaction module...");

        try
        {
            _audioDeviceManager = new SimpleAudioDeviceManager(this);
            if (!_audioDeviceManager.InitializeDefaultDevice())
            {
                Log("Failed to initialize default audio device");
                await CleanupAudioResourcesAsync();
                StopDiagnostics();
                return false;
            }

            WaveFormat? waveFormat = _audioDeviceManager.CurrentWaveFormat;
            if (waveFormat == null || !IsSupportedWaveFormat(waveFormat))
            {
                Log(waveFormat == null
                    ? "No audio wave format available after initialization"
                    : $"Unsupported audio format: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels}ch, {waveFormat.Encoding}");
                await CleanupAudioResourcesAsync();
                StopDiagnostics();
                return false;
            }

            ConfigureAudioPipeline(waveFormat);
            _audioDeviceManager.DataAvailable += OnAudioDataAvailable;
            _audioDeviceManager.CaptureStopped += OnAudioCaptureStopped;
            _audioDeviceManager.DefaultRenderDeviceChanged += OnDefaultRenderDeviceChanged;

            if (!await _audioDeviceManager.StartCaptureAsync())
            {
                Log("OSC Audio Reaction could not enter the capturing state");
                await CleanupAudioResourcesAsync();
                StopDiagnostics();
                return false;
            }

            Log($"OSC Audio Reaction started: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels}ch");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Module start failed: {ex.Message}");
            await CleanupAudioResourcesAsync();
            StopDiagnostics();
            return false;
        }
    }

    protected override async Task OnModuleStop()
    {
        Log("Stopping OSC Audio Reaction module...");
        _isStopping = true;

        SetSafeOutput();
        TryPublishCurrentState("publish neutral audio state during stop", force: true);

        Task<bool>? recoveryTask = _captureRecoveryTask;
        if (recoveryTask != null)
        {
            try { await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }

        await CleanupAudioResourcesAsync();
        ResetRuntimeState();
        Log("OSC Audio Reaction module stopped");
        StopDiagnostics();
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, UpdateIntervalMilliseconds)]
    private void UpdateAudio()
    {
        if (_isStopping)
            return;

        using DiagnosticScope updateMeasurement = _updateProbe?.Measure() ?? default;
        int patchedObservers = VrcOscUiDispatchWorkaround.ApplyIfDue();
        if (patchedObservers > 0)
            _diagnostics?.Event("vrcosc_dispatch_workaround", $"patchedObservers={patchedObservers}");

        long now = Stopwatch.GetTimestamp();

        if (Interlocked.Exchange(ref _safeOutputRequested, 0) != 0)
            SetSafeOutput();

        CompleteCaptureRecovery(now);

        SimpleAudioDeviceManager? manager = _audioDeviceManager;
        if (manager != null && !manager.IsCapturing && _captureRecoveryTask == null)
            RequestCaptureRecovery("the capture provider is not running");

        StartCaptureRecoveryIfDue(now);
        ProcessLatestFrame(now);
        TryPublishCurrentState("publish audio parameters");
        LogHealthIfDue(now);
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_isStopping)
            return;

        using DiagnosticScope callbackMeasurement = _captureCallbackProbe?.Measure() ?? default;

        try
        {
            long now = Stopwatch.GetTimestamp();
            _audioFrames?.Write(e.Buffer, e.BytesRecorded, now);
            Interlocked.Exchange(ref _lastCallbackTimestamp, now);
        }
        catch (Exception ex)
        {
            RegisterProcessingFailure(ex, "buffer audio callback");
        }
    }

    private void OnAudioCaptureStopped(object? sender, AudioCaptureStoppedEventArgs e)
    {
        if (_isStopping || e.Expected)
            return;

        string reason = e.Exception == null
            ? "audio capture stopped unexpectedly"
            : $"audio capture stopped: {e.Exception.Message}";
        Interlocked.Exchange(ref _safeOutputRequested, 1);
        RequestCaptureRecovery(reason);
    }

    private void OnDefaultRenderDeviceChanged(object? sender, EventArgs e)
    {
        if (_isStopping)
            return;

        Interlocked.Exchange(ref _safeOutputRequested, 1);
        RequestCaptureRecovery("the default audio output device changed");
    }

    private void ProcessLatestFrame(long now)
    {
        using DiagnosticScope processingMeasurement = _processingProbe?.Measure() ?? default;
        LatestAudioFrameBuffer? frames = _audioFrames;
        byte[]? processingBuffer = _processingBuffer;
        AudioProcessor? processor = _audioProcessor;
        if (frames == null || processingBuffer == null || processor == null)
            return;
        if (!frames.TryReadLatest(processingBuffer, out int bytesRecorded, out _))
            return;

        try
        {
            AudioSettings settings = RefreshCachedSettings(processor);
            AudioProcessingResult result = processor.ProcessAudio(processingBuffer, bytesRecorded);
            CaptureCurrentOutput(result, settings);

            _consecutiveProcessingFailures = 0;
            Interlocked.Exchange(ref _lastProcessedTimestamp, now);
            Interlocked.Increment(ref _processedFrames);
        }
        catch (Exception ex)
        {
            SetSafeOutput();
            RegisterProcessingFailure(ex, "process audio frame");
        }
    }

    private void CaptureCurrentOutput(AudioProcessingResult result, AudioSettings settings)
    {
        _currentVolume = NormalizeUnitValue(result.Volume, 0f);
        _currentDirection = NormalizeUnitValue(result.Direction, 0.5f);
        _currentSpike = result.Spike;

        for (int i = 0; i < AudioBandDefinitions.Count; i++)
        {
            _currentBands[i] = settings.BandEnabled[i] && i < result.FrequencyBands.Length
                ? NormalizeUnitValue(result.FrequencyBands[i], 0f)
                : 0f;
        }
    }

    private bool TryPublishCurrentState(string operation, bool force = false)
    {
        using DiagnosticScope publicationMeasurement = _publicationProbe?.Measure() ?? default;
        long now = Stopwatch.GetTimestamp();
        if (!force && now < _nextPublicationTimestamp)
            return false;

        try
        {
            if (!TrySendParameter(AudioParameter.AudioVolume, _currentVolume, out double slowMilliseconds) ||
                !TrySendParameter(AudioParameter.AudioDirection, _currentDirection, out slowMilliseconds) ||
                !TrySendParameter(AudioParameter.AudioSpike, _currentSpike, out slowMilliseconds))
            {
                return RegisterPublicationFailure(operation, null, slowMilliseconds, Stopwatch.GetTimestamp());
            }

            for (int i = 0; i < AudioBandDefinitions.Count; i++)
            {
                if (!TrySendParameter(FrequencyBandMappings[i].Parameter, _currentBands[i], out slowMilliseconds))
                    return RegisterPublicationFailure(operation, null, slowMilliseconds, Stopwatch.GetTimestamp());
            }

            if (_consecutivePublicationFailures > 0)
                Log("OSC Audio Reaction publication recovered");

            _consecutivePublicationFailures = 0;
            _nextPublicationTimestamp = 0;
            Interlocked.Exchange(ref _lastSuccessfulPublicationTimestamp, now);
            return true;
        }
        catch (Exception ex)
        {
            return RegisterPublicationFailure(operation, ex, 0d, Stopwatch.GetTimestamp());
        }
    }

    private bool TrySendParameter(AudioParameter parameter, object value, out double slowMilliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        SendParameter(parameter, value);
        slowMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return slowMilliseconds < SlowPublicationCommandMilliseconds;
    }

    private bool RegisterPublicationFailure(string operation, Exception? exception, double slowMilliseconds, long now)
    {
        _consecutivePublicationFailures++;
        int exponent = Math.Min(_consecutivePublicationFailures - 1, 5);
        float delaySeconds = Math.Min(
            InitialPublicationRetrySeconds * (1 << exponent),
            MaximumPublicationRetrySeconds);
        _nextPublicationTimestamp = AddSeconds(now, delaySeconds);

        bool slow = slowMilliseconds >= SlowPublicationCommandMilliseconds;
        if (_consecutivePublicationFailures == 1 || now - _lastPublicationFailureLogTimestamp >= FailureLogCooldown)
        {
            _lastPublicationFailureLogTimestamp = now;
            string detail = slow
                ? $"an individual parameter send took {slowMilliseconds:F0}ms"
                : exception?.Message ?? "unknown transport failure";
            Log($"OSC Audio Reaction could not {operation} ({_consecutivePublicationFailures} consecutive): {detail}. " +
                "The rest of this batch was skipped to protect VRCOSC responsiveness.");
            _diagnostics?.Event(
                slow ? "parameter_publication_slow" : "parameter_publication_failure",
                $"consecutive={_consecutivePublicationFailures};durationMs={slowMilliseconds:F1};exception={exception?.GetType().Name}:{exception?.Message}");
        }
        return false;
    }

    private void RequestCaptureRecovery(string reason)
    {
        lock (_captureRecoveryLock)
        {
            if (_captureRecoveryRequested == 0)
                _captureRecoveryReason = reason;
            _captureRecoveryRequested = 1;
        }
    }

    private void StartCaptureRecoveryIfDue(long now)
    {
        if (_captureRecoveryTask != null ||
            Volatile.Read(ref _captureRecoveryRequested) == 0 ||
            now < _nextCaptureRetryTimestamp)
        {
            return;
        }

        SimpleAudioDeviceManager? manager = _audioDeviceManager;
        if (manager == null)
            return;

        string reason;
        lock (_captureRecoveryLock)
            reason = _captureRecoveryReason;

        _captureRetryCount++;
        if (_captureRetryCount == 1)
            Log($"OSC Audio Reaction became unhealthy: {reason}. Recovery has started.");
        else
            LogDebug($"OSC Audio Reaction capture retry {_captureRetryCount}: {reason}");

        SetSafeOutput();
        _captureRecoveryTask = Task.Run(manager.RestartDefaultCaptureAsync);
    }

    private void CompleteCaptureRecovery(long now)
    {
        Task<bool>? task = _captureRecoveryTask;
        if (task == null || !task.IsCompleted)
            return;

        _captureRecoveryTask = null;
        bool success;
        try { success = task.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            success = false;
            lock (_captureRecoveryLock)
                _captureRecoveryReason = ex.Message;
        }

        WaveFormat? waveFormat = _audioDeviceManager?.CurrentWaveFormat;
        if (success && waveFormat != null && IsSupportedWaveFormat(waveFormat))
        {
            ConfigureAudioPipeline(waveFormat);
            lock (_captureRecoveryLock)
            {
                _captureRecoveryRequested = 0;
                _captureRecoveryReason = string.Empty;
            }

            _captureRetryCount = 0;
            _nextCaptureRetryTimestamp = 0;
            Log($"OSC Audio Reaction capture recovered on {_audioDeviceManager?.CurrentDeviceName ?? "the default device"}");
            return;
        }

        float delaySeconds = Math.Min(
            MaximumCaptureRetrySeconds,
            InitialCaptureRetrySeconds * MathF.Pow(2f, Math.Min(_captureRetryCount - 1, 4)));
        _nextCaptureRetryTimestamp = AddSeconds(now, delaySeconds);
        LogDebug($"OSC Audio Reaction capture recovery failed; retrying in {delaySeconds:F0}s");
    }

    private void ConfigureAudioPipeline(WaveFormat waveFormat)
    {
        _audioProcessor?.Dispose();
        _cachedSettings = BuildAudioSettings(waveFormat.SampleRate, waveFormat.Channels);
        _cachedSampleRate = waveFormat.SampleRate;
        _cachedChannels = waveFormat.Channels;
        _audioProcessor = new AudioProcessor(_cachedSettings);

        int requestedCapacity = Math.Max(
            64 * 1024,
            waveFormat.AverageBytesPerSecond / 2);
        _audioFrames = new LatestAudioFrameBuffer(requestedCapacity, waveFormat.BlockAlign);
        _processingBuffer = new byte[_audioFrames.Capacity];
        _settingsNeedRefresh = false;
        _framesSinceSettingsRefresh = 0;
        SetSafeOutput();
    }

    private AudioSettings RefreshCachedSettings(AudioProcessor processor)
    {
        _framesSinceSettingsRefresh++;
        WaveFormat? format = _audioDeviceManager?.CurrentWaveFormat;
        int sampleRate = format?.SampleRate ?? _cachedSampleRate;
        int channels = format?.Channels ?? _cachedChannels;

        if (_settingsNeedRefresh ||
            _cachedSampleRate != sampleRate ||
            _cachedChannels != channels ||
            _framesSinceSettingsRefresh >= SettingsRefreshUpdates)
        {
            _cachedSampleRate = sampleRate;
            _cachedChannels = channels;
            _cachedSettings = BuildAudioSettings(sampleRate, channels);
            _settingsNeedRefresh = false;
            _framesSinceSettingsRefresh = 0;
            processor.UpdateSettings(_cachedSettings);
        }

        return _cachedSettings ?? BuildAudioSettings(sampleRate, channels);
    }

    private void RegisterProcessingFailure(Exception ex, string operation)
    {
        _consecutiveProcessingFailures++;
        long now = Stopwatch.GetTimestamp();
        if (_consecutiveProcessingFailures != 1 &&
            now - _lastProcessingFailureLogTimestamp < FailureLogCooldown)
        {
            return;
        }

        _lastProcessingFailureLogTimestamp = now;
        Log($"OSC Audio Reaction could not {operation} ({_consecutiveProcessingFailures} consecutive): {ex.Message}");
    }

    private void LogHealthIfDue(long now)
    {
        if (_lastHealthLogTimestamp != 0 && now - _lastHealthLogTimestamp < HealthLogInterval)
            return;

        _lastHealthLogTimestamp = now;
        LatestAudioFrameBuffer? frames = _audioFrames;
        LogDebug(
            $"Audio health: capture={_audioDeviceManager?.IsCapturing == true}, " +
            $"callbacks={frames?.ReceivedFrames ?? 0}, processed={Interlocked.Read(ref _processedFrames)}, " +
            $"coalesced={frames?.CoalescedFrames ?? 0}, truncated={frames?.TruncatedFrames ?? 0}, " +
            $"lastCallback={AgeSeconds(_lastCallbackTimestamp, now):F1}s, " +
            $"lastProcessed={AgeSeconds(_lastProcessedTimestamp, now):F1}s, " +
            $"lastPublish={AgeSeconds(_lastSuccessfulPublicationTimestamp, now):F1}s, " +
            $"captureRetries={_captureRetryCount}, publishFailures={_consecutivePublicationFailures}");
    }

    private async Task CleanupAudioResourcesAsync()
    {
        var manager = _audioDeviceManager;
        var processor = _audioProcessor;

        _audioProcessor = null;
        _audioDeviceManager = null;
        _audioFrames = null;
        _processingBuffer = null;
        _captureRecoveryTask = null;

        if (manager != null)
        {
            manager.DataAvailable -= OnAudioDataAvailable;
            manager.CaptureStopped -= OnAudioCaptureStopped;
            manager.DefaultRenderDeviceChanged -= OnDefaultRenderDeviceChanged;
        }

        await AudioCaptureShutdown.RunAsync(() =>
        {
            manager?.StopCapture();
            manager?.Dispose();
            processor?.Dispose();
        });
    }

    private void ResetRuntimeState()
    {
        _cachedSettings = null;
        _cachedSampleRate = 48000;
        _cachedChannels = 2;
        _settingsNeedRefresh = true;
        _framesSinceSettingsRefresh = 0;
        _captureRetryCount = 0;
        _consecutiveProcessingFailures = 0;
        _consecutivePublicationFailures = 0;
        _lastCallbackTimestamp = 0;
        _lastProcessedTimestamp = 0;
        _lastSuccessfulPublicationTimestamp = 0;
        _lastProcessingFailureLogTimestamp = 0;
        _lastPublicationFailureLogTimestamp = 0;
        _lastHealthLogTimestamp = 0;
        _nextCaptureRetryTimestamp = 0;
        _nextPublicationTimestamp = 0;
        _processedFrames = 0;
        Interlocked.Exchange(ref _captureRecoveryRequested, 0);
        Interlocked.Exchange(ref _safeOutputRequested, 0);
        lock (_captureRecoveryLock)
            _captureRecoveryReason = string.Empty;
        SetSafeOutput();
    }

    private void SetSafeOutput()
    {
        _currentVolume = 0f;
        _currentDirection = 0.5f;
        _currentSpike = false;
        Array.Clear(_currentBands);
    }

    private static bool IsSupportedWaveFormat(WaveFormat format)
        => format.SampleRate > 0 &&
           format.Channels > 0 &&
           format.BitsPerSample == 32 &&
           format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;

    private static float NormalizeUnitValue(float value, float defaultValue)
    {
        if (!float.IsFinite(value))
            return defaultValue;

        return Math.Clamp(RoundValue(value), 0f, 1f);
    }

    private static float RoundValue(float value)
    {
        if (!float.IsFinite(value) || value == 0f)
            return 0f;

        const float minThreshold = 1e-4f;
        if (MathF.Abs(value) < minThreshold)
            return 0f;

        int magnitude = (int)MathF.Floor(MathF.Log10(MathF.Abs(value)));
        float scale = MathF.Pow(10, 4 - magnitude - 1);
        float rounded = MathF.Round(value * scale) / scale;
        return MathF.Abs(rounded) < minThreshold ? 0f : rounded;
    }

    private static long AddSeconds(long timestamp, float seconds)
        => timestamp + (long)(seconds * Stopwatch.Frequency);

    private static double AgeSeconds(long timestamp, long now)
        => timestamp == 0 ? double.PositiveInfinity : (now - timestamp) / (double)Stopwatch.Frequency;

    private void RegisterAudioParameters()
    {
        RegisterParameter<float>(AudioParameter.AudioDirection, "audio_direction",
            ParameterMode.Write, "Audio Direction", "0=left, 0.5=center, 1=right");
        RegisterParameter<float>(AudioParameter.AudioVolume, "audio_volume",
            ParameterMode.Write, "Audio Volume", "0=silent, 1=loud");
        RegisterParameter<bool>(AudioParameter.AudioSpike, "audio_spike",
            ParameterMode.Write, "Audio Spike", "True when sudden volume increase detected");

        for (int i = 0; i < AudioBandDefinitions.Count; i++)
        {
            AudioBandDefinition definition = AudioBandDefinitions.All[i];
            RegisterParameter<float>(
                FrequencyBandMappings[i].Parameter,
                definition.ParameterName,
                ParameterMode.Write,
                $"{definition.Name} Volume ({definition.RangeLabel})",
                $"{definition.Name} frequencies");
        }
    }

    private void CreateAudioSettings()
    {
        CreateSlider(AudioSetting.Gain, "Audio Gain", "Manual gain adjustment for audio input", 1.0f, 0.1f, 5.0f, 0.1f);
        CreateToggle(AudioSetting.EnableAGC, "Automatic Gain Control", "Automatically adjust gain to maintain consistent levels", true);
        CreateSlider(AudioSetting.Smoothing, "Volume Smoothing", "Smoothing factor for volume changes", 0.3f, 0.0f, 0.95f, 0.05f);
        CreateSlider(AudioSetting.DirectionThreshold, "Direction Threshold", "Minimum volume for direction detection", 0.01f, 0.005f, 0.1f, 0.005f);
        CreateSlider(AudioSetting.SpikeThreshold, "Spike Sensitivity", "Threshold for volume spike detection (lower = more sensitive)", 2.0f, 0.5f, 5.0f, 0.1f);
        CreateSlider(AudioSetting.SpikeHoldDuration, "Spike Hold Duration", "How long to hold spike state in seconds", 0.5f, 0.1f, 2.0f, 0.1f);
        CreateToggle(AudioSetting.ScaleFrequencyWithVolume, "Scale Frequencies with Volume", "Scale frequency band outputs with overall volume", false);
        CreateSlider(AudioSetting.FrequencySmoothing, "Frequency Smoothing", "Smoothing factor for frequency band analysis", 0.7f, 0.0f, 0.95f, 0.05f);

        for (int i = 0; i < AudioBandDefinitions.Count; i++)
        {
            AudioBandDefinition definition = AudioBandDefinitions.All[i];
            CreateToggle(
                FrequencyBandMappings[i].Setting,
                $"{definition.Name} ({definition.RangeLabel})",
                $"Enable {definition.Name.ToLowerInvariant()} frequency band",
                true);
        }

        CreateToggle(AudioSetting.EnableDirectionalPause, "Enable Directional Pause", "Audio direction pauses longer when further from center", false);
        CreateSlider(AudioSetting.DirectionalPauseFactor, "Directional Pause Factor", "How much longer to pause at extreme directions", 1.0f, 0.1f, 5.0f, 0.1f);
        CreateSlider(AudioSetting.HabituationIncrease, "Spike Learning Rate", "How quickly the system learns to ignore repetitive spikes", 0.15f, 0.05f, 0.5f, 0.05f);
        CreateSlider(AudioSetting.HabituationDecayRate, "Spike Recovery Rate", "How quickly spike sensitivity returns during quiet periods", 0.02f, 0.005f, 0.1f, 0.005f);
        CreateSlider(AudioSetting.HabituationThreshold, "Spike Habituation Threshold", "Above this level, spikes are treated as expected and ignored", 0.3f, 0.1f, 0.8f, 0.1f);
    }

    private void CreateAudioSettingsGroups()
    {
        CreateGroup("Basic Settings", "Core audio processing settings",
            AudioSetting.Gain, AudioSetting.EnableAGC, AudioSetting.Smoothing, AudioSetting.DirectionThreshold);

        CreateGroup("Spike Detection", "Spike detection and habituation settings",
            AudioSetting.SpikeThreshold, AudioSetting.SpikeHoldDuration,
            AudioSetting.HabituationIncrease, AudioSetting.HabituationDecayRate, AudioSetting.HabituationThreshold);

        var frequencySettings = new List<AudioSetting> { AudioSetting.ScaleFrequencyWithVolume, AudioSetting.FrequencySmoothing };
        frequencySettings.AddRange(FrequencyBandMappings.Select(mapping => mapping.Setting));
        CreateGroup("Frequency Bands", "Individual frequency band controls", frequencySettings.Cast<Enum>().ToArray());

        CreateGroup("Direction Smoothing", "Direction response controls",
            AudioSetting.EnableDirectionalPause, AudioSetting.DirectionalPauseFactor);
    }

    private AudioSettings BuildAudioSettings(int sampleRate, int channels)
    {
        var bandEnabled = new bool[AudioBandDefinitions.Count];
        for (int i = 0; i < AudioBandDefinitions.Count; i++)
            bandEnabled[i] = GetSettingValue<bool>(FrequencyBandMappings[i].Setting);

        return new AudioSettings
        {
            SampleRate = sampleRate,
            Channels = channels,
            Gain = GetSettingValue<float>(AudioSetting.Gain),
            EnableAGC = GetSettingValue<bool>(AudioSetting.EnableAGC),
            Smoothing = GetSettingValue<float>(AudioSetting.Smoothing),
            DirectionThreshold = GetSettingValue<float>(AudioSetting.DirectionThreshold),
            SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold),
            SpikeHoldDuration = GetSettingValue<float>(AudioSetting.SpikeHoldDuration),
            FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
            EnableDirectionalPause = GetSettingValue<bool>(AudioSetting.EnableDirectionalPause),
            DirectionalPauseFactor = GetSettingValue<float>(AudioSetting.DirectionalPauseFactor),
            HabituationIncrease = GetSettingValue<float>(AudioSetting.HabituationIncrease),
            HabituationDecayRate = GetSettingValue<float>(AudioSetting.HabituationDecayRate),
            HabituationThreshold = GetSettingValue<float>(AudioSetting.HabituationThreshold),
            ScaleFrequencyWithVolume = GetSettingValue<bool>(AudioSetting.ScaleFrequencyWithVolume),
            BandEnabled = bandEnabled
        };
    }

    private void StartDiagnostics()
    {
        StopDiagnostics();
        _diagnostics = BoundedDiagnostics.StartModule("OSCAudioReaction");
        _updateProbe = _diagnostics.CreateProbe("update_loop", UpdateIntervalMilliseconds);
        _captureCallbackProbe = _diagnostics.CreateProbe("capture_callback");
        _processingProbe = _diagnostics.CreateProbe("frame_processing");
        _publicationProbe = _diagnostics.CreateProbe("parameter_publication");
        VrcOscUiDispatchWorkaround.ApplyIfDue(force: true);
        _diagnostics.Event("vrcosc_dispatch_workaround", VrcOscUiDispatchWorkaround.Status);
    }

    private void StopDiagnostics()
    {
        _diagnostics?.Dispose();
        _diagnostics = null;
        _updateProbe = null;
        _captureCallbackProbe = null;
        _processingProbe = null;
        _publicationProbe = null;
    }
}
