using NAudio.CoreAudioApi;
using NAudio.Wave;
using VRCOSC.App.SDK.Modules;

namespace CrookedToe.Modules.OSCAudioReaction.AudioProcessing;

public interface IAudioConfiguration
{
    // Immutable configuration properties
    float Gain { get; init; }
    bool EnableAGC { get; init; }
    float Smoothing { get; init; }
    float DirectionThreshold { get; init; }
    int UpdateIntervalMs { get; init; }
    string? PreferredDeviceId { get; init; }
    int FrequencyBands { get; init; }
    float FrequencySmoothing { get; init; }
    int FftSize { get; init; }
    float SpikeThreshold { get; init; }
}

public interface IAudioDeviceManager : IDisposable
{
    bool IsInitialized { get; }
    string? CurrentDeviceName { get; }
    WasapiLoopbackCapture? AudioCapture { get; }
    Task<bool> InitializeDefaultDeviceAsync();
    Task<bool> InitializeDeviceAsync(string deviceId);
    void StopCapture();
    IEnumerable<MMDevice> GetAvailableDevices();
    event EventHandler<WaveInEventArgs>? DataAvailable;
}

public interface IAudioProcessor : IDisposable
{
    // Read-only state properties
    float CurrentVolume { get; }
    float CurrentDirection { get; }
    float CurrentGain { get; }
    float CurrentRms { get; }
    float[] FrequencyBands { get; }
    bool IsActive { get; }
    bool HasSpike { get; }
    bool EnhancedDirectionEnabled { get; }
    bool HeadOrientationEnabled { get; }

    // State mutation methods
    (float[] bands, float volume, float direction, bool spike) ProcessAudioData(WaveInEventArgs e, bool scaleWithVolume);
    void Reset();
    void UpdateEnabledBands(bool[] enabledBands);
    void UpdateGain(float gain);
    void UpdateSmoothing(float smoothing);
    void ConfigureFrequencyBands(float smoothing, bool[] enabledBands);
    
    // Enhanced direction methods
    void SetOpenVRManager(OpenVRManager? manager);
    void EnableEnhancedDirection(bool enabled);
    void EnableHeadOrientation(bool enabled);
    void ConfigureEnhancedDirection(float magnitudeWeight, bool enablePhaseAnalysis);
}

public interface IAudioFactory
{
    IAudioProcessor CreateProcessor(IAudioConfiguration config, int bytesPerSample);
    IAudioDeviceManager CreateDeviceManager(IAudioConfiguration config, OSCAudioDirectionModule module);
}

[Serializable]
public class AudioDeviceException : Exception
{
    public AudioDeviceException() { }
    public AudioDeviceException(string message) : base(message) { }
    public AudioDeviceException(string message, Exception inner) : base(message, inner) { }
}

[Serializable]
public class AudioProcessingException : Exception
{
    public AudioProcessingException() { }
    public AudioProcessingException(string message) : base(message) { }
    public AudioProcessingException(string message, Exception inner) : base(message, inner) { }
}

[Serializable]
public class AudioConfigurationException : Exception
{
    public AudioConfigurationException() { }
    public AudioConfigurationException(string message) : base(message) { }
    public AudioConfigurationException(string message, Exception inner) : base(message, inner) { }
} 