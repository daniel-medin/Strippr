# Third-Party Notices

Strippr application code is licensed separately under the custom Strippr license.

The third-party tools, libraries, models, and services below are not relicensed under the Strippr license and remain subject to their own upstream terms.

## FFmpeg

Strippr can bootstrap and use a bundled FFmpeg build for local media processing.

When distributing Strippr with a bundled FFmpeg build:

- keep FFmpeg license and attribution files intact
- do not remove or replace upstream FFmpeg notices
- make it clear that FFmpeg is a separate third-party component

Current bundled source configured by Strippr:

- Gyan FFmpeg 8.0.1 essentials build

Project references:

- FFmpeg: https://ffmpeg.org/
- FFmpeg legal information: https://ffmpeg.org/legal.html
- FFmpeg licensing documentation: https://ffmpeg.org/doxygen/trunk/md_LICENSE.html

## Microsoft.ML.OnnxRuntime

Strippr uses ONNX Runtime for the optional local Silero VAD path.

Project references:

- ONNX Runtime: https://onnxruntime.ai/
- NuGet package: https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/
- License: MIT

## Silero VAD

Strippr can optionally use the Silero VAD model for local speech / non-speech detection.

Important:

- the Silero model file is optional
- the model file is ignored by git in this repo
- the model is not currently bundled into Strippr release builds by default

Project references:

- Silero VAD repository: https://github.com/snakers4/silero-vad

## OpenAI API

Strippr has optional experimental AI features that can call the OpenAI API when the user enables them and provides an API key.

Important:

- core editing and rendering do not require OpenAI
- AI features are optional and experimental
- any OpenAI API use is governed by OpenAI's own terms, policies, and billing

Project references:

- OpenAI API platform: https://platform.openai.com/
