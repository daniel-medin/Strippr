using System.Net.Http.Headers;
using System.Text;
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

    private static readonly HashSet<string> AllowedContentModels = new(StringComparer.Ordinal)
    {
        "gpt-5.2",
        "gpt-5",
        "gpt-5-mini",
        "gpt-4o-mini"
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
        string language,
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

        var normalizedLanguage = NormalizeLanguage(language);
        var normalizedMinimumGapSeconds = Math.Max(0.1, minimumGapSeconds);
        var transcription = await CreateTranscriptionAsync(
            video,
            effectiveApiKey,
            effectiveModel,
            normalizedLanguage,
            includeWordTimestamps: false,
            cancellationToken);
        var silenceRanges = InferSilenceRanges(
            transcription.DurationSeconds,
            transcription.Transcription,
            normalizedMinimumGapSeconds);

        var message = silenceRanges.Count == 0
            ? "AI found no speech gaps matching the current minimum silence."
            : $"AI found {silenceRanges.Count} silence {(silenceRanges.Count == 1 ? "range" : "ranges")} from transcript timing gaps.";

        return new AiSilenceAnalysisResult(effectiveModel, transcription.DurationSeconds, silenceRanges, message);
    }

    public async Task<AiContentAnalysisResult> AnalyzeContentAsync(
        IFormFile video,
        string apiKey,
        string analysisModel,
        string language,
        CancellationToken cancellationToken)
    {
        if (video.Length <= 0)
        {
            throw new InvalidOperationException("Choose a video file before running A2 analysis.");
        }

        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? _options.OpenAiApiKey
            : apiKey;
        var effectiveAnalysisModel = string.IsNullOrWhiteSpace(analysisModel)
            ? "gpt-5.2"
            : analysisModel;

        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new InvalidOperationException("Enter an OpenAI API key or set Strippr:OpenAiApiKey in appsettings.Local.json before running A2 analysis.");
        }

        if (!AllowedContentModels.Contains(effectiveAnalysisModel))
        {
            throw new InvalidOperationException("Choose a supported OpenAI analysis model. A2 currently supports gpt-5.2, gpt-5, gpt-5-mini, and gpt-4o-mini.");
        }

        var transcriptionModel = "whisper-1";
        var normalizedLanguage = NormalizeLanguage(language);
        var transcription = await CreateTranscriptionAsync(
            video,
            effectiveApiKey,
            transcriptionModel,
            normalizedLanguage,
            includeWordTimestamps: true,
            cancellationToken);
        var transcriptSegments = BuildTranscriptSegments(transcription.DurationSeconds, transcription.Transcription);
        var transcriptWords = BuildTranscriptWords(transcription.DurationSeconds, transcription.Transcription);
        var transcriptText = !string.IsNullOrWhiteSpace(transcription.Transcription.Text)
            ? transcription.Transcription.Text.Trim()
            : string.Join(" ", transcriptSegments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return new AiContentAnalysisResult(
                TranscriptionModel: transcriptionModel,
                AnalysisModel: effectiveAnalysisModel,
                DurationSeconds: transcription.DurationSeconds,
                TranscriptText: string.Empty,
                Summary: "A2 could not recover usable transcript text from this clip.",
                TranscriptSegments: transcriptSegments,
                Issues: [],
                Message: "A2 found no spoken content to analyze.");
        }

        var analysis = await AnalyzeTranscriptContentAsync(
            transcription.DurationSeconds,
            transcriptText,
            transcriptSegments,
            transcriptWords,
            effectiveApiKey,
            effectiveAnalysisModel,
            cancellationToken);

        var message = analysis.Issues.Count == 0
            ? "A2 found no obvious content mistakes."
            : $"A2 found {analysis.Issues.Count} likely retake {(analysis.Issues.Count == 1 ? "range" : "ranges")}.";

        return new AiContentAnalysisResult(
            TranscriptionModel: transcriptionModel,
            AnalysisModel: effectiveAnalysisModel,
            DurationSeconds: transcription.DurationSeconds,
            TranscriptText: transcriptText,
            Summary: string.IsNullOrWhiteSpace(analysis.Summary)
                ? message
                : analysis.Summary,
            TranscriptSegments: transcriptSegments,
            Issues: analysis.Issues,
            Message: message);
    }

    private async Task<OpenAiVerboseTranscriptionResponse> TranscribeAsync(
        string audioPath,
        string apiKey,
        string model,
        string? language,
        bool includeWordTimestamps,
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
        if (!string.IsNullOrWhiteSpace(language))
        {
            form.Add(new StringContent(language), "language");
        }

        var transcriptionPrompt = BuildTranscriptionPrompt(language);
        if (!string.IsNullOrWhiteSpace(transcriptionPrompt))
        {
            form.Add(new StringContent(transcriptionPrompt), "prompt");
        }

        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (includeWordTimestamps)
        {
            form.Add(new StringContent("word"), "timestamp_granularities[]");
        }

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

    private async Task<PreparedTranscription> CreateTranscriptionAsync(
        IFormFile video,
        string apiKey,
        string model,
        string? language,
        bool includeWordTimestamps,
        CancellationToken cancellationToken)
    {
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

            var transcription = await TranscribeAsync(audioPath, apiKey, model, language, includeWordTimestamps, cancellationToken);
            return new PreparedTranscription(durationSeconds, transcription);
        }
        finally
        {
            TryDeleteTempFile(uploadPath);
            TryDeleteTempFile(audioPath);
        }
    }

    private async Task<OpenAiContentResponse> AnalyzeTranscriptContentAsync(
        double durationSeconds,
        string transcriptText,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiTranscriptWord> transcriptWords,
        string apiKey,
        string analysisModel,
        CancellationToken cancellationToken)
    {
        const string systemPrompt =
            "You identify spoken retake or correction moments for local video editing. " +
            "Be conservative. Only flag ranges where the speaker likely said something wrong, restarted, corrected themselves, " +
            "repeated a phrase, abandoned a sentence, or produced a filler-heavy stumble that is a plausible cut candidate. " +
            "Do not flag normal pauses, natural thinking, or fluent speech. " +
            "Default editing rule: when a speaker retakes or corrects themselves, keep the latest valid take and cut the earlier superseded take. " +
            "For self-corrections, start the cut on the wrong phrase and end it immediately before the final kept correction begins. " +
            "If the speaker says 'Friday, no, sorry, Thursday', cut 'Friday, no, sorry' and keep 'Thursday'. " +
            "If the speaker says one full attempt and then repeats it better, cut the first attempt and any bridge/filler before the better take, and keep the later take. " +
            "Never remove the final valid take just because it is close to the mistake. " +
            "Use word timestamps when available so the range starts on the first superseded words and ends on the word right before the kept take begins. " +
            "Return strict JSON only.";

        var userPrompt =
            $"Clip duration: {durationSeconds:0.###} seconds.\n" +
            $"Full transcript:\n{transcriptText}\n\n" +
            "Timestamped transcript segments (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptSegments, JsonOptions)}\n\n" +
            "Timestamped transcript words (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptWords, JsonOptions)}\n\n" +
            "Return the smallest useful cut ranges that preserve the last valid take. " +
            "If there are no likely spoken mistakes, return an empty issues array.";

        var requestBody = new
        {
            model = analysisModel,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = new object[]
                    {
                        new { type = "input_text", text = systemPrompt }
                    }
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = userPrompt }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "strippr_content_analysis",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            summary = new { type = "string" },
                            issues = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        startSeconds = new { type = "number" },
                                        endSeconds = new { type = "number" },
                                        label = new { type = "string" },
                                        reason = new { type = "string" },
                                        excerpt = new { type = "string" }
                                    },
                                    required = new[] { "startSeconds", "endSeconds", "label", "reason", "excerpt" }
                                }
                            }
                        },
                        required = new[] { "summary", "issues" }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildOpenAiErrorMessage(responseContent, (int)response.StatusCode));
        }

        var responsePayload = JsonSerializer.Deserialize<OpenAiResponseApiResponse>(responseContent, JsonOptions);
        var responseText = ExtractResponseText(responsePayload);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException("OpenAI returned an unreadable A2 analysis response.");
        }

        var analysis = JsonSerializer.Deserialize<OpenAiContentResponsePayload>(responseText, JsonOptions);
        if (analysis is null)
        {
            throw new InvalidOperationException("OpenAI returned invalid structured JSON for A2 analysis.");
        }

        var normalizedIssues = analysis.Issues?
            .Where(issue =>
                issue is not null &&
                double.IsFinite(issue.StartSeconds) &&
                double.IsFinite(issue.EndSeconds))
            .Select(issue => new AiContentIssue(
                StartSeconds: Math.Clamp(issue.StartSeconds, 0, durationSeconds),
                EndSeconds: Math.Clamp(issue.EndSeconds, 0, durationSeconds),
                Label: string.IsNullOrWhiteSpace(issue.Label) ? "Retake" : issue.Label.Trim(),
                Reason: string.IsNullOrWhiteSpace(issue.Reason) ? "Potential spoken mistake." : issue.Reason.Trim(),
                Excerpt: string.IsNullOrWhiteSpace(issue.Excerpt) ? string.Empty : issue.Excerpt.Trim()))
            .Where(issue => issue.EndSeconds > issue.StartSeconds)
            .ToList()
            ?? [];

        return new OpenAiContentResponse(
            Summary: analysis.Summary,
            Issues: normalizedIssues);
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

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var normalized = language.Trim().ToLowerInvariant();
        if (normalized.Length > 16)
        {
            return null;
        }

        foreach (var character in normalized)
        {
            if (!char.IsLetter(character) && character != '-')
            {
                return null;
            }
        }

        return normalized;
    }

    private static string? BuildTranscriptionPrompt(string? language) => language switch
    {
        "en" => "This audio is spoken in English. Transcribe it in English.",
        "sv" => "Detta ljud ar pa svenska. Transkribera det pa svenska.",
        _ => null
    };

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

    private static string? ExtractResponseText(OpenAiResponseApiResponse? response)
    {
        if (!string.IsNullOrWhiteSpace(response?.OutputText))
        {
            return response.OutputText;
        }

        return response?.Output?
            .SelectMany(output => output.Content ?? [])
            .Select(content => content.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static List<AiTranscriptSegment> BuildTranscriptSegments(
        double durationSeconds,
        OpenAiVerboseTranscriptionResponse transcription)
    {
        return transcription.Segments?
            .Where(segment =>
                segment is not null &&
                double.IsFinite(segment.Start) &&
                double.IsFinite(segment.End) &&
                segment.End > segment.Start &&
                !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => new AiTranscriptSegment(
                StartSeconds: Math.Clamp(segment.Start, 0, durationSeconds),
                EndSeconds: Math.Clamp(segment.End, 0, durationSeconds),
                Text: segment.Text!.Trim()))
            .Where(segment => segment.EndSeconds > segment.StartSeconds)
            .ToList()
            ?? [];
    }

    private static List<AiTranscriptWord> BuildTranscriptWords(
        double durationSeconds,
        OpenAiVerboseTranscriptionResponse transcription)
    {
        return transcription.Words?
            .Where(word =>
                word is not null &&
                double.IsFinite(word.Start) &&
                double.IsFinite(word.End) &&
                word.End > word.Start &&
                !string.IsNullOrWhiteSpace(word.Word))
            .Select(word => new AiTranscriptWord(
                StartSeconds: Math.Clamp(word.Start, 0, durationSeconds),
                EndSeconds: Math.Clamp(word.End, 0, durationSeconds),
                Word: word.Word!.Trim()))
            .Where(word => word.EndSeconds > word.StartSeconds)
            .ToList()
            ?? [];
    }

    private sealed record OpenAiVerboseTranscriptionResponse(
        double? Duration,
        IReadOnlyList<OpenAiVerboseSegment>? Segments,
        IReadOnlyList<OpenAiVerboseWord>? Words,
        string? Text);

    private sealed record OpenAiVerboseSegment(
        double Start,
        double End,
        string? Text);

    private sealed record OpenAiVerboseWord(
        double Start,
        double End,
        string? Word);

    private sealed record AiTranscriptWord(
        double StartSeconds,
        double EndSeconds,
        string Word);

    private sealed record PreparedTranscription(
        double DurationSeconds,
        OpenAiVerboseTranscriptionResponse Transcription);

    private sealed record OpenAiResponseApiResponse(
        string? OutputText,
        IReadOnlyList<OpenAiResponseOutput>? Output);

    private sealed record OpenAiResponseOutput(
        string? Type,
        IReadOnlyList<OpenAiResponseContent>? Content);

    private sealed record OpenAiResponseContent(
        string? Type,
        string? Text);

    private sealed record OpenAiContentResponsePayload(
        string? Summary,
        IReadOnlyList<OpenAiContentIssueResponse>? Issues);

    private sealed record OpenAiContentResponse(
        string? Summary,
        IReadOnlyList<AiContentIssue> Issues);

    private sealed record OpenAiContentIssueResponse(
        double StartSeconds,
        double EndSeconds,
        string? Label,
        string? Reason,
        string? Excerpt);

    private sealed record OpenAiErrorResponse(OpenAiErrorBody? Error);

    private sealed record OpenAiErrorBody(string? Message);
}
