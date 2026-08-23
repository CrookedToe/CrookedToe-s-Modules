# OSC Voice Emotion

Local, real-time vocal-expression analysis for VRCOSC. The module captures VRCOSC's selected microphone, keeps audio only in a bounded in-memory buffer, and runs SenseVoiceSmall through CPU ONNX Runtime. It does not transcribe, upload, or save audio.

> This software estimates vocal expression for animation and interaction. It does not determine a person's true emotional or medical state.

## Model setup

Export or download the official **SenseVoiceSmall INT8 ONNX** model and place these files together:

```text
%APPDATA%\VRCOSC\models\SenseVoiceSmall\
  model_quant.onnx   (model.int8.onnx or model.onnx are also recognized)
  am.mvn
```

The model must use the official FunASR inputs (`speech`, `speech_lengths`, `language`, and `textnorm`) and `ctc_logits` output. The model is deliberately not downloaded automatically: after installation and model setup, operation is offline.

The module follows VRCOSC's native microphone selection under **Settings → Speech**. If VRCOSC is set to Default, the current Windows default capture device is used. Changing the VRCOSC microphone while the module is running automatically switches Voice Emotion to the same device.

The model contract, root-cause findings, fixes, and remaining limitations are documented in [AUDIT.md](AUDIT.md).

## VRChat parameters

| Parameter | Type | Meaning |
| --- | --- | --- |
| `voice_happy` | Float | Relative happy-expression score |
| `voice_sad` | Float | Relative sad-expression score |
| `voice_angry` | Float | Relative angry-expression score |
| `voice_fear` | Float | Relative fearful-expression score |
| `voice_surprise` | Float | Relative surprised-expression score |
| `voice_neutral` | Float | Relative neutral-expression score |
| `voice_laughter` | Float | Laughter event score |
| `voice_crying` | Float | Cry event score |
| `voice_energy` | Float | Loudness and non-neutral expression blend |
| `voice_confidence` | Float | Operational stabilization confidence |
| `voice_speaking` | Bool | Voice activity state |
| `voice_laughing` | Bool | Sustained laughter state |
| `voice_crying_active` | Bool | Sustained crying state |

Scores are model-relative confidence values, not verified probabilities of internal emotion. Boolean event outputs use 0.65/0.40 hysteresis with activation and deactivation holds.

## Runtime behavior

- Input: native-rate shared-mode WASAPI from VRCOSC's selected microphone; complete inference windows are downmixed and resampled to 16 kHz mono at quality 60
- Buffer: bounded eight seconds with 180 ms pre-speech retention
- Inference: preliminary response after 0.9 seconds, 2.5-second live context every 400 ms, and a final utterance correction capped at six seconds
- VAD: adaptive energy detector with start/stop hysteresis
- Pure laughter/crying: model-event activity latch keeps vocal state and inference alive when speech VAD drops
- Output: confidence-weighted attack/release smoothing at 10 Hz
- Output transport: the complete state remains redundantly published at 10 Hz while healthy; a send taking at least 50 ms stops that batch and enters a bounded 100 ms to 2 second retry backoff
- VRCOSC compatibility: non-ChatBox output is filtered before ChatBox-preview UI dispatch so expression publication cannot synchronously wait on that view
- Silence: 350 ms utterance endpoint, 500 ms expression hold, 1.5-second fade, 3.5-second reset
- Privacy: no network, telemetry, transcription, or persistent audio storage

The inference interval, minimum speech occupancy, and CPU thread count are configurable in VRCOSC.
