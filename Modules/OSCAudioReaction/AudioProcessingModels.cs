using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CrookedToe.Modules.OSCAudioReaction;

public sealed record AudioSettings
{
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 2;
    public float Gain { get; init; } = 1.0f;
    public bool EnableAGC { get; init; } = true;
    public float Smoothing { get; init; } = 0.3f;
    public float DirectionThreshold { get; init; } = 0.01f;
    public float SpikeThreshold { get; init; } = 2.0f;
    public float SpikeHoldDuration { get; init; } = 0.5f;
    public float FrequencySmoothing { get; init; } = 0.7f;
    public bool EnableDirectionalPause { get; init; }
    public float DirectionalPauseFactor { get; init; } = 1.0f;
    public double HabituationIncrease { get; init; } = 0.15;
    public double HabituationDecayRate { get; init; } = 0.02;
    public double HabituationThreshold { get; init; } = 0.3;
    public bool ScaleFrequencyWithVolume { get; init; }
    public bool[] BandEnabled { get; init; } = [true, true, true, true, true, true, true];
}

public sealed class AudioProcessingResult
{
    public float Volume { get; set; }
    public float Direction { get; set; } = 0.5f;
    public float[] FrequencyBands { get; } = new float[AudioBandDefinitions.Count];
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

public sealed class SpikeDetector(AudioSettings settings) : IDisposable
{
    private AudioSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private float _lastAverageVolume;
    private bool _currentSpike;
    private bool _hasBaseline;
    private long _lastSpikeTimestamp;
    private double _habituationLevel;
    private long _lastHabituationTimestamp = Stopwatch.GetTimestamp();

    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    public void UpdateSettings(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool DetectSpike(float volume)
    {
        long now = Stopwatch.GetTimestamp();
        UpdateHabituationLevel(now);

        if (!_hasBaseline)
        {
            _lastAverageVolume = volume;
            _hasBaseline = true;
            return false;
        }

        float baseline = MathF.Max(_lastAverageVolume, 0.01f);
        bool processorDetectedSpike = volume > baseline * _settings.SpikeThreshold && volume > 0.1f;

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
        if (timeDeltaSeconds <= 0)
            return;

        _habituationLevel = Math.Max(0.0, _habituationLevel * Math.Exp(-_settings.HabituationDecayRate * timeDeltaSeconds));
        _lastHabituationTimestamp = currentTimestamp;
    }

    public void Reset()
    {
        _lastAverageVolume = 0f;
        _currentSpike = false;
        _hasBaseline = false;
        _habituationLevel = 0;
        _lastHabituationTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose() { }
}
