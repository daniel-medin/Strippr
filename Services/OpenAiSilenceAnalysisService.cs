using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Strippr.Models;
using Strippr.Options;

namespace Strippr.Services;

public sealed class OpenAiSilenceAnalysisService
{
    private const long MaxOpenAiAudioBytes = 25L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> AllowedModels = new(StringComparer.Ordinal)
    {
        "whisper-1"
    };

    private readonly FfmpegService _ffmpegService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiSilenceAnalysisService> _logger;
    private readonly StripprOptions _options;
    private readonly WorkspaceService _workspaceService;

    public OpenAiSilenceAnalysisService(
        FfmpegService ffmpegService,
        HttpClient httpClient,
        ILogger<OpenAiSilenceAnalysisService> logger,
        IOptions<StripprOptions> options,
        WorkspaceService workspaceService)
    {
        _ffmpegService = ffmpegService;
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _workspaceService = workspaceService;
    }

    public async Task<AiSilenceAnalysisResult> AnalyzeAsync(
        IFormFile video,
        string apiKey,
        string model,
        double minimumGapSeconds,
        CancellationToken cancellationToken)
    {
        if (video.Length <= 0)
        {
            throw new InvalidOperationException("Choose a video file before running AI analysis.");
        }

        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? _options.OpenAiApiKey
            : apiKey;
        var effectiveModel = string.IsNullOrWhiteSpace(model)
            ? _options.DefaultOpenAiModel
            : model;

        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new InvalidOperationException("Enter an OpenAI API key or set Strippr:OpenAiApiKey in appsettings.Local.json before running AI analysis.");
        }

        if (!AllowedModels.Contains(effectiveModel))
        {
            throw new InvalidOperationException("Choose a timestamp-capable OpenAI transcription model. AI silence overlay currently supports whisper-1.");
        }

        var normalizedMinimumGapSeconds = Math.Max(0.1, minimumGapSeconds);
        var originalExtension = Path.GetExtension(video.FileName);
        var uploadPath = _workspaceService.CreateTempPath(Path.GetFileNameWithoutExtension(video.FileName), originalExtension);
        var audioPath = _workspaceService.CreateTempPath(Path.GetFileNameWithoutExtension(video.FileName) + "-ai", ".mp3");

        try
        {
            await using (var uploadStream = File.Create(uploadPath))
            {
                await video.CopyToAsync(uploadStream, cancellationToken);
            }

            var durationSeconds = await _ffmpegService.ProbeDurationAsync(uploadPath, cancellationToken);
            await _ffmpegService.ExtractTranscriptionAudioAsync(uploadPath, audioPath, cancellationToken);

            var audioInfo = new FileInfo(audioPath);
            if (!audioInfo.Exists)
            {
                throw new InvalidOperationException("The temporary AI audio file could not be created.");
            }

            if (audioInfo.Length > MaxOpenAiAudioBytes)
            {
                var maximumMinutes = TimeSpan.FromSeconds(durationSeconds).TotalMinutes;
                throw new InvalidOperationException(
                    $"The extracted AI audio is {audioInfo.Length / (1024d * 1024d):0.0} MB, which exceeds OpenAI's 25 MB upload limit. " +
                    $"Try a shorter clip. Current duration: {maximumMinutes:0.#} minutes.");
            }

            var transcription = await TranscribeAsync(audioPath, effectiveApiKey, effectiveModel, cancellationToken);
            var silenceRanges = InferSilenceRanges(
                durationSeconds,
                transcription,
                normalizedMinimumGapSeconds);

            var message = silenceRanges.Count == 0
                ? "AI found no speech gaps matching the current minimum silence."
                : $"AI found {silenceRanges.Count} silence {(silenceRanges.Count == 1 ? "range" : "ranges")} from transcript timing gaps.";

            return new AiSilenceAnalysisResult(effectiveModel, durationSeconds, silenceRanges, message);
        }
        finally
        {
            TryDeleteTempFile(uploadPath);
            TryDeleteTempFile(audioPath);
        }
    }

    private async Task<OpenAiVerboseTranscriptionResponse> TranscribeAsync(
        string audioPath,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var form = new MultipartFormDataContent();
        using var audioStream = File.OpenRead(audioPath);
        using var audioContent = new StreamContent(audioStream);

        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(audioContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");

        request.Content = form;

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildOpenAiErrorMessage(responseContent, (int)response.StatusCode));
        }

        var transcription = JsonSerializer.Deserialize<OpenAiVerboseTranscriptionResponse>(responseContent, JsonOptions);
        if (transcription is null)
        {
            throw new InvalidOperationException("OpenAI returned an unreadable transcription response.");
        }

        return transcription;
    }

    private static IReadOnlyList<AiDetectedSilenceRange> InferSilenceRanges(
        double durationSeconds,
        OpenAiVerboseTranscriptionResponse transcription,
        double minimumGapSeconds)
    {
        var speechSegments = transcription.Segments?
            .Where(segment =>
                segment is not null &&
                double.IsFinite(segment.Start) &&
                double.IsFinite(segment.End) &&
                segment.End > segment.Start)
            .Select(segment => new AiDetectedSilenceRange(
                StartSeconds: Math.Max(0, segment.Start),
                EndSeconds: Math.Min(durationSeconds, segment.End)))
            .OrderBy(segment => segment.StartSeconds)
            .ToList()
            ?? [];

        if (speechSegments.Count == 0)
        {
            return durationSeconds >= minimumGapSeconds
                ? [new AiDetectedSilenceRange(0, durationSeconds)]
                : [];
        }

        var mergedSpeechSegments = new List<AiDetectedSilenceRange> { speechSegments[0] };
        for (var index = 1; index < speechSegments.Count; index += 1)
        {
            var previous = mergedSpeechSegments[^1];
            var current = speechSegments[index];
            if (current.StartSeconds <= previous.EndSeconds)
            {
                mergedSpeechSegments[^1] = previous with
                {
                    EndSeconds = Math.Max(previous.EndSeconds, current.EndSeconds)
                };
                continue;
            }

            mergedSpeechSegments.Add(current);
        }

        var silenceRanges = new List<AiDetectedSilenceRange>();
        var cursor = 0d;
        foreach (var speechSegment in mergedSpeechSegments)
        {
            if (speechSegment.StartSeconds - cursor >= minimumGapSeconds)
            {
                silenceRanges.Add(new AiDetectedSilenceRange(cursor, speechSegment.StartSeconds));
            }

            cursor = Math.Max(cursor, speechSegment.EndSeconds);
        }

        if (durationSeconds - cursor >= minimumGapSeconds)
        {
            silenceRanges.Add(new AiDetectedSilenceRange(cursor, durationSeconds));
        }

        return silenceRanges;
    }

    private string BuildOpenAiErrorMessage(string responseContent, int statusCode)
    {
        try
        {
            var errorResponse = JsonSerializer.Deserialize<OpenAiErrorResponse>(responseContent, JsonOptions);
            var apiMessage = errorResponse?.Error?.Message;
            if (!string.IsNullOrWhiteSpace(apiMessage))
            {
                return $"OpenAI API error ({statusCode}): {apiMessage}";
            }
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(exception, "OpenAI error response was not valid JSON.");
        }

        return $"OpenAI API error ({statusCode}).";
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
            _logger.LogDebug(exception, "Failed to delete temporary AI file {Path}", path);
        }
    }

    private sealed record OpenAiVerboseTranscriptionResponse(
        double? Duration,
        IReadOnlyList<OpenAiVerboseSegment>? Segments,
        string? Text);

    private sealed record OpenAiVerboseSegment(
        double Start,
        double End,
        string? Text);

    private sealed record OpenAiErrorResponse(OpenAiErrorBody? Error);

    private sealed record OpenAiErrorBody(string? Message);
}
