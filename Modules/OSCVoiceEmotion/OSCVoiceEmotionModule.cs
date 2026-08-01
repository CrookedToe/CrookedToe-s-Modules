using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Parameters;
using System.IO;
using VRCOSC.App.Settings;

namespace CrookedToe.Modules.OSCVoiceEmotion;

[ModuleTitle("OSC Voice Emotion")]
[ModuleDescription("Local SenseVoice vocal-expression and audio-event analysis for avatar animation")]
[ModuleType(ModuleType.Generic)]
public sealed class OSCVoiceEmotionModule : Module
{
    private enum P { Happy, Sad, Angry, Fear, Surprise, Neutral, Laughter, Crying, Energy, Confidence, Speaking, Laughing, CryingActive }
    private enum S { InferenceInterval, MinimumSpeech, ModelThreads }
    private VoiceEmotionRuntime? _runtime;
    private readonly SemaphoreSlim _microphoneChangeGate = new(1, 1);
    private bool _stopping;
    private bool _microphoneSubscribed;
    private readonly HysteresisBoolean _laughing = new(), _crying = new();

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
        Publish(EmotionState.NeutralState);
    }

    private void StartRuntime(string selectedMicrophoneId)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCOSC", "models", "SenseVoiceSmall");
        string model = new[] { "model_quant.onnx", "model.int8.onnx", "model.onnx" }.Select(x => Path.Combine(directory, x)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Place an official SenseVoiceSmall INT8 ONNX export in {directory}");
        var options = new VoiceEmotionOptions { InferenceInterval = TimeSpan.FromMilliseconds(GetSettingValue<float>(S.InferenceInterval)),
            MinimumSpeechOccupancy = GetSettingValue<float>(S.MinimumSpeech) };
        _runtime = new VoiceEmotionRuntime(new SenseVoiceOnnxBackend(model, (int)GetSettingValue<float>(S.ModelThreads)), options, selectedMicrophoneId);
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
        SendParameter(P.Happy, ThreeSignificantFigures(s.Happy));
        SendParameter(P.Sad, ThreeSignificantFigures(s.Sad));
        SendParameter(P.Angry, ThreeSignificantFigures(s.Angry));
        SendParameter(P.Fear, ThreeSignificantFigures(s.Fear));
        SendParameter(P.Surprise, ThreeSignificantFigures(s.Surprise));
        SendParameter(P.Neutral, ThreeSignificantFigures(s.Neutral));
        SendParameter(P.Laughter, ThreeSignificantFigures(s.Laughter));
        SendParameter(P.Crying, ThreeSignificantFigures(s.Crying));
        SendParameter(P.Energy, ThreeSignificantFigures(s.Energy));
        SendParameter(P.Confidence, ThreeSignificantFigures(s.Confidence));
        SendParameter(P.Speaking, s.Speaking);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SendParameter(P.Laughing, _laughing.Update(s.Laughter, now)); SendParameter(P.CryingActive, _crying.Update(s.Crying, now));
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
}
