using System.Diagnostics;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NWaves.Transforms;
using NWaves.Windows;

namespace CrookedToe.Modules.OSCAudioReaction;

public record AudioSettings
{
    public int SampleRate { get; init; } = 48000;
    public float Gain { get; init; } = 1.0f;
    public bool EnableAGC { get; init; } = true;
    public float Smoothing { get; init; } = 0.3f;
    public float DirectionThreshold { get; init; } = 0.01f;
    public float SpikeThreshold { get; init; } = 2.0f;
    public float SpikeHoldDuration { get; init; } = 0.5f;
    public float FrequencySmoothing { get; init; } = 0.7f;
    public float MagnitudePhaseRatio { get; init; } = 0.7f;
    public bool EnablePhaseAnalysis { get; init; } = true;
    public bool EnableDirectionalPause { get; init; }
    public float DirectionalPauseFactor { get; init; } = 1.0f;
    public double HabituationIncrease { get; init; } = 0.15;
    public double HabituationDecayRate { get; init; } = 0.02;
    public double HabituationThreshold { get; init; } = 0.3;
    public bool ScaleFrequencyWithVolume { get; init; }
    public bool[] BandEnabled { get; init; } = [true, true, true, true, true, true, true];
    
    public bool EnableSubBass => BandEnabled[0];
    public bool EnableBass => BandEnabled[1];
    public bool EnableLowMid => BandEnabled[2];
    public bool EnableMid => BandEnabled[3];
    public bool EnableUpperMid => BandEnabled[4];
    public bool EnablePresence => BandEnabled[5];
    public bool EnableBrilliance => BandEnabled[6];
}

public sealed class AudioProcessingResult
{
    public float Volume { get; set; }
    public float Direction { get; set; } = 0.5f;
    public float[] FrequencyBands { get; } = new float[7];
    public bool Spike { get; set; }
    
    public void Reset()
    {
        Volume = 0f;
        Direction = 0.5f;
        Array.Clear(FrequencyBands);
        Spike = false;
    }
    
    public static AudioProcessingResult Empty { get; } = new();
}

public sealed class AudioProcessor : IDisposable
{
    private static readonly (float Low, float High)[] FrequencyBandRanges =
    [
        (20f, 60f), (60f, 250f), (250f, 500f), (500f, 2000f),
        (2000f, 4000f), (4000f, 6000f), (6000f, 20000f)
    ];

    private static readonly float[] FrequencyBandWeights = [0.8f, 1.0f, 1.2f, 1.5f, 1.3f, 1.1f, 0.9f];

    private const int FFT_SIZE = 8192;
    private const int SPECTRUM_SIZE = FFT_SIZE / 2 + 1;
    private const int MIN_SAMPLES = 128;
    private const float MIN_VOLUME_THRESHOLD = 0.001f;
    private const float NOISE_FLOOR = 1e-5f;

    private AudioSettings _settings;
    private readonly SpikeDetector _spikeDetector;
    private readonly object _processingLock = new();
    private volatile bool _disposed;

    private float _currentVolume;
    private float _currentDirection = 0.5f;
    private float _currentGain;
    private float _currentRms;

    private readonly RealFft _fft;
    private readonly float[] _window;
    
    private readonly float[] _leftSamples;
    private readonly float[] _rightSamples;
    private readonly float[] _monoSamples;
    private readonly float[] _fftBuffer;
    private readonly float[] _realSpectrum;
    private readonly float[] _imagSpectrum;
    private readonly float[] _leftMagnitudes;
    private readonly float[] _rightMagnitudes;
    private readonly float[] _monoMagnitudes;
    
    private (int Start, int End)[] _bandBinRanges = new (int, int)[7];
    private float _cachedBinWidth;

    private readonly float[] _smoothedFrequencyBands = new float[7];
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
        _fftBuffer = new float[FFT_SIZE];
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
        
        for (int band = 0; band < 7; band++)
        {
            var (low, high) = FrequencyBandRanges[band];
            int startBin = Math.Max(1, (int)MathF.Round(low / binWidth));
            int endBin = Math.Min(SPECTRUM_SIZE - 1, (int)MathF.Round(high / binWidth) - 1);
            
            if (endBin < startBin) endBin = startBin;
            _bandBinRanges[band] = (startBin, endBin);
        }
    }

    public float CurrentVolume => _currentVolume;
    public float CurrentDirection => _currentDirection;
    public float CurrentGain => _currentGain;
    public float CurrentRms => _currentRms;
    public bool IsActive => _currentVolume > MIN_VOLUME_THRESHOLD;

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

    public void UpdateGain(float gain) => _currentGain = Math.Clamp(gain, 0.1f, 5.0f);

    public AudioProcessingResult ProcessAudio(WaveInEventArgs e)
    {
        if (_disposed || e?.Buffer == null || e.BytesRecorded == 0)
            return AudioProcessingResult.Empty;

        lock (_processingLock)
        {
            if (_disposed)
                return AudioProcessingResult.Empty;
            
            return ProcessAudioInternal(e);
        }
    }

    private AudioProcessingResult ProcessAudioInternal(WaveInEventArgs e)
    {
        int samplesAvailable = e.BytesRecorded / 4;
        if (samplesAvailable < MIN_SAMPLES)
            return AudioProcessingResult.Empty;

        int sampleCount = ExtractSamplesOptimized(e.Buffer, samplesAvailable);
        
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

        _result.Volume = _currentVolume;
        _result.Direction = _currentDirection;
        _result.Spike = spike;
        
        return _result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ExtractSamplesOptimized(byte[] buffer, int samplesAvailable)
    {
        int sampleCount = Math.Min(samplesAvailable / 2, FFT_SIZE);
        
        if (sampleCount < FFT_SIZE)
        {
            Array.Clear(_leftSamples, sampleCount, FFT_SIZE - sampleCount);
            Array.Clear(_rightSamples, sampleCount, FFT_SIZE - sampleCount);
            Array.Clear(_monoSamples, sampleCount, FFT_SIZE - sampleCount);
        }
        
        for (int i = 0; i < sampleCount; i++)
        {
            int byteOffset = i * 8;
            float left = BitConverter.ToSingle(buffer, byteOffset);
            float right = BitConverter.ToSingle(buffer, byteOffset + 4);
            
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
            _fftBuffer[i] = samples[i] * _window[i];
        
        if (copyLen < FFT_SIZE)
            Array.Clear(_fftBuffer, copyLen, FFT_SIZE - copyLen);
        
        Array.Copy(_fftBuffer, _realSpectrum, FFT_SIZE);
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
        _currentRms = rms;
        
        float spectralPower = 0f;
        int maxBin = Math.Min(SPECTRUM_SIZE - 1, (int)(20000 / _cachedBinWidth));
        
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

        for (int band = 0; band < 7; band++)
        {
            if (!_settings.BandEnabled[band]) continue;

            var (startBin, endBin) = _bandBinRanges[band];
            float bandWeight = FrequencyBandWeights[band];
            
            for (int bin = startBin; bin <= endBin; bin++)
            {
                float leftMag = leftMags[bin];
                float rightMag = rightMags[bin];
                float totalMag = leftMag + rightMag;
                
                if (totalMag < 1e-6f) continue;
                
                float magDirection = rightMag / totalMag;
                float combinedDirection = _settings.EnablePhaseAnalysis
                    ? _settings.MagnitudePhaseRatio * magDirection + (1f - _settings.MagnitudePhaseRatio) * 0.5f
                    : magDirection;
                
                weightedDirectionSum += combinedDirection * totalMag * bandWeight;
                totalWeight += totalMag * bandWeight;
            }
        }
        
        return totalWeight < 1e-6f ? 0.5f : Math.Clamp(weightedDirectionSum / totalWeight, 0f, 1f);
    }

    private void ProcessFrequencyBandsOptimized(float[] magnitudes, bool scaleWithVolume)
    {
        Span<float> rawBands = stackalloc float[7];
        float totalPower = 0f;
        
        for (int band = 0; band < 7; band++)
        {
            if (!_settings.BandEnabled[band]) continue;

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
            for (int band = 0; band < 7; band++)
            {
                if (!_settings.BandEnabled[band])
                {
                    _result.FrequencyBands[band] = 0f;
                    continue;
                }

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
            for (int band = 0; band < 7; band++)
            {
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
            _spikeDetector.Reset();
            Array.Clear(_smoothedFrequencyBands);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spikeDetector?.Dispose();
    }
}

public sealed class SpikeDetector : IDisposable
{
    private AudioSettings _settings;
    private float _lastAverageVolume;
    private bool _currentSpike;
    private long _lastSpikeTimestamp;
    private double _habituationLevel;
    private long _lastHabituationTimestamp;
    
    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    public SpikeDetector(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lastHabituationTimestamp = Stopwatch.GetTimestamp();
    }

    public void UpdateSettings(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool DetectSpike(float volume)
    {
        long now = Stopwatch.GetTimestamp();
        UpdateHabituationLevel(now);

        bool processorDetectedSpike = volume > _lastAverageVolume * _settings.SpikeThreshold && volume > 0.1f;
        
        if (processorDetectedSpike && _habituationLevel < _settings.HabituationThreshold)
        {
            _lastSpikeTimestamp = now;
            _currentSpike = true;
            _habituationLevel = Math.Min(1.0, _habituationLevel + _settings.HabituationIncrease);
        }
        else if (_currentSpike)
        {
            double secondsSinceSpike = (double)(now - _lastSpikeTimestamp) / TicksPerSecond;
            if (secondsSinceSpike >= _settings.SpikeHoldDuration)
                _currentSpike = false;
        }

        _lastAverageVolume = _lastAverageVolume * 0.9f + volume * 0.1f;
        return _currentSpike;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateHabituationLevel(long currentTimestamp)
    {
        double timeDeltaSeconds = (double)(currentTimestamp - _lastHabituationTimestamp) / TicksPerSecond;
        if (timeDeltaSeconds <= 0) return;

        _habituationLevel = Math.Max(0.0, _habituationLevel * Math.Exp(-_settings.HabituationDecayRate * timeDeltaSeconds));
        _lastHabituationTimestamp = currentTimestamp;
    }

    public void Reset()
    {
        _lastAverageVolume = 0f;
        _currentSpike = false;
        _habituationLevel = 0;
        _lastHabituationTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose() { }
}
