using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VRCOSC.App.Audio;

namespace CrookedToe.Modules.OSCVoiceEmotion;

internal sealed class MicrophoneCapture : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly MMDevice _device;
    public event Action<ReadOnlyMemory<float>, DateTimeOffset>? SamplesAvailable;
    public event Action<Exception>? CaptureFailed;
    public string DeviceName => _device.FriendlyName;
    public int SampleRate => _capture.WaveFormat.SampleRate;

    public MicrophoneCapture(string selectedDeviceId)
    {
        _device = string.IsNullOrEmpty(selectedDeviceId)
            ? WasapiCapture.GetDefaultCaptureDevice()
            : AudioDeviceHelper.GetDeviceByID(selectedDeviceId)
                ?? throw new InvalidOperationException("VRCOSC's selected microphone is not available.");
        _capture = new WasapiCapture(_device) { ShareMode = AudioClientShareMode.Shared };
        _capture.DataAvailable += OnData;
    }
    public void Start() => _capture.StartRecording();
    public void Stop() { try { _capture.StopRecording(); } catch { } }
    private void OnData(object? _, WaveInEventArgs e)
    {
        try
        {
            float[] interleaved = DecodeSamples(e.Buffer.AsSpan(0, e.BytesRecorded), _capture.WaveFormat);
            float[] mono = AudioConverter.ToMono(interleaved, _capture.WaveFormat.Channels);
            SamplesAvailable?.Invoke(mono, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) { CaptureFailed?.Invoke(ex); }
    }

    private static float[] DecodeSamples(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        bool extensibleFloat = format is WaveFormatExtensible extensible &&
            extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || extensibleFloat)
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();

        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample < 2) throw new NotSupportedException($"Unsupported microphone format: {format}");
        var output = new float[bytes.Length / bytesPerSample];
        for (int i = 0; i < output.Length; i++)
        {
            int offset = i * bytesPerSample;
            output[i] = bytesPerSample switch
            {
                2 => (short)(bytes[offset] | bytes[offset + 1] << 8) / 32768f,
                3 => ((bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(bytes.Slice(offset, 4)) / 2147483648f,
                _ => throw new NotSupportedException($"Unsupported microphone format: {format}")
            };
        }
        return output;
    }

    public void Dispose() { Stop(); _capture.DataAvailable -= OnData; _capture.Dispose(); _device.Dispose(); }
}

internal sealed class VoiceEmotionRuntime : IAsyncDisposable
{
    private readonly VoiceEmotionOptions _options;
    private readonly IEmotionInferenceBackend _backend;
    private readonly MicrophoneCapture _capture;
    private readonly FloatRingBuffer _buffer;
    private readonly int _captureSampleRate;
    private readonly EnergyVoiceActivityDetector _vad = new();
    private readonly SpeakingGate _speaking;
    private readonly EmotionStateEngine _state;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private float _latestVad;
    private long _lastSpeechTicks;
    private long _utteranceStartTicks;
    private long _voicedSamples;
    private volatile bool _finalInferenceDone;
    private long _eventActivityUntilTicks;
    private int _resetEvidenceRequested;
    private long _lastWarningTicks;
    public event Action<EmotionState>? StateChanged;
    public event Action<string>? Warning;

    public VoiceEmotionRuntime(IEmotionInferenceBackend backend, VoiceEmotionOptions? options = null, string selectedDeviceId = "")
    {
        _options = options ?? new(); _backend = backend;
        _capture = new MicrophoneCapture(selectedDeviceId);
        _captureSampleRate = _capture.SampleRate;
        _buffer = new FloatRingBuffer((int)(_captureSampleRate * _options.BufferCapacity.TotalSeconds));
        _speaking = new SpeakingGate(_options); _state = new EmotionStateEngine(_options);
        _capture.SamplesAvailable += OnSamples;
        _capture.CaptureFailed += OnCaptureFailed;
    }

    public string MicrophoneName => _capture.DeviceName;

    public void Start() { _capture.Start(); _loop = Task.Run(RunAsync); }
    private void OnSamples(ReadOnlyMemory<float> memory, DateTimeOffset timestamp)
    {
        ReadOnlySpan<float> samples = memory.Span;
        float vad = _vad.Analyze(samples);
        bool wasSpeaking = _speaking.IsSpeaking;
        bool speaking = _speaking.Update(vad, timestamp);
        bool voiced = vad >= .5f;
        _latestVad = vad;
        if (speaking)
        {
            if (!wasSpeaking)
            {
                if (Interlocked.Read(ref _utteranceStartTicks) == 0)
                {
                    Interlocked.Exchange(ref _utteranceStartTicks, timestamp.UtcTicks);
                    Interlocked.Exchange(ref _resetEvidenceRequested, 1);
                }
                _finalInferenceDone = false;
            }
        }
        if (voiced)
        {
            Interlocked.Add(ref _voicedSamples, samples.Length);
            Interlocked.Exchange(ref _lastSpeechTicks, timestamp.UtcTicks);
        }
        _buffer.Write(samples, vad);
    }

    private async Task RunAsync()
    {
        using var inferenceTimer = new PeriodicTimer(_options.InferenceInterval);
        using var outputTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        Task<bool> nextInference = inferenceTimer.WaitForNextTickAsync(_stop.Token).AsTask();
        Task<bool> nextOutput = outputTimer.WaitForNextTickAsync(_stop.Token).AsTask();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(nextInference, nextOutput).ConfigureAwait(false);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (completed == nextInference)
                {
                    if (await nextInference.ConfigureAwait(false)) TryInferNewest(now, final: false);
                    nextInference = inferenceTimer.WaitForNextTickAsync(_stop.Token).AsTask();
                }
                if (completed == nextOutput)
                {
                    if (await nextOutput.ConfigureAwait(false))
                    {
                        long ticks = Interlocked.Read(ref _lastSpeechTicks);
                        TimeSpan silence = ticks == 0 ? TimeSpan.MaxValue : now - new DateTimeOffset(ticks, TimeSpan.Zero);
                        bool modelEventActive = now.UtcTicks < Interlocked.Read(ref _eventActivityUntilTicks);
                        bool vocalActive = _speaking.IsSpeaking || modelEventActive;
                        _state.SetVocalActivity(vocalActive);
                        if (!vocalActive && (ticks == 0 || silence > _options.SpeechOffHold))
                        {
                            if (!_finalInferenceDone && Interlocked.Read(ref _utteranceStartTicks) != 0)
                            {
                                _finalInferenceDone = TryInferNewest(now, final: true);
                            }
                            _state.TickSilence(now);
                            if (_finalInferenceDone && silence >= TimeSpan.FromMilliseconds(700))
                            {
                                Interlocked.Exchange(ref _utteranceStartTicks, 0);
                                Interlocked.Exchange(ref _voicedSamples, 0);
                            }
                        }
                        try { StateChanged?.Invoke(_state.State); }
                        catch (Exception ex) { ReportWarning($"Could not publish voice-emotion state: {ex.Message}"); }
                    }
                    nextOutput = outputTimer.WaitForNextTickAsync(_stop.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { ReportWarning($"Voice-emotion worker stopped unexpectedly: {ex.Message}"); }
    }

    private bool TryInferNewest(DateTimeOffset now, bool final)
    {
        try { InferNewest(now, final); return true; }
        catch (Exception ex) { ReportWarning($"SenseVoice inference failed: {ex.Message}"); return false; }
    }

    private void InferNewest(DateTimeOffset now, bool final)
    {
        long voicedSamples = Interlocked.Read(ref _voicedSamples);
        float voicedSeconds = voicedSamples / (float)_captureSampleRate;
        if (voicedSeconds < _options.MinimumEventSpeech.TotalSeconds) return;

        TimeSpan requestedWindow;
        if (final)
        {
            long startTicks = Interlocked.Read(ref _utteranceStartTicks);
            double utteranceSeconds = startTicks == 0 ? 0 : (now - new DateTimeOffset(startTicks, TimeSpan.Zero)).TotalSeconds;
            requestedWindow = TimeSpan.FromSeconds(Math.Min(_options.FinalInferenceWindow.TotalSeconds,
                utteranceSeconds + _options.PreSpeechAudio.TotalSeconds));
        }
        else
        {
            long startTicks = Interlocked.Read(ref _utteranceStartTicks);
            double utteranceSeconds = startTicks == 0 ? 0 : (now - new DateTimeOffset(startTicks, TimeSpan.Zero)).TotalSeconds;
            requestedWindow = TimeSpan.FromSeconds(Math.Min(_options.InferenceWindow.TotalSeconds,
                utteranceSeconds + _options.PreSpeechAudio.TotalSeconds));
        }
        int length = Math.Min(_buffer.Count, (int)(_captureSampleRate * requestedWindow.TotalSeconds));
        if (length < _captureSampleRate / 4) return;
        if (!_buffer.TryReadLatest(length, out float[] nativeAudio, out float occupancy, out float windowVad)) return;
        double sumSquares = 0;
        bool windowClipped = false;
        foreach (float sample in nativeAudio)
        {
            sumSquares += sample * sample;
            windowClipped |= Math.Abs(sample) >= .995f;
        }
        float windowRms = (float)Math.Sqrt(sumSquares / Math.Max(1, nativeAudio.Length));
        float[] audio16Khz = AudioResampler.To16Khz(nativeAudio, _captureSampleRate);
        if (Interlocked.Exchange(ref _resetEvidenceRequested, 0) != 0) _backend.ResetEvidence();
        RawEmotionPrediction prediction = _backend.Predict(audio16Khz);
        long lastVoiceTicks = Interlocked.Read(ref _lastSpeechTicks);
        TimeSpan recentSilence = lastVoiceTicks == 0 ? TimeSpan.MaxValue : now - new DateTimeOffset(lastVoiceTicks, TimeSpan.Zero);
        if (prediction.Laughter >= .25f && (_latestVad >= .2f || recentSilence < TimeSpan.FromMilliseconds(500)))
            ExtendEventActivity(now.AddMilliseconds(900));
        if (prediction.Crying >= .25f && (_latestVad >= .2f || recentSilence < TimeSpan.FromMilliseconds(800)))
            ExtendEventActivity(now.AddMilliseconds(1400));
        float evidenceWeight = voicedSeconds < _options.PreliminarySpeech.TotalSeconds ? 0f
            : voicedSeconds < _options.MinimumEmotionSpeech.TotalSeconds ? .40f
            : voicedSeconds < _options.FullConfidenceSpeech.TotalSeconds ? .65f
            : final ? .85f : 1f;
        if (_options.MinimumSpeechOccupancy > 0)
            evidenceWeight *= Math.Clamp(occupancy / _options.MinimumSpeechOccupancy, .35f, 1f);
        _state.Apply(prediction, occupancy, windowVad, windowRms, windowClipped, now, evidenceWeight, voicedSeconds);
    }

    private void OnCaptureFailed(Exception ex) => ReportWarning($"Microphone capture failed: {ex.Message}");

    private void ReportWarning(string message)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastWarningTicks);
        if (previous != 0 && now - previous < System.Diagnostics.Stopwatch.Frequency * 5) return;
        Interlocked.Exchange(ref _lastWarningTicks, now);
        try { Warning?.Invoke(message); } catch { }
    }

    private void ExtendEventActivity(DateTimeOffset until)
    {
        long requested = until.UtcTicks;
        while (true)
        {
            long current = Interlocked.Read(ref _eventActivityUntilTicks);
            if (current >= requested || Interlocked.CompareExchange(ref _eventActivityUntilTicks, requested, current) == current)
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _capture.Stop();
        if (_loop != null) try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _capture.SamplesAvailable -= OnSamples; _capture.CaptureFailed -= OnCaptureFailed;
        _capture.Dispose(); _backend.Dispose(); _stop.Dispose();
    }
}
