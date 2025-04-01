using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Modules.Attributes.Settings;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;
using CrookedToe.Modules.OSCAudioReaction.AudioProcessing;

namespace CrookedToe.Modules.OSCAudioReaction;

[ModuleTitle("Audio Direction")]
[ModuleDescription("Sends audio direction and volume to VRChat for stereo visualization")]
[ModuleType(ModuleType.Generic)]
public class OSCAudioDirectionModule : Module
{
    private const int PROCESSING_LOCK_TIMEOUT_MS = 5;
    
    private readonly IAudioFactory _audioFactory;
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly object _configLock = new();
    
    private IAudioProcessor? _audioProcessor;
    private IAudioDeviceManager? _deviceManager;
    private IAudioConfiguration _config;
    
    private DateTime _lastVolumeUpdate = DateTime.Now;
    private DateTime _lastDirectionUpdate = DateTime.Now;
    private float _currentVolume;
    private float _currentDirection;
    private volatile bool _configurationChanged;

    // Frequency band information for consistent use across the module
    private static readonly (AudioParameter Parameter, AudioSetting Setting, string Name, string Description)[] FrequencyBandInfo = 
    {
        (AudioParameter.SubBassVolume, AudioSetting.EnableSubBass, "Sub Bass (16-60Hz)", "Very low frequencies: bass drops, explosions, rumble effects"),
        (AudioParameter.BassVolume, AudioSetting.EnableBass, "Bass (60-250Hz)", "Low frequencies: bass guitar, kick drums, male vocals"),
        (AudioParameter.LowMidVolume, AudioSetting.EnableLowMid, "Low Mid (250-500Hz)", "Lower midrange: bass line melodies, lower harmonics"),
        (AudioParameter.MidVolume, AudioSetting.EnableMid, "Mid (500Hz-2kHz)", "Main vocal range, most instruments' fundamental frequencies"),
        (AudioParameter.UpperMidVolume, AudioSetting.EnableUpperMid, "Upper Mid (2-4kHz)", "Presence range: vocal clarity, instrument attack sounds"),
        (AudioParameter.PresenceVolume, AudioSetting.EnablePresence, "Presence (4-6kHz)", "Definition range: speech intelligibility, high harmonics"),
        (AudioParameter.BrillianceVolume, AudioSetting.EnableBrilliance, "Brilliance (6-20kHz)", "Air frequencies: cymbals, sibilance, sparkle")
    };

    private enum AudioParameter 
    { 
        AudioDirection, 
        AudioVolume,
        AudioSpike,
        // Individual frequency band parameters
        SubBassVolume,    // 16-60Hz
        BassVolume,       // 60-250Hz
        LowMidVolume,     // 250-500Hz
        MidVolume,        // 500Hz-2kHz
        UpperMidVolume,   // 2-4kHz
        PresenceVolume,   // 4-6kHz
        BrillianceVolume  // 6-20kHz
    }

    private enum AudioSetting 
    { 
        Gain, 
        EnableAGC, 
        Smoothing, 
        DirectionThreshold,
        PresetSelection,
        ScaleFrequencyWithVolume,
        SpikeThreshold,
        // Frequency band toggles
        EnableSubBass,      // 16-60Hz
        EnableBass,         // 60-250Hz
        EnableLowMid,       // 250-500Hz
        EnableMid,          // 500Hz-2kHz
        EnableUpperMid,     // 2-4kHz
        EnablePresence,     // 4-6kHz
        EnableBrilliance,   // 6-20kHz
        FrequencySmoothing,  // Smoothing for frequency analysis
        
        // Enhanced direction settings
        EnableEnhancedDirection,
        MagnitudePhaseRatio,
        EnablePhaseAnalysis,
        
        // OpenVR integration
        // EnableHeadOrientation,
        // HeadOrientationSmoothing
    }

    private enum PresetSelection
    {
        Custom,
        Default,         // Balanced settings for most use cases
        LowLatency,     // For real-time responsiveness
        VoiceOptimized, // For speech and vocal pattern detection
        HighSmoothing,  // For stable, gradual reactions
        MusicOptimized  // For musical performance and visualization
    }

    public OSCAudioDirectionModule()
    {
        _audioFactory = new AudioFactory();
        _config = new AudioConfiguration();
    }

    protected override void OnPreLoad()
    {
        // Basic settings
        CreateDropdown(AudioSetting.PresetSelection, "Avatar Preset", 
            "Select a preset for your avatar's audio reactions. Each preset has optimized settings for different use cases.", 
            PresetSelection.Default);
        
        CreateToggle(AudioSetting.EnableAGC, "Auto Gain Control", 
            "Automatically adjusts gain to maintain consistent volume levels. Target level: 0.5", 
            true);
        
        CreateToggle(AudioSetting.ScaleFrequencyWithVolume, "Scale Frequencies with Volume", 
            "When enabled, frequency bands are scaled by audio volume. When disabled, they show pure proportions (sum to 1)", 
            true);
        
        CreateSlider(AudioSetting.SpikeThreshold, "Spike Sensitivity", 
            "Threshold for volume spike detection (0.5 = very sensitive, 5.0 = less sensitive). Note: Can be finnicky, works best with sharp volume changes.", 
            AudioConstants.DEFAULT_SPIKE_THRESHOLD, 
            AudioConstants.MIN_SPIKE_THRESHOLD, 
            AudioConstants.MAX_SPIKE_THRESHOLD);

        // Custom Preset Settings
        CreateSlider(AudioSetting.Gain, "Custom: Gain", 
            "Manual volume multiplier when using Custom preset or AGC is disabled (0.1 = quiet, 5.0 = loud)", 
            1.0f, 0.1f, 5.0f);
        
        CreateSlider(AudioSetting.Smoothing, "Custom: Animation Smoothing", 
            "Smoothing factor for avatar animations in Custom preset (0 = immediate response but jittery, 1 = smooth but delayed)", 
            0.5f, 0.0f, 1.0f);
        
        CreateSlider(AudioSetting.DirectionThreshold, "Custom: Direction Sensitivity", 
            "Minimum volume required to update direction. Lower values are more responsive but may be jittery", 
            0.01f, 0.0f, 0.1f, 0.005f);
        
        CreateSlider(AudioSetting.FrequencySmoothing, "Custom: Frequency Smoothing", 
            "Smoothing applied to frequency band values. Higher values reduce flickering but increase latency", 
            0.5f, 0.0f, 1.0f);

        // Frequency band toggles with detailed descriptions
        CreateToggle(AudioSetting.EnableSubBass, "Sub Bass (16-60Hz)", 
            "Very low frequencies: bass drops, explosions, rumble effects", false);
        
        CreateToggle(AudioSetting.EnableBass, "Bass (60-250Hz)", 
            "Low frequencies: bass guitar, kick drums, male vocals", true);
        
        CreateToggle(AudioSetting.EnableLowMid, "Low Mid (250-500Hz)", 
            "Lower midrange: bass line melodies, lower harmonics", true);
        
        CreateToggle(AudioSetting.EnableMid, "Mid (500Hz-2kHz)", 
            "Main vocal range, most instruments' fundamental frequencies", true);
        
        CreateToggle(AudioSetting.EnableUpperMid, "Upper Mid (2-4kHz)", 
            "Presence range: vocal clarity, instrument attack sounds", false);
        
        CreateToggle(AudioSetting.EnablePresence, "Presence (4-6kHz)", 
            "Definition range: speech intelligibility, high harmonics", false);
        
        CreateToggle(AudioSetting.EnableBrilliance, "Brilliance (6-20kHz)", 
            "Air frequencies: cymbals, sibilance, sparkle", false);

        // Enhanced Audio Direction Settings
        CreateToggle(AudioSetting.EnableEnhancedDirection, "Enhanced Direction Detection", 
            "Use advanced algorithm for more accurate audio direction detection. Inspired by spatial audio technology.", 
            false);
        
        CreateToggle(AudioSetting.EnablePhaseAnalysis, "Enable Phase Analysis", 
            "Use phase difference between channels to improve direction detection. Most effective with headphones.", 
            true);
        
        CreateSlider(AudioSetting.MagnitudePhaseRatio, "Magnitude/Phase Balance", 
            "Balance between magnitude-based (0) and phase-based (1) direction calculation.", 
            0.3f, 0.0f, 1.0f);

        // Main parameters
        RegisterParameter<float>(AudioParameter.AudioDirection, "audio_direction", 
            ParameterMode.Write, "Audio Direction", "0=left, 0.5=center, 1=right");
        
        RegisterParameter<float>(AudioParameter.AudioVolume, "audio_volume", 
            ParameterMode.Write, "Audio Volume", "0=silent, 1=loud");
        
        RegisterParameter<bool>(AudioParameter.AudioSpike, "audio_spike", 
            ParameterMode.Write, "Audio Spike", "True when a sudden volume increase is detected. Note: Can be inconsistent.");

        // Frequency band parameters
        RegisterParameter<float>(AudioParameter.SubBassVolume, "audio_subbass", 
            ParameterMode.Write, "Sub Bass Volume (16-60Hz)", "Very low frequencies: bass drops, explosions, rumble");
        
        RegisterParameter<float>(AudioParameter.BassVolume, "audio_bass", 
            ParameterMode.Write, "Bass Volume (60-250Hz)", "Low frequencies: bass guitar, kick drums, male vocals");
        
        RegisterParameter<float>(AudioParameter.LowMidVolume, "audio_lowmid", 
            ParameterMode.Write, "Low Mid Volume (250-500Hz)", "Lower midrange: bass line melodies, lower harmonics");
        
        RegisterParameter<float>(AudioParameter.MidVolume, "audio_mid", 
            ParameterMode.Write, "Mid Volume (500Hz-2kHz)", "Main vocal range, most instruments");
        
        RegisterParameter<float>(AudioParameter.UpperMidVolume, "audio_uppermid", 
            ParameterMode.Write, "Upper Mid Volume (2-4kHz)", "Presence range: vocal clarity, attack sounds");
        
        RegisterParameter<float>(AudioParameter.PresenceVolume, "audio_presence", 
            ParameterMode.Write, "Presence Volume (4-6kHz)", "Definition range: speech intelligibility, high harmonics");
        
        RegisterParameter<float>(AudioParameter.BrillianceVolume, "audio_brilliance", 
            ParameterMode.Write, "Brilliance Volume (6-20kHz)", "Air frequencies: cymbals, sibilance, sparkle");

        // Create groups
        CreateGroup("Basic Settings", 
            AudioSetting.PresetSelection,
            AudioSetting.SpikeThreshold,
            AudioSetting.EnableAGC,
            AudioSetting.ScaleFrequencyWithVolume);
        
        CreateGroup("Custom Preset Settings", 
            AudioSetting.Gain, AudioSetting.Smoothing, 
            AudioSetting.DirectionThreshold, AudioSetting.FrequencySmoothing);
        
        CreateGroup("Frequency Bands", 
            AudioSetting.EnableSubBass, AudioSetting.EnableBass, AudioSetting.EnableLowMid, 
            AudioSetting.EnableMid, AudioSetting.EnableUpperMid, AudioSetting.EnablePresence, 
            AudioSetting.EnableBrilliance);
        
        CreateGroup("Enhanced Direction", 
            AudioSetting.EnableEnhancedDirection, 
            AudioSetting.EnablePhaseAnalysis,
            AudioSetting.MagnitudePhaseRatio);
    }

    protected override async Task<bool> OnModuleStart()
    {
        try
        {
            UpdateConfigurationFromSettings();
            _deviceManager = _audioFactory.CreateDeviceManager(_config, this);
            
            if (!await _deviceManager.InitializeDefaultDeviceAsync())
            {
                Log("Failed to initialize audio device");
                return false;
            }

            _audioProcessor = _audioFactory.CreateProcessor(_config, _deviceManager.AudioCapture!.WaveFormat.BitsPerSample / 8);
            _deviceManager.DataAvailable += OnDataAvailable;
            
            // Reset state
            _currentVolume = 0f;
            _currentDirection = 0.5f;
            _lastVolumeUpdate = DateTime.Now;
            _lastDirectionUpdate = DateTime.Now;
            _configurationChanged = false;

            // Apply initial configuration
            ApplyConfigurationToProcessor();
            
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to start module: {ex.Message}");
            return false;
        }
    }

    protected override Task OnModuleStop()
    {
        // Unsubscribe from events first
        if (_deviceManager != null)
        {
            _deviceManager.DataAvailable -= OnDataAvailable;
        }
        
        // Reset parameters to default values
        SendParameter(AudioParameter.AudioVolume, 0f);
        SendParameter(AudioParameter.AudioDirection, 0.5f);
        SendParameter(AudioParameter.AudioSpike, false);
        
        // Reset frequency band parameters
        foreach (var band in FrequencyBandInfo)
        {
            SendParameter(band.Parameter, 0f);
        }
        
        // Dispose resources
        _deviceManager?.Dispose();
        _deviceManager = null;
        
        _audioProcessor?.Dispose();
        _audioProcessor = null;
        
        return Task.CompletedTask;
    }

    private void UpdateConfigurationFromSettings()
    {
        lock (_configLock)
        {
            var selectedPreset = GetSettingValue<PresetSelection>(AudioSetting.PresetSelection);
            var newConfig = selectedPreset switch
            {
                PresetSelection.Default => new AudioConfiguration
                {
                    Gain = 1.0f,
                    Smoothing = 0.5f,
                    DirectionThreshold = 0.01f,
                    FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                    FftSize = AudioConstants.DEFAULT_FFT_SIZE,
                    SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold)
                },
                PresetSelection.LowLatency => new AudioConfiguration
                {
                    Gain = 1.2f,
                    Smoothing = 0.3f,
                    DirectionThreshold = 0.01f,
                    FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                    FftSize = AudioConstants.FFT_SIZE_LOW,
                    SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold)
                },
                PresetSelection.VoiceOptimized => new AudioConfiguration
                {
                    Gain = 1.5f,
                    Smoothing = 0.4f,
                    DirectionThreshold = 0.02f,
                    FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                    FftSize = AudioConstants.FFT_SIZE_LOW,
                    SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold)
                },
                PresetSelection.HighSmoothing => new AudioConfiguration
                {
                    Gain = 1.0f,
                    Smoothing = 0.8f,
                    DirectionThreshold = 0.015f,
                    FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                    FftSize = AudioConstants.FFT_SIZE_HIGH,
                    SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold)
                },
                PresetSelection.MusicOptimized => new AudioConfiguration
                {
                    Gain = 1.1f,
                    Smoothing = 0.5f,
                    DirectionThreshold = 0.01f,
                    FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                    FftSize = AudioConstants.FFT_SIZE_MEDIUM,
                    SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold)
                },
                _ => new AudioConfiguration // Custom or default fallback
                {
                    Gain = GetSettingValue<float>(AudioSetting.Gain),
                    Smoothing = GetSettingValue<float>(AudioSetting.Smoothing),
                    DirectionThreshold = GetSettingValue<float>(AudioSetting.DirectionThreshold),
                    FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                    SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold),
                    FftSize = _config.FftSize,
                    UpdateIntervalMs = _config.UpdateIntervalMs,
                    PreferredDeviceId = _config.PreferredDeviceId,
                    FrequencyBands = _config.FrequencyBands
                }
            };

            // Apply global settings
            newConfig = newConfig with
            {
                EnableAGC = GetSettingValue<bool>(AudioSetting.EnableAGC),
                FrequencyBands = AudioConstants.DEFAULT_FREQUENCY_BANDS,
                SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold)
            };

            if (!ConfigurationsEqual(newConfig, _config))
            {
                _config = newConfig;
                _configurationChanged = true;
            }
        }
    }

    private bool ConfigurationsEqual(IAudioConfiguration a, IAudioConfiguration b) =>
        a.Gain == b.Gain &&
        a.EnableAGC == b.EnableAGC &&
        a.Smoothing == b.Smoothing &&
        a.DirectionThreshold == b.DirectionThreshold &&
        a.FrequencySmoothing == b.FrequencySmoothing &&
        a.SpikeThreshold == b.SpikeThreshold &&
        a.FftSize == b.FftSize;

    private void ApplyConfigurationToProcessor()
    {
        if (_audioProcessor == null) return;

        lock (_configLock)
        {
            try
            {
                _audioProcessor.UpdateGain(_config.Gain);
                _audioProcessor.UpdateSmoothing(_config.Smoothing);

                // Use FrequencyBandInfo to get enabled bands
                var enabledBands = FrequencyBandInfo
                    .Select(band => GetSettingValue<bool>(band.Setting))
                    .ToArray();
                
                _audioProcessor.ConfigureFrequencyBands(_config.FrequencySmoothing, enabledBands);
                _configurationChanged = false;
            }
            catch (Exception ex)
            {
                Log($"Error applying configuration: {ex.Message}");
            }
        }
    }

    protected override void OnRegisteredParameterReceived(RegisteredParameter parameter)
    {
        switch ((AudioParameter)parameter.Lookup)
        {
            case AudioParameter.AudioDirection:
                _currentDirection = parameter.GetValue<float>();
                break;
            case AudioParameter.AudioVolume:
                _currentVolume = parameter.GetValue<float>();
                break;
        }
    }

    private async void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_audioProcessor == null || !_audioProcessor.IsActive) 
            return;

        if (!await _processingLock.WaitAsync(PROCESSING_LOCK_TIMEOUT_MS))
            return;

        try
        {
            if (_configurationChanged)
            {
                ApplyConfigurationToProcessor();
            }

            var (bands, volume, direction, spike) = _audioProcessor.ProcessAudioData(
                e, GetSettingValue<bool>(AudioSetting.ScaleFrequencyWithVolume));

            var now = DateTime.Now;
            
            // Update volume if changed enough
            if (ShouldUpdateParameter(volume, _currentVolume, _lastVolumeUpdate, now))
            {
                _currentVolume = volume;
                SendParameter(AudioParameter.AudioVolume, volume);
                _lastVolumeUpdate = now;
            }

            // Update direction if changed enough
            if (ShouldUpdateParameter(direction, _currentDirection, _lastDirectionUpdate, now))
            {
                _currentDirection = direction;
                SendParameter(AudioParameter.AudioDirection, direction);
                _lastDirectionUpdate = now;
            }

            // Update frequency bands
            if (bands.Length >= FrequencyBandInfo.Length)
            {
                for (int i = 0; i < FrequencyBandInfo.Length; i++)
                {
                    var bandInfo = FrequencyBandInfo[i];
                    var value = GetSettingValue<bool>(bandInfo.Setting) ? Math.Min(bands[i], 1.0f) : 0f;
                    SendParameter(bandInfo.Parameter, value);
                }
            }

            // Update spike state
            SendParameter(AudioParameter.AudioSpike, spike);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private bool ShouldUpdateParameter(float newValue, float currentValue, DateTime lastUpdate, DateTime now) =>
        Math.Abs(newValue - currentValue) > 0.0001f && 
        (now - lastUpdate).TotalMilliseconds >= _config.UpdateIntervalMs;

    [ModuleUpdate(ModuleUpdateMode.Custom, true, 33)]
    private void UpdateAudio()
    {
        try
        {
            if (_deviceManager == null || !_deviceManager.IsInitialized)
            {
                Log("Audio device needs initialization");
                OnModuleStop();
                _ = OnModuleStart();
                return;
            }

            UpdateConfigurationFromSettings();
        }
        catch (Exception ex)
        {
            Log($"Error in update loop: {ex.Message}");
        }
    }

    private void OnModuleSettingChanged(string settingId)
    {
        if (!Enum.TryParse<AudioSetting>(settingId, out var audioSetting))
            return;
            
        switch (audioSetting)
        {
            case AudioSetting.EnableEnhancedDirection:
                if (_audioProcessor != null)
                    _audioProcessor.EnableEnhancedDirection(GetSettingValue<bool>(AudioSetting.EnableEnhancedDirection));
                break;
                
            case AudioSetting.EnablePhaseAnalysis:
                UpdateDirectionCalculationSettings();
                break;
        }
        _configurationChanged = true;
    }

    private void UpdateDirectionCalculationSettings()
    {
        if (_audioProcessor == null) return;
        
        float magnitudeWeight = 1.0f - GetSettingValue<float>(AudioSetting.MagnitudePhaseRatio);
        bool enablePhaseAnalysis = GetSettingValue<bool>(AudioSetting.EnablePhaseAnalysis);
        _audioProcessor.ConfigureEnhancedDirection(magnitudeWeight, enablePhaseAnalysis);
    }
} 