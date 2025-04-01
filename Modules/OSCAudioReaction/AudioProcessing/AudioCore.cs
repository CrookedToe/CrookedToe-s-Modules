using System.Collections.Concurrent;
using System.Numerics;
using NAudio.Wave;
using NWaves.Filters;
using NWaves.Filters.Base;
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;
using NWaves.Features;
using VRCOSC.App.SDK.Modules;

namespace CrookedToe.Modules.OSCAudioReaction.AudioProcessing;

public class AudioFactory : IAudioFactory
{
    public IAudioProcessor CreateProcessor(IAudioConfiguration config, int bytesPerSample)
    {
        if (bytesPerSample <= 0)
            throw new ArgumentException("Bytes per sample must be greater than 0", nameof(bytesPerSample));

        return new AudioProcessor(config, bytesPerSample);
    }

    public IAudioDeviceManager CreateDeviceManager(IAudioConfiguration config, OSCAudioDirectionModule module)
    {
        return new AudioDeviceManager(config, module);
    }
}

public class AudioProcessor : IAudioProcessor, IDisposable
{
    // Immutable configuration
    private readonly IAudioConfiguration _config;
    private readonly int _bytesPerSample;
    
    // Mutable state collections
    private readonly Queue<float> _volumeHistory;
    private readonly Queue<float> _directionHistory;
    private readonly Queue<float> _recentVolumes;
    private readonly float[] _frequencyBands;
    private readonly float[] _smoothedBands;
    private readonly bool[] _enabledBands;
    private readonly float[] _bandDirections;
    private readonly object _fftLock = new();
    
    // NWaves components
    private RealFft _fft;
    private float[] _fftBuffer;
    private Complex[] _spectrum;
    private float[] _window;
    
    // Mutable state
    private float _currentGain;
    private float _smoothedVolume;
    private float _smoothedDirection;
    private float _currentRms;
    private bool _isActive;
    private DateTime _lastProcessTime;
    private float _lastAverageVolume;
    private bool _currentSpike;
    private DateTime _lastSpikeTime;
    private bool _isDisposed;
    private int _adaptedFftSize;
    
    // Enhanced direction processing
    private EnhancedDirectionProcessor? _enhancedDirectionProcessor;
    private OpenVRManager? _openVRManager;
    private bool _enhancedDirectionEnabled;
    private bool _headOrientationEnabled;
    
    // Constants
    private const float MIN_VALID_SIGNAL = 1e-6f;
    private const float MIN_SPIKE_VOLUME = 0.1f;
    private const int MIN_SPIKE_INTERVAL_MS = 100;
    private const int SPIKE_DURATION_MS = 1000;
    private const int VOLUME_HISTORY_SIZE = 3;
    private const int MIN_SAMPLES = 128;

    // Public read-only state
    public float CurrentVolume => _smoothedVolume;
    public float CurrentDirection => _smoothedDirection;
    public float CurrentGain => _currentGain;
    public float CurrentRms => _currentRms;
    public float[] FrequencyBands => _smoothedBands;
    public bool IsActive => _isActive;
    public bool HasSpike => _currentSpike;
    public bool EnhancedDirectionEnabled => _enhancedDirectionEnabled;
    public bool HeadOrientationEnabled => _headOrientationEnabled && _openVRManager?.IsInitialized == true;

    public AudioProcessor(IAudioConfiguration config, int bytesPerSample)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (bytesPerSample <= 0)
            throw new ArgumentException("Bytes per sample must be greater than 0", nameof(bytesPerSample));
            
        _bytesPerSample = bytesPerSample;
        _currentGain = config.Gain;
        _smoothedDirection = 0.5f;
        _smoothedVolume = 0f;
        
        // Initialize collections
        _volumeHistory = new Queue<float>(AudioConstants.HISTORY_SIZE);
        _directionHistory = new Queue<float>(AudioConstants.HISTORY_SIZE);
        _recentVolumes = new Queue<float>(VOLUME_HISTORY_SIZE);
        
        // Initialize FFT components
        _adaptedFftSize = config.FftSize;
        _fft = new RealFft(_adaptedFftSize);
        _fftBuffer = new float[_adaptedFftSize];
        _spectrum = new Complex[_adaptedFftSize / 2 + 1];
        _window = Window.Hamming(_adaptedFftSize);
        
        // Initialize frequency analysis
        _frequencyBands = new float[config.FrequencyBands];
        _smoothedBands = new float[config.FrequencyBands];
        _enabledBands = new bool[config.FrequencyBands];
        _bandDirections = new float[config.FrequencyBands];
        Array.Fill(_bandDirections, 0.5f);
        
        // Initialize enhanced direction processing
        _enhancedDirectionProcessor = new EnhancedDirectionProcessor(config);
        _enhancedDirectionEnabled = false;
        _headOrientationEnabled = false;
        
        _lastProcessTime = DateTime.Now;
        ConfigureFrequencyBands(config.FrequencySmoothing, new bool[AudioConstants.DEFAULT_FREQUENCY_BANDS]);
        Reset();
    }

    public void Reset()
    {
        if (_isDisposed) return;

        _volumeHistory.Clear();
        _directionHistory.Clear();
        _recentVolumes.Clear();
        
        for (int i = 0; i < AudioConstants.HISTORY_SIZE; i++)
        {
            _volumeHistory.Enqueue(0f);
            _directionHistory.Enqueue(0.5f);
        }
        
        for (int i = 0; i < VOLUME_HISTORY_SIZE; i++)
        {
            _recentVolumes.Enqueue(0f);
        }

        _currentRms = 0f;
        _smoothedVolume = 0f;
        _lastAverageVolume = 0f;
        _currentSpike = false;
        
        lock (_fftLock)
        {
            Array.Clear(_frequencyBands, 0, _frequencyBands.Length);
            Array.Clear(_smoothedBands, 0, _smoothedBands.Length);
            Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
            Array.Clear(_spectrum, 0, _spectrum.Length);
        }
        
        _isActive = true;
        _lastProcessTime = DateTime.Now;
    }

    public (float[] bands, float volume, float direction, bool spike) ProcessAudioData(WaveInEventArgs e, bool scaleWithVolume)
    {
        if (_isDisposed || e.Buffer == null || e.BytesRecorded == 0)
            return (new float[_frequencyBands.Length], 0f, 0.5f, false);

        int samplesAvailable = e.BytesRecorded / _bytesPerSample;
        if (samplesAvailable < MIN_SAMPLES)
            return (new float[_frequencyBands.Length], 0f, 0.5f, false);

        // Ensure FFT size matches configuration
        AdaptFftSize(samplesAvailable);
        
        // Allocate sample buffers based on FFT size
        int sampleCount = Math.Min(samplesAvailable / 2, _adaptedFftSize);
        var leftSamples = new float[_adaptedFftSize];
        var rightSamples = new float[_adaptedFftSize];
        var monoSamples = new float[_adaptedFftSize];
        
        // Zero-pad the rest of the buffer if we don't have enough samples
        Array.Clear(leftSamples, sampleCount, _adaptedFftSize - sampleCount);
        Array.Clear(rightSamples, sampleCount, _adaptedFftSize - sampleCount);
        Array.Clear(monoSamples, sampleCount, _adaptedFftSize - sampleCount);
        
        // Split stereo channels and create mono mix
        for (int i = 0; i < sampleCount; i++)
        {
            int sampleIndex = i * 2;
            leftSamples[i] = BitConverter.ToSingle(e.Buffer, sampleIndex * _bytesPerSample);
            rightSamples[i] = BitConverter.ToSingle(e.Buffer, (sampleIndex + 1) * _bytesPerSample);
            monoSamples[i] = (leftSamples[i] + rightSamples[i]) / 2f;
        }
        
        // Process left channel FFT
        Complex[] leftSpectrum;
        lock (_fftLock)
        {
            Array.Copy(leftSamples, _fftBuffer, Math.Min(leftSamples.Length, _fftBuffer.Length));
            
            var window = Window.Hamming(_fftBuffer.Length);
            for (int i = 0; i < _fftBuffer.Length; i++)
            {
                _fftBuffer[i] *= window[i];
            }
            
            var realSpectrum = new float[_fftBuffer.Length];
            var imagSpectrum = new float[_fftBuffer.Length];
            Array.Copy(_fftBuffer, realSpectrum, _fftBuffer.Length);
            _fft.Direct(realSpectrum, realSpectrum, imagSpectrum);
            
            leftSpectrum = new Complex[_spectrum.Length];
            float normalizationFactor = 2.0f / _fftBuffer.Length;
            for (int i = 0; i < leftSpectrum.Length; i++)
            {
                leftSpectrum[i] = new Complex(
                    realSpectrum[i] * normalizationFactor,
                    imagSpectrum[i] * normalizationFactor
                );
            }
            
            if (leftSpectrum.Length > 0)
                leftSpectrum[0] = leftSpectrum[0] * 0.5f;
            if (leftSpectrum.Length > 1)
                leftSpectrum[leftSpectrum.Length - 1] = leftSpectrum[leftSpectrum.Length - 1] * 0.5f;
        }
        
        // Process right channel FFT
        Complex[] rightSpectrum;
        lock (_fftLock)
        {
            Array.Copy(rightSamples, _fftBuffer, Math.Min(rightSamples.Length, _fftBuffer.Length));
            
            var window = Window.Hamming(_fftBuffer.Length);
            for (int i = 0; i < _fftBuffer.Length; i++)
            {
                _fftBuffer[i] *= window[i];
            }
            
            var realSpectrum = new float[_fftBuffer.Length];
            var imagSpectrum = new float[_fftBuffer.Length];
            Array.Copy(_fftBuffer, realSpectrum, _fftBuffer.Length);
            _fft.Direct(realSpectrum, realSpectrum, imagSpectrum);
            
            rightSpectrum = new Complex[_spectrum.Length];
            float normalizationFactor = 2.0f / _fftBuffer.Length;
            for (int i = 0; i < rightSpectrum.Length; i++)
            {
                rightSpectrum[i] = new Complex(
                    realSpectrum[i] * normalizationFactor,
                    imagSpectrum[i] * normalizationFactor
                );
            }
            
            if (rightSpectrum.Length > 0)
                rightSpectrum[0] = rightSpectrum[0] * 0.5f;
            if (rightSpectrum.Length > 1)
                rightSpectrum[rightSpectrum.Length - 1] = rightSpectrum[rightSpectrum.Length - 1] * 0.5f;
        }
        
        // Process mono channel FFT for overall frequency analysis
        lock (_fftLock)
        {
            Array.Copy(monoSamples, _fftBuffer, Math.Min(monoSamples.Length, _fftBuffer.Length));
            
            var window = Window.Hamming(_fftBuffer.Length);
            for (int i = 0; i < _fftBuffer.Length; i++)
            {
                _fftBuffer[i] *= window[i];
            }
            
            var realSpectrum = new float[_fftBuffer.Length];
            var imagSpectrum = new float[_fftBuffer.Length];
            Array.Copy(_fftBuffer, realSpectrum, _fftBuffer.Length);
            _fft.Direct(realSpectrum, realSpectrum, imagSpectrum);
            
            float normalizationFactor = 2.0f / _fftBuffer.Length;
            for (int i = 0; i < _spectrum.Length; i++)
            {
                _spectrum[i] = new Complex(
                    realSpectrum[i] * normalizationFactor,
                    imagSpectrum[i] * normalizationFactor
                );
            }
            
            if (_spectrum.Length > 0)
                _spectrum[0] = _spectrum[0] * 0.5f;
            if (_spectrum.Length > 1)
                _spectrum[_spectrum.Length - 1] = _spectrum[_spectrum.Length - 1] * 0.5f;
        }

        // Calculate volume using mono samples
        float rawVolume = CalculateVolume(monoSamples);
        
        // Calculate direction using enabled frequency bands
        float direction = CalculateDirection(leftSpectrum, rightSpectrum);
        
        // Process frequency bands
        var bands = ProcessFrequencyBands(_spectrum, scaleWithVolume);
        
        // Detect spikes using raw volume before AGC
        bool spike = DetectSpike(rawVolume);

        // Handle AGC
        if (_config.EnableAGC && rawVolume > MIN_VALID_SIGNAL)
        {
            float targetLevel = AudioConstants.TARGET_LEVEL;
            float currentLevel = rawVolume * _currentGain;
            float gainAdjustment = targetLevel / Math.Max(MIN_VALID_SIGNAL, currentLevel);
            
            // Slower adjustment up, faster adjustment down to prevent sudden volume spikes
            float adjustmentSpeed = gainAdjustment > 1.0f ? 0.1f : 0.3f;
            _currentGain = _currentGain * (1 - adjustmentSpeed) + (_config.Gain * gainAdjustment) * adjustmentSpeed;
            _currentGain = Math.Clamp(_currentGain, AudioConstants.MIN_GAIN, AudioConstants.MAX_GAIN);
        }
        else if (!_config.EnableAGC)
        {
            _currentGain = _config.Gain;
        }

        // Apply gain and soft clip
        float volume = rawVolume * _currentGain;
        
        // Soft clip only if volume is above 1.0 to maintain linearity for lower volumes
        if (volume > 1.0f)
        {
            volume = 1.0f - (1.0f / (1.0f + volume - 1.0f));
        }
        
        volume = Math.Clamp(volume, 0f, 1f);
        
        // Update state with final volume and direction
        UpdateState(volume, direction);
        
        return (bands, volume, direction, spike);
    }

    private float[] ProcessSpectrum(float[] samples)
    {
        // Create DiscreteSignal for NWaves processing
        var signal = new DiscreteSignal(AudioConstants.DEFAULT_SAMPLE_RATE, samples);
        
        // Apply window function
        var windowedSamples = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            windowedSamples[i] = samples[i] * _window[i % _window.Length];
        }

        // Perform FFT
        var realSpectrum = new float[_fftBuffer.Length];
        var imagSpectrum = new float[_fftBuffer.Length];
        Array.Copy(windowedSamples, realSpectrum, Math.Min(windowedSamples.Length, realSpectrum.Length));
        _fft.Direct(realSpectrum, realSpectrum, imagSpectrum);

        // Calculate power spectrum
        var powerSpectrum = new float[realSpectrum.Length / 2 + 1];
        for (int i = 0; i < powerSpectrum.Length; i++)
        {
            powerSpectrum[i] = (realSpectrum[i] * realSpectrum[i] + imagSpectrum[i] * imagSpectrum[i]);
        }

        return powerSpectrum;
    }

    private float CalculateVolume(float[] samples)
    {
        if (samples == null || samples.Length == 0)
            return 0f;

        // Calculate RMS using NWaves' DiscreteSignal
        var signal = new DiscreteSignal(AudioConstants.DEFAULT_SAMPLE_RATE, samples);
        float rms = (float)signal.Rms();
        _currentRms = rms;
        
        // Calculate spectral power
        var powerSpectrum = ProcessSpectrum(samples);
        float spectralPower = 0f;
        
        // Only consider frequencies up to 20kHz (human hearing range)
        int maxBin = Math.Min(
            powerSpectrum.Length - 1, 
            FrequencyToBin(20000) // 20kHz max
        );
        
        for (int i = 0; i <= maxBin; i++)
        {
            spectralPower += powerSpectrum[i];
        }
        
        // Normalize spectral power
        if (maxBin > 0)
        {
            spectralPower = MathF.Sqrt(spectralPower / maxBin);
        }

        // Scale and combine RMS and spectral power
        rms *= 4.0f; // RMS typically needs scaling up as it's usually very small
        spectralPower *= 0.25f; // Spectral power tends to be larger, so scale it down
        float rawVolume = (rms * 0.7f + spectralPower * 0.3f);

        return rawVolume;
    }

    private float CalculateDirection(Complex[] leftSpectrum, Complex[] rightSpectrum)
    {
        // If enhanced direction detection is enabled, use that
        if (_enhancedDirectionEnabled && _enhancedDirectionProcessor != null)
        {
            float enhancedDirection = _enhancedDirectionProcessor.CalculateDirection(
                leftSpectrum, 
                rightSpectrum, 
                AudioConstants.DEFAULT_SAMPLE_RATE,
                _enabledBands
            );
            
            // If head orientation is enabled, adjust the direction based on head yaw
            if (_headOrientationEnabled && _openVRManager != null && _openVRManager.IsInitialized)
            {
                _openVRManager.UpdateHeadOrientation();
                return _openVRManager.GetNormalizedAudioDirection(enhancedDirection);
            }
            
            return enhancedDirection;
        }
        
        // Otherwise, use the original algorithm
        float leftPower = 0f;
        float rightPower = 0f;
        int enabledBandCount = 0;

        // Calculate power for each enabled frequency band
        for (int band = 0; band < _frequencyBands.Length; band++)
        {
            if (!_enabledBands[band]) continue;
            enabledBandCount++;

            // Get frequency range for this band
            var (startFreq, endFreq) = GetFrequencyRange(band);
            int startBin = FrequencyToBin(startFreq);
            int endBin = FrequencyToBin(endFreq);

            // Calculate power for this band in each channel
            float bandLeftPower = 0f;
            float bandRightPower = 0f;

            for (int bin = startBin; bin <= endBin && bin < leftSpectrum.Length; bin++)
            {
                bandLeftPower += (float)leftSpectrum[bin].Magnitude;
                bandRightPower += (float)rightSpectrum[bin].Magnitude;
            }

            leftPower += bandLeftPower;
            rightPower += bandRightPower;
        }

        // If no bands are enabled or total power is too low, return center
        if (enabledBandCount == 0 || leftPower + rightPower < MIN_VALID_SIGNAL)
            return 0.5f;

        // Calculate direction (0 = full left, 0.5 = center, 1 = full right)
        float totalPower = leftPower + rightPower;
        float direction = rightPower / totalPower;

        // Apply smoothing
        float smoothingFactor = _config.Smoothing;
        _smoothedDirection = smoothingFactor * _smoothedDirection + (1 - smoothingFactor) * direction;

        return _smoothedDirection;
    }

    private (float startFreq, float endFreq) GetFrequencyRange(int band)
    {
        // Define frequency ranges for each band (in Hz)
        // Using standard audio frequency bands with better overlap handling
        switch (band)
        {
            case 0: return (20, 60);     // Sub Bass (20-60 Hz)
            case 1: return (60, 250);    // Bass (60-250 Hz)
            case 2: return (250, 500);   // Low Mids (250-500 Hz)
            case 3: return (500, 2000);  // Mids (500-2kHz)
            case 4: return (2000, 4000); // Upper Mids (2-4kHz)
            case 5: return (4000, 6000); // Presence (4-6kHz)
            case 6: return (6000, 25000);// Brilliance (6-25kHz)
            default: return (0, 0);
        }
    }

    private int FrequencyToBin(float frequency)
    {
        // More accurate frequency to bin conversion
        float binWidth = AudioConstants.DEFAULT_SAMPLE_RATE / (float)_adaptedFftSize;
        int bin = (int)Math.Round(frequency / binWidth);
        return Math.Min(Math.Max(bin, 0), _adaptedFftSize / 2);
    }

    private float[] ProcessFrequencyBands(Complex[] spectrum, bool scaleWithVolume)
    {
        int numBins = spectrum.Length;
        float totalPower = 0f;
        float sampleRate = AudioConstants.DEFAULT_SAMPLE_RATE;
        float binWidth = sampleRate / (2f * (numBins - 1)); // Nyquist frequency / (N/2)

        // First pass: calculate band powers
        for (int band = 0; band < _frequencyBands.Length; band++)
        {
            if (!_enabledBands[band]) continue;

            var (lowFreq, highFreq) = GetFrequencyRange(band);
            
            // Convert frequencies to bin indices more accurately
            int startBin = Math.Max(1, (int)Math.Floor(lowFreq / binWidth));
            int endBin = Math.Min(numBins - 1, (int)Math.Ceiling(highFreq / binWidth));
            
            float bandPower = 0f;
            int binsInBand = 0;

            for (int bin = startBin; bin <= endBin; bin++)
            {
                float binFreq = bin * binWidth;
                if (binFreq >= lowFreq && binFreq <= highFreq)
                {
                    // Calculate magnitude in decibels
                    float magnitude = (float)spectrum[bin].Magnitude;
                    bandPower += magnitude * magnitude; // Use power instead of magnitude
                    binsInBand++;
                }
            }
            
            // Average power for this band
            if (binsInBand > 0)
            {
                bandPower = MathF.Sqrt(bandPower / binsInBand);
                _frequencyBands[band] = bandPower;
                totalPower += bandPower;
            }
            else
            {
                _frequencyBands[band] = 0f;
            }
        }

        // Second pass: normalize and apply smoothing
        if (totalPower > MIN_VALID_SIGNAL)
        {
            for (int band = 0; band < _frequencyBands.Length; band++)
            {
                if (!_enabledBands[band]) continue;

                float normalizedPower;
                if (scaleWithVolume)
                {
                    normalizedPower = _frequencyBands[band] * _smoothedVolume;
                }
                else
                {
                    normalizedPower = _frequencyBands[band] / totalPower;
                }

                // Apply exponential smoothing
                _smoothedBands[band] = _smoothedBands[band] * _config.FrequencySmoothing + 
                                     normalizedPower * (1 - _config.FrequencySmoothing);
            }
        }
        else
        {
            Array.Clear(_smoothedBands, 0, _smoothedBands.Length);
        }

        return _smoothedBands;
    }

    private void UpdateState(float volume, float direction)
    {
        // Update volume history
        _volumeHistory.Enqueue(volume);
        if (_volumeHistory.Count > AudioConstants.HISTORY_SIZE)
            _volumeHistory.Dequeue();
            
        // Update direction history
        _directionHistory.Enqueue(direction);
        if (_directionHistory.Count > AudioConstants.HISTORY_SIZE)
            _directionHistory.Dequeue();
            
        // Update recent volumes for spike detection
        _recentVolumes.Enqueue(volume);
        if (_recentVolumes.Count > VOLUME_HISTORY_SIZE)
            _recentVolumes.Dequeue();
            
        // Apply smoothing
        _smoothedVolume = _smoothedVolume * _config.Smoothing + volume * (1 - _config.Smoothing);
        _smoothedDirection = _smoothedDirection * _config.Smoothing + direction * (1 - _config.Smoothing);
        
        _lastAverageVolume = _recentVolumes.Average();
    }

    private bool DetectSpike(float volume)
    {
        var now = DateTime.Now;
        
        // Check if we're still in spike duration
        if (_currentSpike && (now - _lastSpikeTime).TotalMilliseconds < SPIKE_DURATION_MS)
            return true;
            
        // Reset spike state if duration expired
        if (_currentSpike)
            _currentSpike = false;
            
        // Check for new spike
        if (volume > MIN_SPIKE_VOLUME && 
            volume > _lastAverageVolume * (1 + _config.SpikeThreshold) &&
            (now - _lastSpikeTime).TotalMilliseconds >= MIN_SPIKE_INTERVAL_MS)
        {
            _currentSpike = true;
            _lastSpikeTime = now;
            return true;
        }
        
        return false;
    }

    private void AdaptFftSize(int samplesAvailable)
    {
        // Always use the configured FFT size from the preset
        int targetSize = _config.FftSize;
        
        // Only change if necessary
        if (targetSize != _adaptedFftSize)
        {
            lock (_fftLock)
            {
                _adaptedFftSize = targetSize;
                _fft = new RealFft(targetSize);
                _fftBuffer = new float[targetSize];
                _spectrum = new Complex[targetSize / 2 + 1];
                
                // Recreate window for new size
                _window = Window.Hamming(targetSize);
            }
        }
    }

    private static int FindNearestPowerOfTwo(int value)
    {
        int power = (int)Math.Floor(Math.Log2(value));
        int lowerPower = 1 << power;
        int upperPower = 1 << (power + 1);
        return (value - lowerPower) < (upperPower - value) ? lowerPower : upperPower;
    }

    public void UpdateEnabledBands(bool[] enabledBands)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(AudioProcessor));
        if (enabledBands.Length != _frequencyBands.Length) return;
        
        Array.Copy(enabledBands, _enabledBands, enabledBands.Length);
    }

    public void UpdateSmoothing(float smoothing)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(AudioProcessor));
        // No need to store smoothing as it's used directly from config
    }

    public void UpdateGain(float gain)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(AudioProcessor));
        if (!_config.EnableAGC)
        {
            _currentGain = Math.Clamp(gain, AudioConstants.MIN_GAIN, AudioConstants.MAX_GAIN);
        }
    }

    public void ConfigureFrequencyBands(float smoothing, bool[] enabledBands)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(AudioProcessor));
        if (enabledBands == null || enabledBands.Length < 1) return;
        
        Array.Copy(enabledBands, _enabledBands, Math.Min(enabledBands.Length, _enabledBands.Length));
    }

    public void SetOpenVRManager(OpenVRManager? manager)
    {
        _openVRManager = manager;
    }
    
    public void EnableEnhancedDirection(bool enabled)
    {
        _enhancedDirectionEnabled = enabled;
    }
    
    public void EnableHeadOrientation(bool enabled)
    {
        _headOrientationEnabled = enabled;
    }
    
    public void ConfigureEnhancedDirection(float magnitudeWeight, bool enablePhaseAnalysis)
    {
        if (_enhancedDirectionProcessor == null)
            return;
            
        _enhancedDirectionProcessor.SetMagnitudeWeight(magnitudeWeight);
        _enhancedDirectionProcessor.SetPhaseAnalysisEnabled(enablePhaseAnalysis);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _fft = null!;
                _fftBuffer = Array.Empty<float>();
                _spectrum = Array.Empty<Complex>();
            }
            _isDisposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
} 