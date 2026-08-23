namespace CrookedToe.Modules.OSCVoiceEmotion;

internal sealed class EmotionStateEngine
{
    private readonly VoiceEmotionOptions _options;
    private EmotionState _state = EmotionState.NeutralState;
    private DateTimeOffset? _silenceSince;
    private EmotionState? _silenceStartState;
    private DateTimeOffset? _lastPrediction;
    private readonly VoicedRmsNormalizer _rmsNormalizer = new();
    public EmotionState State => _state;

    public EmotionStateEngine(VoiceEmotionOptions options) => _options = options;

    public EmotionState Apply(RawEmotionPrediction raw, float occupancy, float vad, float rms, bool clipped, DateTimeOffset now,
        float evidenceWeight = 1f, float voicedSeconds = float.PositiveInfinity)
    {
        _silenceSince = null;
        _silenceStartState = null;
        float[] emotions = [raw.Happy, raw.Sad, raw.Angry, raw.Fear, raw.Disgust, raw.Surprise, raw.Neutral];
        Array.Sort(emotions);
        float top = emotions[^1], margin = top - emotions[^2];
        float confidence = .45f * top + .30f * margin + .15f * occupancy + .10f * vad;
        confidence *= 1f - .65f * Math.Clamp(raw.Unknown, 0, 1);
        if (clipped) confidence *= .65f;
        if (rms < .005f) confidence *= .5f;
        confidence = Math.Clamp(confidence, 0, 1) * Math.Clamp(evidenceWeight, 0, 1);

        float strongest = new[] { raw.Happy, raw.Sad, raw.Angry, raw.Fear, raw.Disgust, raw.Surprise }.Max();
        float loudness = _rmsNormalizer.Normalize(rms);
        float energy = Math.Clamp(.5f * loudness + .5f * strongest, 0, 1);
        float entropy = 0;
        float emotionSum = raw.Happy + raw.Sad + raw.Angry + raw.Fear + raw.Disgust + raw.Surprise + raw.Neutral + raw.Unknown;
        if (emotionSum > 0)
        {
            foreach (float score in new[] { raw.Happy, raw.Sad, raw.Angry, raw.Fear, raw.Disgust, raw.Surprise, raw.Neutral, raw.Unknown })
            {
                float p = Math.Max(score / emotionSum, 1e-7f);
                entropy -= p * MathF.Log(p);
            }
        }
        float certainty = Math.Clamp(1f - entropy / MathF.Log(8), 0, 1);
        float responsiveness = (0.35f + 0.65f * certainty) * Math.Clamp(evidenceWeight, 0, 1);
        float deltaSeconds = _lastPrediction is null ? .4f : Math.Clamp((float)(now - _lastPrediction.Value).TotalSeconds, .05f, 2f);
        _lastPrediction = now;
        bool expressionDisplacingNeutral = strongest > raw.Neutral;
        _state = new EmotionState(
            Smooth(_state.Happy, raw.Happy, responsiveness, deltaSeconds), Smooth(_state.Sad, raw.Sad, responsiveness, deltaSeconds),
            Smooth(_state.Angry, raw.Angry, responsiveness, deltaSeconds), Smooth(_state.Fear, raw.Fear, responsiveness, deltaSeconds),
            Smooth(_state.Surprise, raw.Surprise, responsiveness, deltaSeconds),
            SmoothWithTau(_state.Neutral, raw.Neutral, expressionDisplacingNeutral ? .35f : 1f, deltaSeconds, responsiveness),
            SmoothEvent(_state.Laughter, voicedSeconds >= .45f ? raw.Laughter : 0f, .18f, .55f, deltaSeconds),
            SmoothEvent(_state.Crying, voicedSeconds >= .80f ? raw.Crying : 0f, .5f, 1.2f, deltaSeconds),
            Smooth(_state.Energy, energy, responsiveness, deltaSeconds), confidence, true);
        return _state;
    }

    public EmotionState TickSilence(DateTimeOffset now)
    {
        if (_silenceSince is null)
        {
            _silenceSince = now;
            _silenceStartState = _state;
        }
        TimeSpan elapsed = now - _silenceSince.Value;
        if (elapsed >= _options.FullReset)
        {
            Reset();
            return _state;
        }
        if (elapsed <= _options.SilenceHold) return _state with { Speaking = false };
        float amount = Math.Clamp((float)((elapsed - _options.SilenceHold).TotalSeconds / _options.SilenceDecay.TotalSeconds), 0, 1);
        float keep = 1 - amount;
        EmotionState start = _silenceStartState ?? _state;
        return _state = new EmotionState(start.Happy * keep, start.Sad * keep, start.Angry * keep,
            start.Fear * keep, start.Surprise * keep, start.Neutral + (1 - start.Neutral) * amount,
            start.Laughter * keep, start.Crying * keep, start.Energy * keep, start.Confidence * keep, false);
    }

    public EmotionState SetVocalActivity(bool active) => _state = _state with { Speaking = active };

    public void Reset()
    {
        _state = EmotionState.NeutralState;
        _silenceSince = null;
        _silenceStartState = null;
        _lastPrediction = null;
        _rmsNormalizer.Reset();
    }

    private float Smooth(float previous, float target, float responsiveness, float deltaSeconds)
    {
        target = Math.Clamp(float.IsFinite(target) ? target : 0, 0, 1);
        float tau = target > previous ? _options.ExpressionAttackTau : _options.ExpressionReleaseTau;
        float alpha = (1f - MathF.Exp(-deltaSeconds / tau)) * responsiveness;
        return previous + (target - previous) * alpha;
    }

    private static float SmoothEvent(float previous, float target, float attackTau, float releaseTau, float deltaSeconds) =>
        SmoothWithTau(previous, target, target > previous ? attackTau : releaseTau, deltaSeconds, 1f);

    private static float SmoothWithTau(float previous, float target, float tau, float deltaSeconds, float responsiveness)
    {
        target = Math.Clamp(float.IsFinite(target) ? target : 0, 0, 1);
        float alpha = (1f - MathF.Exp(-deltaSeconds / tau)) * responsiveness;
        return previous + (target - previous) * alpha;
    }

}

internal sealed class VoicedRmsNormalizer
{
    private const int Capacity = 150; // roughly one minute at the default inference rate
    private readonly Queue<float> _history = new();

    public float Normalize(float rms)
    {
        if (float.IsFinite(rms) && rms > .001f)
        {
            _history.Enqueue(rms);
            while (_history.Count > Capacity) _history.Dequeue();
        }
        if (_history.Count < 10) return Math.Clamp((rms - .005f) / .12f, 0, 1);
        float[] sorted = _history.Order().ToArray();
        float quiet = sorted[(int)((sorted.Length - 1) * .20f)];
        float loud = sorted[(int)((sorted.Length - 1) * .95f)];
        return Math.Clamp((rms - quiet) / Math.Max(loud - quiet, .003f), 0, 1);
    }

    public void Reset() => _history.Clear();
}

internal sealed class HysteresisBoolean
{
    private readonly float _on, _off;
    private readonly TimeSpan _onHold, _offHold;
    private DateTimeOffset? _pendingSince;
    public bool Value { get; private set; }
    public HysteresisBoolean(float on = .65f, float off = .40f, int onMs = 600, int offMs = 800) =>
        (_on, _off, _onHold, _offHold) = (on, off, TimeSpan.FromMilliseconds(onMs), TimeSpan.FromMilliseconds(offMs));
    public bool Update(float score, DateTimeOffset now)
    {
        bool requested = Value ? score > _off : score >= _on;
        if (requested == Value) { _pendingSince = null; return Value; }
        _pendingSince ??= now;
        if (now - _pendingSince >= (requested ? _onHold : _offHold)) { Value = requested; _pendingSince = null; }
        return Value;
    }
    public void Reset() { Value = false; _pendingSince = null; }
}
