using System.Buffers.Binary;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Options;
using Strippr.Models;
using Strippr.Options;

namespace Strippr.Services;

public sealed class SileroVadService : IDisposable
{
    private const int StateSize = 128;

    private readonly string _contentRootPath;
    private readonly FfmpegService _ffmpegService;
    private readonly ILogger<SileroVadService> _logger;
    private readonly StripprOptions _options;
    private readonly WorkspaceService _workspaceService;
    private readonly object _sessionGate = new();
    private InferenceSession? _session;
    private string? _loadedModelPath;

    public SileroVadService(
        IWebHostEnvironment environment,
        FfmpegService ffmpegService,
        ILogger<SileroVadService> logger,
        IOptions<StripprOptions> options,
        WorkspaceService workspaceService)
    {
        _contentRootPath = environment.ContentRootPath;
        _ffmpegService = ffmpegService;
        _logger = logger;
        _options = options.Value;
        _workspaceService = workspaceService;
    }

    public SileroVadStatus GetStatus()
    {
        var modelPath = ResolveModelPath();
        var runtimeVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
        var modelFileExists = File.Exists(modelPath);

        var message = !_options.SileroVadEnabled
            ? $"Silero VAD is configured but disabled. ONNX Runtime {runtimeVersion} is available."
            : modelFileExists
                ? $"Silero VAD is enabled and ready to load model {Path.GetFileName(modelPath)}."
                : $"Silero VAD is enabled but the model file was not found at {modelPath}.";

        return new SileroVadStatus(
            Enabled: _options.SileroVadEnabled,
            RuntimeAvailable: true,
            ModelFileExists: modelFileExists,
            ModelPath: modelPath,
            SampleRate: _options.SileroVadSampleRate,
            SpeechThreshold: _options.SileroVadSpeechThreshold,
            NegativeSpeechThreshold: _options.SileroVadNegativeSpeechThreshold,
            MinSpeechMilliseconds: _options.SileroVadMinSpeechMilliseconds,
            MinSilenceMilliseconds: _options.SileroVadMinSilenceMilliseconds,
            SpeechPadMilliseconds: _options.SileroVadSpeechPadMilliseconds,
            Message: message);
    }

    public async Task<SileroVadComparisonResult> CompareAsync(
        IFormFile video,
        string? ffmpegNoiseThreshold,
        double? ffmpegMinimumSilenceSeconds,
        CancellationToken cancellationToken)
    {
        if (video.Length <= 0)
        {
            throw new InvalidOperationException("Choose a video file before running Silero comparison.");
        }

        var uploadPath = _workspaceService.CreateTempPath(Path.GetFileNameWithoutExtension(video.FileName), Path.GetExtension(video.FileName));

        try
        {
            await using (var uploadStream = File.Create(uploadPath))
            {
                await video.CopyToAsync(uploadStream, cancellationToken);
            }

            return await CompareFileAsync(
                uploadPath,
                cancellationToken,
                ffmpegNoiseThreshold,
                ffmpegMinimumSilenceSeconds);
        }
        finally
        {
            TryDeleteTempFile(uploadPath);
        }
    }

    public async Task<SileroVadAnalysisResult> AnalyzeFileAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        EnsureReadyForInference();
        var durationSeconds = await _ffmpegService.ProbeDurationAsync(inputPath, cancellationToken);
        var vadAudioPath = _workspaceService.CreateTempPath(Path.GetFileNameWithoutExtension(inputPath) + "-silero", ".wav");

        try
        {
            await _ffmpegService.ExtractVadAudioAsync(
                inputPath,
                vadAudioPath,
                _options.SileroVadSampleRate,
                cancellationToken);

            return AnalyzePreparedVadAudio(vadAudioPath, durationSeconds);
        }
        finally
        {
            TryDeleteTempFile(vadAudioPath);
        }
    }

    public async Task<SileroVadComparisonResult> CompareFileAsync(
        string inputPath,
        CancellationToken cancellationToken,
        string? ffmpegNoiseThreshold = null,
        double? ffmpegMinimumSilenceSeconds = null)
    {
        EnsureReadyForInference();

        var noiseThreshold = string.IsNullOrWhiteSpace(ffmpegNoiseThreshold)
            ? _options.DefaultNoiseThreshold
            : ffmpegNoiseThreshold.Trim();
        var minimumSilenceSeconds = ffmpegMinimumSilenceSeconds.HasValue && double.IsFinite(ffmpegMinimumSilenceSeconds.Value)
            ? Math.Max(0.05, ffmpegMinimumSilenceSeconds.Value)
            : _options.DefaultMinimumSilenceSeconds;

        var ffmpegAnalysis = await _ffmpegService.AnalyzeSilenceAsync(
            inputPath,
            noiseThreshold,
            minimumSilenceSeconds,
            cancellationToken);
        var sileroAnalysis = await AnalyzeFileAsync(inputPath, cancellationToken);
        var message =
            $"Silero detected {sileroAnalysis.SpeechIntervals.Count} speech {(sileroAnalysis.SpeechIntervals.Count == 1 ? "region" : "regions")} " +
            $"and {sileroAnalysis.SilenceIntervals.Count} non-speech {(sileroAnalysis.SilenceIntervals.Count == 1 ? "gap" : "gaps")}. " +
            $"FFmpeg detected {ffmpegAnalysis.SilenceIntervals.Count} silence {(ffmpegAnalysis.SilenceIntervals.Count == 1 ? "range" : "ranges")}.";

        return new SileroVadComparisonResult(
            DurationSeconds: ffmpegAnalysis.DurationSeconds,
            FfmpegNoiseThreshold: noiseThreshold,
            FfmpegMinimumSilenceSeconds: minimumSilenceSeconds,
            Silero: sileroAnalysis,
            FfmpegSilenceIntervals: ffmpegAnalysis.SilenceIntervals,
            Message: message);
    }

    public string ResolveModelPath()
    {
        return Path.IsPathRooted(_options.SileroVadModelPath)
            ? _options.SileroVadModelPath
            : Path.GetFullPath(Path.Combine(_contentRootPath, _options.SileroVadModelPath));
    }

    public void Dispose()
    {
        lock (_sessionGate)
        {
            _session?.Dispose();
            _session = null;
            _loadedModelPath = null;
        }
    }

    private SileroVadAnalysisResult AnalyzePreparedVadAudio(string wavPath, double durationSeconds)
    {
        var wav = ReadPcm16MonoWav(wavPath);
        if (wav.SampleRate != _options.SileroVadSampleRate)
        {
            throw new InvalidOperationException($"Silero VAD expected {_options.SileroVadSampleRate} Hz audio, but received {wav.SampleRate} Hz.");
        }

        if (wav.Samples.Length == 0)
        {
            return new SileroVadAnalysisResult(
                DurationSeconds: durationSeconds,
                SampleRate: wav.SampleRate,
                SampleCount: 0,
                SpeechIntervals: [],
                SilenceIntervals: durationSeconds > 0 ? [new SilenceInterval(0, durationSeconds)] : [],
                Message: "Silero VAD received empty audio.");
        }

        var speechProbabilities = RunModel(wav.Samples, wav.SampleRate);
        var speechIntervals = BuildSpeechIntervals(speechProbabilities, wav.SampleRate, wav.Samples.Length);
        var silenceIntervals = BuildSilenceIntervals(speechIntervals, durationSeconds);
        var message = speechIntervals.Count == 0
            ? "Silero VAD found no speech."
            : $"Silero VAD found {speechIntervals.Count} speech {(speechIntervals.Count == 1 ? "region" : "regions")}.";

        return new SileroVadAnalysisResult(
            DurationSeconds: durationSeconds,
            SampleRate: wav.SampleRate,
            SampleCount: wav.Samples.Length,
            SpeechIntervals: speechIntervals,
            SilenceIntervals: silenceIntervals,
            Message: message);
    }

    private List<SpeechProbabilityFrame> RunModel(float[] samples, int sampleRate)
    {
        var session = GetOrCreateSession();
        var chunkSize = sampleRate == 16000 ? 512 : 256;
        var contextSize = sampleRate == 16000 ? 64 : 32;
        var totalChunks = (int)Math.Ceiling(samples.Length / (double)chunkSize);
        var probabilities = new List<SpeechProbabilityFrame>(totalChunks);
        var context = new float[contextSize];
        var state = new float[2 * StateSize];
        var srTensorData = new long[] { sampleRate };
        var outputNames = session.OutputMetadata.Keys.ToArray();

        for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex += 1)
        {
            var chunk = new float[chunkSize];
            var sampleOffset = chunkIndex * chunkSize;
            var available = Math.Max(0, Math.Min(chunkSize, samples.Length - sampleOffset));
            if (available > 0)
            {
                Array.Copy(samples, sampleOffset, chunk, 0, available);
            }

            var modelInput = new float[chunkSize + contextSize];
            Array.Copy(context, 0, modelInput, 0, contextSize);
            Array.Copy(chunk, 0, modelInput, contextSize, chunkSize);

            var inputTensor = new DenseTensor<float>(modelInput, [1, chunkSize + contextSize]);
            var stateTensor = new DenseTensor<float>(state, [2, 1, StateSize]);
            var srTensor = new DenseTensor<long>(srTensorData, [1]);

            var inputName = session.InputMetadata.Keys.ElementAtOrDefault(0) ?? "input";
            var stateName = session.InputMetadata.Keys.ElementAtOrDefault(1) ?? "state";
            var srName = session.InputMetadata.Keys.ElementAtOrDefault(2) ?? "sr";

            using var results = session.Run(
                [
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor),
                    NamedOnnxValue.CreateFromTensor(stateName, stateTensor),
                    NamedOnnxValue.CreateFromTensor(srName, srTensor)
                ],
                outputNames);

            var probability = ExtractProbability(results);
            state = ExtractState(results);
            Array.Copy(modelInput, chunkSize, context, 0, contextSize);

            probabilities.Add(new SpeechProbabilityFrame(
                StartSampleIndex: sampleOffset,
                EndSampleIndex: Math.Min(samples.Length, sampleOffset + available),
                Probability: probability));
        }

        return probabilities;
    }

    private InferenceSession GetOrCreateSession()
    {
        var modelPath = ResolveModelPath();

        lock (_sessionGate)
        {
            if (_session is not null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return _session;
            }

            _session?.Dispose();
            _session = new InferenceSession(modelPath);
            _loadedModelPath = modelPath;
            return _session;
        }
    }

    private void EnsureReadyForInference()
    {
        var status = GetStatus();
        if (!status.ModelFileExists)
        {
            throw new InvalidOperationException($"Silero VAD model not found. Expected file: {status.ModelPath}");
        }

        if (_options.SileroVadSampleRate is not 8000 and not 16000)
        {
            throw new InvalidOperationException("Silero VAD currently supports only 8000 Hz or 16000 Hz audio.");
        }
    }

    private List<SpeechInterval> BuildSpeechIntervals(
        IReadOnlyList<SpeechProbabilityFrame> probabilities,
        int sampleRate,
        int sampleCount)
    {
        if (probabilities.Count == 0 || sampleCount <= 0)
        {
            return [];
        }

        var speechThreshold = _options.SileroVadSpeechThreshold;
        var negativeThreshold = Math.Min(speechThreshold - 0.01f, _options.SileroVadNegativeSpeechThreshold);
        var minSpeechSamples = sampleRate * _options.SileroVadMinSpeechMilliseconds / 1000;
        var minSilenceSamples = sampleRate * _options.SileroVadMinSilenceMilliseconds / 1000;
        var speechPadSamples = sampleRate * _options.SileroVadSpeechPadMilliseconds / 1000;
        var intervals = new List<(int StartSample, int EndSample)>();

        var triggered = false;
        var speechStart = 0;
        int? pendingEnd = null;

        foreach (var frame in probabilities)
        {
            if (!triggered)
            {
                if (frame.Probability >= speechThreshold)
                {
                    triggered = true;
                    speechStart = Math.Max(0, frame.StartSampleIndex - speechPadSamples);
                    pendingEnd = null;
                }

                continue;
            }

            if (frame.Probability < negativeThreshold)
            {
                pendingEnd ??= frame.StartSampleIndex;

                if (frame.EndSampleIndex - pendingEnd.Value >= minSilenceSamples)
                {
                    var speechEnd = Math.Min(sampleCount, pendingEnd.Value + speechPadSamples);
                    if (speechEnd - speechStart >= minSpeechSamples)
                    {
                        intervals.Add((speechStart, speechEnd));
                    }

                    triggered = false;
                    pendingEnd = null;
                }
            }
            else
            {
                pendingEnd = null;
            }
        }

        if (triggered)
        {
            var speechEnd = sampleCount;
            if (speechEnd - speechStart >= minSpeechSamples)
            {
                intervals.Add((speechStart, speechEnd));
            }
        }

        return MergeSpeechIntervals(intervals, sampleRate);
    }

    private List<SpeechInterval> MergeSpeechIntervals(
        IReadOnlyList<(int StartSample, int EndSample)> intervals,
        int sampleRate)
    {
        if (intervals.Count == 0)
        {
            return [];
        }

        var minSilenceSamples = sampleRate * _options.SileroVadMinSilenceMilliseconds / 1000;
        var ordered = intervals
            .Where(interval => interval.EndSample > interval.StartSample)
            .OrderBy(interval => interval.StartSample)
            .ThenBy(interval => interval.EndSample)
            .ToList();
        var merged = new List<(int StartSample, int EndSample)> { ordered[0] };

        foreach (var interval in ordered.Skip(1))
        {
            var last = merged[^1];
            if (interval.StartSample - last.EndSample <= minSilenceSamples)
            {
                merged[^1] = (last.StartSample, Math.Max(last.EndSample, interval.EndSample));
                continue;
            }

            merged.Add(interval);
        }

        return merged
            .Select(interval => new SpeechInterval(
                StartSeconds: interval.StartSample / (double)sampleRate,
                EndSeconds: interval.EndSample / (double)sampleRate))
            .ToList();
    }

    private static List<SilenceInterval> BuildSilenceIntervals(
        IReadOnlyList<SpeechInterval> speechIntervals,
        double durationSeconds)
    {
        var silences = new List<SilenceInterval>();
        var cursor = 0d;

        foreach (var speech in speechIntervals.OrderBy(interval => interval.StartSeconds))
        {
            if (speech.StartSeconds > cursor)
            {
                silences.Add(new SilenceInterval(cursor, speech.StartSeconds));
            }

            cursor = Math.Max(cursor, speech.EndSeconds);
        }

        if (durationSeconds > cursor)
        {
            silences.Add(new SilenceInterval(cursor, durationSeconds));
        }

        return silences
            .Where(interval => interval.DurationSeconds > 0)
            .ToList();
    }

    private static float ExtractProbability(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var result in results)
        {
            if (result.AsTensor<float>() is not { } tensor)
            {
                continue;
            }

            if (tensor.Length == 1)
            {
                return tensor.ToArray()[0];
            }
        }

        throw new InvalidOperationException("Silero VAD did not return a probability output.");
    }

    private static float[] ExtractState(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var result in results)
        {
            if (result.AsTensor<float>() is not { } tensor)
            {
                continue;
            }

            if (tensor.Length == 2 * StateSize)
            {
                return tensor.ToArray();
            }
        }

        throw new InvalidOperationException("Silero VAD did not return a state tensor.");
    }

    private static WavAudioData ReadPcm16MonoWav(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidOperationException("Silero VAD audio was not a RIFF WAV file.");
        }

        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidOperationException("Silero VAD audio was not a WAVE file.");
        }

        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (reader.BaseStream.Position <= reader.BaseStream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var chunkStart = reader.BaseStream.Position;

            switch (chunkId)
            {
                case "fmt ":
                    audioFormat = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();
                    break;

                case "data":
                    data = reader.ReadBytes((int)chunkSize);
                    break;

                default:
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                    break;
            }

            reader.BaseStream.Position = chunkStart + chunkSize;
            if ((chunkSize & 1) == 1)
            {
                reader.BaseStream.Seek(1, SeekOrigin.Current);
            }
        }

        if (audioFormat != 1 || channels != 1 || bitsPerSample != 16 || data is null)
        {
            throw new InvalidOperationException("Silero VAD expected mono 16-bit PCM WAV input.");
        }

        var sampleCount = data.Length / 2;
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index += 1)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(index * 2, 2));
            samples[index] = Math.Clamp(sample / 32768f, -1f, 1f);
        }

        return new WavAudioData(sampleRate, samples);
    }

    private void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to delete temporary Silero file {Path}", path);
        }
    }

    private sealed record SpeechProbabilityFrame(
        int StartSampleIndex,
        int EndSampleIndex,
        float Probability);

    private sealed record WavAudioData(
        int SampleRate,
        float[] Samples);
}
