# Strippr

![Strippr logo](wwwroot/img/logo.png)

Local-first video cleanup for silence, pauses, and retakes.

Strippr is an ASP.NET Core 9 Razor Pages app for cleaning spoken-video timelines on your own machine. The main editor is local-first: you load a clip, inspect the waveform, tune automatic silence detection, add manual cuts, preview the result, and render locally with FFmpeg.

Strippr also includes an `AI (experimental)` workspace tab. Those AI tools are optional and are not fully local: when you use them, Strippr can send derived media and transcript data to OpenAI using your API key.

If Strippr saves you time, consider supporting development.

## Download for Windows

End users should download a release build instead of cloning the repo.

- Latest Windows build: [GitHub Releases](https://github.com/daniel-medin/Strippr/releases/latest)
- Release zip includes the .NET runtime and bundled FFmpeg
- Download, unzip, and run `Strippr.exe`
- The standalone app opens in your default browser at `http://127.0.0.1:5123`

## What It Does

- Upload a local video file
- Preview the audio waveform in the browser
- Detect automatic cuts with `FFmpeg`, `Silero VAD`, or `Hybrid`
- Adjust green automatic cut regions directly in the waveform
- Add manual in/out cuts on top of the automatic pass
- Compress pauses instead of only hard-cutting them
- Render the cleaned result locally with FFmpeg
- Optionally try experimental AI helpers for pauses, retakes, and script-aware cutting

## Editor vs AI

### Core editor

The `Editor` tab is the main product surface.

- local waveform preview
- local render pipeline
- auto silence selector: `FFmpeg`, `Silero VAD`, `Hybrid`
- manual cut markers
- adjustable automatic cut handles
- pause compression

### AI (experimental)

The `AI (experimental)` tab is intentionally separate because it is still exploratory.

Current experimental tools:

- `Pauses`: transcript-gap finder
- `Retakes`: content-aware bad-take suggestions
- `A3`: auto-cut-to-text prototype

Important:

- review every AI cut manually
- AI can miss retakes
- AI can overcut good lines
- AI can label a cut incorrectly
- AI behavior will change as the project evolves

## Privacy and Network Behavior

Strippr is local-first, but not every feature is fully offline.

- Core editing and rendering stay on your machine
- FFmpeg processing is local
- Silero VAD is local
- The waveform preview is browser-side local audio analysis
- Experimental AI features require internet access and an OpenAI API key
- When AI features are used, Strippr can send extracted audio and transcript/context data to OpenAI

If you want a fully local workflow, stay in the `Editor` tab and leave the AI tools off.

## Stack

- .NET 9
- ASP.NET Core Razor Pages
- C#
- FFmpeg
- Browser-side Web Audio API for waveform preview
- Optional Silero VAD via ONNX Runtime
- Optional OpenAI API integration for experimental AI tools

## Run Locally

Requirements:

- .NET SDK 9.0+
- Windows
- Internet access on first run if `tools/ffmpeg/package` is not already available locally

Optional:

- internet access plus an OpenAI API key for the `AI (experimental)` tab
- the Silero model file if you want to use `Silero VAD` or `Hybrid`

From the repo root:

```powershell
dotnet run
```

Then open the local URL printed by ASP.NET Core, or use:

```text
http://127.0.0.1:5123
```

## Build a Windows Release

From the repo root:

```powershell
.\scripts\publish-release.ps1 -Version 1.0.0
```

That writes these files to `artifacts/`:

- `Strippr-v1.0.0-win-x64/`
- `Strippr-v1.0.0-win-x64.zip`
- `Strippr-v1.0.0-win-x64.sha256.txt`

The release script publishes a self-contained Windows build, includes the bundled FFmpeg runtime, removes debug-only extras, and emits a SHA-256 checksum alongside the zip.

## FFmpeg Handling

Strippr bootstraps a pinned FFmpeg build automatically on startup if a local bundled copy is not already available.

Current behavior:

- preferred local bundle path: `tools/ffmpeg`
- fallback: machine-installed FFmpeg if available
- current pinned download: Gyan FFmpeg 8.0.1 essentials build
- release builds include the bundled local FFmpeg runtime when `tools/ffmpeg/package` is available

Important GitHub note:

- the pinned FFmpeg archive is roughly 101 MB
- GitHub's normal per-file limit is 100 MB
- that means the archive should either be:
  - downloaded on first run, which Strippr already supports, or
  - stored with Git LFS if you want it versioned inside the repo

The extracted FFmpeg runtime is intentionally ignored by git.

## Auto Silence Modes

Strippr now exposes the automatic silence analyzer directly in the editor.

### `FFmpeg`

- uses FFmpeg `silencedetect`
- threshold-driven
- fastest and simplest baseline

### `Silero VAD`

- uses a local speech / non-speech model
- model-driven instead of pure dB threshold logic
- better for messy voice recordings, breaths, and room tone changes

### `Hybrid`

- keeps only gaps both FFmpeg and Silero agree on
- safer and usually more conservative

## Optional Silero VAD

Strippr has an optional Silero VAD path for better local speech / non-speech boundaries than raw FFmpeg `silencedetect`.

Setup:

```powershell
.\scripts\download-silero-vad.ps1
```

Recommended local config in `appsettings.Local.json`:

```json
{
  "Strippr": {
    "SileroVadEnabled": true,
    "AutomaticSilenceAnalyzer": "hybrid"
  }
}
```

Supported analyzer values:

- `Strippr:AutomaticSilenceAnalyzer = ffmpeg`
- `Strippr:AutomaticSilenceAnalyzer = silero`
- `Strippr:AutomaticSilenceAnalyzer = hybrid`

Required toggle:

- `Strippr:SileroVadEnabled = true`

Current status/debug endpoints:

- `GET /api/silero/status`
- `POST /api/silero/compare`

Notes:

- `ffmpeg` remains the safest baseline
- `hybrid` is the safer Silero-enabled mode
- the Silero model file is ignored by git
- the Silero model is not currently bundled into release builds

## Experimental AI Setup

The experimental AI tools live in the `AI (experimental)` tab.

You can either paste an API key into the UI or store a local default in `appsettings.Local.json`.

Template:

```json
{
  "Strippr": {
    "OpenAiApiKey": "sk-...",
    "DefaultOpenAiModel": "whisper-1"
  }
}
```

Notes:

- `appsettings.Local.json` is ignored by git
- `appsettings.Local.example.json` is only a template
- the AI tools are experimental and should not be treated as deterministic

## Output Location

Strippr renders files into internal app storage and then exposes them through the browser download flow.

Use:

- `Save As...` to choose the destination yourself in supported Chromium browsers
- `Download cleaned video` to use your browser's normal download behavior

## Current Status

This is still an MVP, but it is no longer only a silence-slider prototype.

Included:

- waveform preview
- automatic silence modes: `FFmpeg`, `Silero VAD`, `Hybrid`
- manual cut markers
- adjustable automatic cut handles
- pause compression
- local output handling
- experimental AI tab with pause, retake, and A3 tools

Still evolving:

- AI quality and recall
- retake classification
- script-aware cut planning
- evaluation / training workflow
- desktop-wrapper packaging beyond the browser launch model

## Repo Structure

```text
Pages/            Razor Pages UI
Services/         FFmpeg, Silero, AI, processing, storage, bootstrap logic
Models/           Processing and AI models
Options/          App configuration
scripts/          Publish and helper scripts
tools/ffmpeg      FFmpeg bootstrap location
tools/silero      Optional Silero model location
wwwroot/          CSS and browser-side JS
```

## Licensing

Strippr is source-available, not open source.

The Strippr application code is licensed under the custom proprietary personal-use terms in [LICENSE](LICENSE).

What that means in practice:

- personal, non-commercial, and internal evaluation use is allowed
- redistribution, resale, and commercial use are not allowed without written permission
- ownership of Strippr remains with Daniel Medin

Bundled FFmpeg, ONNX Runtime, Silero model files, and optional OpenAI API usage remain under their own separate terms and are not relicensed under the Strippr license.

Important GitHub note:

- if a repository is public on GitHub, other users can still view and fork it under GitHub's platform rules
- if you want stronger practical control over access to the source, keep the repository private

## License

Custom proprietary personal-use license. See [LICENSE](LICENSE).

Third-party licensing notes are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
