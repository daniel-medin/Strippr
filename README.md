# Strippr

![Strippr logo](wwwroot/img/logo.png)

Local-first video cleanup for dead air and silence.

Strippr is an ASP.NET Core 9 Razor Pages app that lets you upload a video, preview its audio waveform, tune silence thresholds, process the file with FFmpeg, and export a cleaned result without sending media to a cloud service.

If Strippr saves you time, consider supporting development.

## Download for Windows

End users should download a release build instead of cloning the repo.

- Latest Windows build: [GitHub Releases](https://github.com/daniel-medin/Strippr/releases/latest)
- Release zip includes the .NET runtime and bundled FFmpeg
- Download, unzip, and run `Strippr.exe`

## What It Does

- Upload a local video file
- Detect silence with FFmpeg
- Remove silent segments from audio and video together
- Preview the audio waveform in the browser
- Adjust silence threshold and minimum silence with sliders
- Download processed output when and where you want

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
- Internet access on first run if `tools/ffmpeg/package` is not already available locally

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

## Licensing

Strippr is source-available, not open source.

The Strippr application code is licensed under the custom proprietary personal-use terms in [LICENSE](LICENSE).

What that means in practice:

- personal, non-commercial, and internal evaluation use is allowed
- redistribution, resale, and commercial use are not allowed without written permission
- ownership of Strippr remains with Daniel Medin

Bundled FFmpeg is distributed under its own separate license terms and is not covered by the Strippr license.

Important GitHub note:

- if a repository is public on GitHub, other users can still view and fork it under GitHub's platform rules
- if you want stronger practical control over access to the source, keep the repository private

## Output Location

Strippr renders files into internal app storage and then exposes them through the browser download flow.

Use:

- `Save As...` to choose the destination yourself in supported Chromium browsers
- `Download cleaned video` to use your browser's normal download behavior

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

Custom proprietary personal-use license. See [LICENSE](LICENSE).

Third-party licensing notes are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
