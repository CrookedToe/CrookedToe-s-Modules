# OSC Audio Reaction

Captures your system audio output and exposes volume, stereo direction, spike detection, and seven frequency-band values as VRCOSC parameters.

Based on [VRC-OSC-Audio-Reaction](https://github.com/Codel1417/VRC-OSC-Audio-Reaction) by Codel1417.

## Features

- Stereo direction output from `0` to `1`
- Smoothed overall volume output from `0` to `1`
- Habituation-based spike detection for sudden loud changes
- Seven independently toggleable frequency bands
- Optional frequency scaling by overall volume
- Automatic gain control with manual gain fallback

## Parameters

- `audio_direction`: `0 = left`, `0.5 = center`, `1 = right`
- `audio_volume`: overall loudness
- `audio_spike`: `true` while a spike is active
- `audio_subbass`: `20-60Hz`
- `audio_bass`: `60-250Hz`
- `audio_lowmid`: `250-500Hz`
- `audio_mid`: `500-2000Hz`
- `audio_uppermid`: `2000-4000Hz`
- `audio_presence`: `4000-6000Hz`
- `audio_brilliance`: `6000-20000Hz`

Disabled bands always output `0`.

## Settings

- `Audio Gain`: manual gain from `0.1` to `5.0`
- `Automatic Gain Control`: keeps volume response more consistent
- `Volume Smoothing`: smooths overall volume changes
- `Direction Threshold`: minimum loudness required before direction is evaluated
- `Spike Sensitivity`: lower values detect smaller jumps
- `Spike Hold Duration`: keeps the spike output active briefly
- `Scale Frequencies with Volume`: multiplies band output by overall volume
- `Frequency Smoothing`: smooths band movement
- Band toggles: enable only the ranges you need
- `Enable Directional Pause`: slows direction settling when far from center
- `Directional Pause Factor`: controls how strong that slowdown is
- Habituation settings: tune how quickly repeated spikes are ignored and later recovered

## Technical Notes

- Uses `WasapiLoopbackCapture` for system output capture
- Uses the active output device sample rate
- Supports 32-bit float loopback input with one or more channels; analysis uses the first stereo pair
- Uses a fixed FFT size of `8192`
- Uses a `Blackman` window before FFT analysis
- Direction is weighted by the enabled bands only
- Frequency outputs are normalized per enabled-band power unless volume scaling is enabled
- The audio callback only copies into a bounded latest-frame buffer. Processing and all ten parameter sends run from one 50 ms module update.
- Every update republishes the complete output state so a missed OSC packet is naturally repaired without a second resend loop.
- A rejected publication stops that update's shared send batch and retries with a bounded 100 ms to 2 second backoff; successful publication immediately restores the normal 50 ms rate.
- If capture stops or the default output device changes, outputs immediately return to neutral and capture retries with bounded backoff.
- A low-rate debug health line reports callback, processing, coalescing, publication, and recovery state without per-frame logging.

## Installation

1. Enable the module in VRCOSC.
2. Add the parameters you want to your avatar.
3. Adjust smoothing, gain, and band toggles to match your use case.

## Troubleshooting

- If no audio is detected, confirm your default output device is active and producing sound.
- If the default output device was replaced or restarted, allow the module's automatic capture recovery to reconnect.
- If direction feels unstable, raise `Direction Threshold` or increase smoothing.
- If volume is too low or too high, enable AGC or adjust `Audio Gain`.
- If spikes trigger too often, raise `Spike Sensitivity` or tune the habituation settings.
