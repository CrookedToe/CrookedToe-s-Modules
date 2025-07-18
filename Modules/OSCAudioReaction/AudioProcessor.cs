using System.Numerics;
using NAudio.Wave;
using NWaves.Transforms;
using NWaves.Windows;
using NWaves.Signals;

namespace CrookedToe.Modules.OSCAudioReaction;

/// <summary>
/// Audio processor that handles FFT analysis, direction detection, and frequency band processing
/// </summary>
public sealed class AudioProcessor : IDisposable
{
    #region Private Fields

    private readonly AudioSettings _settings;
    private readonly SpikeDetector _spikeDetector;
    private readonly FrequencyBandProcessor _frequencyProcessor;
    private readonly object _processingLock = new();
    private readonly object _fftLock = new();
    private volatile bool _disposed;

    // Audio state
    private float _currentVolume;
    private float _currentDirection = 0.5f;
    private float _currentGain;
    private float _currentRms;

    // FFT processing components
    private RealFft _fft;
    private float[] _fftBuffer;
    private Complex[] _spectrum;
    private float[] _window;
    private int _fftSize;

    // Enhanced direction processing
    private bool _enhancedDirectionEnabled;
    private float _magnitudeWeight = 0.7f;
    private bool _enablePhaseAnalysis = true;

    // Volume history for smoothing
    private readonly Queue<float> _volumeHistory = new(3);
    private readonly Queue<float> _directionHistory = new(3);
    private readonly Queue<float> _recentVolumes = new(3);

    // Frequency band smoothing state
    private float[] _smoothedFrequencyBands = new float[7];

    // Directional pause state
    private DateTime _lastDirectionUpdate = DateTime.Now;
    private float _directionPauseMultiplier = 1.0f;

    // Performance tracking
    private int _processedFrames = 0;
    private DateTime _lastPerformanceLog = DateTime.Now;

    #endregion

    #region Constructor

    public AudioProcessor(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _currentGain = settings.Gain;
        _fftSize = 8192;
        
        InitializeFFTComponents();
        _spikeDetector = new SpikeDetector(settings);
        _frequencyProcessor = new FrequencyBandProcessor(settings);

        // Configure enhanced direction processing
        _enhancedDirectionEnabled = settings.EnableEnhancedDirection;
        _magnitudeWeight = settings.MagnitudePhaseRatio;
        _enablePhaseAnalysis = settings.EnablePhaseAnalysis;
    }

    private void InitializeFFTComponents()
    {
        _fft = new RealFft(_fftSize);
        _fftBuffer = new float[_fftSize];
        _spectrum = new Complex[_fftSize / 2 + 1];
        _window = Window.Hamming(_fftSize);
    }

    #endregion

    #region Properties

    public float CurrentVolume => _currentVolume;
    public float CurrentDirection => _currentDirection;
    public float CurrentGain => _currentGain;
    public float CurrentRms => _currentRms;
    public bool IsActive => _currentVolume > 0.001f;

    #endregion

    #region Configuration Methods

    public void UpdateGain(float gain)
    {
        _currentGain = Math.Clamp(gain, 0.1f, 5.0f);
    }

    public void EnableEnhancedDirection(bool enabled)
    {
        _enhancedDirectionEnabled = enabled;
    }

    public void ConfigureEnhancedDirection(float magnitudeWeight, bool enablePhaseAnalysis)
    {
        _magnitudeWeight = Math.Clamp(magnitudeWeight, 0.0f, 1.0f);
        _enablePhaseAnalysis = enablePhaseAnalysis;
    }

    #endregion

    #region Audio Processing

    public AudioProcessingResult ProcessAudio(WaveInEventArgs e)
    {
        if (_disposed || e?.Buffer == null || e.BytesRecorded == 0)
            return AudioProcessingResult.Empty;

        lock (_processingLock)
        {
            try
            {
                var result = ProcessAudioInternal(e);
                
                // Track performance metrics
                _processedFrames++;
                if (_processedFrames % 500 == 0)
                {
                    LogPerformanceMetrics();
                }
                
                return result;
            }
            catch (Exception ex)
            {
                LogError($"Audio processing failed: {ex.Message}");
                return AudioProcessingResult.Empty;
            }
        }
    }

    private AudioProcessingResult ProcessAudioInternal(WaveInEventArgs e)
    {
        int samplesAvailable = e.BytesRecorded / 4;
        if (samplesAvailable < 128)
            return AudioProcessingResult.Empty;

        AdaptFftSize(samplesAvailable);
        
        var (leftSamples, rightSamples, monoSamples) = ExtractSamples(e, samplesAvailable);
        
        // Process FFT for each channel
        var leftSpectrum = ProcessSpectrum(leftSamples);
        var rightSpectrum = ProcessSpectrum(rightSamples);
        var monoSpectrum = ProcessSpectrum(monoSamples);

        // Calculate audio metrics
        float rawVolume = CalculateVolume(monoSamples);
        float direction = CalculateDirection(leftSpectrum, rightSpectrum, rawVolume);
        var bands = ProcessFrequencyBands(monoSpectrum, _settings.ScaleFrequencyWithVolume);
        bool spike = _spikeDetector.DetectSpike(rawVolume);

        // Apply gain control
        ApplyGainControl(rawVolume);

        // Apply gain and soft clipping
        float volume = ApplyGainAndClipping(rawVolume);
        
        UpdateState(volume, direction);

        return new AudioProcessingResult
        {
            Volume = volume,
            Direction = direction,
            FrequencyBands = bands,
            Spike = spike
        };
    }

    private (float[] left, float[] right, float[] mono) ExtractSamples(WaveInEventArgs e, int samplesAvailable)
    {
        int sampleCount = Math.Min(samplesAvailable / 2, _fftSize);
        var leftSamples = new float[_fftSize];
        var rightSamples = new float[_fftSize];
        var monoSamples = new float[_fftSize];
        
        // Zero-pad unused buffer space
        Array.Clear(leftSamples, sampleCount, _fftSize - sampleCount);
        Array.Clear(rightSamples, sampleCount, _fftSize - sampleCount);
        Array.Clear(monoSamples, sampleCount, _fftSize - sampleCount);
        
        // Extract stereo samples and create mono mix
        for (int i = 0; i < sampleCount; i++)
        {
            int sampleIndex = i * 2;
            leftSamples[i] = BitConverter.ToSingle(e.Buffer, sampleIndex * 4);
            rightSamples[i] = BitConverter.ToSingle(e.Buffer, (sampleIndex + 1) * 4);
            monoSamples[i] = (leftSamples[i] + rightSamples[i]) / 2f;
        }
        
        return (leftSamples, rightSamples, monoSamples);
    }

    private Complex[] ProcessSpectrum(float[] samples)
    {
        lock (_fftLock)
        {
            Array.Copy(samples, _fftBuffer, Math.Min(samples.Length, _fftBuffer.Length));
            
            // Apply windowing
            for (int i = 0; i < _fftBuffer.Length; i++)
            {
                _fftBuffer[i] *= _window[i];
            }
            
            var realSpectrum = new float[_fftBuffer.Length];
            var imagSpectrum = new float[_fftBuffer.Length];
            Array.Copy(_fftBuffer, realSpectrum, _fftBuffer.Length);
            _fft.Direct(realSpectrum, realSpectrum, imagSpectrum);
            
            var spectrum = new Complex[_spectrum.Length];
            float normalizationFactor = 2.0f / _fftBuffer.Length;
            
            for (int i = 0; i < spectrum.Length; i++)
            {
                spectrum[i] = new Complex(
                    realSpectrum[i] * normalizationFactor,
                    imagSpectrum[i] * normalizationFactor
                );
            }
            
            // Apply DC and Nyquist corrections
            if (spectrum.Length > 0)
                spectrum[0] = spectrum[0] * 0.5f;
            if (spectrum.Length > 1)
                spectrum[spectrum.Length - 1] = spectrum[spectrum.Length - 1] * 0.5f;
                
            return spectrum;
        }
    }

    private float CalculateVolume(float[] samples)
    {
        if (samples.Length == 0)
            return 0f;

        // Calculate RMS
        var signal = new DiscreteSignal(48000, samples);
        float rms = (float)signal.Rms();
        _currentRms = rms;
        
        // Calculate spectral power
        var powerSpectrum = ProcessPowerSpectrum(samples);
        float spectralPower = 0f;
        
        int maxBin = Math.Min(powerSpectrum.Length - 1, FrequencyToBin(20000));
        
        for (int i = 0; i <= maxBin; i++)
        {
            spectralPower += powerSpectrum[i];
        }
        
        if (maxBin > 0)
        {
            spectralPower = MathF.Sqrt(spectralPower / maxBin);
        }

        // Combine RMS and spectral power
        rms *= 4.0f;
        spectralPower *= 0.25f;
        float rawVolume = (rms * 0.7f + spectralPower * 0.3f);

        return rawVolume;
    }

    private float[] ProcessPowerSpectrum(float[] samples)
    {
        var windowedSamples = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            windowedSamples[i] = samples[i] * _window[i % _window.Length];
        }

        var realSpectrum = new float[_fftBuffer.Length];
        var imagSpectrum = new float[_fftBuffer.Length];
        Array.Copy(windowedSamples, realSpectrum, Math.Min(windowedSamples.Length, realSpectrum.Length));
        _fft.Direct(realSpectrum, realSpectrum, imagSpectrum);

        var powerSpectrum = new float[realSpectrum.Length / 2 + 1];
        for (int i = 0; i < powerSpectrum.Length; i++)
        {
            powerSpectrum[i] = (realSpectrum[i] * realSpectrum[i] + imagSpectrum[i] * imagSpectrum[i]);
        }

        return powerSpectrum;
    }

    private float CalculateDirection(Complex[] leftSpectrum, Complex[] rightSpectrum, float volume)
    {
        if (volume < _settings.DirectionThreshold)
        {
            return ApplyDirectionalPause(0.5f);
        }

        float direction = _enhancedDirectionEnabled 
            ? CalculateEnhancedDirection(leftSpectrum, rightSpectrum)
            : CalculateBasicDirection(leftSpectrum, rightSpectrum);

        return ApplyDirectionalPause(direction);
    }

    private float ApplyDirectionalPause(float direction)
    {
        if (!_settings.EnableDirectionalPause)
            return direction;

        var now = DateTime.Now;
        var timeSinceLastUpdate = (now - _lastDirectionUpdate).TotalSeconds;

        float deviationFromCenter = Math.Abs(direction - 0.5f) * 2f;
        _directionPauseMultiplier = 1.0f + (deviationFromCenter * (_settings.DirectionalPauseFactor - 1.0f));

        float requiredPauseTime = 0.016f * _directionPauseMultiplier;

        if (timeSinceLastUpdate >= requiredPauseTime)
        {
            _lastDirectionUpdate = now;
            return direction;
        }

        return _currentDirection;
    }

    private float CalculateBasicDirection(Complex[] leftSpectrum, Complex[] rightSpectrum)
    {
        float leftPower = 0f;
        float rightPower = 0f;
        int enabledBandCount = 0;

        for (int band = 0; band < 7; band++)
        {
            if (!IsBandEnabled(band)) continue;
            enabledBandCount++;

            var (startFreq, endFreq) = GetFrequencyRange(band);
            int startBin = FrequencyToBin(startFreq);
            int endBin = FrequencyToBin(endFreq);

            for (int bin = startBin; bin <= endBin && bin < leftSpectrum.Length; bin++)
            {
                leftPower += (float)leftSpectrum[bin].Magnitude;
                rightPower += (float)rightSpectrum[bin].Magnitude;
            }
        }

        if (enabledBandCount == 0 || leftPower + rightPower < 0.001f)
            return 0.5f;

        return rightPower / (leftPower + rightPower);
    }

    private float CalculateEnhancedDirection(Complex[] leftSpectrum, Complex[] rightSpectrum)
    {
        int numBins = leftSpectrum.Length;
        float sampleRate = 48000f;
        float binWidth = sampleRate / (2f * (numBins - 1));
        
        float totalWeight = 0f;
        float weightedDirectionSum = 0f;

        var frequencyWeights = new float[] { 0.8f, 1.0f, 1.2f, 1.5f, 1.3f, 1.1f, 0.9f };
        var frequencyRanges = new (float low, float high)[]
        {
            (20f, 60f), (60f, 250f), (250f, 500f), (500f, 2000f),
            (2000f, 4000f), (4000f, 6000f), (6000f, 25000f)
        };

        for (int band = 0; band < frequencyRanges.Length; band++)
        {
            var (startFreq, endFreq) = frequencyRanges[band];
            int startBin = Math.Max(1, (int)Math.Floor(startFreq / binWidth));
            int endBin = Math.Min(numBins - 1, (int)Math.Ceiling(endFreq / binWidth));
            
            float bandWeight = frequencyWeights[band];
            
            for (int bin = startBin; bin <= endBin; bin++)
            {
                var left = leftSpectrum[bin];
                var right = rightSpectrum[bin];
                
                float leftMag = (float)left.Magnitude;
                float rightMag = (float)right.Magnitude;
                float totalMag = leftMag + rightMag;
                
                if (totalMag < 1e-6f) continue;
                
                float magDirection = rightMag / totalMag;
                
                float phaseDirection = 0.5f;
                if (_enablePhaseAnalysis)
                {
                    float leftPhase = (float)Math.Atan2(left.Imaginary, left.Real);
                    float rightPhase = (float)Math.Atan2(right.Imaginary, right.Real);
                    float phaseDiff = rightPhase - leftPhase;
                    
                    // Normalize phase difference
                    while (phaseDiff > Math.PI) phaseDiff -= 2 * (float)Math.PI;
                    while (phaseDiff < -Math.PI) phaseDiff += 2 * (float)Math.PI;
                    
                    phaseDirection = (phaseDiff + (float)Math.PI) / (2 * (float)Math.PI);
                }
                
                float combinedDirection = (_magnitudeWeight * magDirection) + 
                                        ((1f - _magnitudeWeight) * phaseDirection);
                
                float binPower = totalMag;
                weightedDirectionSum += combinedDirection * binPower * bandWeight;
                totalWeight += binPower * bandWeight;
            }
        }
        
        if (totalWeight < 1e-6f)
            return 0.5f;
        
        return Math.Clamp(weightedDirectionSum / totalWeight, 0f, 1f);
    }

    private float[] ProcessFrequencyBands(Complex[] spectrum, bool scaleWithVolume)
    {
        int numBins = spectrum.Length;
        float totalPower = 0f;
        float sampleRate = 48000f;
        float binWidth = sampleRate / (2f * (numBins - 1));

        var rawBands = new float[7];
        
        for (int band = 0; band < 7; band++)
        {
            if (!IsBandEnabled(band)) continue;

            var (lowFreq, highFreq) = GetFrequencyRange(band);
            
            int startBin = Math.Max(1, (int)Math.Floor(lowFreq / binWidth));
            int endBin = Math.Min(numBins - 1, (int)Math.Ceiling(highFreq / binWidth));
            
            float bandPower = 0f;
            int binsInBand = 0;

            for (int bin = startBin; bin <= endBin; bin++)
            {
                float binFreq = bin * binWidth;
                if (binFreq >= lowFreq && binFreq <= highFreq)
                {
                    float magnitude = (float)spectrum[bin].Magnitude;
                    bandPower += magnitude * magnitude;
                    binsInBand++;
                }
            }
            
            if (binsInBand > 0)
            {
                bandPower = MathF.Sqrt(bandPower / binsInBand);
                rawBands[band] = bandPower;
                totalPower += bandPower;
            }
            else
            {
                rawBands[band] = 0f;
            }
        }

        // Apply normalization and smoothing
        var processedBands = new float[7];
        if (totalPower > 0.001f)
        {
            for (int band = 0; band < 7; band++)
            {
                if (!IsBandEnabled(band)) 
                {
                    processedBands[band] = 0f;
                    continue;
                }

                float normalizedPower = scaleWithVolume 
                    ? rawBands[band] * _currentVolume
                    : rawBands[band] / totalPower;

                float smoothing = _settings.FrequencySmoothing;
                _smoothedFrequencyBands[band] = _smoothedFrequencyBands[band] * smoothing + 
                                              normalizedPower * (1f - smoothing);
                
                processedBands[band] = _smoothedFrequencyBands[band];
            }
        }
        else
        {
            // Fade to zero with smoothing
            for (int band = 0; band < 7; band++)
            {
                float smoothing = _settings.FrequencySmoothing;
                _smoothedFrequencyBands[band] = _smoothedFrequencyBands[band] * smoothing;
                processedBands[band] = _smoothedFrequencyBands[band];
            }
        }

        return processedBands;
    }

    private void ApplyGainControl(float rawVolume)
    {
        if (_settings.EnableAGC && rawVolume > 0.001f)
        {
            float targetLevel = 0.5f;
            float currentLevel = rawVolume * _currentGain;
            float gainAdjustment = targetLevel / Math.Max(0.001f, currentLevel);
            
            // Asymmetric adjustment speeds
            float adjustmentSpeed = gainAdjustment > 1.0f ? 0.1f : 0.3f;
            _currentGain = _currentGain * (1 - adjustmentSpeed) + (_settings.Gain * gainAdjustment) * adjustmentSpeed;
            _currentGain = Math.Clamp(_currentGain, 0.1f, 5.0f);
        }
        else if (!_settings.EnableAGC)
        {
            _currentGain = _settings.Gain;
        }
    }

    private float ApplyGainAndClipping(float rawVolume)
    {
        float volume = rawVolume * _currentGain;
        
        // Soft clipping for volumes above 1.0
        if (volume > 1.0f)
        {
            volume = 1.0f - (1.0f / (1.0f + volume - 1.0f));
        }
        
        return Math.Clamp(volume, 0f, 1f);
    }

    private void AdaptFftSize(int samplesAvailable)
    {
        int targetSize = 8192;
        
        if (targetSize != _fftSize)
        {
            lock (_fftLock)
            {
                _fftSize = targetSize;
                InitializeFFTComponents();
            }
        }
    }

    private void UpdateState(float volume, float direction)
    {
        _volumeHistory.Enqueue(volume);
        if (_volumeHistory.Count > 3)
            _volumeHistory.Dequeue();
            
        _directionHistory.Enqueue(direction);
        if (_directionHistory.Count > 3)
            _directionHistory.Dequeue();
            
        _recentVolumes.Enqueue(volume);
        if (_recentVolumes.Count > 3)
            _recentVolumes.Dequeue();
            
        // Apply smoothing
        float smoothing = _settings.Smoothing;
        _currentVolume = _currentVolume * smoothing + volume * (1 - smoothing);
        _currentDirection = _currentDirection * smoothing + direction * (1 - smoothing);
    }

    private void LogPerformanceMetrics()
    {
        var now = DateTime.Now;
        var elapsed = now - _lastPerformanceLog;
        
        if (elapsed.TotalSeconds >= 60) // Log every minute
        {
            var fps = 500 / elapsed.TotalSeconds;
            LogDebug($"Audio processor performance: {fps:F1} FPS, Gain: {_currentGain:F2}, Enhanced Direction: {_enhancedDirectionEnabled}");
            _lastPerformanceLog = now;
        }
    }

    private void LogDebug(string message)
    {
        // This would be injected in a real implementation
        // For now, we'll use console output in debug builds
        #if DEBUG
        Console.WriteLine($"[AudioProcessor] {message}");
        #endif
    }

    private void LogError(string message)
    {
        // This would be injected in a real implementation
        // For now, we'll use console output
        Console.WriteLine($"[AudioProcessor ERROR] {message}");
    }

    #endregion

    #region Helper Methods

    private (float lowFreq, float highFreq) GetFrequencyRange(int band)
    {
        return band switch
        {
            0 => (20, 60),     // Sub Bass
            1 => (60, 250),    // Bass  
            2 => (250, 500),   // Low Mids
            3 => (500, 2000),  // Mids
            4 => (2000, 4000), // Upper Mids
            5 => (4000, 6000), // Presence
            6 => (6000, 25000),// Brilliance
            _ => (0, 0)
        };
    }

    private int FrequencyToBin(float frequency)
    {
        float binWidth = 48000f / (float)_fftSize;
        int bin = (int)Math.Round(frequency / binWidth);
        return Math.Min(Math.Max(bin, 0), _fftSize / 2);
    }

    private bool IsBandEnabled(int bandIndex)
    {
        return bandIndex switch
        {
            0 => _settings.EnableSubBass,
            1 => _settings.EnableBass,
            2 => _settings.EnableLowMid,
            3 => _settings.EnableMid,
            4 => _settings.EnableUpperMid,
            5 => _settings.EnablePresence,
            6 => _settings.EnableBrilliance,
            _ => false
        };
    }

    #endregion

    #region Reset and Dispose

    public void Reset()
    {
        lock (_processingLock)
        {
            _currentVolume = 0f;
            _currentDirection = 0.5f;
            _volumeHistory.Clear();
            _directionHistory.Clear();
            _recentVolumes.Clear();
            _spikeDetector.Reset();
            _frequencyProcessor.Reset();
            Array.Clear(_smoothedFrequencyBands, 0, _smoothedFrequencyBands.Length);
            _lastDirectionUpdate = DateTime.Now;
            _directionPauseMultiplier = 1.0f;
            _processedFrames = 0;
            _lastPerformanceLog = DateTime.Now;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _spikeDetector?.Dispose();
        _frequencyProcessor?.Dispose();
        _fft = null;
        _fftBuffer = null;
        _spectrum = null;
        _window = null;
    }

    #endregion
}

/// <summary>
/// Simplified audio settings class
/// </summary>
public class AudioSettings
{
    public float Gain { get; set; } = 1.0f;
    public bool EnableAGC { get; set; } = true;
    public float Smoothing { get; set; } = 0.3f;
    public float DirectionThreshold { get; set; } = 0.01f;
    public float SpikeThreshold { get; set; } = 2.0f;
    public float SpikeHoldDuration { get; set; } = 0.5f;
    public float FrequencySmoothing { get; set; } = 0.7f;
    public bool EnableEnhancedDirection { get; set; } = false;
    public float MagnitudePhaseRatio { get; set; } = 0.7f;
    public bool EnablePhaseAnalysis { get; set; } = true;
    public bool EnableDirectionalPause { get; set; } = false;
    public float DirectionalPauseFactor { get; set; } = 1.0f;
    public bool EnableHabituation { get; set; } = true;
    public double HabituationIncrease { get; set; } = 0.15;
    public double HabituationDecayRate { get; set; } = 0.02;
    public double HabituationThreshold { get; set; } = 0.3;
    public bool ScaleFrequencyWithVolume { get; set; } = false;
    
    // Frequency band enables
    public bool EnableSubBass { get; set; } = true;
    public bool EnableBass { get; set; } = true;
    public bool EnableLowMid { get; set; } = true;
    public bool EnableMid { get; set; } = true;
    public bool EnableUpperMid { get; set; } = true;
    public bool EnablePresence { get; set; } = true;
    public bool EnableBrilliance { get; set; } = true;
}

/// <summary>
/// Audio processing result
/// </summary>
public class AudioProcessingResult
{
    public float Volume { get; set; }
    public float Direction { get; set; }
    public float[] FrequencyBands { get; set; } = Array.Empty<float>();
    public bool Spike { get; set; }
    
    public static AudioProcessingResult Empty => new()
    {
        Volume = 0f,
        Direction = 0.5f,
        FrequencyBands = new float[7],
        Spike = false
    };
}

/// <summary>
/// Simplified spike detector
/// </summary>
public class SpikeDetector : IDisposable
{
    private readonly AudioSettings _settings;
    private float _lastAverageVolume;
    private bool _currentSpike;
    private DateTime _lastSpikeTime;
    private double _habituationLevel;
    private DateTime _lastHabituationUpdate = DateTime.Now;

    public SpikeDetector(AudioSettings settings)
    {
        _settings = settings;
    }

    public bool DetectSpike(float volume)
    {
        var now = DateTime.Now;
        
        // Always update habituation regardless of spike detection
        UpdateHabituationLevel(now);

        // Check for spike
        bool processorDetectedSpike = volume > _lastAverageVolume * _settings.SpikeThreshold && volume > 0.1f;
        
        if (processorDetectedSpike && ShouldActivateNewSpike())
        {
            ActivateNewSpike(now);
        }
        else if (_currentSpike && ShouldDeactivateCurrentSpike(now))
        {
            DeactivateCurrentSpike();
        }

        // Update running average
        _lastAverageVolume = _lastAverageVolume * 0.9f + volume * 0.1f;

        return _currentSpike;
    }

    private void UpdateHabituationLevel(DateTime currentTime)
    {
        if (!_settings.EnableHabituation)
            return;
            
        if (_lastHabituationUpdate == DateTime.MinValue)
        {
            _lastHabituationUpdate = currentTime;
            return;
        }

        var timeDeltaSeconds = (currentTime - _lastHabituationUpdate).TotalSeconds;
        if (timeDeltaSeconds <= 0) return;

        // Apply exponential decay: habituation naturally decreases over time
        var decayFactor = Math.Exp(-_settings.HabituationDecayRate * timeDeltaSeconds);
        _habituationLevel = Math.Max(0.0, _habituationLevel * decayFactor);

        _lastHabituationUpdate = currentTime;
    }

    private bool ShouldActivateNewSpike()
    {
        if (!_settings.EnableHabituation)
            return true;
            
        // If habituation level is above threshold, ignore the spike (it's expected)
        return _habituationLevel < _settings.HabituationThreshold;
    }

    private void ActivateNewSpike(DateTime currentTime)
    {
        _lastSpikeTime = currentTime;
        _currentSpike = true;

        // Increase habituation when a spike is activated
        if (_settings.EnableHabituation)
        {
            _habituationLevel = Math.Min(1.0, _habituationLevel + _settings.HabituationIncrease);
        }
    }

    private bool ShouldDeactivateCurrentSpike(DateTime currentTime)
    {
        var timeSinceSpike = (currentTime - _lastSpikeTime).TotalSeconds;
        return timeSinceSpike >= _settings.SpikeHoldDuration;
    }

    private void DeactivateCurrentSpike()
    {
        _currentSpike = false;
    }

    public void ForceHabituationUpdate()
    {
        if (_settings.EnableHabituation)
        {
            UpdateHabituationLevel(DateTime.Now);
        }
    }

    public void Reset()
    {
        _lastAverageVolume = 0f;
        _currentSpike = false;
        _habituationLevel = 0;
        _lastHabituationUpdate = DateTime.Now;
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

/// <summary>
/// Simplified frequency band processor
/// </summary>
public class FrequencyBandProcessor : IDisposable
{
    private readonly AudioSettings _settings;

    public FrequencyBandProcessor(AudioSettings settings)
    {
        _settings = settings;
    }

    public void Reset()
    {
        // Nothing to reset
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
} 