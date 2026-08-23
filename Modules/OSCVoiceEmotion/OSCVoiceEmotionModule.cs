using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using System.IO;
using VRCOSC.App.Settings;
using System.Diagnostics;
using CrookedToe.Modules.Compatibility;
using CrookedToe.Modules.Diagnostics;

namespace CrookedToe.Modules.OSCVoiceEmotion;

[ModuleTitle("OSC Voice Emotion")]
[ModuleDescription("Local SenseVoice vocal-expression and audio-event analysis for avatar animation")]
[ModuleType(ModuleType.Generic)]
public sealed class OSCVoiceEmotionModule : Module
{
    private const double SlowPublicationCommandMilliseconds = 50d;
    private static readonly long PublicationWarningCooldown = Stopwatch.Frequency * 5;
    private enum P { Happy, Sad, Angry, Fear, Surprise, Neutral, Laughter, Crying, Energy, Confidence, Speaking, Laughing, CryingActive }
    private enum S { InferenceInterval, MinimumSpeech, ModelThreads }
    private VoiceEmotionRuntime? _runtime;
    private readonly SemaphoreSlim _microphoneChangeGate = new(1, 1);
    private bool _stopping;
    private bool _microphoneSubscribed;
    private readonly HysteresisBoolean _laughing = new(), _crying = new();
    private ModuleDiagnostics? _diagnostics;
    private DiagnosticProbe? _captureProbe;
    private DiagnosticProbe? _inferenceProbe;
    private DiagnosticProbe? _outputProbe;
    private DiagnosticProbe? _publicationProbe;
    private long _nextPublicationTimestamp;
    private long _lastPublicationWarningTimestamp;
    private int _consecutivePublicationFailures;

    protected override void OnPreLoad()
    {
        RegisterParameter<float>(P.Happy, "voice_happy", ParameterMode.Write, "Happy", "Relative vocal-expression score");
        RegisterParameter<float>(P.Sad, "voice_sad", ParameterMode.Write, "Sad", "Relative vocal-expression score");
        RegisterParameter<float>(P.Angry, "voice_angry", ParameterMode.Write, "Angry", "Relative vocal-expression score");
        RegisterParameter<float>(P.Fear, "voice_fear", ParameterMode.Write, "Fear", "Relative vocal-expression score");
        RegisterParameter<float>(P.Surprise, "voice_surprise", ParameterMode.Write, "Surprise", "Relative vocal-expression score");
        RegisterParameter<float>(P.Neutral, "voice_neutral", ParameterMode.Write, "Neutral", "Relative vocal-expression score");
        RegisterParameter<float>(P.Laughter, "voice_laughter", ParameterMode.Write, "Laughter", "Detected laughter score");
        RegisterParameter<float>(P.Crying, "voice_crying", ParameterMode.Write, "Crying", "Detected crying score");
        RegisterParameter<float>(P.Energy, "voice_energy", ParameterMode.Write, "Expression Energy", "Loudness/expression intensity blend");
        RegisterParameter<float>(P.Confidence, "voice_confidence", ParameterMode.Write, "Confidence", "Operational stabilization confidence");
        RegisterParameter<bool>(P.Speaking, "voice_speaking", ParameterMode.Write, "Speaking", "Voice activity state");
        RegisterParameter<bool>(P.Laughing, "voice_laughing", ParameterMode.Write, "Laughing", "Hysteresis-filtered laughter state");
        RegisterParameter<bool>(P.CryingActive, "voice_crying_active", ParameterMode.Write, "Crying Active", "Hysteresis-filtered crying state");
        CreateSlider(S.InferenceInterval, "Inference interval", "Milliseconds between utterance-aware live inference", 400f, 300f, 1000f, 50f);
        CreateSlider(S.MinimumSpeech, "Preferred speech occupancy", "Quality weighting target for the utterance-aware inference window", .35f, .2f, .8f, .05f);
        CreateSlider(S.ModelThreads, "CPU threads", "SenseVoice CPU inference threads", 2f, 1f, 2f, 1f);
        CreateGroup("Inference", "Local CPU inference controls", S.InferenceInterval, S.MinimumSpeech, S.ModelThreads);
    }
    protected override void OnPostLoad() { }

    protected override async Task<bool> OnModuleStart()
    {
        StartDiagnostics();
        try
        {
            _stopping = false;
            string selectedMicrophoneId = SettingsManager.GetInstance().GetValue<string>(VRCOSCSetting.SelectedMicrophoneID);
            StartRuntime(selectedMicrophoneId);
            SettingsManager.GetInstance().GetObservable<string>(VRCOSCSetting.SelectedMicrophoneID)
                .Subscribe(OnSelectedMicrophoneChanged);
            _microphoneSubscribed = true;
            Log($"OSC Voice Emotion started using VRCOSC microphone '{_runtime!.MicrophoneName}'. Audio remains local and in memory; this estimates vocal expression, not true emotional or medical state.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"OSC Voice Emotion could not start: {ex.Message}");
            await DisposeRuntimeAsync();
            StopDiagnostics();
            return false;
        }
    }

    protected override async Task OnModuleStop()
    {
        _stopping = true;
        if (_microphoneSubscribed)
        {
            SettingsManager.GetInstance().GetObservable<string>(VRCOSCSetting.SelectedMicrophoneID)
                .Unsubscribe(OnSelectedMicrophoneChanged);
            _microphoneSubscribed = false;
        }
        await _microphoneChangeGate.WaitAsync();
        try { await DisposeRuntimeAsync(); }
        finally { _microphoneChangeGate.Release(); }
        _laughing.Reset();
        _crying.Reset();
        _nextPublicationTimestamp = 0;
        Publish(EmotionState.NeutralState);
        StopDiagnostics();
    }

    private void StartRuntime(string selectedMicrophoneId)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCOSC", "models", "SenseVoiceSmall");
        string model = new[] { "model_quant.onnx", "model.int8.onnx", "model.onnx" }.Select(x => Path.Combine(directory, x)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Place an official SenseVoiceSmall INT8 ONNX export in {directory}");
        var options = new VoiceEmotionOptions { InferenceInterval = TimeSpan.FromMilliseconds(GetSettingValue<float>(S.InferenceInterval)),
            MinimumSpeechOccupancy = GetSettingValue<float>(S.MinimumSpeech) };
        _runtime = new VoiceEmotionRuntime(
            new SenseVoiceOnnxBackend(model, (int)GetSettingValue<float>(S.ModelThreads)),
            options,
            selectedMicrophoneId,
            _captureProbe,
            _inferenceProbe,
            _outputProbe);
        _runtime.StateChanged += Publish;
        _runtime.Warning += OnRuntimeWarning;
        _runtime.Start();
    }

    private void OnSelectedMicrophoneChanged(string selectedMicrophoneId) => _ = SwitchMicrophoneAsync(selectedMicrophoneId);

    private async Task SwitchMicrophoneAsync(string selectedMicrophoneId)
    {
        await _microphoneChangeGate.WaitAsync();
        try
        {
            if (_stopping) return;
            await DisposeRuntimeAsync();
            StartRuntime(selectedMicrophoneId);
            Log($"Switched to VRCOSC microphone '{_runtime!.MicrophoneName}'");
        }
        catch (Exception ex) { Log($"Could not switch VRCOSC microphone: {ex.Message}"); }
        finally { _microphoneChangeGate.Release(); }
    }

    private async Task DisposeRuntimeAsync()
    {
        if (_runtime == null) return;
        _runtime.StateChanged -= Publish;
        _runtime.Warning -= OnRuntimeWarning;
        await _runtime.DisposeAsync();
        _runtime = null;
    }
    private void OnRuntimeWarning(string message) => Log(message);
    private void Publish(EmotionState s)
    {
        using DiagnosticScope measurement = _publicationProbe?.Measure() ?? default;
        VrcOscUiDispatchWorkaround.ApplyIfDue();
        long now = Stopwatch.GetTimestamp();
        if (now < _nextPublicationTimestamp)
            return;

        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        bool laughing = _laughing.Update(s.Laughter, utcNow);
        bool crying = _crying.Update(s.Crying, utcNow);
        try
        {
            if (!TrySend(P.Happy, ThreeSignificantFigures(s.Happy), out double slowMilliseconds) ||
                !TrySend(P.Sad, ThreeSignificantFigures(s.Sad), out slowMilliseconds) ||
                !TrySend(P.Angry, ThreeSignificantFigures(s.Angry), out slowMilliseconds) ||
                !TrySend(P.Fear, ThreeSignificantFigures(s.Fear), out slowMilliseconds) ||
                !TrySend(P.Surprise, ThreeSignificantFigures(s.Surprise), out slowMilliseconds) ||
                !TrySend(P.Neutral, ThreeSignificantFigures(s.Neutral), out slowMilliseconds) ||
                !TrySend(P.Laughter, ThreeSignificantFigures(s.Laughter), out slowMilliseconds) ||
                !TrySend(P.Crying, ThreeSignificantFigures(s.Crying), out slowMilliseconds) ||
                !TrySend(P.Energy, ThreeSignificantFigures(s.Energy), out slowMilliseconds) ||
                !TrySend(P.Confidence, ThreeSignificantFigures(s.Confidence), out slowMilliseconds) ||
                !TrySend(P.Speaking, s.Speaking, out slowMilliseconds) ||
                !TrySend(P.Laughing, laughing, out slowMilliseconds) ||
                !TrySend(P.CryingActive, crying, out slowMilliseconds))
            {
                RegisterPublicationFailure(Stopwatch.GetTimestamp(), null, slowMilliseconds);
                return;
            }

            if (_consecutivePublicationFailures > 0)
                Log("OSC Voice Emotion publication recovered");
            _consecutivePublicationFailures = 0;
            _nextPublicationTimestamp = 0;
        }
        catch (Exception ex)
        {
            RegisterPublicationFailure(Stopwatch.GetTimestamp(), ex, 0d);
        }
    }

    private bool TrySend(P parameter, object value, out double slowMilliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        SendParameter(parameter, value);
        slowMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return slowMilliseconds < SlowPublicationCommandMilliseconds;
    }

    private void RegisterPublicationFailure(long now, Exception? exception, double slowMilliseconds)
    {
        _consecutivePublicationFailures++;
        int exponent = Math.Min(_consecutivePublicationFailures - 1, 5);
        double delaySeconds = Math.Min(2d, 0.1d * (1 << exponent));
        _nextPublicationTimestamp = now + (long)(delaySeconds * Stopwatch.Frequency);
        if (_consecutivePublicationFailures != 1 && now - _lastPublicationWarningTimestamp < PublicationWarningCooldown)
            return;

        _lastPublicationWarningTimestamp = now;
        bool slow = slowMilliseconds >= SlowPublicationCommandMilliseconds;
        string detail = slow ? $"an individual send took {slowMilliseconds:F0}ms" : exception?.Message ?? "unknown transport failure";
        Log($"OSC Voice Emotion publication paused after {detail}; remaining sends in the batch were skipped.");
        _diagnostics?.Event(
            slow ? "parameter_publication_slow" : "parameter_publication_failure",
            $"consecutive={_consecutivePublicationFailures};durationMs={slowMilliseconds:F1};exception={exception?.GetType().Name}:{exception?.Message}");
    }

    internal static float ThreeSignificantFigures(float value)
    {
        if (!float.IsFinite(value)) return 0f;
        value = Math.Clamp(value, 0f, 1f);
        if (value == 0f) return 0f;
        int magnitude = (int)MathF.Floor(MathF.Log10(MathF.Abs(value)));
        float scale = MathF.Pow(10f, 2 - magnitude);
        return MathF.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    private void StartDiagnostics()
    {
        StopDiagnostics();
        _nextPublicationTimestamp = 0;
        _lastPublicationWarningTimestamp = 0;
        _consecutivePublicationFailures = 0;
        _diagnostics = BoundedDiagnostics.StartModule("OSCVoiceEmotion");
        _captureProbe = _diagnostics.CreateProbe("microphone_callback");
        _inferenceProbe = _diagnostics.CreateProbe("onnx_inference");
        _outputProbe = _diagnostics.CreateProbe("runtime_output", 100);
        _publicationProbe = _diagnostics.CreateProbe("parameter_publication", 100);
        int patchedObservers = VrcOscUiDispatchWorkaround.ApplyIfDue(force: true);
        if (patchedObservers > 0)
            _diagnostics.Event("vrcosc_dispatch_workaround", $"patchedObservers={patchedObservers}");
    }

    private void StopDiagnostics()
    {
        _diagnostics?.Dispose();
        _diagnostics = null;
        _captureProbe = null;
        _inferenceProbe = null;
        _outputProbe = null;
        _publicationProbe = null;
    }
}
