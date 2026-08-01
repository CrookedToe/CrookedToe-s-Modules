using CrookedToe.Modules.OSCVoiceEmotion;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CrookedToesModules.Tests.OSCVoiceEmotion;

[TestClass]
public sealed class VoiceEmotionCoreTests
{
    [TestMethod]
    public void RingBuffer_ReturnsNewestBoundedWindow()
    {
        var buffer = new FloatRingBuffer(5);
        buffer.Write([.1f, .2f, .3f], 1);
        buffer.Write([.4f, .5f, .6f], 0);
        Assert.IsTrue(buffer.TryReadLatest(4, out float[] audio, out float occupancy));
        CollectionAssert.AreEqual(new float[] { .3f, .4f, .5f, .6f }, audio);
        Assert.AreEqual(.25f, occupancy, .001f);
    }

    [TestMethod]
    public void Converter_DownmixesAndResamples()
    {
        float[] stereo = new float[48_000 * 2];
        for (int i = 0; i < 48_000; i++) { stereo[i * 2] = .75f; stereo[i * 2 + 1] = .25f; }
        float[] result = AudioConverter.ToMono16Khz(stereo, 48_000, 2);
        Assert.AreEqual(16_000, result.Length);
        Assert.AreEqual(.5f, result[100], .0001f);
    }

    [TestMethod]
    public void SpeakingGate_UsesStopHoldAndHysteresis()
    {
        var gate = new SpeakingGate(new VoiceEmotionOptions());
        DateTimeOffset start = DateTimeOffset.UtcNow;
        Assert.IsTrue(gate.Update(.7f, start));
        Assert.IsTrue(gate.Update(.2f, start.AddMilliseconds(200)));
        Assert.IsFalse(gate.Update(.2f, start.AddMilliseconds(551)));
    }

    [TestMethod]
    public void Silence_DecaysToNeutralThenResets()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        engine.Apply(new RawEmotionPrediction(1, 0, 0, 0, 0, 0, 0, 0, 0, 0), 1, 1, .1f, false, now);
        engine.TickSilence(now);
        EmotionState faded = engine.TickSilence(now.AddSeconds(2));
        Assert.IsTrue(faded.Happy < engine.State.Neutral);
        Assert.AreEqual(EmotionState.NeutralState, engine.TickSilence(now.AddSeconds(8)));
    }

    [TestMethod]
    public void BooleanGate_RequiresSustainedActivationAndDeactivation()
    {
        var gate = new HysteresisBoolean();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.IsFalse(gate.Update(.8f, now));
        Assert.IsTrue(gate.Update(.8f, now.AddMilliseconds(601)));
        Assert.IsTrue(gate.Update(.2f, now.AddMilliseconds(700)));
        Assert.IsFalse(gate.Update(.2f, now.AddMilliseconds(1501)));
    }

    [TestMethod]
    [DataRow(0.123456f, 0.123f)]
    [DataRow(0.0123456f, 0.0123f)]
    [DataRow(0.00123456f, 0.00123f)]
    [DataRow(0.9999f, 1f)]
    [DataRow(-1f, 0f)]
    [DataRow(2f, 1f)]
    public void PublishedFloats_UseThreeSignificantFigures(float input, float expected)
    {
        Assert.AreEqual(expected, OSCVoiceEmotionModule.ThreeSignificantFigures(input), 0.000001f);
    }

    [TestMethod]
    public void ExpressionPromptlyDisplacesSilenceNeutralState()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        engine.TickSilence(now);
        engine.TickSilence(now.AddSeconds(8));

        var expressive = new RawEmotionPrediction(.8f, .02f, .02f, .02f, .01f, .04f, .08f, .01f, 0, 0);
        EmotionState first = engine.Apply(expressive, .6f, .8f, .08f, false, now.AddSeconds(9));
        EmotionState second = engine.Apply(expressive, .6f, .8f, .08f, false, now.AddSeconds(9.75));

        Assert.IsTrue(first.Happy > .25f);
        Assert.IsTrue(first.Neutral < .75f);
        Assert.IsTrue(second.Happy > second.Neutral);
    }

    [TestMethod]
    public void ModelScores_RemainVisibleWithoutSpeculativeClassFloors()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var prediction = new RawEmotionPrediction(.70f, .08f, .06f, .04f, .02f, .03f, .05f, .02f, 0, 0);
        EmotionState state = engine.Apply(prediction, .8f, .9f, .08f, false, now);
        Assert.IsTrue(state.Happy > 0);
        Assert.IsTrue(state.Sad > 0);
        Assert.IsTrue(state.Angry > 0);
    }

    [TestMethod]
    public void EmotionChanges_DoNotWaitForHiddenDominantLabelState()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var happy = new RawEmotionPrediction(.65f, .05f, .10f, .02f, .01f, .03f, .10f, .04f, 0, 0);
        var angry = new RawEmotionPrediction(.10f, .04f, .65f, .02f, .01f, .03f, .10f, .05f, 0, 0);
        engine.Apply(happy, .8f, .9f, .08f, false, now);
        EmotionState firstChallenge = engine.Apply(angry, .8f, .9f, .08f, false, now.AddMilliseconds(400));
        EmotionState confirmed = engine.Apply(angry, .8f, .9f, .08f, false, now.AddMilliseconds(800));
        Assert.IsTrue(firstChallenge.Angry > 0);
        Assert.IsTrue(confirmed.Angry > 0);
    }

    [TestMethod]
    public void CryingRequiresLongerEvidenceThanBriefVocalEvent()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var crying = new RawEmotionPrediction(0, 0, 0, 0, 0, 0, .8f, .2f, 0, 1f);
        EmotionState tooBrief = engine.Apply(crying, .8f, .9f, .08f, false, now, voicedSeconds: .5f);
        EmotionState enoughEvidence = engine.Apply(crying, .8f, .9f, .08f, false, now.AddMilliseconds(400), voicedSeconds: .9f);
        Assert.AreEqual(0f, tooBrief.Crying);
        Assert.IsTrue(enoughEvidence.Crying > 0);
    }

    [TestMethod]
    public void ModelEventActivityPreventsSpeakingStateFromDropping()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        engine.TickSilence(now);
        Assert.IsFalse(engine.State.Speaking);
        Assert.IsTrue(engine.SetVocalActivity(true).Speaking);
    }

    [TestMethod]
    public void SilenceDecay_IsBasedOnSilenceStartRatherThanCompoundingPerTick()
    {
        var engine = new EmotionStateEngine(new VoiceEmotionOptions());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        engine.Apply(new RawEmotionPrediction(1, 0, 0, 0, 0, 0, 0, 0, 0, 0), 1, 1, .1f, false, now);
        engine.TickSilence(now);
        EmotionState once = engine.TickSilence(now.AddSeconds(1));
        EmotionState repeated = engine.TickSilence(now.AddSeconds(1));
        Assert.AreEqual(once.Happy, repeated.Happy, .000001f);
    }

    [TestMethod]
    public void HighQualityResampler_ProducesExpectedSampleRate()
    {
        var source = new float[4_800];
        for (int i = 0; i < source.Length; i++) source[i] = MathF.Sin(2 * MathF.PI * 440 * i / 48_000f);
        float[] output = AudioResampler.To16Khz(source, 48_000);
        Assert.AreEqual(1_600, output.Length, 2);
        Assert.IsTrue(output.Max() > .9f);
    }

    [TestMethod]
    public void HighQualityResampler_SuppressesFrequenciesAboveNewNyquist()
    {
        var source = new float[48_000];
        for (int i = 0; i < source.Length; i++) source[i] = MathF.Sin(2 * MathF.PI * 12_000 * i / 48_000f);
        float[] output = AudioResampler.To16Khz(source, 48_000);
        float rms = MathF.Sqrt(output.Select(x => x * x).Average());
        Assert.IsTrue(rms < .05f, $"Aliased high-frequency energy remained at RMS {rms}");
    }

    [TestMethod]
    public void FeatureFrontend_MatchesOfficialKaldiReference()
    {
        var audio = new float[16_000];
        for (int i = 0; i < audio.Length; i++)
        {
            float t = i / 16_000f;
            audio[i] = .1f * MathF.Sin(2 * MathF.PI * 220 * t) + .03f * MathF.Sin(2 * MathF.PI * 880 * t);
        }
        float[,] features = SenseVoiceFeatures.Extract(audio, new float[560], Enumerable.Repeat(1f, 560).ToArray(), dither: false);
        Assert.AreEqual(17, features.GetLength(0));
        float[] expected = [12.798139f, 8.581795f, 10.831838f, 9.636045f, 8.551712f, 12.798139f, 8.551712f, 8.032234f];
        int[] indices = [0, 1, 20, 40, 79, 80, 159, 559];
        for (int i = 0; i < indices.Length; i++)
            Assert.AreEqual(expected[i], features[0, indices[i]], .08f, $"Feature {indices[i]} differs from kaldi-native-fbank");
    }

    [TestMethod]
    public void RichTokenDecoder_UsesSerFrameOneAndAedFrameTwo()
    {
        var logits = new DenseTensor<float>(new[] { 1, 4, 25_055 });
        logits[0, 1, 25_001] = 8f; // HAPPY on SER frame
        logits[0, 2, 25_002] = 20f; // SAD on AED frame must not contaminate emotion
        logits[0, 1, 25_010] = 20f; // Cry on SER frame must not contaminate event
        logits[0, 2, 24_997] = 7f; // Laughter on AED frame
        float[] emotions = SenseVoiceOnnxBackend.ReadEmotionLogits(logits);
        float[] events = SenseVoiceOnnxBackend.ReadEventProbabilities(logits);
        Assert.AreEqual(8, emotions.Length);
        Assert.AreEqual(8f, emotions[0]);
        Assert.AreEqual(0f, emotions[1]);
        Assert.IsTrue(events[0] > events[1]);
        Assert.IsTrue(events[0] < .1f, "AED confidence must be normalized against the full vocabulary");
    }
}
