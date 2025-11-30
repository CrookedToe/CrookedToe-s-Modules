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
        MagnitudePhaseRatio, EnablePhaseAnalysis, EnableDirectionalPause, DirectionalPauseFactor,
        HabituationIncrease, HabituationDecayRate, HabituationThreshold 
    }

    private static readonly (AudioParameter Param, AudioSetting Setting, string Name, string Range)[] FrequencyBandInfo =
    [
        (AudioParameter.SubBassVolume, AudioSetting.EnableSubBass, "Sub Bass", "20-60Hz"),
        (AudioParameter.BassVolume, AudioSetting.EnableBass, "Bass", "60-250Hz"),
        (AudioParameter.LowMidVolume, AudioSetting.EnableLowMid, "Low Mid", "250-500Hz"),
        (AudioParameter.MidVolume, AudioSetting.EnableMid, "Mid", "500-2000Hz"),
        (AudioParameter.UpperMidVolume, AudioSetting.EnableUpperMid, "Upper Mid", "2000-4000Hz"),
        (AudioParameter.PresenceVolume, AudioSetting.EnablePresence, "Presence", "4000-6000Hz"),
        (AudioParameter.BrillianceVolume, AudioSetting.EnableBrilliance, "Brilliance", "6000-25000Hz")
    ];

    private AudioProcessor? _audioProcessor;
    private SimpleAudioDeviceManager? _audioDeviceManager;
    private float _currentVolumeLevel;
    private float _currentDirectionValue = 0.5f;
    
    private AudioSettings? _cachedSettings;
    private int _cachedSampleRate;
    private bool _settingsNeedRefresh = true;
    private int _framesSinceSettingsRefresh;
    private const int SETTINGS_REFRESH_INTERVAL = 50;
    
    private float _lastSentVolume = -1f;
    private float _lastSentDirection = -1f;
    private bool _lastSentSpike;
    private readonly float[] _lastSentBands = new float[7];
    private const float PARAM_CHANGE_THRESHOLD = 0.001f;

    protected override void OnPreLoad()
    {
        RegisterAudioParameters();
        CreateAudioSettings();
        CreateAudioSettingsGroups();
    }

    protected override void OnPostLoad() { }

    protected override async Task<bool> OnModuleStart()
    {
        try
        {
            Log("Starting OSC Audio Reaction module...");
            
            _audioDeviceManager = new SimpleAudioDeviceManager(this);
            
            if (!await _audioDeviceManager.InitializeDefaultDevice())
            {
                Log("Failed to initialize default audio device");
                return false;
            }

            var waveFormat = _audioDeviceManager.AudioCapture?.WaveFormat;
            if (waveFormat == null)
            {
                Log("No audio wave format available after initialization");
                return false;
            }

            Log($"Audio initialized: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels}ch");
            
            _audioProcessor = new AudioProcessor(BuildAudioSettings(waveFormat.SampleRate));
            _audioDeviceManager.DataAvailable += OnAudioDataAvailable;
            
            ResetState();
            
            await _audioDeviceManager.StartCaptureAsync();
            
            Log(_audioDeviceManager.IsCapturing 
                ? "OSC Audio Reaction module started successfully" 
                : "OSC Audio Reaction module started with audio capture issues");
            
            return true;
        }
        catch (Exception ex)
        {
            Log($"Module start failed: {ex.Message}");
            await CleanupAfterError();
            return false;
        }
    }

    protected override Task OnModuleStop()
    {
        Log("Stopping OSC Audio Reaction module...");
        CleanupAudioResources();
        ResetParametersToSafeValues();
        ResetState();
        Log("OSC Audio Reaction module stopped");
        return Task.CompletedTask;
    }

    private async Task CleanupAfterError()
    {
        try
        {
            _audioDeviceManager?.StopCapture();
            await Task.Delay(100);
            _audioDeviceManager?.Dispose();
            _audioProcessor?.Dispose();
        }
        catch { }
    }

    private void CleanupAudioResources()
    {
        if (_audioDeviceManager != null)
            _audioDeviceManager.DataAvailable -= OnAudioDataAvailable;
        
        _audioDeviceManager?.StopCapture();
        _audioProcessor?.Dispose();
        _audioProcessor = null;
        _audioDeviceManager?.Dispose();
        _audioDeviceManager = null;
    }

    private void ResetParametersToSafeValues()
    {
        TrySendParameter(AudioParameter.AudioVolume, 0f);
        TrySendParameter(AudioParameter.AudioDirection, 0.5f);
        TrySendParameter(AudioParameter.AudioSpike, false);
        
        foreach (var (param, _, _, _) in FrequencyBandInfo)
            TrySendParameter(param, 0f);
    }

    private void TrySendParameter<T>(AudioParameter param, T value) where T : notnull
    {
        try { SendParameter(param, value); }
        catch { }
    }

    private void ResetState()
    {
        _currentVolumeLevel = 0f;
        _currentDirectionValue = 0.5f;
        _settingsNeedRefresh = true;
        _framesSinceSettingsRefresh = 0;
        _cachedSettings = null;
        _lastSentVolume = -1f;
        _lastSentDirection = -1f;
        _lastSentSpike = false;
        Array.Clear(_lastSentBands);
    }

    private void RegisterAudioParameters()
    {
        RegisterParameter<float>(AudioParameter.AudioDirection, "audio_direction", 
            ParameterMode.Write, "Audio Direction", "0=left, 0.5=center, 1=right");
        RegisterParameter<float>(AudioParameter.AudioVolume, "audio_volume", 
            ParameterMode.Write, "Audio Volume", "0=silent, 1=loud");
        RegisterParameter<bool>(AudioParameter.AudioSpike, "audio_spike", 
            ParameterMode.Write, "Audio Spike", "True when sudden volume increase detected");

        foreach (var (param, _, name, range) in FrequencyBandInfo)
        {
            RegisterParameter<float>(param, $"audio_{name.ToLowerInvariant().Replace(" ", "")}", 
                ParameterMode.Write, $"{name} Volume ({range})", $"{name} frequencies");
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

        foreach (var (_, setting, name, range) in FrequencyBandInfo)
            CreateToggle(setting, $"{name} ({range})", $"Enable {name.ToLowerInvariant()} frequency band", true);

        CreateToggle(AudioSetting.EnablePhaseAnalysis, "Enable Phase Analysis", "Use phase difference between channels to improve direction detection", true);
        CreateSlider(AudioSetting.MagnitudePhaseRatio, "Magnitude/Phase Balance", "Balance between magnitude-based (0) and phase-based (1) direction calculation", 0.7f, 0.0f, 1.0f, 0.05f);

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
        frequencySettings.AddRange(FrequencyBandInfo.Select(b => b.Setting));
        CreateGroup("Frequency Bands", "Individual frequency band controls", frequencySettings.Cast<Enum>().ToArray());
        
        CreateGroup("Enhanced Direction", "Advanced directional audio features", 
            AudioSetting.EnablePhaseAnalysis, AudioSetting.MagnitudePhaseRatio, 
            AudioSetting.EnableDirectionalPause, AudioSetting.DirectionalPauseFactor);
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        var processor = _audioProcessor;
        if (processor == null) return;

        _framesSinceSettingsRefresh++;
        int currentSampleRate = _audioDeviceManager?.AudioCapture?.WaveFormat?.SampleRate ?? 48000;
        
        if (_settingsNeedRefresh || _cachedSampleRate != currentSampleRate || _framesSinceSettingsRefresh >= SETTINGS_REFRESH_INTERVAL)
        {
            _cachedSampleRate = currentSampleRate;
            _cachedSettings = BuildAudioSettings(currentSampleRate);
            _settingsNeedRefresh = false;
            _framesSinceSettingsRefresh = 0;
            processor.UpdateSettings(_cachedSettings);
        }

        var result = processor.ProcessAudio(e);
        UpdateAudioParameters(result);
        
        _currentVolumeLevel = result.Volume;
        _currentDirectionValue = result.Direction;
    }

    private void UpdateAudioParameters(AudioProcessingResult result)
    {
        float volume = RoundValue(result.Volume);
        float direction = RoundValue(result.Direction);
        
        bool volumeChanged = MathF.Abs(volume - _lastSentVolume) > PARAM_CHANGE_THRESHOLD;
        bool volumeSnapToZero = volume == 0f && _lastSentVolume != 0f;
        if (volumeChanged || volumeSnapToZero)
        {
            SendParameter(AudioParameter.AudioVolume, volume);
            _lastSentVolume = volume;
        }
        
        if (MathF.Abs(direction - _lastSentDirection) > PARAM_CHANGE_THRESHOLD)
        {
            SendParameter(AudioParameter.AudioDirection, direction);
            _lastSentDirection = direction;
        }
        
        if (result.Spike != _lastSentSpike)
        {
            SendParameter(AudioParameter.AudioSpike, result.Spike);
            _lastSentSpike = result.Spike;
        }
        
        if (result.FrequencyBands.Length >= 7)
        {
            for (int i = 0; i < FrequencyBandInfo.Length && i < result.FrequencyBands.Length; i++)
            {
                var (param, setting, _, _) = FrequencyBandInfo[i];
                if (GetSettingValue<bool>(setting))
                {
                    float bandValue = RoundValue(result.FrequencyBands[i]);
                    bool significantChange = MathF.Abs(bandValue - _lastSentBands[i]) > PARAM_CHANGE_THRESHOLD;
                    bool shouldSnapToZero = bandValue == 0f && _lastSentBands[i] != 0f;
                    
                    if (significantChange || shouldSnapToZero)
                    {
                        SendParameter(param, bandValue);
                        _lastSentBands[i] = bandValue;
                    }
                }
            }
        }
    }

    private static float RoundValue(float value)
    {
        if (value == 0f || float.IsNaN(value) || float.IsInfinity(value)) return value;

        const float minThreshold = 1e-4f;
        if (MathF.Abs(value) < minThreshold) return 0f;

        int magnitude = (int)MathF.Floor(MathF.Log10(MathF.Abs(value)));
        float scale = MathF.Pow(10, 4 - magnitude - 1);
        float rounded = MathF.Round(value * scale) / scale;
        
        return MathF.Abs(rounded) < minThreshold ? 0f : rounded;
    }

    private AudioSettings BuildAudioSettings(int sampleRate)
    {
        var bandEnabled = new bool[7];
        for (int i = 0; i < FrequencyBandInfo.Length; i++)
            bandEnabled[i] = GetSettingValue<bool>(FrequencyBandInfo[i].Setting);

        return new AudioSettings
        {
            SampleRate = sampleRate,
            Gain = GetSettingValue<float>(AudioSetting.Gain),
            EnableAGC = GetSettingValue<bool>(AudioSetting.EnableAGC),
            Smoothing = GetSettingValue<float>(AudioSetting.Smoothing),
            DirectionThreshold = GetSettingValue<float>(AudioSetting.DirectionThreshold),
            SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold),
            SpikeHoldDuration = GetSettingValue<float>(AudioSetting.SpikeHoldDuration),
            FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
            MagnitudePhaseRatio = GetSettingValue<float>(AudioSetting.MagnitudePhaseRatio),
            EnablePhaseAnalysis = GetSettingValue<bool>(AudioSetting.EnablePhaseAnalysis),
            EnableDirectionalPause = GetSettingValue<bool>(AudioSetting.EnableDirectionalPause),
            DirectionalPauseFactor = GetSettingValue<float>(AudioSetting.DirectionalPauseFactor),
            HabituationIncrease = GetSettingValue<float>(AudioSetting.HabituationIncrease),
            HabituationDecayRate = GetSettingValue<float>(AudioSetting.HabituationDecayRate),
            HabituationThreshold = GetSettingValue<float>(AudioSetting.HabituationThreshold),
            ScaleFrequencyWithVolume = GetSettingValue<bool>(AudioSetting.ScaleFrequencyWithVolume),
            BandEnabled = bandEnabled
        };
    }
}
