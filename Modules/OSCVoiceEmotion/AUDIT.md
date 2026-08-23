# OSC Voice Emotion deep audit and root-cause analysis

Audit date: 2026-07-12

## Verified SenseVoice contract

- ONNX inputs: `speech` (`float32`, `[batch,time,560]`), `speech_lengths` (`int32`), `language` (`int32`), and `textnorm` (`int32`).
- English language query value: `4`; without-ITN query value: `15`.
- Rich output frames: `0=language`, `1=SER emotion`, `2=AED event`, `3=text normalization`.
- SER normalization uses exactly eight tokens: `25001=HAPPY`, `25002=SAD`, `25003=ANGRY`, `25004=NEUTRAL`, `25005=FEARFUL`, `25006=DISGUSTED`, `25007=SURPRISED`, and `25009=EMO_UNKNOWN`. `25008=OTHER` is not an SER training label and is excluded.
- Event tokens used publicly: `24997=Laughter`, `25010=Cry`.
- Frontend: 16 kHz, 25 ms Hamming frames, 10 ms shift, 80 mel bins, LFR `m=7/n=6`, then the supplied 560-value CMVN transform.

The frontend has a deterministic regression test against `kaldi-native-fbank`, and the rich-frame decoder has a synthetic-logit regression test that prevents SER/AED frame reversal.

## Root causes found and corrected

1. **SER and AED frames had previously been reversed.** Emotion now reads only frame 1 and events only frame 2.
2. **Event confidence was conditionally normalized over a hand-picked event subset.** This inflated weak `Cry` evidence. Laughter and Cry now use full-vocabulary softmax confidence at the AED frame.
3. **The custom frontend used the wrong upper mel boundary.** The official default is Nyquist (8 kHz), not 7.6 kHz.
4. **Microphone callbacks were independently downsampled with linear interpolation.** That introduced callback discontinuities and aliasing. Native-rate mono audio is now buffered, then each complete inference window is resampled with Media Foundation quality 60, matching VRCOSC's own speech path.
5. **32-bit extensible PCM could be decoded as float.** Extensible audio now checks its sub-format GUID before choosing float versus integer decoding.
6. **VAD endpoint delay was applied twice.** `lastSpeech` now tracks actual voiced frames, not the hysteresis-held speaking state.
7. **Held silence was counted as voiced duration.** Voiced duration now increments only for frames with VAD probability at least 0.5.
8. **Final inference quality used the latest callback, which was normally silence.** RMS, clipping, occupancy, and VAD quality now come from the exact inference window.
9. **Silence decay compounded on every 10 Hz output tick.** Decay is now calculated from a captured silence-start state, producing the configured linear fade.
10. **Evidence leaked between utterances.** Smoothed logits reset when a genuinely new utterance begins.
11. **`OTHER` was incorrectly included as a ninth emotion class.** It is not in the official SER target set and diluted all real emotion scores. It is now excluded from the eight-class emotion softmax.
12. **Speculative calibration obscured model output.** Unvalidated temperature scaling, class floors, sparse top-two suppression, hidden dominant-label state, and derived-neutral remapping were removed. Public emotion floats are now confidence-adapted temporal envelopes of the decoded model scores.
13. **Worker failures could be silent.** Capture, inference, and OSC publication failures are caught and rate-limited into the VRCOSC module log; a transient final-inference failure is retried.
14. **Boolean event state survived module restart.** Hysteresis state is explicitly reset during module stop.
15. **Old event windows could perpetually renew vocal activity.** Event latches now require recent acoustic activity as well as model confidence.

## Intentional behavior

- Emotion probabilities are a softmax over the eight official emotion/uncertainty tokens because the model's SER head is a single competing utterance label.
- `EMO_UNKNOWN` is retained as the uncertainty penalty; `OTHER` is excluded because it is not an official SER target.
- Event probabilities use the full vocabulary because AED is trained through the same vocabulary-wide cross-entropy head.
- Disgust remains internal, as allowed by the MVP specification.
- Emotion logits are smoothed only within an utterance. Animation envelopes provide the second, independent temporal layer.
- Pure laughter or crying can hold `voice_speaking` as a broader “vocalizing” state even when speech VAD drops.

## Remaining limitations

- The energy VAD is intentionally lightweight and is not a second machine-learning model. Very quiet nonverbal vocalizations can still fail to open an utterance.
- SenseVoice produces one SER label per supplied segment; it is not a frame-level or multi-label emotion detector.
- No class-specific calibration is applied. Proper temperature or bias calibration requires a labeled recording set from the intended microphones and social-VR environment.
- Float outputs represent model-relative vocal-expression confidence, not verified internal emotion.
