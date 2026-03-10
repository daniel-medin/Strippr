# Strippr

![Strippr logo](wwwroot/img/logo.png)

Local-first video cleanup for dead air and silence.

Strippr is an ASP.NET Core 9 Razor Pages app that lets you upload a video, preview its audio waveform, tune silence thresholds, process the file with FFmpeg, and export a cleaned result without sending media to a cloud service.

If Strippr saves you time, consider supporting development.

## What It Does

- Upload a local video file
- Detect silence with FFmpeg
- Remove silent segments from audio and video together
- Preview the audio waveform in the browser
- Adjust silence threshold and minimum silence with sliders
- Save processed output to `Downloads\Strippr`

## Stack

- .NET 9
- ASP.NET Core Razor Pages
- C#
- FFmpeg
- Browser-side Web Audio API for waveform preview

## Run Locally

Requirements:

- .NET SDK 9.0+
- Windows
- Internet access on first run if FFmpeg has not already been bundled locally

From the repo root:

```powershell
dotnet run
```

Then open the local URL printed by ASP.NET Core, or use:

```text
http://127.0.0.1:5123
```

## FFmpeg Handling

Strippr bootstraps a pinned FFmpeg build automatically on startup if a local bundled copy is not already available.

Current behavior:

- preferred local bundle path: `tools/ffmpeg`
- fallback: machine-installed FFmpeg if available
- current pinned download: Gyan FFmpeg 8.0.1 essentials build

Important GitHub note:

- the pinned FFmpeg archive is roughly 101 MB
- GitHub's normal per-file limit is 100 MB
- that means the archive should either be:
  - downloaded on first run, which Strippr already supports, or
  - stored with Git LFS if you want it versioned inside the repo

The extracted FFmpeg runtime is intentionally ignored by git.

## Licensing

Strippr source code is licensed under MIT.

Bundled FFmpeg is distributed under its own license terms and is not relicensed under MIT. See FFmpeg licensing and the bundled upstream attribution files when distributing it.

Practical rules for this repo:

- keep `LICENSE` as the license for Strippr application code
- do not remove or replace FFmpeg's own license terms
- include FFmpeg license and attribution materials when redistributing a bundled FFmpeg build
- treat FFmpeg as a separate third-party dependency with its own terms

## Output Location

Processed files are written to:

```text
C:\Users\<you>\Downloads\Strippr
```

The app also exposes a download endpoint and a browser `Save As...` flow for supported Chromium browsers.

## Current Status

This is an MVP focused on deterministic silence cleanup.

Included:

- silence detection
- waveform preview
- threshold tuning
- local output handling
- client-side progress UI

Not included yet:

- timeline editing
- transcription-based cut heuristics
- filler-word detection
- cloud deployment
- multi-user job handling

## Repo Structure

```text
Pages/       Razor Pages UI
Services/    FFmpeg, processing, storage, bootstrap logic
Models/      Processing models
Options/     App configuration
wwwroot/     CSS and browser-side JS
tools/ffmpeg FFmpeg bootstrap location
```

## License

MIT. See [LICENSE](LICENSE).

Third-party licensing notes are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
