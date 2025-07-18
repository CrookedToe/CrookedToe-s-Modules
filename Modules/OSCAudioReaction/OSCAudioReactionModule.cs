using NAudio.Wave;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;

namespace CrookedToe.Modules.OSCAudioReaction;

/// <summary>
/// OSC Audio Reaction Module - Captures system audio and sends comprehensive analysis to VRChat
/// </summary>
[ModuleTitle("OSC Audio Reaction")]
[ModuleDescription("Comprehensive audio analysis with direction, volume, frequency bands, and intelligent spike detection")]
[ModuleType(ModuleType.Generic)]
public class OSCAudioReactionModule : Module
{
    #region Enums

    private enum AudioParameter 
    { 
        AudioDirection, 
        AudioVolume, 
        AudioSpike, 
        SubBassVolume, 
        BassVolume, 
        LowMidVolume, 
        MidVolume, 
        UpperMidVolume, 
        PresenceVolume, 
        BrillianceVolume 
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
        SpikeHoldDuration, 
        EnableSubBass, 
        EnableBass, 
        EnableLowMid, 
        EnableMid, 
        EnableUpperMid, 
        EnablePresence, 
        EnableBrilliance, 
        FrequencySmoothing, 
        EnableEnhancedDirection, 
        MagnitudePhaseRatio, 
        EnablePhaseAnalysis,
        EnableDirectionalPause,
        DirectionalPauseFactor,
        EnableHabituation, 
        HabituationIncrease, 
        HabituationDecayRate, 
        HabituationThreshold 
    }
    
    private enum PresetSelection 
    { 
        Custom, 
        Default, 
        LowLatency, 
        VoiceOptimized, 
        HighSmoothing, 
        MusicOptimized 
    }

    #endregion

    #region Private Fields

    private AudioProcessor? _audioProcessor;
    private SimpleAudioDeviceManager? _audioDeviceManager;
    
    private float _currentVolumeLevel;
    private float _currentDirectionValue = 0.5f;
    private int _deviceRecoveryAttempts = 0;
    private DateTime _lastUpdateTime = DateTime.Now;
    private int _totalAudioFramesProcessed = 0;
    private DateTime _lastPerformanceLog = DateTime.Now;

    #endregion

    #region Module Lifecycle

    protected override void OnPreLoad()
    {
        RegisterAudioParameters();
        CreateAudioSettings();
        CreateAudioSettingsGroups();
        LogDebug("Module pre-loaded with parameters and settings registered");
    }

    protected override void OnPostLoad()
    {
        LogDebug("Module post-load completed");
    }

    protected override async Task<bool> OnModuleStart()
    {
        try
        {
            Log("Starting OSC Audio Reaction module...");
            
            // Initialize audio device manager
            _audioDeviceManager = new SimpleAudioDeviceManager(this);
            
            if (!await _audioDeviceManager.InitializeDefaultDevice())
            {
                Log("Failed to initialize default audio device");
                return false;
            }

            // Validate audio format
            var waveFormat = _audioDeviceManager.AudioCapture?.WaveFormat;
            if (waveFormat == null)
            {
                Log("No audio wave format available after initialization");
                return false;
            }

            Log($"Audio initialized: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels}ch");
            
            // Initialize audio processor
            var audioSettings = GetCurrentAudioSettings();
            _audioProcessor = new AudioProcessor(audioSettings);
            
            // Setup event handling
            _audioDeviceManager.DataAvailable += OnAudioDataAvailable;
            
            // Reset counters and state
            ResetModuleState();
            
            // Start audio capture
            _audioDeviceManager.StartCapture();
            
            // Check if capture actually started
            if (_audioDeviceManager.IsCapturing)
            {
                Log("OSC Audio Reaction module started successfully");
            }
            else
            {
                Log("OSC Audio Reaction module started with audio capture issues - check device availability");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Log($"Module start failed: {ex.Message}");
            LogDebug($"Start error details: {ex}");
            
            await CleanupAfterError();
            return false;
        }
    }

    protected override Task OnModuleStop()
    {
        Log("Stopping OSC Audio Reaction module...");
        
        CleanupAudioResources();
        ResetParametersToSafeValues();
        ClearModuleState();
        
        Log("OSC Audio Reaction module stopped");
        return Task.CompletedTask;
    }

    private async Task CleanupAfterError()
    {
        try
        {
            LogDebug("Performing error cleanup...");
            
            _audioDeviceManager?.StopCapture();
            await Task.Delay(100); // Brief delay for cleanup
            _audioDeviceManager?.Dispose();
            _audioProcessor?.Dispose();
            
            LogDebug("Error cleanup completed");
        }
        catch (Exception cleanupEx)
        {
            LogDebug($"Error during cleanup: {cleanupEx.Message}");
        }
    }

    private void CleanupAudioResources()
    {
        try
        {
            LogDebug("Cleaning up audio resources...");
            
            // Remove event handlers first
            if (_audioDeviceManager != null)
            {
                _audioDeviceManager.DataAvailable -= OnAudioDataAvailable;
            }
            
            // Stop capture
            _audioDeviceManager?.StopCapture();
            
            // Brief delay for pending operations
            System.Threading.Thread.Sleep(100);
            
            // Dispose in order: processor first, then device manager
            _audioProcessor?.Dispose();
            _audioProcessor = null;
            
            _audioDeviceManager?.Dispose();
            _audioDeviceManager = null;
            
            LogDebug("Audio resources cleaned up");
        }
        catch (Exception ex)
        {
            Log($"Error during cleanup: {ex.Message}");
            LogDebug($"Cleanup error details: {ex}");
            
            // Force clear references
            _audioProcessor = null;
            _audioDeviceManager = null;
        }
    }

    private void ResetParametersToSafeValues()
    {
        try
        {
            LogDebug("Resetting parameters to safe values...");
            
            // Reset main parameters
            SendParameter(AudioParameter.AudioVolume, 0f);
            SendParameter(AudioParameter.AudioDirection, 0.5f);
            SendParameter(AudioParameter.AudioSpike, false);
            
            // Reset frequency bands
            SendParameter(AudioParameter.SubBassVolume, 0f);
            SendParameter(AudioParameter.BassVolume, 0f);
            SendParameter(AudioParameter.LowMidVolume, 0f);
            SendParameter(AudioParameter.MidVolume, 0f);
            SendParameter(AudioParameter.UpperMidVolume, 0f);
            SendParameter(AudioParameter.PresenceVolume, 0f);
            SendParameter(AudioParameter.BrillianceVolume, 0f);
            
            LogDebug("Parameters reset to safe values");
        }
        catch (Exception ex)
        {
            Log($"Error resetting parameters: {ex.Message}");
        }
    }

    private void ClearModuleState()
    {
        _currentVolumeLevel = 0f;
        _currentDirectionValue = 0.5f;
        _deviceRecoveryAttempts = 0;
        _totalAudioFramesProcessed = 0;
        _lastUpdateTime = DateTime.Now;
        _lastPerformanceLog = DateTime.Now;
        
        LogDebug("Module state cleared");
    }

    #endregion

    #region Parameter and Settings Registration

    private void RegisterAudioParameters()
    {
        // Main parameters
        RegisterParameter<float>(AudioParameter.AudioDirection, "audio_direction", 
            ParameterMode.Write, "Audio Direction", "0=left, 0.5=center, 1=right");
        RegisterParameter<float>(AudioParameter.AudioVolume, "audio_volume", 
            ParameterMode.Write, "Audio Volume", "0=silent, 1=loud");
        RegisterParameter<bool>(AudioParameter.AudioSpike, "audio_spike", 
            ParameterMode.Write, "Audio Spike", "True when sudden volume increase detected");

        // Frequency band parameters
        RegisterParameter<float>(AudioParameter.SubBassVolume, "audio_subbass", 
            ParameterMode.Write, "Sub Bass Volume (20-60Hz)", "Deep bass frequencies");
        RegisterParameter<float>(AudioParameter.BassVolume, "audio_bass", 
            ParameterMode.Write, "Bass Volume (60-250Hz)", "Bass frequencies");
        RegisterParameter<float>(AudioParameter.LowMidVolume, "audio_lowmid", 
            ParameterMode.Write, "Low Mid Volume (250-500Hz)", "Lower midrange");
        RegisterParameter<float>(AudioParameter.MidVolume, "audio_mid", 
            ParameterMode.Write, "Mid Volume (500-2000Hz)", "Midrange frequencies");
        RegisterParameter<float>(AudioParameter.UpperMidVolume, "audio_uppermid", 
            ParameterMode.Write, "Upper Mid Volume (2000-4000Hz)", "Upper midrange");
        RegisterParameter<float>(AudioParameter.PresenceVolume, "audio_presence", 
            ParameterMode.Write, "Presence Volume (4000-6000Hz)", "Presence frequencies");
        RegisterParameter<float>(AudioParameter.BrillianceVolume, "audio_brilliance", 
            ParameterMode.Write, "Brilliance Volume (6000-25000Hz)", "High frequencies");
    }

    private void CreateAudioSettings()
    {
        // Preset selection
        CreateDropdown(AudioSetting.PresetSelection, "Audio Preset", 
            "Choose a preset configuration for different use cases", PresetSelection.Default);

        // Basic settings
        CreateSlider(AudioSetting.Gain, "Audio Gain", 
            "Manual gain adjustment for audio input", 1.0f, 0.1f, 5.0f, 0.1f);
        CreateToggle(AudioSetting.EnableAGC, "Automatic Gain Control", 
            "Automatically adjust gain to maintain consistent levels", true);
        CreateSlider(AudioSetting.Smoothing, "Volume Smoothing", 
            "Smoothing factor for volume changes", 0.3f, 0.0f, 0.95f, 0.05f);
        CreateSlider(AudioSetting.DirectionThreshold, "Direction Threshold", 
            "Minimum volume for direction detection", 0.01f, 0.005f, 0.1f, 0.005f);

        // Spike detection settings
        CreateSlider(AudioSetting.SpikeThreshold, "Spike Sensitivity", 
            "Threshold for volume spike detection (lower = more sensitive)", 2.0f, 0.5f, 5.0f, 0.1f);
        CreateSlider(AudioSetting.SpikeHoldDuration, "Spike Hold Duration", 
            "How long to hold spike state in seconds", 0.5f, 0.1f, 2.0f, 0.1f);

        // Frequency processing
        CreateToggle(AudioSetting.ScaleFrequencyWithVolume, "Scale Frequencies with Volume", 
            "Scale frequency band outputs with overall volume", false);
        CreateSlider(AudioSetting.FrequencySmoothing, "Frequency Smoothing", 
            "Smoothing factor for frequency band analysis", 0.7f, 0.0f, 0.95f, 0.05f);

        // Frequency band toggles
        CreateToggle(AudioSetting.EnableSubBass, "Sub Bass (20-60Hz)", 
            "Enable sub bass frequency band", true);
        CreateToggle(AudioSetting.EnableBass, "Bass (60-250Hz)", 
            "Enable bass frequency band", true);
        CreateToggle(AudioSetting.EnableLowMid, "Low Mid (250-500Hz)", 
            "Enable low mid frequency band", true);
        CreateToggle(AudioSetting.EnableMid, "Mid (500-2000Hz)", 
            "Enable mid frequency band", true);
        CreateToggle(AudioSetting.EnableUpperMid, "Upper Mid (2000-4000Hz)", 
            "Enable upper mid frequency band", true);
        CreateToggle(AudioSetting.EnablePresence, "Presence (4000-6000Hz)", 
            "Enable presence frequency band", true);
        CreateToggle(AudioSetting.EnableBrilliance, "Brilliance (6000-25000Hz)", 
            "Enable brilliance frequency band", true);

        // Enhanced direction settings
        CreateToggle(AudioSetting.EnableEnhancedDirection, "Enhanced Direction Detection", 
            "Use advanced algorithm for more accurate audio direction detection", false);
        CreateToggle(AudioSetting.EnablePhaseAnalysis, "Enable Phase Analysis", 
            "Use phase difference between channels to improve direction detection", true);
        CreateSlider(AudioSetting.MagnitudePhaseRatio, "Magnitude/Phase Balance", 
            "Balance between magnitude-based (0) and phase-based (1) direction calculation", 0.7f, 0.0f, 1.0f, 0.05f);

        // Directional pause settings
        CreateToggle(AudioSetting.EnableDirectionalPause, "Enable Directional Pause", 
            "Audio direction pauses longer when further from center", false);
        CreateSlider(AudioSetting.DirectionalPauseFactor, "Directional Pause Factor", 
            "How much longer to pause at extreme directions", 1.0f, 0.1f, 5.0f, 0.1f);

        // Habituation settings
        CreateToggle(AudioSetting.EnableHabituation, "Enable Smart Spike Detection", 
            "Use habituation to ignore repetitive spikes while staying responsive to new ones", true);
        CreateSlider(AudioSetting.HabituationIncrease, "Habituation Learning Rate", 
            "How quickly the system adapts to repetitive spikes", 0.15f, 0.05f, 0.5f, 0.05f);
        CreateSlider(AudioSetting.HabituationDecayRate, "Habituation Recovery Rate", 
            "How quickly sensitivity returns during quiet periods", 0.02f, 0.005f, 0.1f, 0.005f);
        CreateSlider(AudioSetting.HabituationThreshold, "Habituation Threshold", 
            "Above this level, spikes are ignored as expected", 0.3f, 0.1f, 0.8f, 0.1f);
    }

    private void CreateAudioSettingsGroups()
    {
        CreateGroup("Basic Settings", "Core audio processing settings", 
            AudioSetting.PresetSelection, AudioSetting.Gain, AudioSetting.EnableAGC, 
            AudioSetting.Smoothing, AudioSetting.DirectionThreshold);
        
        CreateGroup("Spike Detection", "Volume spike detection settings", 
            AudioSetting.SpikeThreshold, AudioSetting.SpikeHoldDuration);
        
        CreateGroup("Frequency Bands", "Individual frequency band controls", 
            AudioSetting.ScaleFrequencyWithVolume, AudioSetting.FrequencySmoothing,
            AudioSetting.EnableSubBass, AudioSetting.EnableBass, AudioSetting.EnableLowMid, 
            AudioSetting.EnableMid, AudioSetting.EnableUpperMid, AudioSetting.EnablePresence, 
            AudioSetting.EnableBrilliance);
        
        CreateGroup("Enhanced Direction", "Advanced directional audio features", 
            AudioSetting.EnableEnhancedDirection, AudioSetting.EnablePhaseAnalysis, 
            AudioSetting.MagnitudePhaseRatio, AudioSetting.EnableDirectionalPause, 
            AudioSetting.DirectionalPauseFactor);
        
        CreateGroup("Smart Spike Detection", "Habituation-based spike detection", 
            AudioSetting.EnableHabituation, AudioSetting.HabituationIncrease, 
            AudioSetting.HabituationDecayRate, AudioSetting.HabituationThreshold);
    }

    #endregion

    #region Audio Processing

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (_audioProcessor == null)
                return;

            var result = _audioProcessor.ProcessAudio(e);
            UpdateAudioParameters(result);
            
            // Update state and counters
            _currentVolumeLevel = result.Volume;
            _currentDirectionValue = result.Direction;
            _lastUpdateTime = DateTime.Now;
            _totalAudioFramesProcessed++;
            
            // Log performance metrics occasionally (every 1000 frames)
            if (_totalAudioFramesProcessed % 1000 == 0)
            {
                var timeSinceLastLog = DateTime.Now - _lastPerformanceLog;
                if (timeSinceLastLog.TotalSeconds >= 30) // Log every 30 seconds max
                {
                    var fps = 1000 / timeSinceLastLog.TotalSeconds;
                    LogDebug($"Audio processing: {fps:F1} FPS, Volume: {result.Volume:F3}, Direction: {result.Direction:F3}");
                    _lastPerformanceLog = DateTime.Now;
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Audio processing error: {ex.Message}");
            
            // Attempt recovery with exponential backoff
            _deviceRecoveryAttempts++;
            if (_deviceRecoveryAttempts < 3)
            {
                Log($"Attempting audio device recovery (attempt {_deviceRecoveryAttempts}/3)...");
                Task.Run(async () => await AttemptDeviceRecovery());
            }
            else if (_deviceRecoveryAttempts == 3)
            {
                Log("Maximum recovery attempts reached. Audio processing may be unstable.");
            }
        }
    }

    private void UpdateAudioParameters(AudioProcessingResult result)
    {
        // Send main parameters
        SendParameter(AudioParameter.AudioVolume, RoundToSignificantDigits(result.Volume, 4));
        SendParameter(AudioParameter.AudioDirection, RoundToSignificantDigits(result.Direction, 4));
        SendParameter(AudioParameter.AudioSpike, result.Spike);
        
        // Send frequency band parameters if enabled
        if (result.FrequencyBands.Length >= 7)
        {
            if (GetSettingValue<bool>(AudioSetting.EnableSubBass))
                SendParameter(AudioParameter.SubBassVolume, RoundToSignificantDigits(result.FrequencyBands[0], 4));
            if (GetSettingValue<bool>(AudioSetting.EnableBass))
                SendParameter(AudioParameter.BassVolume, RoundToSignificantDigits(result.FrequencyBands[1], 4));
            if (GetSettingValue<bool>(AudioSetting.EnableLowMid))
                SendParameter(AudioParameter.LowMidVolume, RoundToSignificantDigits(result.FrequencyBands[2], 4));
            if (GetSettingValue<bool>(AudioSetting.EnableMid))
                SendParameter(AudioParameter.MidVolume, RoundToSignificantDigits(result.FrequencyBands[3], 4));
            if (GetSettingValue<bool>(AudioSetting.EnableUpperMid))
                SendParameter(AudioParameter.UpperMidVolume, RoundToSignificantDigits(result.FrequencyBands[4], 4));
            if (GetSettingValue<bool>(AudioSetting.EnablePresence))
                SendParameter(AudioParameter.PresenceVolume, RoundToSignificantDigits(result.FrequencyBands[5], 4));
            if (GetSettingValue<bool>(AudioSetting.EnableBrilliance))
                SendParameter(AudioParameter.BrillianceVolume, RoundToSignificantDigits(result.FrequencyBands[6], 4));
        }
    }

    private static float RoundToSignificantDigits(float value, int significantDigits)
    {
        if (value == 0f || float.IsNaN(value) || float.IsInfinity(value))
            return value;

        // Clamp extremely small values to prevent scientific notation
        const float MIN_THRESHOLD = 1e-4f;
        if (Math.Abs(value) < MIN_THRESHOLD)
            return 0f;

        int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        double scale = Math.Pow(10, significantDigits - magnitude - 1);
        float rounded = (float)(Math.Round(value * scale) / scale);
        
        return Math.Abs(rounded) < MIN_THRESHOLD ? 0f : rounded;
    }

    #endregion

    #region Configuration Management

    private AudioSettings GetCurrentAudioSettings()
    {
        var preset = GetSettingValue<PresetSelection>(AudioSetting.PresetSelection);
        
        return preset switch
        {
            PresetSelection.LowLatency => new AudioSettings
            {
                Gain = 1.0f,
                EnableAGC = true,
                Smoothing = 0.2f,
                DirectionThreshold = 0.01f,
                SpikeThreshold = 2.0f,
                SpikeHoldDuration = 0.3f,
                FrequencySmoothing = 0.5f,
                EnableEnhancedDirection = false,
                EnableHabituation = true,
                HabituationIncrease = 0.2,
                HabituationDecayRate = 0.03,
                HabituationThreshold = 0.3,
                ScaleFrequencyWithVolume = false,
                EnableSubBass = true,
                EnableBass = true,
                EnableLowMid = true,
                EnableMid = true,
                EnableUpperMid = true,
                EnablePresence = true,
                EnableBrilliance = true
            },
            PresetSelection.VoiceOptimized => new AudioSettings
            {
                Gain = 1.2f,
                EnableAGC = true,
                Smoothing = 0.4f,
                DirectionThreshold = 0.02f,
                SpikeThreshold = 1.8f,
                SpikeHoldDuration = 0.4f,
                FrequencySmoothing = 0.6f,
                EnableEnhancedDirection = true,
                EnableHabituation = true,
                HabituationIncrease = 0.1,
                HabituationDecayRate = 0.015,
                HabituationThreshold = 0.4,
                ScaleFrequencyWithVolume = false,
                EnableSubBass = false,
                EnableBass = true,
                EnableLowMid = true,
                EnableMid = true,
                EnableUpperMid = true,
                EnablePresence = true,
                EnableBrilliance = false
            },
            PresetSelection.HighSmoothing => new AudioSettings
            {
                Gain = 1.0f,
                EnableAGC = true,
                Smoothing = 0.8f,
                DirectionThreshold = 0.01f,
                SpikeThreshold = 2.5f,
                SpikeHoldDuration = 0.8f,
                FrequencySmoothing = 0.9f,
                EnableEnhancedDirection = false,
                EnableHabituation = true,
                HabituationIncrease = 0.1,
                HabituationDecayRate = 0.01,
                HabituationThreshold = 0.5,
                ScaleFrequencyWithVolume = false,
                EnableSubBass = true,
                EnableBass = true,
                EnableLowMid = true,
                EnableMid = true,
                EnableUpperMid = true,
                EnablePresence = true,
                EnableBrilliance = true
            },
            PresetSelection.MusicOptimized => new AudioSettings
            {
                Gain = 1.1f,
                EnableAGC = true,
                Smoothing = 0.5f,
                DirectionThreshold = 0.015f,
                SpikeThreshold = 2.2f,
                SpikeHoldDuration = 0.6f,
                FrequencySmoothing = 0.7f,
                EnableEnhancedDirection = true,
                EnableHabituation = true,
                HabituationIncrease = 0.12,
                HabituationDecayRate = 0.02,
                HabituationThreshold = 0.35,
                ScaleFrequencyWithVolume = true,
                EnableSubBass = true,
                EnableBass = true,
                EnableLowMid = true,
                EnableMid = true,
                EnableUpperMid = true,
                EnablePresence = true,
                EnableBrilliance = true
            },
            PresetSelection.Custom => new AudioSettings
            {
                Gain = GetSettingValue<float>(AudioSetting.Gain),
                EnableAGC = GetSettingValue<bool>(AudioSetting.EnableAGC),
                Smoothing = GetSettingValue<float>(AudioSetting.Smoothing),
                DirectionThreshold = GetSettingValue<float>(AudioSetting.DirectionThreshold),
                SpikeThreshold = GetSettingValue<float>(AudioSetting.SpikeThreshold),
                SpikeHoldDuration = GetSettingValue<float>(AudioSetting.SpikeHoldDuration),
                FrequencySmoothing = GetSettingValue<float>(AudioSetting.FrequencySmoothing),
                EnableEnhancedDirection = GetSettingValue<bool>(AudioSetting.EnableEnhancedDirection),
                MagnitudePhaseRatio = GetSettingValue<float>(AudioSetting.MagnitudePhaseRatio),
                EnablePhaseAnalysis = GetSettingValue<bool>(AudioSetting.EnablePhaseAnalysis),
                EnableDirectionalPause = GetSettingValue<bool>(AudioSetting.EnableDirectionalPause),
                DirectionalPauseFactor = GetSettingValue<float>(AudioSetting.DirectionalPauseFactor),
                EnableHabituation = GetSettingValue<bool>(AudioSetting.EnableHabituation),
                HabituationIncrease = GetSettingValue<float>(AudioSetting.HabituationIncrease),
                HabituationDecayRate = GetSettingValue<float>(AudioSetting.HabituationDecayRate),
                HabituationThreshold = GetSettingValue<float>(AudioSetting.HabituationThreshold),
                ScaleFrequencyWithVolume = GetSettingValue<bool>(AudioSetting.ScaleFrequencyWithVolume),
                EnableSubBass = GetSettingValue<bool>(AudioSetting.EnableSubBass),
                EnableBass = GetSettingValue<bool>(AudioSetting.EnableBass),
                EnableLowMid = GetSettingValue<bool>(AudioSetting.EnableLowMid),
                EnableMid = GetSettingValue<bool>(AudioSetting.EnableMid),
                EnableUpperMid = GetSettingValue<bool>(AudioSetting.EnableUpperMid),
                EnablePresence = GetSettingValue<bool>(AudioSetting.EnablePresence),
                EnableBrilliance = GetSettingValue<bool>(AudioSetting.EnableBrilliance)
            },
            _ => new AudioSettings()
        };
    }

    #endregion

    #region Helper Methods

    private void ResetModuleState()
    {
        _currentVolumeLevel = 0f;
        _currentDirectionValue = 0.5f;
        _deviceRecoveryAttempts = 0;
        _totalAudioFramesProcessed = 0;
        _lastUpdateTime = DateTime.Now;
        _lastPerformanceLog = DateTime.Now;
    }

    private async Task AttemptDeviceRecovery()
    {
        try
        {
            LogDebug("Attempting audio device recovery...");
            
            _audioDeviceManager?.StopCapture();
            await Task.Delay(1000);
            
            var deviceName = _audioDeviceManager?.CurrentDeviceName ?? "Unknown";
            if (await _audioDeviceManager?.InitializeDefaultDevice() == true)
            {
                _audioDeviceManager.StartCapture();
                _deviceRecoveryAttempts = 0;
                Log($"Audio device recovery successful: {deviceName}");
            }
            else
            {
                Log($"Audio device recovery failed: {deviceName}");
            }
        }
        catch (Exception ex)
        {
            Log($"Device recovery error: {ex.Message}");
            LogDebug($"Recovery error details: {ex}");
        }
    }

    #endregion
} 