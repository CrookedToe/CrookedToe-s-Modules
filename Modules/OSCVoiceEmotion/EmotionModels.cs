namespace CrookedToe.Modules.OSCVoiceEmotion;

public readonly record struct AudioFrame(ReadOnlyMemory<float> Samples, int SampleRate, DateTimeOffset Timestamp);

public sealed record RawEmotionPrediction(
    float Happy, float Sad, float Angry, float Fear, float Disgust, float Surprise,
    float Neutral, float Unknown, float Laughter, float Crying);

public sealed record EmotionState(
    float Happy, float Sad, float Angry, float Fear, float Surprise, float Neutral,
    float Laughter, float Crying, float Energy, float Confidence, bool Speaking)
{
    public static EmotionState NeutralState { get; } = new(0, 0, 0, 0, 0, 1, 0, 0, 0, 0, false);
}

public interface IEmotionInferenceBackend : IDisposable
{
    RawEmotionPrediction Predict(ReadOnlyMemory<float> audio16Khz);
    void ResetEvidence();
}

internal sealed record VoiceEmotionOptions
{
    public int SampleRate { get; init; } = 16_000;
    public TimeSpan BufferCapacity { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan InferenceWindow { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan FinalInferenceWindow { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan InferenceInterval { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan PreliminarySpeech { get; init; } = TimeSpan.FromSeconds(.9);
    public TimeSpan FullConfidenceSpeech { get; init; } = TimeSpan.FromSeconds(1.6);
    public TimeSpan MinimumEmotionSpeech { get; init; } = TimeSpan.FromSeconds(1.2);
    public TimeSpan MinimumEventSpeech { get; init; } = TimeSpan.FromSeconds(.45);
    public TimeSpan PreSpeechAudio { get; init; } = TimeSpan.FromMilliseconds(180);
    public float MinimumSpeechOccupancy { get; init; } = 0.35f;
    public float SpeechStartThreshold { get; init; } = 0.60f;
    public float SpeechStopThreshold { get; init; } = 0.35f;
    public TimeSpan SpeechOffHold { get; init; } = TimeSpan.FromMilliseconds(350);
    public float ExpressionAttackTau { get; init; } = .42f;
    public float ExpressionReleaseTau { get; init; } = 1.05f;
    public TimeSpan SilenceHold { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan SilenceDecay { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan FullReset { get; init; } = TimeSpan.FromSeconds(3.5);
}
