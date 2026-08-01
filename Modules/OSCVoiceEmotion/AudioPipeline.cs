using System.Runtime.InteropServices;
using System.IO;
using NAudio.Wave;

namespace CrookedToe.Modules.OSCVoiceEmotion;

internal sealed class FloatRingBuffer
{
    private readonly float[] _audio;
    private readonly float[] _speech;
    private int _write;
    private int _count;
    private readonly object _gate = new();

    public FloatRingBuffer(int capacity) => (_audio, _speech) = (new float[capacity], new float[capacity]);

    public void Write(ReadOnlySpan<float> samples, float speechProbability)
    {
        lock (_gate)
        {
            foreach (float sample in samples)
            {
                _audio[_write] = Math.Clamp(float.IsFinite(sample) ? sample : 0f, -1f, 1f);
                _speech[_write] = Math.Clamp(speechProbability, 0f, 1f);
                _write = (_write + 1) % _audio.Length;
                _count = Math.Min(_count + 1, _audio.Length);
            }
        }
    }

    public bool TryReadLatest(int length, out float[] audio, out float speechOccupancy)
    {
        return TryReadLatest(length, out audio, out speechOccupancy, out _);
    }

    public bool TryReadLatest(int length, out float[] audio, out float speechOccupancy, out float averageSpeechProbability)
    {
        lock (_gate)
        {
            if (_count < length) { audio = []; speechOccupancy = 0; averageSpeechProbability = 0; return false; }
            audio = new float[length];
            float speech = 0, probabilitySum = 0;
            int start = (_write - length + _audio.Length) % _audio.Length;
            for (int i = 0; i < length; i++)
            {
                int index = (start + i) % _audio.Length;
                audio[i] = _audio[index];
                speech += _speech[index] >= 0.5f ? 1f : 0f;
                probabilitySum += _speech[index];
            }
            speechOccupancy = speech / length;
            averageSpeechProbability = probabilitySum / length;
            return true;
        }
    }

    public int Count { get { lock (_gate) return _count; } }
}

internal sealed class EnergyVoiceActivityDetector
{
    private const float FloorAdaptation = 0.02f;
    private float _noiseFloor = 0.003f;

    public float Analyze(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
        float rms = (float)Math.Sqrt(sum / samples.Length);
        if (rms < _noiseFloor * 2f)
            _noiseFloor = Math.Clamp(_noiseFloor * (1 - FloorAdaptation) + rms * FloorAdaptation, 0.0005f, 0.03f);
        float low = Math.Max(0.004f, _noiseFloor * 1.8f);
        float high = Math.Max(0.018f, _noiseFloor * 5f);
        return Math.Clamp((rms - low) / (high - low), 0f, 1f);
    }
}

internal sealed class SpeakingGate
{
    private readonly VoiceEmotionOptions _options;
    private DateTimeOffset? _belowSince;
    public bool IsSpeaking { get; private set; }
    public SpeakingGate(VoiceEmotionOptions options) => _options = options;

    public bool Update(float probability, DateTimeOffset now)
    {
        if (!IsSpeaking && probability >= _options.SpeechStartThreshold) { IsSpeaking = true; _belowSince = null; }
        else if (IsSpeaking && probability < _options.SpeechStopThreshold)
        {
            _belowSince ??= now;
            if (now - _belowSince >= _options.SpeechOffHold) IsSpeaking = false;
        }
        else if (probability >= _options.SpeechStopThreshold) _belowSince = null;
        return IsSpeaking;
    }
}

internal static class AudioConverter
{
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    public static float[] ToMono16Khz(ReadOnlySpan<float> interleaved, int sourceRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        int frames = interleaved.Length / channels;
        if (frames == 0) return [];
        var mono = ToMono(interleaved, channels);
        if (sourceRate == 16_000) return mono;
        int outputLength = (int)Math.Round(frames * (16_000d / sourceRate));
        var output = new float[outputLength];
        double step = sourceRate / 16_000d;
        for (int i = 0; i < output.Length; i++)
        {
            double position = i * step;
            int left = Math.Min((int)position, frames - 1);
            int right = Math.Min(left + 1, frames - 1);
            float fraction = (float)(position - left);
            output[i] = mono[left] + (mono[right] - mono[left]) * fraction;
        }
        return output;
    }
}

internal static class AudioResampler
{
    public static float[] To16Khz(ReadOnlySpan<float> mono, int sourceRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1);
        if (sourceRate == 16_000) return mono.ToArray();
        byte[] inputBytes = MemoryMarshal.AsBytes(mono).ToArray();
        using var memory = new MemoryStream(inputBytes, writable: false);
        using var source = new RawSourceWaveStream(memory, WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 1));
        using var resampler = new MediaFoundationResampler(source, WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1))
        {
            ResamplerQuality = 60
        };
        int expectedBytes = (int)Math.Ceiling(mono.Length * (16_000d / sourceRate)) * sizeof(float);
        using var output = new MemoryStream(expectedBytes + 4096);
        var buffer = new byte[16_384];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        return MemoryMarshal.Cast<byte, float>(output.GetBuffer().AsSpan(0, (int)output.Length)).ToArray();
    }
}
