# Silero VAD Plan

This document is now mostly historical. The rollout described below has been implemented, and Strippr currently exposes Silero through the `Auto silence` selector in the `Editor` tab.

Current shipped modes:

- `FFmpeg`
- `Silero VAD`
- `Hybrid`

Current UI behavior:

- the waveform preview follows the selected analyzer
- `Play result` uses the same automatic cut ranges shown in the waveform
- users can manually tighten green automatic cut regions with waveform handles
- AI features remain separate in the `AI (experimental)` tab

## Goal

Add `Silero VAD` to Strippr as a better local speech / non-speech detector than raw FFmpeg `silencedetect`, without breaking the current workflow.

The target outcome is:

- better pause boundaries
- better handling of breaths and throat-noise gaps
- cleaner input for A1 / A3
- a safe fallback to the current FFmpeg-only pipeline

## Constraints

- keep everything local
- do not remove the FFmpeg path
- do not require Python at runtime
- prefer a C# + ONNX Runtime integration
- keep the feature disabled by default until it is verified

## Current Baseline

Today Strippr uses:

- FFmpeg `silencedetect` for automatic pause detection
- Whisper/OpenAI for transcript-based AI layers
- waveform preview driven by browser audio analysis

This means the weakest current boundary logic is still the first speech / silence split.

## Why Silero

Silero VAD is useful here because it is good at:

- speech vs non-speech segmentation
- tighter speech boundaries than fixed dB thresholds
- reducing false silence detections caused by room tone differences

It will not solve:

- retake quality
- wrong-line detection
- content-aware take selection

So the right use is:

- improve the base speech map
- feed that into existing cut planning

## Rollout Phases

### Phase 1: Scaffolding

Add:

- `ONNX Runtime` package reference
- Strippr config for Silero
- `SileroVadService`
- model-path resolution
- status endpoint / health reporting

Definition of done:

- the app builds
- Silero is disabled by default
- the app can report whether the model file exists and whether the runtime is wired in

### Phase 2: Audio Prep

Add:

- FFmpeg export for `16 kHz mono PCM WAV`
- a safe temp-file path for VAD input
- future-facing helper methods for reading sample windows

Definition of done:

- a local clip can be converted into the correct Silero input format

### Phase 3: Inference Spike

Implement:

- load the ONNX model
- run VAD over audio windows
- emit per-window speech probabilities
- convert probabilities into speech ranges

Definition of done:

- a sample clip produces speech segments from Silero without touching the main processing path

### Phase 4: Post-Processing

Implement:

- merge nearby speech windows
- derive non-speech gaps
- minimum speech / minimum gap guardrails
- confidence-aware cleanup of tiny junk regions

Definition of done:

- Silero output becomes stable enough to compare against FFmpeg silence intervals

### Phase 5: Comparison Mode

Add:

- a developer comparison path: `ffmpeg`, `silero`, `hybrid`
- status/debug output showing interval differences
- optional waveform overlay for Silero speech regions

Definition of done:

- the same clip can be analyzed with both detectors and compared directly

### Phase 6: Production Integration

Use Silero for:

- A1 silence candidates
- safer pause compression boundaries
- cleaner A3 gap-only cleanup

Keep FFmpeg as:

- fallback
- low-complexity mode
- release-safe default until Silero proves better

Definition of done:

- Strippr can switch analyzers without breaking rendering

## Integration Strategy

### Analyzer Modes

Planned modes:

- `ffmpeg`
- `silero`
- `hybrid`

`hybrid` should probably become the best end-state:

- Silero decides speech / non-speech
- FFmpeg still handles rendering
- existing cut planner still builds keep/cut segments

### Safe First Rule

Do not let Silero replace FFmpeg immediately.

First:

- build it beside the current path
- compare interval quality
- only then let it drive cuts

## Technical Notes

### Runtime

Preferred runtime path:

- `Microsoft.ML.OnnxRuntime`
- local `.onnx` model file in `tools/silero/`

### Model File Convention

Initial expected path:

- `tools/silero/silero_vad.onnx`

This should stay outside publish output until the integration is proven.

### Audio Format

Initial export target:

- mono
- `16 kHz`
- PCM WAV

## Risks

### Risk: Model Packaging

The ONNX model should not be treated like a required production asset yet.

Mitigation:

- keep it optional
- surface clear status if missing

### Risk: Over-Cutting

Better VAD boundaries can still be too aggressive if post-processing is wrong.

Mitigation:

- preserve FFmpeg fallback
- use guardrails on minimum speech / minimum gap

### Risk: Windows Runtime Friction

ONNX Runtime adds native dependencies.

Mitigation:

- verify local build first
- keep the code path disabled until status and inference are stable

## Immediate Next Steps

1. Add 16 kHz WAV extraction for VAD.
2. Load the ONNX model in a non-rendering spike path.
3. Compare Silero speech intervals against FFmpeg silence intervals on real clips.
4. Only after comparison, let Silero drive A1 or A3 cleanup.

## Current Status

- [x] Phase 1 scaffolding
- [x] Phase 2 audio prep
- [x] Phase 3 inference spike
- [x] Phase 4 post-processing
- [x] Phase 5 comparison mode
- [x] Phase 6 production integration

Open follow-ups:

- tune `Silero` vs `Hybrid` defaults on more real clips
- decide whether `Hybrid` should become the default analyzer later
- keep comparing analyzer quality separately from the experimental AI layers
