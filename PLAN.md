# Strippr Plan

## Goal

Build a local web app called `strippr` that lets a user:

1. Upload a video file.
2. Detect silence and obvious dead air.
3. Cut or compress those sections.
4. Download the cleaned result.

The first version should be practical, deterministic, and easy to run on a local machine. It should avoid unnecessary AI complexity until the silence-cutting pipeline is solid.

## Product Direction

### MVP

The MVP focuses on one job:

- Upload a single video.
- Detect silence with FFmpeg.
- Remove or compress silent gaps.
- Return a processed output file.

### Post-MVP

After the silence pipeline works reliably, add:

- Preview timeline of cuts.
- Adjustable thresholds for silence detection.
- "Bad take" detection with speech heuristics.
- Batch processing.
- Export presets for Shorts, Reels, YouTube.

## Technical Approach

### Stack

Use:

- `ASP.NET Core 9`
- `Razor Pages`
- `FFmpeg` for media analysis and rendering
- `C#`

Reasoning:

- A single Razor Pages app keeps the local UI and backend logic in one codebase.
- FFmpeg already solves the hard media-processing primitives.
- C# keeps the cut-planning and process orchestration code easy to test.

### Storage

Use local disk storage first:

- uploaded files in `App_Data/uploads`
- rendered outputs in `App_Data/outputs`
- temp working files in `App_Data/temp`

Do not require a database for the MVP.

If persistence becomes necessary later, use `SQLite` rather than SQL Server because this app is local-only and single-user.

### Local-First Runtime

The app should run locally on the user's machine, for example:

```bash
dotnet run
```

Then open:

```text
https://localhost:5001
```

### Processing Pipeline

1. User uploads a video.
2. Server stores it in a temp working directory.
3. FFmpeg extracts or analyzes audio.
4. `silencedetect` identifies silence ranges.
5. App converts silence ranges into keep/cut segments.
6. FFmpeg renders a cleaned output.
7. User downloads the result.

## Core Decisions

### Decision 1: Start with silence removal, not bad-take AI

Silence removal is the fastest way to get a useful product. "Bad take" detection is much less reliable and should be treated as an enhancement layer, not a foundation.

### Decision 2: Prefer pause compression over hard deletion

Completely deleting pauses can make speech feel unnatural. The better default is:

- keep short pauses untouched
- compress long pauses to a smaller fixed duration

Example:

- `3.0s` pause becomes `0.3s`

### Decision 3: Use a cut plan as an intermediate data model

Do not couple the UI directly to FFmpeg output. The app should generate an internal cut plan like:

```json
{
  "source": "input.mp4",
  "segments": [
    { "start": 0, "end": 12.4, "action": "keep" },
    { "start": 12.4, "end": 14.1, "action": "compress", "targetDuration": 0.3 }
  ]
}
```

This makes the logic testable and supports future timeline previews.

## Delivery Phases

### Phase 1: Project Bootstrap

Create:

- ASP.NET Core 9 Razor Pages app
- basic homepage
- upload form
- local temp/output directories
- FFmpeg presence check

Definition of done:

- app boots locally
- user can select a video file
- backend accepts upload and stores it

### Phase 2: Silence Detection

Implement:

- FFmpeg `silencedetect` execution
- parser for silence timestamps
- normalization of silence ranges
- basic user-configurable thresholds

Initial defaults:

- noise threshold: `-30dB`
- minimum silence duration: `0.5s`

Definition of done:

- backend returns parsed silence intervals for a sample file

### Phase 3: Cut Plan Builder

Implement:

- conversion from silence ranges to timeline segments
- strategies:
  - `remove`
  - `compress`
- guardrails against tiny unusable cuts

Definition of done:

- a sample input produces a deterministic cut plan
- cut plan is test-covered

### Phase 4: Video Rendering

Implement:

- render output from the cut plan
- preserve A/V sync
- write output file to disk
- expose downloadable result

Preferred implementation path:

- start with hard cuts for silent segments
- add pause compression after the hard-cut path is stable

Definition of done:

- uploaded video produces a downloadable edited file

### Phase 5: UX Pass

Add:

- processing states
- error messages
- job progress indicator
- result metadata
- clean download flow

Definition of done:

- user can upload, process, and download without using the terminal

### Phase 6: Smart Cut Enhancements

Potential features:

- Whisper transcription
- filler word detection
- restart phrase detection
- optional "aggressive cleanup" mode

Definition of done:

- heuristics are optional and do not block the core silence workflow

## File Structure Target

Suggested initial structure:

```text
/
  PLAN.md
  Program.cs
  Strippr.csproj
  Pages/
    Index.cshtml
    Index.cshtml.cs
    Shared/
      _Layout.cshtml
  Services/
    FfmpegService.cs
    CutPlanBuilder.cs
    VideoProcessingService.cs
    WorkspaceService.cs
  Models/
  Options/
  App_Data/
    uploads/
    outputs/
    temp/
```

## Risks

### FFmpeg Availability

Risk:

- local machines may not have FFmpeg installed or available on `PATH`

Mitigation:

- check for FFmpeg on startup
- show a direct setup error in the UI

### A/V Sync

Risk:

- naive cuts can desync video and audio

Mitigation:

- centralize render logic
- test with real speech-heavy sample videos

### Large Files

Risk:

- uploads and renders can be slow or memory-heavy

Mitigation:

- stream files where possible
- keep processing on disk, not fully in memory

### Bad-Take Detection Quality

Risk:

- heuristic or AI-based cuts may be wrong and frustrate users

Mitigation:

- keep it opt-in
- ship silence cleanup first

## Acceptance Criteria For MVP

The MVP is complete when:

- a user can run the app locally
- a user can upload one video through the browser
- the app detects silence using FFmpeg
- the app outputs a cleaned video file
- the user can download the result from the browser
- common failures produce understandable errors

## Immediate Next Steps

1. Install FFmpeg locally and add it to `PATH`.
2. Run the app and verify the health panel reports FFmpeg as available.
3. Test the upload/process/download flow with a real sample clip.
4. Add progress reporting for long renders.
5. Add optional pause compression after the hard-cut path is stable.
6. Decide later whether any persistence actually needs `SQLite`.

## Non-Goals For First Pass

Do not spend time yet on:

- cloud deployment
- authentication
- accounts
- multi-user job management
- advanced timeline editor
- visual scene analysis

## Working Principle

Keep the first version boring and reliable:

- local
- single-user
- FFmpeg-first
- deterministic
- testable

If the MVP works well, the smart features can be layered on top without rewriting the core pipeline.
