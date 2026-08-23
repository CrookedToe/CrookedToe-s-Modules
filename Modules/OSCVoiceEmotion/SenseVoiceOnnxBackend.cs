using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NWaves.Transforms;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrookedToe.Modules.OSCVoiceEmotion;

/// <summary>CPU-only adapter for an official FunASR SenseVoiceSmall ONNX export.</summary>
public sealed class SenseVoiceOnnxBackend : IEmotionInferenceBackend
{
    // Token IDs from the official SenseVoiceSmall vocabulary/model definition.
    private const int Happy = 25001, Sad = 25002, Angry = 25003, Neutral = 25004, Fearful = 25005,
        Disgusted = 25006, Surprised = 25007, Unknown = 25009,
        LaughterToken = 24997, CryingToken = 25010;
    private readonly InferenceSession _session;
    private readonly string _logitsName;
    private readonly (float[] Shift, float[] Scale) _cmvn;
    private float[]? _smoothedEmotionLogits;
    private long _lastPredictionTimestamp;
    // Keep raw relative model confidence until a labelled calibration set exists.
    private const float EmotionTemperature = 1f;
    private const float EvidenceTauSeconds = .5f;

    public SenseVoiceOnnxBackend(string modelPath, int threads = 2)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("SenseVoiceSmall ONNX model was not found.", modelPath);
        OnnxNativeRuntime.EnsureLoaded();
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(threads, 1, 2), InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _session = new InferenceSession(modelPath, options);
        string cmvnPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "am.mvn");
        if (!File.Exists(cmvnPath)) throw new FileNotFoundException("SenseVoice requires am.mvn beside the ONNX model.", cmvnPath);
        _cmvn = SenseVoiceFeatures.LoadCmvn(cmvnPath);
        string[] required = ["speech", "speech_lengths", "language", "textnorm"];
        foreach (string input in required)
            if (!_session.InputMetadata.ContainsKey(input)) throw new InvalidDataException($"ONNX model lacks required input '{input}'. Use the official FunASR SenseVoice export.");
        _logitsName = _session.OutputMetadata.ContainsKey("ctc_logits") ? "ctc_logits" :
            _session.OutputMetadata.Keys.FirstOrDefault() ?? throw new InvalidDataException("ONNX model has no outputs.");
    }

    public RawEmotionPrediction Predict(ReadOnlyMemory<float> audio16Khz)
    {
        float[,] features = SenseVoiceFeatures.Extract(audio16Khz.Span, _cmvn.Shift, _cmvn.Scale);
        int frames = features.GetLength(0), width = features.GetLength(1);
        var speech = new DenseTensor<float>(new[] { 1, frames, width });
        for (int t = 0; t < frames; t++) for (int f = 0; f < width; f++) speech[0, t, f] = features[t, f];
        using var results = _session.Run([
            NamedOnnxValue.CreateFromTensor("speech", speech),
            NamedOnnxValue.CreateFromTensor("speech_lengths", new DenseTensor<int>(new[] { frames }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("language", new DenseTensor<int>(new[] { 4 }, new[] { 1 })), // English
            NamedOnnxValue.CreateFromTensor("textnorm", new DenseTensor<int>(new[] { 15 }, new[] { 1 })) // without ITN
        ]);
        Tensor<float> logits = results.First(x => x.Name == _logitsName).AsTensor<float>();
        if (logits.Rank != 3 || logits.Dimensions[1] < 4 || logits.Dimensions[2] <= Unknown)
            throw new InvalidDataException("SenseVoice output does not match [batch, time, vocabulary] with rich-token frames.");
        // Official rich-token layout: 0=language, 1=emotion (SER), 2=audio event (AED), 3=text normalization.
        float[] emotionLogits = ReadEmotionLogits(logits);
        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_smoothedEmotionLogits == null)
        {
            _smoothedEmotionLogits = emotionLogits.ToArray();
        }
        else
        {
            float delta = Math.Clamp((timestamp - _lastPredictionTimestamp) / (float)System.Diagnostics.Stopwatch.Frequency, .05f, 2f);
            float alpha = 1f - MathF.Exp(-delta / EvidenceTauSeconds);
            for (int i = 0; i < emotionLogits.Length; i++)
                _smoothedEmotionLogits[i] += alpha * (emotionLogits[i] - _smoothedEmotionLogits[i]);
        }
        _lastPredictionTimestamp = timestamp;
        float[] probabilities = Softmax(_smoothedEmotionLogits, EmotionTemperature);
        // AED is trained with cross-entropy over the complete vocabulary. A softmax over only
        // hand-picked event tokens spuriously inflates weak event logits on ambiguous audio.
        float[] eventProbabilities = ReadEventProbabilities(logits);
        float laughter = eventProbabilities[0];
        float crying = eventProbabilities[1];
        return new(probabilities[0], probabilities[1], probabilities[2], probabilities[3], probabilities[4],
            probabilities[5], probabilities[6], probabilities[7], laughter, crying);
    }

    public void ResetEvidence()
    {
        _smoothedEmotionLogits = null;
        _lastPredictionTimestamp = 0;
    }

    private static float[] Softmax(float[] values, float temperature)
    {
        float max = values.Max() / temperature;
        var result = new float[values.Length];
        float sum = 0;
        for (int i = 0; i < values.Length; i++) { result[i] = MathF.Exp(values[i] / temperature - max); sum += result[i]; }
        for (int i = 0; i < result.Length; i++) result[i] /= Math.Max(sum, float.Epsilon);
        return result;
    }

    internal static float[] ReadEmotionLogits(Tensor<float> logits) =>
        [logits[0, 1, Happy], logits[0, 1, Sad], logits[0, 1, Angry], logits[0, 1, Fearful],
         logits[0, 1, Disgusted], logits[0, 1, Surprised], logits[0, 1, Neutral], logits[0, 1, Unknown]];

    internal static float[] ReadEventProbabilities(Tensor<float> logits) =>
        FullVocabularyTokenProbabilities(logits, 2, [LaughterToken, CryingToken]);

    private static float[] FullVocabularyTokenProbabilities(Tensor<float> logits, int frame, int[] tokens)
    {
        int vocab = logits.Dimensions[^1];
        float max = float.NegativeInfinity;
        for (int token = 0; token < vocab; token++) max = Math.Max(max, logits[0, frame, token]);
        double denominator = 0;
        for (int token = 0; token < vocab; token++) denominator += Math.Exp(logits[0, frame, token] - max);
        var result = new float[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            result[i] = tokens[i] < vocab ? (float)(Math.Exp(logits[0, frame, tokens[i]] - max) / denominator) : 0f;
        return result;
    }
    public void Dispose() => _session.Dispose();
}

internal static class OnnxNativeRuntime
{
    private static readonly object Gate = new();
    private static IntPtr _handle;

    public static void EnsureLoaded()
    {
        if (_handle != IntPtr.Zero) return;
        lock (Gate)
        {
            if (_handle != IntPtr.Zero) return;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCOSC", "runtime", "CrookedToe", "onnxruntime-1.22.0", "win-x64");
            Directory.CreateDirectory(directory);
            Extract("CrookedToe.Native.onnxruntime_providers_shared.dll", Path.Combine(directory, "onnxruntime_providers_shared.dll"));
            string runtimePath = Path.Combine(directory, "onnxruntime.dll");
            Extract("CrookedToe.Native.onnxruntime.dll", runtimePath);
            _handle = NativeLibrary.Load(runtimePath);
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (_, _, _) => _handle);
        }
    }

    private static void Extract(string resourceName, string destination)
    {
        using Stream source = typeof(OnnxNativeRuntime).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded native runtime '{resourceName}' is missing.");
        if (File.Exists(destination) && new FileInfo(destination).Length == source.Length) return;
        string temporary = destination + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(output);
        File.Move(temporary, destination, true);
    }
}

internal static class SenseVoiceFeatures
{
    private const int FftSize = 512, MelBins = 80, Stack = 7, Stride = 6;
    public static float[,] Extract(ReadOnlySpan<float> audio, float[] shift, float[] scale, bool dither = true)
    {
        const int frameLength = 400, frameStep = 160;
        int count = Math.Max(1, 1 + Math.Max(0, audio.Length - frameLength) / frameStep);
        var mel = new float[count, MelBins];
        var fft = new RealFft(FftSize);
        var input = new float[FftSize]; var real = new float[FftSize]; var imag = new float[FftSize];
        var random = new Random(0x53454E53); // deterministic dither keeps identical windows stable
        for (int frame = 0; frame < count; frame++)
        {
            Array.Clear(input);
            int start = frame * frameStep;
            float mean = 0;
            for (int i = 0; i < frameLength && start + i < audio.Length; i++)
            {
                input[i] = audio[start + i] * 32768f + (dither ? NextGaussian(random) : 0f); // official fbank input scale and dither=1
                mean += input[i];
            }
            mean /= frameLength;
            for (int i = 0; i < frameLength; i++) input[i] -= mean; // remove_dc_offset=true
            for (int i = frameLength - 1; i >= 1; i--) input[i] -= .97f * input[i - 1];
            input[0] -= .97f * input[0]; // preemphasis_coefficient=0.97
            for (int i = 0; i < frameLength; i++)
                input[i] *= .54f - .46f * MathF.Cos(2 * MathF.PI * i / (frameLength - 1));
            fft.Direct(input, real, imag);
            for (int m = 0; m < MelBins; m++)
            {
                float leftMel = MelValue(MelPoint(m)), centerMel = MelValue(MelPoint(m + 1)), rightMel = MelValue(MelPoint(m + 2));
                double power = 0;
                for (int b = 0; b <= FftSize / 2; b++)
                {
                    float melPosition = MelValue(b * 16_000f / FftSize);
                    float weight = melPosition < centerMel
                        ? (melPosition - leftMel) / (centerMel - leftMel)
                        : (rightMel - melPosition) / (rightMel - centerMel);
                    if (weight > 0) power += (real[b] * real[b] + imag[b] * imag[b]) * weight;
                }
                mel[frame, m] = MathF.Log(Math.Max((float)power, 1e-10f));
            }
        }
        int outputFrames = Math.Max(1, (count + Stride - 1) / Stride);
        var output = new float[outputFrames, MelBins * Stack];
        for (int t = 0; t < outputFrames; t++) for (int s = 0; s < Stack; s++) for (int m = 0; m < MelBins; m++)
        {
            int index = s * MelBins + m;
            // Official LFR prepends (m-1)/2 copies of the first frame.
            int sourceFrame = Math.Clamp(t * Stride + s - (Stack - 1) / 2, 0, count - 1);
            float value = mel[sourceFrame, m];
            output[t, index] = (value + shift[index]) * scale[index];
        }
        return output;
    }
    private static float NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble(), u2 = 1.0 - random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
    public static (float[] Shift, float[] Scale) LoadCmvn(string path)
    {
        string text = File.ReadAllText(path);
        MatchCollection arrays = Regex.Matches(text, @"\[([^\]]+)\]");
        if (arrays.Count < 3) throw new InvalidDataException("am.mvn has an unexpected format.");
        float[] Parse(string value) => Regex.Matches(value, @"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")
            .Select(x => float.Parse(x.Value, CultureInfo.InvariantCulture)).ToArray();
        float[] shift = Parse(arrays[1].Groups[1].Value), scale = Parse(arrays[2].Groups[1].Value);
        if (shift.Length != MelBins * Stack || scale.Length != MelBins * Stack) throw new InvalidDataException("am.mvn must contain 560 shift and scale values.");
        return (shift, scale);
    }
    private static float MelPoint(int index)
    {
        float low = 2595f * MathF.Log10(1 + 20f / 700f), high = 2595f * MathF.Log10(1 + 8000f / 700f);
        float mel = low + (high - low) * index / (MelBins + 1);
        return 700f * (MathF.Pow(10, mel / 2595f) - 1);
    }
    private static float MelValue(float hz) => 2595f * MathF.Log10(1 + hz / 700f);
}
