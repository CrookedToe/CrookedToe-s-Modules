using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NWaves.Transforms;
using NWaves.Windows;

namespace CrookedToe.Modules.OSCAudioReaction;

public sealed class AudioProcessor : IDisposable
{
    private const int FFT_SIZE = 8192;
    private const int SPECTRUM_SIZE = FFT_SIZE / 2 + 1;
    private const int MIN_SAMPLES = 128;
    private const float MIN_VOLUME_THRESHOLD = 0.001f;
    private const float NOISE_FLOOR = 1e-5f;
    private const float MaximumAnalyzedFrequency = 20000f;

    private AudioSettings _settings;
    private readonly SpikeDetector _spikeDetector;
    private readonly object _processingLock = new();
    private volatile bool _disposed;

    private float _currentVolume;
    private float _currentDirection = 0.5f;
    private float _currentGain;

    private readonly RealFft _fft;
    private readonly float[] _window;
    
    private readonly float[] _leftSamples;
    private readonly float[] _rightSamples;
    private readonly float[] _monoSamples;
    private readonly float[] _realSpectrum;
    private readonly float[] _imagSpectrum;
    private readonly float[] _leftMagnitudes;
    private readonly float[] _rightMagnitudes;
    private readonly float[] _monoMagnitudes;
    
    private readonly (int Start, int End)[] _bandBinRanges = new (int, int)[AudioBandDefinitions.Count];
    private float _cachedBinWidth;

    private readonly float[] _smoothedFrequencyBands = new float[AudioBandDefinitions.Count];
    private readonly AudioProcessingResult _result = new();

    public AudioProcessor(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _currentGain = settings.Gain;
        
        _fft = new RealFft(FFT_SIZE);
        _window = Window.Blackman(FFT_SIZE);
        _spikeDetector = new SpikeDetector(settings);
        
        _leftSamples = new float[FFT_SIZE];
        _rightSamples = new float[FFT_SIZE];
        _monoSamples = new float[FFT_SIZE];
        _realSpectrum = new float[FFT_SIZE];
        _imagSpectrum = new float[FFT_SIZE];
        _leftMagnitudes = new float[SPECTRUM_SIZE];
        _rightMagnitudes = new float[SPECTRUM_SIZE];
        _monoMagnitudes = new float[SPECTRUM_SIZE];
        
        UpdateBinRanges();
    }

    private void UpdateBinRanges()
    {
        float binWidth = _settings.SampleRate / (float)FFT_SIZE;
        _cachedBinWidth = binWidth;
        
        for (int band = 0; band < AudioBandDefinitions.Count; band++)
        {
            var definition = AudioBandDefinitions.All[band];
            int startBin = Math.Max(1, (int)MathF.Round(definition.LowFrequency / binWidth));
            int endBin = Math.Min(SPECTRUM_SIZE - 1, (int)MathF.Round(definition.HighFrequency / binWidth) - 1);
            
            if (endBin < startBin)
                endBin = startBin;

            _bandBinRanges[band] = (startBin, endBin);
        }
    }

    public void UpdateSettings(AudioSettings newSettings)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        
        lock (_processingLock)
        {
            if (_settings.SampleRate != newSettings.SampleRate)
            {
                _settings = newSettings;
                UpdateBinRanges();
            }
            else
            {
                _settings = newSettings;
            }
            
            _spikeDetector.UpdateSettings(newSettings);
            
            if (!newSettings.EnableAGC)
                _currentGain = newSettings.Gain;
        }
    }

    public AudioProcessingResult ProcessAudio(WaveInEventArgs e)
    {
        if (e?.Buffer == null)
            return AudioProcessingResult.Empty;

        return ProcessAudio(e.Buffer, e.BytesRecorded);
    }

    public AudioProcessingResult ProcessAudio(byte[] buffer, int bytesRecorded)
    {
        if (_disposed || buffer == null || bytesRecorded <= 0)
            return AudioProcessingResult.Empty;

        lock (_processingLock)
        {
            if (_disposed)
                return AudioProcessingResult.Empty;

            return ProcessAudioInternal(buffer, Math.Min(bytesRecorded, buffer.Length));
        }
    }

    private AudioProcessingResult ProcessAudioInternal(byte[] buffer, int bytesRecorded)
    {
        int channels = Math.Max(1, _settings.Channels);
        int samplesAvailable = bytesRecorded / sizeof(float);
        int framesAvailable = samplesAvailable / channels;
        if (framesAvailable < MIN_SAMPLES)
            return AudioProcessingResult.Empty;

        int sampleCount = ExtractSamplesOptimized(buffer, framesAvailable, channels);
        
        ProcessSpectrumOptimized(_leftSamples, sampleCount, _leftMagnitudes);
        ProcessSpectrumOptimized(_rightSamples, sampleCount, _rightMagnitudes);
        ProcessSpectrumOptimized(_monoSamples, sampleCount, _monoMagnitudes);

        float rawVolume = CalculateVolumeOptimized(_monoSamples, sampleCount, _monoMagnitudes);
        float rawDirection = CalculateDirectionOptimized(_leftMagnitudes, _rightMagnitudes, rawVolume);
        ProcessFrequencyBandsOptimized(_monoMagnitudes, _settings.ScaleFrequencyWithVolume);
        bool spike = _spikeDetector.DetectSpike(rawVolume);

        ApplyGainControl(rawVolume);
        float volume = ApplyGainAndClipping(rawVolume);
        UpdateState(volume, rawDirection);
        UpdateResult(spike);
        return _result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateResult(bool spike)
    {
        _result.Volume = _currentVolume;
        _result.Direction = _currentDirection;
        _result.Spike = spike;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ExtractSamplesOptimized(byte[] buffer, int framesAvailable, int channels)
    {
        int sampleCount = Math.Min(framesAvailable, FFT_SIZE);
        int bytesRequired = sampleCount * sizeof(float) * channels;
        ReadOnlySpan<float> interleavedSamples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRequired));
        
        if (sampleCount < FFT_SIZE)
        {
            Array.Clear(_leftSamples, sampleCount, FFT_SIZE - sampleCount);
            Array.Clear(_rightSamples, sampleCount, FFT_SIZE - sampleCount);
            Array.Clear(_monoSamples, sampleCount, FFT_SIZE - sampleCount);
        }
        
        for (int i = 0; i < sampleCount; i++)
        {
            int sourceIndex = i * channels;
            float left = interleavedSamples[sourceIndex];
            float right = channels > 1 ? interleavedSamples[sourceIndex + 1] : left;
            
            _leftSamples[i] = left;
            _rightSamples[i] = right;
            _monoSamples[i] = (left + right) * 0.5f;
        }
        
        return sampleCount;
    }

    private void ProcessSpectrumOptimized(float[] samples, int sampleCount, float[] magnitudesOut)
    {
        int copyLen = Math.Min(sampleCount, FFT_SIZE);
        for (int i = 0; i < copyLen; i++)
            _realSpectrum[i] = samples[i] * _window[i];
        
        if (copyLen < FFT_SIZE)
            Array.Clear(_realSpectrum, copyLen, FFT_SIZE - copyLen);
        
        Array.Clear(_imagSpectrum, 0, FFT_SIZE);
        
        _fft.Direct(_realSpectrum, _realSpectrum, _imagSpectrum);
        
        float normFactor = 2.0f / FFT_SIZE;
        for (int i = 0; i < SPECTRUM_SIZE; i++)
        {
            float real = _realSpectrum[i] * normFactor;
            float imag = _imagSpectrum[i] * normFactor;
            float mag = MathF.Sqrt(real * real + imag * imag);
            magnitudesOut[i] = mag < NOISE_FLOOR ? 0f : mag;
        }
        
        magnitudesOut[0] *= 0.5f;
        magnitudesOut[SPECTRUM_SIZE - 1] *= 0.5f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float CalculateVolumeOptimized(float[] samples, int sampleCount, float[] magnitudes)
    {
        if (sampleCount == 0) return 0f;

        float sumSquares = 0f;
        for (int i = 0; i < sampleCount; i++)
            sumSquares += samples[i] * samples[i];
        
        float rms = MathF.Sqrt(sumSquares / sampleCount);
        float spectralPower = 0f;
        int maxBin = Math.Min(SPECTRUM_SIZE - 1, (int)(MaximumAnalyzedFrequency / _cachedBinWidth));
        
        for (int i = 0; i <= maxBin; i++)
            spectralPower += magnitudes[i] * magnitudes[i];
        
        if (maxBin > 0)
            spectralPower = MathF.Sqrt(spectralPower / maxBin);

        return (rms * 4.0f * 0.7f) + (spectralPower * 0.25f * 0.3f);
    }

    private float CalculateDirectionOptimized(float[] leftMags, float[] rightMags, float volume)
    {
        if (volume < _settings.DirectionThreshold)
            return 0.5f;

        float totalWeight = 0f;
        float weightedDirectionSum = 0f;

        for (int band = 0; band < AudioBandDefinitions.Count; band++)
        {
            if (!_settings.BandEnabled[band])
                continue;

            var (startBin, endBin) = _bandBinRanges[band];
            float bandWeight = AudioBandDefinitions.All[band].DirectionWeight;
            
            for (int bin = startBin; bin <= endBin; bin++)
            {
                float leftMag = leftMags[bin];
                float rightMag = rightMags[bin];
                float totalMag = leftMag + rightMag;
                
                if (totalMag < 1e-6f)
                    continue;
                
                float direction = rightMag / totalMag;
                weightedDirectionSum += direction * totalMag * bandWeight;
                totalWeight += totalMag * bandWeight;
            }
        }
        
        return totalWeight < 1e-6f ? 0.5f : Math.Clamp(weightedDirectionSum / totalWeight, 0f, 1f);
    }

    private void ProcessFrequencyBandsOptimized(float[] magnitudes, bool scaleWithVolume)
    {
        Span<float> rawBands = stackalloc float[AudioBandDefinitions.Count];
        float totalPower = 0f;
        
        for (int band = 0; band < AudioBandDefinitions.Count; band++)
        {
            if (!_settings.BandEnabled[band])
            {
                _smoothedFrequencyBands[band] = 0f;
                _result.FrequencyBands[band] = 0f;
                continue;
            }

            var (startBin, endBin) = _bandBinRanges[band];
            float bandPower = 0f;
            int binsInBand = endBin - startBin + 1;

            for (int bin = startBin; bin <= endBin; bin++)
            {
                float mag = magnitudes[bin];
                if (mag > NOISE_FLOOR)
                    bandPower += mag * mag;
            }
            
            if (binsInBand > 0)
            {
                bandPower = MathF.Sqrt(bandPower / binsInBand);
                rawBands[band] = bandPower;
                totalPower += bandPower;
            }
        }

        float smoothing = _settings.FrequencySmoothing;
        
        if (totalPower > MIN_VOLUME_THRESHOLD)
        {
            for (int band = 0; band < AudioBandDefinitions.Count; band++)
            {
                if (!_settings.BandEnabled[band])
                    continue;

                float bandValue = rawBands[band];
                
                if (bandValue < NOISE_FLOOR * 5f)
                {
                    bandValue = 0f;
                }
                else
                {
                    bandValue = scaleWithVolume 
                        ? bandValue * _currentVolume * 2f
                        : bandValue / totalPower;
                }

                _smoothedFrequencyBands[band] = _smoothedFrequencyBands[band] * smoothing + bandValue * (1f - smoothing);
                _result.FrequencyBands[band] = _smoothedFrequencyBands[band];
            }
        }
        else
        {
            for (int band = 0; band < AudioBandDefinitions.Count; band++)
            {
                if (!_settings.BandEnabled[band])
                {
                    _smoothedFrequencyBands[band] = 0f;
                    _result.FrequencyBands[band] = 0f;
                    continue;
                }

                _smoothedFrequencyBands[band] *= smoothing;
                _result.FrequencyBands[band] = _smoothedFrequencyBands[band];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyGainControl(float rawVolume)
    {
        if (_settings.EnableAGC && rawVolume > MIN_VOLUME_THRESHOLD)
        {
            const float targetLevel = 0.5f;
            float currentLevel = rawVolume * _currentGain;
            float gainAdjustment = targetLevel / MathF.Max(0.001f, currentLevel);
            
            float adjustmentSpeed = gainAdjustment > 1.0f ? 0.1f : 0.3f;
            _currentGain = _currentGain * (1 - adjustmentSpeed) + (_settings.Gain * gainAdjustment) * adjustmentSpeed;
            _currentGain = Math.Clamp(_currentGain, 0.1f, 5.0f);
        }
        else if (!_settings.EnableAGC)
        {
            _currentGain = _settings.Gain;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ApplyGainAndClipping(float rawVolume)
    {
        float volume = rawVolume * _currentGain;
        if (volume > 1.0f)
            volume = 1.0f - (1.0f / (1.0f + volume - 1.0f));
        return Math.Clamp(volume, 0f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateState(float volume, float direction)
    {
        float baseSmoothing = Math.Clamp(_settings.Smoothing, 0f, 0.99f);
        _currentVolume = _currentVolume * baseSmoothing + volume * (1f - baseSmoothing);

        float directionSmoothing = baseSmoothing;
        if (_settings.EnableDirectionalPause)
        {
            float deviationFromCenter = MathF.Abs(direction - 0.5f) * 2f;
            float multiplier = 1.0f + deviationFromCenter * (_settings.DirectionalPauseFactor - 1.0f);
            directionSmoothing = Math.Clamp(baseSmoothing * multiplier, 0f, 0.99f);
        }

        _currentDirection = _currentDirection * directionSmoothing + direction * (1f - directionSmoothing);
    }

    public void Reset()
    {
        lock (_processingLock)
        {
            _currentVolume = 0f;
            _currentDirection = 0.5f;
            _currentGain = _settings.Gain;
            _spikeDetector.Reset();
            Array.Clear(_smoothedFrequencyBands);
            _result.Reset();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _spikeDetector.Dispose();
    }
}
