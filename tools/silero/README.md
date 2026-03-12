Place the Silero ONNX model here when testing or using local VAD in Strippr.

Expected default file name:

- `silero_vad.onnx`

Download helper:

- `.\scripts\download-silero-vad.ps1`

What Strippr uses it for:

- `Silero VAD` analyzer mode
- `Hybrid` analyzer mode together with FFmpeg `silencedetect`
- local speech / non-speech boundary detection for automatic cuts

What it does not do by itself:

- retake detection
- script-aware cutting
- A2 or A3 reasoning

Current app integration:

- status endpoint: `GET /api/silero/status`
- comparison endpoint: `POST /api/silero/compare`
- editor dropdown: `FFmpeg`, `Silero VAD`, `Hybrid`

Notes:

- the model file is ignored by git
- the model is optional
- the model is not currently bundled into release builds
- if the model is missing, `Silero VAD` cannot run and Strippr falls back to FFmpeg behavior where appropriate
