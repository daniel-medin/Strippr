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
    private const string ChunkSilenceNoiseThreshold = "-30dB";
    private const double ChunkSilenceMinimumSeconds = 0.22;
    private const double MinimumChunkDurationSeconds = 0.45;
    private const double MaximumChunkDurationSeconds = 12.5;
    private const int MaximumChunkTranscriptions = 18;

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

    private static readonly HashSet<string> CoverageStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "but",
        "by",
        "for",
        "from",
        "had",
        "has",
        "have",
        "he",
        "her",
        "his",
        "in",
        "into",
        "is",
        "it",
        "its",
        "of",
        "on",
        "or",
        "our",
        "out",
        "she",
        "so",
        "that",
        "the",
        "their",
        "they",
        "this",
        "to",
        "was",
        "we",
        "were",
        "what",
        "when",
        "with"
    };

    private readonly CutPlanBuilder _cutPlanBuilder;
    private readonly FfmpegService _ffmpegService;
    private readonly AiFeedbackStore _aiFeedbackStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiSilenceAnalysisService> _logger;
    private readonly StripprOptions _options;
    private readonly WorkspaceService _workspaceService;

    public OpenAiSilenceAnalysisService(
        CutPlanBuilder cutPlanBuilder,
        FfmpegService ffmpegService,
        AiFeedbackStore aiFeedbackStore,
        HttpClient httpClient,
        ILogger<OpenAiSilenceAnalysisService> logger,
        IOptions<StripprOptions> options,
        WorkspaceService workspaceService)
    {
        _cutPlanBuilder = cutPlanBuilder;
        _ffmpegService = ffmpegService;
        _aiFeedbackStore = aiFeedbackStore;
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
        var preparedMedia = await PrepareMediaAsync(video, cancellationToken);

        try
        {
            EnsureOpenAiAudioSize(preparedMedia.AudioPath, preparedMedia.DurationSeconds);

            var transcription = await TranscribeAsync(
                preparedMedia.AudioPath,
                effectiveApiKey,
                effectiveModel,
                normalizedLanguage,
                includeWordTimestamps: false,
                cancellationToken);
            var silenceRanges = InferSilenceRanges(
                preparedMedia.DurationSeconds,
                transcription,
                normalizedMinimumGapSeconds);

            var message = silenceRanges.Count == 0
                ? "AI found no speech gaps matching the current minimum silence."
                : $"AI found {silenceRanges.Count} silence {(silenceRanges.Count == 1 ? "range" : "ranges")} from transcript timing gaps.";

            return new AiSilenceAnalysisResult(effectiveModel, preparedMedia.DurationSeconds, silenceRanges, message);
        }
        finally
        {
            CleanupPreparedMedia(preparedMedia);
        }
    }

    public async Task<AiContentAnalysisResult> AnalyzeContentAsync(
        IFormFile video,
        string apiKey,
        string analysisModel,
        string language,
        bool useMemory,
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
        var preparedMedia = await PrepareMediaAsync(video, cancellationToken);

        try
        {
            EnsureOpenAiAudioSize(preparedMedia.AudioPath, preparedMedia.DurationSeconds);

            var transcription = await TranscribeAsync(
                preparedMedia.AudioPath,
                effectiveApiKey,
                transcriptionModel,
                normalizedLanguage,
                includeWordTimestamps: true,
                cancellationToken);
            var transcriptSegments = BuildTranscriptSegments(preparedMedia.DurationSeconds, transcription);
            var transcriptWords = BuildTranscriptWords(preparedMedia.DurationSeconds, transcription);
            var transcriptText = !string.IsNullOrWhiteSpace(transcription.Text)
                ? transcription.Text.Trim()
                : string.Join(" ", transcriptSegments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

            if (string.IsNullOrWhiteSpace(transcriptText))
            {
                return new AiContentAnalysisResult(
                    TranscriptionModel: transcriptionModel,
                    AnalysisModel: effectiveAnalysisModel,
                    DurationSeconds: preparedMedia.DurationSeconds,
                    TranscriptText: string.Empty,
                    Summary: "A2 could not recover usable transcript text from this clip.",
                    TranscriptSegments: transcriptSegments,
                    Issues: [],
                    Message: "A2 found no spoken content to analyze.");
            }

            var transcriptAnalysis = await AnalyzeTranscriptContentAsync(
                preparedMedia.DurationSeconds,
                transcriptText,
                transcriptSegments,
                transcriptWords,
                effectiveApiKey,
                effectiveAnalysisModel,
                cancellationToken);
            var feedbackMemory = useMemory
                ? await _aiFeedbackStore.GetRememberedContentFeedbackAsync(
                    video.FileName,
                    transcriptText,
                    cancellationToken)
                : new AiContentFeedbackMemory([], [], 0);
            var transcriptChunks = await BuildTranscriptChunksAsync(
                preparedMedia.AudioPath,
                preparedMedia.DurationSeconds,
                effectiveApiKey,
                normalizedLanguage,
                cancellationToken);
            var chunkAnalysis = await AnalyzeChunkedTranscriptContentAsync(
                preparedMedia.DurationSeconds,
                transcriptText,
                transcriptChunks,
                effectiveApiKey,
                effectiveAnalysisModel,
                cancellationToken);
            var heuristicIssues = DetectChunkRetakeHeuristics(preparedMedia.DurationSeconds, transcriptChunks);
            var deliveryGapIssues = InferDeliveryGapIssues(
                preparedMedia.DurationSeconds,
                transcriptSegments,
                transcriptAnalysis.Issues
                    .Concat(chunkAnalysis.Issues)
                    .Concat(heuristicIssues)
                    .ToList());
            var detectedIssues = MergeDetectedIssues(
                transcriptAnalysis.Issues,
                chunkAnalysis.Issues,
                heuristicIssues,
                deliveryGapIssues);
            var filteredDetectedIssues = FilterSuppressedIssues(detectedIssues, feedbackMemory.SuppressedRanges);
            var combinedIssues = MergeDetectedIssues(
                filteredDetectedIssues,
                feedbackMemory.LearnedIssues);

            var message = combinedIssues.Count == 0
                ? "A2 found no obvious content mistakes."
                : $"A2 found {combinedIssues.Count} likely retake {(combinedIssues.Count == 1 ? "range" : "ranges")}.";

            return new AiContentAnalysisResult(
                TranscriptionModel: transcriptionModel,
                AnalysisModel: effectiveAnalysisModel,
                DurationSeconds: preparedMedia.DurationSeconds,
                TranscriptText: transcriptText,
                Summary: BuildCombinedSummary(
                    transcriptAnalysis.Summary,
                    chunkAnalysis.Summary,
                    combinedIssues,
                    deliveryGapIssues,
                    heuristicIssues,
                    transcriptChunks,
                    feedbackMemory,
                    useMemory),
                TranscriptSegments: transcriptSegments,
                Issues: combinedIssues,
                Message: message);
        }
        finally
        {
            CleanupPreparedMedia(preparedMedia);
        }
    }

    public async Task<AiAutoCutAnalysisResult> AnalyzeAutoCutAsync(
        IFormFile video,
        string apiKey,
        string analysisModel,
        string language,
        string? targetText,
        CancellationToken cancellationToken)
    {
        if (video.Length <= 0)
        {
            throw new InvalidOperationException("Choose a video file before running A3 analysis.");
        }

        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? _options.OpenAiApiKey
            : apiKey;
        var effectiveAnalysisModel = string.IsNullOrWhiteSpace(analysisModel)
            ? "gpt-5.2"
            : analysisModel;

        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new InvalidOperationException("Enter an OpenAI API key or set Strippr:OpenAiApiKey in appsettings.Local.json before running A3 analysis.");
        }

        if (!AllowedContentModels.Contains(effectiveAnalysisModel))
        {
            throw new InvalidOperationException("Choose a supported OpenAI analysis model. A3 currently supports gpt-5.2, gpt-5, gpt-5-mini, and gpt-4o-mini.");
        }

        var transcriptionModel = "whisper-1";
        var normalizedLanguage = NormalizeLanguage(language);
        var normalizedTargetText = NormalizeTranscriptWhitespace(targetText);
        var usedProvidedTargetText = !string.IsNullOrWhiteSpace(normalizedTargetText);
        var preparedMedia = await PrepareMediaAsync(video, cancellationToken);

        try
        {
            EnsureOpenAiAudioSize(preparedMedia.AudioPath, preparedMedia.DurationSeconds);

            var transcription = await TranscribeAsync(
                preparedMedia.AudioPath,
                effectiveApiKey,
                transcriptionModel,
                normalizedLanguage,
                includeWordTimestamps: true,
                cancellationToken);
            var transcriptSegments = BuildTranscriptSegments(preparedMedia.DurationSeconds, transcription);
            var transcriptWords = BuildTranscriptWords(preparedMedia.DurationSeconds, transcription);
            var transcriptText = !string.IsNullOrWhiteSpace(transcription.Text)
                ? transcription.Text.Trim()
                : string.Join(" ", transcriptSegments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

            if (string.IsNullOrWhiteSpace(transcriptText))
            {
                return new AiAutoCutAnalysisResult(
                    TranscriptionModel: transcriptionModel,
                    AnalysisModel: effectiveAnalysisModel,
                    DurationSeconds: preparedMedia.DurationSeconds,
                    TranscriptText: string.Empty,
                    TargetTranscript: string.Empty,
                    ResultTranscript: string.Empty,
                    Summary: "A3 could not recover usable transcript text from this clip.",
                    TranscriptSegments: transcriptSegments,
                    Issues: [],
                    Message: "A3 found no spoken content to analyze.");
            }

            var transcriptChunks = await BuildTranscriptChunksAsync(
                preparedMedia.AudioPath,
                preparedMedia.DurationSeconds,
                effectiveApiKey,
                normalizedLanguage,
                cancellationToken);

            var targetInference = !usedProvidedTargetText
                ? await InferTargetTranscriptAsync(
                    preparedMedia.DurationSeconds,
                    transcriptText,
                    transcriptSegments,
                    transcriptChunks,
                    effectiveApiKey,
                    effectiveAnalysisModel,
                    cancellationToken)
                : null;
            var effectiveTargetTranscript = !string.IsNullOrWhiteSpace(normalizedTargetText)
                ? normalizedTargetText
                : NormalizeTranscriptWhitespace(targetInference?.TargetTranscript) ?? transcriptText;

            var planAnalysis = await AnalyzeAutoCutPlanAsync(
                preparedMedia.DurationSeconds,
                transcriptText,
                effectiveTargetTranscript,
                usedProvidedTargetText,
                transcriptSegments,
                transcriptWords,
                transcriptChunks,
                effectiveApiKey,
                effectiveAnalysisModel,
                cancellationToken);
            var heuristicIssues = DetectChunkRetakeHeuristics(preparedMedia.DurationSeconds, transcriptChunks);
            var deliveryGapIssues = InferDeliveryGapIssues(
                preparedMedia.DurationSeconds,
                transcriptSegments,
                planAnalysis.Issues
                    .Concat(heuristicIssues)
                    .ToList());
            var combinedIssues = MergeDetectedIssues(
                planAnalysis.Issues,
                heuristicIssues,
                deliveryGapIssues);
            IReadOnlyList<AiContentIssue> finalIssues = combinedIssues;
            var resultTranscript = transcriptText;
            string? verificationSummary = null;
            string? targetCoverageSummary = null;
            var verificationChangedPlan = false;

            if (combinedIssues.Count > 0)
            {
                resultTranscript = await RenderAndTranscribeCutResultAsync(
                    preparedMedia.AudioPath,
                    preparedMedia.DurationSeconds,
                    combinedIssues,
                    effectiveApiKey,
                    normalizedLanguage,
                    cancellationToken);

                var targetCoverage = AnalyzeTargetCoverage(
                    effectiveTargetTranscript,
                    resultTranscript,
                    usedProvidedTargetText);
                targetCoverageSummary = targetCoverage.Summary;

                var verification = await VerifyAutoCutPlanAsync(
                    preparedMedia.DurationSeconds,
                    transcriptText,
                    effectiveTargetTranscript,
                    resultTranscript,
                    usedProvidedTargetText,
                    targetCoverage,
                    transcriptSegments,
                    transcriptWords,
                    transcriptChunks,
                    combinedIssues,
                    effectiveApiKey,
                    effectiveAnalysisModel,
                    cancellationToken);

                verificationSummary = verification.Summary;

                if (!verification.KeepCurrentPlan)
                {
                    finalIssues = verification.Issues;
                    verificationChangedPlan = !IssueSetsMatch(combinedIssues, finalIssues);

                    if (finalIssues.Count == 0)
                    {
                        resultTranscript = transcriptText;
                    }
                    else if (verificationChangedPlan)
                    {
                        resultTranscript = await RenderAndTranscribeCutResultAsync(
                            preparedMedia.AudioPath,
                            preparedMedia.DurationSeconds,
                            finalIssues,
                            effectiveApiKey,
                            normalizedLanguage,
                            cancellationToken);
                    }
                }

                if (usedProvidedTargetText)
                {
                    var finalCoverage = AnalyzeTargetCoverage(
                        effectiveTargetTranscript,
                        resultTranscript,
                        enforceStrict: true);
                    targetCoverageSummary = finalCoverage.Summary;

                    if (finalCoverage.IsHardFailure)
                    {
                        var restoredIssues = RestoreCutsForMissingTargetContent(
                            finalIssues,
                            transcriptSegments,
                            finalCoverage.MissingClauses,
                            finalCoverage.MissingTerms);

                        if (!IssueSetsMatch(finalIssues, restoredIssues))
                        {
                            finalIssues = restoredIssues;
                            verificationChangedPlan = true;
                            verificationSummary = string.IsNullOrWhiteSpace(verificationSummary)
                                ? "A3 restored cut ranges to preserve the provided target text."
                                : verificationSummary + " A3 restored cut ranges to preserve the provided target text.";
                            resultTranscript = finalIssues.Count == 0
                                ? transcriptText
                                : await RenderAndTranscribeCutResultAsync(
                                    preparedMedia.AudioPath,
                                    preparedMedia.DurationSeconds,
                                    finalIssues,
                                    effectiveApiKey,
                                    normalizedLanguage,
                                    cancellationToken);

                            finalCoverage = AnalyzeTargetCoverage(
                                effectiveTargetTranscript,
                                resultTranscript,
                                enforceStrict: true);
                            targetCoverageSummary = finalCoverage.Summary;
                        }

                        if (finalCoverage.IsHardFailure)
                        {
                            var safeGapIssues = KeepOnlyGapSafeIssues(finalIssues, transcriptSegments, transcriptWords);
                            if (!IssueSetsMatch(finalIssues, safeGapIssues))
                            {
                                finalIssues = safeGapIssues;
                                verificationChangedPlan = true;
                                verificationSummary = string.IsNullOrWhiteSpace(verificationSummary)
                                    ? "A3 fell back to pause-only safe cuts to avoid dropping target text."
                                    : verificationSummary + " A3 fell back to pause-only safe cuts to avoid dropping target text.";
                                resultTranscript = finalIssues.Count == 0
                                    ? transcriptText
                                    : await RenderAndTranscribeCutResultAsync(
                                        preparedMedia.AudioPath,
                                        preparedMedia.DurationSeconds,
                                        finalIssues,
                                        effectiveApiKey,
                                        normalizedLanguage,
                                        cancellationToken);

                                finalCoverage = AnalyzeTargetCoverage(
                                    effectiveTargetTranscript,
                                    resultTranscript,
                                    enforceStrict: true);
                                targetCoverageSummary = finalCoverage.Summary;
                            }
                        }

                        var gapSafeIssues = KeepOnlyGapSafeIssues(finalIssues, transcriptSegments, transcriptWords);
                        var gapSafeTimingChanged = !IssueSetsMatch(finalIssues, gapSafeIssues);
                        var gapSafeMetadataChanged = !IssueMetadataMatches(finalIssues, gapSafeIssues);
                        finalIssues = gapSafeIssues;

                        if (gapSafeTimingChanged)
                        {
                            verificationChangedPlan = true;
                            verificationSummary = string.IsNullOrWhiteSpace(verificationSummary)
                                ? "A3 kept only gap-safe cleanup cuts because a user-provided target text is active."
                                : verificationSummary + " A3 kept only gap-safe cleanup cuts because a user-provided target text is active.";
                            resultTranscript = finalIssues.Count == 0
                                ? transcriptText
                                : await RenderAndTranscribeCutResultAsync(
                                    preparedMedia.AudioPath,
                                    preparedMedia.DurationSeconds,
                                    finalIssues,
                                    effectiveApiKey,
                                    normalizedLanguage,
                                    cancellationToken);

                            finalCoverage = AnalyzeTargetCoverage(
                                effectiveTargetTranscript,
                                resultTranscript,
                                enforceStrict: true);
                            targetCoverageSummary = finalCoverage.Summary;
                        }
                        else if (gapSafeMetadataChanged)
                        {
                            verificationSummary = string.IsNullOrWhiteSpace(verificationSummary)
                                ? "A3 relabeled the remaining target-safe cuts as gap cleanup."
                                : verificationSummary + " A3 relabeled the remaining target-safe cuts as gap cleanup.";
                        }
                    }
                }
            }
            else if (usedProvidedTargetText)
            {
                targetCoverageSummary = AnalyzeTargetCoverage(
                    effectiveTargetTranscript,
                    transcriptText,
                    enforceStrict: true).Summary;
            }

            var message = finalIssues.Count == 0
                ? "A3 found no combined cut ranges."
                : $"A3 found {finalIssues.Count} combined cut {(finalIssues.Count == 1 ? "range" : "ranges")}.";

            return new AiAutoCutAnalysisResult(
                TranscriptionModel: transcriptionModel,
                AnalysisModel: effectiveAnalysisModel,
                DurationSeconds: preparedMedia.DurationSeconds,
                TranscriptText: transcriptText,
                TargetTranscript: effectiveTargetTranscript,
                ResultTranscript: resultTranscript,
                Summary: BuildAutoCutSummary(
                    targetInference?.Summary,
                    usedProvidedTargetText,
                    planAnalysis.Summary,
                    verificationSummary,
                    targetCoverageSummary,
                    verificationChangedPlan,
                    heuristicIssues,
                    deliveryGapIssues,
                    finalIssues,
                    transcriptChunks),
                TranscriptSegments: transcriptSegments,
                Issues: finalIssues,
                Message: message);
        }
        finally
        {
            CleanupPreparedMedia(preparedMedia);
        }
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

    private async Task<PreparedMedia> PrepareMediaAsync(
        IFormFile video,
        CancellationToken cancellationToken)
    {
        var originalExtension = Path.GetExtension(video.FileName);
        var uploadPath = _workspaceService.CreateTempPath(Path.GetFileNameWithoutExtension(video.FileName), originalExtension);
        var audioPath = _workspaceService.CreateTempPath(Path.GetFileNameWithoutExtension(video.FileName) + "-ai", ".mp3");

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

        return new PreparedMedia(
            DurationSeconds: durationSeconds,
            UploadPath: uploadPath,
            AudioPath: audioPath);
    }

    private static void EnsureOpenAiAudioSize(string audioPath, double durationSeconds)
    {
        var audioInfo = new FileInfo(audioPath);
        if (!audioInfo.Exists)
        {
            throw new InvalidOperationException("The temporary AI audio file could not be created.");
        }

        if (audioInfo.Length <= MaxOpenAiAudioBytes)
        {
            return;
        }

        var maximumMinutes = TimeSpan.FromSeconds(durationSeconds).TotalMinutes;
        throw new InvalidOperationException(
            $"The extracted AI audio is {audioInfo.Length / (1024d * 1024d):0.0} MB, which exceeds OpenAI's 25 MB upload limit. " +
            $"Try a shorter clip. Current duration: {maximumMinutes:0.#} minutes.");
    }

    private void CleanupPreparedMedia(PreparedMedia preparedMedia)
    {
        TryDeleteTempFile(preparedMedia.UploadPath);
        TryDeleteTempFile(preparedMedia.AudioPath);
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
            "Return a confidence value from 0 to 1 for each issue. " +
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
                                        excerpt = new { type = "string" },
                                        confidence = new { type = "number" }
                                    },
                                    required = new[] { "startSeconds", "endSeconds", "label", "reason", "excerpt", "confidence" }
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
                Label: NormalizeIssueLabel(issue.Label, issue.Reason),
                Reason: string.IsNullOrWhiteSpace(issue.Reason) ? "Potential spoken mistake." : issue.Reason.Trim(),
                Excerpt: string.IsNullOrWhiteSpace(issue.Excerpt) ? string.Empty : issue.Excerpt.Trim(),
                Confidence: Math.Clamp(double.IsFinite(issue.Confidence) ? issue.Confidence : 0.5, 0, 1)))
            .Where(issue => issue.EndSeconds > issue.StartSeconds)
            .ToList()
            ?? [];

        return new OpenAiContentResponse(
            Summary: analysis.Summary,
            Issues: normalizedIssues);
    }

    private async Task<IReadOnlyList<AiTranscriptChunk>> BuildTranscriptChunksAsync(
        string audioPath,
        double durationSeconds,
        string apiKey,
        string? language,
        CancellationToken cancellationToken)
    {
        var mediaAnalysis = await _ffmpegService.AnalyzeSilenceAsync(
            audioPath,
            ChunkSilenceNoiseThreshold,
            ChunkSilenceMinimumSeconds,
            cancellationToken);
        var rawChunks = BuildSpeechChunks(durationSeconds, mediaAnalysis.SilenceIntervals);
        var candidateChunks = TrimChunkCount(rawChunks);

        if (candidateChunks.Count == 0)
        {
            return [];
        }

        var transcriptChunks = new List<AiTranscriptChunk>(candidateChunks.Count);

        foreach (var chunk in candidateChunks)
        {
            var segmentAudioPath = _workspaceService.CreateTempPath("a2-chunk", ".mp3");
            try
            {
                await _ffmpegService.ExtractAudioSegmentAsync(
                    audioPath,
                    segmentAudioPath,
                    chunk.StartSeconds,
                    chunk.EndSeconds,
                    cancellationToken);

                var segmentInfo = new FileInfo(segmentAudioPath);
                if (!segmentInfo.Exists || segmentInfo.Length == 0 || segmentInfo.Length > MaxOpenAiAudioBytes)
                {
                    continue;
                }

                var transcription = await TranscribeAsync(
                    segmentAudioPath,
                    apiKey,
                    "whisper-1",
                    language,
                    includeWordTimestamps: false,
                    cancellationToken);
                var transcriptText = !string.IsNullOrWhiteSpace(transcription.Text)
                    ? transcription.Text.Trim()
                    : string.Join(
                        " ",
                        transcription.Segments?
                            .Select(segment => segment.Text?.Trim())
                            .Where(text => !string.IsNullOrWhiteSpace(text))
                        ?? []);

                if (string.IsNullOrWhiteSpace(transcriptText))
                {
                    continue;
                }

                transcriptChunks.Add(new AiTranscriptChunk(
                    StartSeconds: chunk.StartSeconds,
                    EndSeconds: chunk.EndSeconds,
                    GapBeforeSeconds: chunk.GapBeforeSeconds,
                    GapAfterSeconds: chunk.GapAfterSeconds,
                    Text: transcriptText));
            }
            finally
            {
                TryDeleteTempFile(segmentAudioPath);
            }
        }

        return transcriptChunks;
    }

    private async Task<OpenAiContentResponse> AnalyzeChunkedTranscriptContentAsync(
        double durationSeconds,
        string transcriptText,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks,
        string apiKey,
        string analysisModel,
        CancellationToken cancellationToken)
    {
        if (transcriptChunks.Count < 2)
        {
            return new OpenAiContentResponse(
                Summary: "Chunk analysis found too little segmented speech to reason about retakes.",
                Issues: []);
        }

        const string systemPrompt =
            "You identify bad spoken takes for local video editing from pause-separated speech chunks. " +
            "The full transcript may be too clean because transcription can collapse rough takes, so pay close attention to the raw chunk sequence. " +
            "Flag earlier chunks that look like false starts, restarted lines, discarded reads, repeated openings, abandoned attempts, or nearby alternate takes. " +
            "Prefer keeping the latest valid nearby attempt and cutting the earlier bad take. " +
            "Be willing to flag delivery mistakes even when the final words are correct, if the chunk sequence shows the speaker clearly restarted or replaced a read. " +
            "Use chunk ranges directly unless a smaller range is clearly justified. " +
            "Do not flag normal pacing or a single fluent read. Return strict JSON only.";

        var userPrompt =
            $"Clip duration: {durationSeconds:0.###} seconds.\n" +
            $"Full transcript:\n{transcriptText}\n\n" +
            "Pause-separated speech chunks (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptChunks, JsonOptions)}\n\n" +
            "Look for nearby chunks that represent multiple attempts at the same line or a bad read followed by a better retry. " +
            "Return the earlier chunks that should be cut. If nothing looks like a bad take, return an empty issues array.";

        return await AnalyzeStructuredContentAsync(
            durationSeconds,
            apiKey,
            analysisModel,
            systemPrompt,
            userPrompt,
            cancellationToken);
    }

    private async Task<OpenAiTargetTranscriptResponse> InferTargetTranscriptAsync(
        double durationSeconds,
        string transcriptText,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks,
        string apiKey,
        string analysisModel,
        CancellationToken cancellationToken)
    {
        const string systemPrompt =
            "You infer the intended clean final read for a spoken clip used in local video editing. " +
            "The clip may contain false starts, repeated attempts, abandoned phrases, long pauses, and corrected lines. " +
            "Reconstruct the single clean target transcript the speaker most likely meant to keep. " +
            "Keep the later valid wording when there are alternate takes. Do not include notes, alternatives, or stage directions. " +
            "Return strict JSON only.";

        var userPrompt =
            $"Clip duration: {durationSeconds:0.###} seconds.\n" +
            $"Full transcript:\n{transcriptText}\n\n" +
            "Timestamped transcript segments (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptSegments, JsonOptions)}\n\n" +
            "Pause-separated speech chunks (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptChunks, JsonOptions)}\n\n" +
            "Infer the clean final read the editor should aim to keep.";

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
                    name = "strippr_target_transcript",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            summary = new { type = "string" },
                            targetTranscript = new { type = "string" }
                        },
                        required = new[] { "summary", "targetTranscript" }
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
            throw new InvalidOperationException("OpenAI returned an unreadable A3 target transcript response.");
        }

        var analysis = JsonSerializer.Deserialize<OpenAiTargetTranscriptPayload>(responseText, JsonOptions);
        if (analysis is null)
        {
            throw new InvalidOperationException("OpenAI returned invalid structured JSON for A3 target transcript inference.");
        }

        var targetTranscript = NormalizeTranscriptWhitespace(analysis.TargetTranscript);
        if (string.IsNullOrWhiteSpace(targetTranscript))
        {
            throw new InvalidOperationException("OpenAI could not infer a usable A3 target transcript.");
        }

        return new OpenAiTargetTranscriptResponse(
            Summary: string.IsNullOrWhiteSpace(analysis.Summary)
                ? "A3 inferred a clean target transcript from the clip."
                : analysis.Summary.Trim(),
            TargetTranscript: targetTranscript);
    }

    private async Task<OpenAiContentResponse> AnalyzeAutoCutPlanAsync(
        double durationSeconds,
        string transcriptText,
        string targetTranscript,
        bool usedProvidedTargetText,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiTranscriptWord> transcriptWords,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks,
        string apiKey,
        string analysisModel,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "You build a combined keep-cut plan for spoken video editing. " +
            "Goal: make the remaining audio sound like a single clean read of the target transcript. " +
            "Use the full transcript, word timings, transcript segments, and pause-separated speech chunks together. " +
            "Prefer the latest clean take when nearby chunks represent multiple attempts at the same phrase. " +
            "Cut superseded attempts, abandoned reads, false starts, filler bridges, and long pauses that delay the next needed words. " +
            "Keep natural short breaths and the final chosen take. " +
            (usedProvidedTargetText
                ? "The target transcript was provided by the user and is required content. Do not drop, summarize, or paraphrase any target clause. Prefer leaving an imperfect pause over removing target words. "
                : "Preserve the full meaning of the target transcript and prefer later clean takes when the clip contains retries. ") +
            "Return the smallest useful cut ranges only, with confidence and short reasons. " +
            "Return strict JSON only.";

        var userPrompt =
            $"Clip duration: {durationSeconds:0.###} seconds.\n" +
            $"Target transcript:\n{targetTranscript}\n\n" +
            (usedProvidedTargetText
                ? "The target transcript above is authoritative and should still be fully present after cutting.\n\n"
                : string.Empty) +
            $"Full transcript:\n{transcriptText}\n\n" +
            "Timestamped transcript segments (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptSegments, JsonOptions)}\n\n" +
            "Timestamped transcript words (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptWords, JsonOptions)}\n\n" +
            "Pause-separated speech chunks (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptChunks, JsonOptions)}\n\n" +
            "Return the cut ranges that should be removed so the remaining audio reads as closely as possible to the target transcript.";

        return await AnalyzeStructuredContentAsync(
            durationSeconds,
            apiKey,
            analysisModel,
            systemPrompt,
            userPrompt,
            cancellationToken);
    }

    private async Task<OpenAiAutoCutVerificationResponse> VerifyAutoCutPlanAsync(
        double durationSeconds,
        string transcriptText,
        string targetTranscript,
        string resultTranscript,
        bool usedProvidedTargetText,
        TargetCoverageReport targetCoverage,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiTranscriptWord> transcriptWords,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks,
        IReadOnlyList<AiContentIssue> currentIssues,
        string apiKey,
        string analysisModel,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "You verify and correct a source-time cut plan for spoken video editing. " +
            "Goal: the rendered result should sound like a single clean read of the target transcript. " +
            "You are given the target transcript, the original transcript with source timings, the current source-time cut ranges, and the transcript of the rendered post-cut result. " +
            (usedProvidedTargetText
                ? "The target transcript was provided by the user and is authoritative. Missing target clauses are not acceptable. If the rendered result drops target content, keepCurrentPlan must be false and you must restore that content by shrinking or removing cuts. "
                : "If the current plan already produces a good result close to the target transcript, keep the plan. ") +
            "If the result still contains repeats, wrong takes, abandoned reads, or long disruptive pauses, revise the final source-time cut ranges. " +
            "If the result is missing necessary words, restore them by shrinking or removing earlier cuts. " +
            "Always return final source-time cut ranges, not delta edits. Return strict JSON only.";

        var userPrompt =
            $"Clip duration: {durationSeconds:0.###} seconds.\n" +
            $"Target transcript:\n{targetTranscript}\n\n" +
            $"Original full transcript:\n{transcriptText}\n\n" +
            $"Rendered result transcript after current cuts:\n{resultTranscript}\n\n" +
            $"Rendered-result target coverage summary:\n{targetCoverage.Summary}\n\n" +
            (targetCoverage.MissingClauses.Count > 0
                ? $"Missing target clauses right now (JSON):\n{JsonSerializer.Serialize(targetCoverage.MissingClauses, JsonOptions)}\n\n"
                : string.Empty) +
            (targetCoverage.MissingTerms.Count > 0
                ? $"Missing key target terms right now (JSON):\n{JsonSerializer.Serialize(targetCoverage.MissingTerms, JsonOptions)}\n\n"
                : string.Empty) +
            "Current source-time cut ranges (JSON):\n" +
            $"{JsonSerializer.Serialize(currentIssues, JsonOptions)}\n\n" +
            "Timestamped transcript segments (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptSegments, JsonOptions)}\n\n" +
            "Timestamped transcript words (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptWords, JsonOptions)}\n\n" +
            "Pause-separated speech chunks (JSON):\n" +
            $"{JsonSerializer.Serialize(transcriptChunks, JsonOptions)}\n\n" +
            "Decide whether to keep the current plan. If not, return the revised final source-time cut ranges.";

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
                    name = "strippr_auto_cut_verification",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            summary = new { type = "string" },
                            keepCurrentPlan = new { type = "boolean" },
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
                                        excerpt = new { type = "string" },
                                        confidence = new { type = "number" }
                                    },
                                    required = new[] { "startSeconds", "endSeconds", "label", "reason", "excerpt", "confidence" }
                                }
                            }
                        },
                        required = new[] { "summary", "keepCurrentPlan", "issues" }
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
            throw new InvalidOperationException("OpenAI returned an unreadable A3 verification response.");
        }

        var analysis = JsonSerializer.Deserialize<OpenAiAutoCutVerificationPayload>(responseText, JsonOptions);
        if (analysis is null)
        {
            throw new InvalidOperationException("OpenAI returned invalid structured JSON for A3 verification.");
        }

        var normalizedIssues = analysis.Issues?
            .Where(issue =>
                issue is not null &&
                double.IsFinite(issue.StartSeconds) &&
                double.IsFinite(issue.EndSeconds))
            .Select(issue => new AiContentIssue(
                StartSeconds: Math.Clamp(issue.StartSeconds, 0, durationSeconds),
                EndSeconds: Math.Clamp(issue.EndSeconds, 0, durationSeconds),
                Label: NormalizeIssueLabel(issue.Label, issue.Reason),
                Reason: string.IsNullOrWhiteSpace(issue.Reason) ? "Potential spoken mistake." : issue.Reason.Trim(),
                Excerpt: string.IsNullOrWhiteSpace(issue.Excerpt) ? string.Empty : issue.Excerpt.Trim(),
                Confidence: Math.Clamp(double.IsFinite(issue.Confidence) ? issue.Confidence : 0.5, 0, 1)))
            .Where(issue => issue.EndSeconds > issue.StartSeconds)
            .ToList()
            ?? [];

        return new OpenAiAutoCutVerificationResponse(
            Summary: string.IsNullOrWhiteSpace(analysis.Summary)
                ? "A3 verified the current cut plan."
                : analysis.Summary.Trim(),
            KeepCurrentPlan: analysis.KeepCurrentPlan,
            Issues: normalizedIssues);
    }

    private async Task<string> RenderAndTranscribeCutResultAsync(
        string audioPath,
        double durationSeconds,
        IReadOnlyList<AiContentIssue> issues,
        string apiKey,
        string? language,
        CancellationToken cancellationToken)
    {
        if (issues.Count == 0)
        {
            return string.Empty;
        }

        var renderSegments = _cutPlanBuilder.BuildRenderSegments(
            durationSeconds,
            [],
            issues.Select(issue => new SilenceInterval(issue.StartSeconds, issue.EndSeconds)).ToList(),
            minimumKeepSegmentSeconds: 0.15,
            pauseSpeedMultiplier: 1,
            retainedSilenceSeconds: 0);

        if (renderSegments.Count == 0)
        {
            return string.Empty;
        }

        var previewPath = _workspaceService.CreateTempPath("a3-preview", ".mp3");

        try
        {
            await _ffmpegService.RenderAudioPreviewAsync(
                audioPath,
                previewPath,
                renderSegments,
                crossfadeMilliseconds: 0,
                cancellationToken);

            var previewDurationSeconds = Math.Max(0.1, renderSegments.Sum(segment => segment.OutputDurationSeconds));
            EnsureOpenAiAudioSize(previewPath, previewDurationSeconds);

            var previewTranscription = await TranscribeAsync(
                previewPath,
                apiKey,
                "whisper-1",
                language,
                includeWordTimestamps: false,
                cancellationToken);

            return NormalizeTranscriptWhitespace(previewTranscription.Text)
                ?? NormalizeTranscriptWhitespace(string.Join(
                    " ",
                    previewTranscription.Segments?
                        .Select(segment => segment.Text?.Trim())
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                    ?? []))
                ?? string.Empty;
        }
        finally
        {
            TryDeleteTempFile(previewPath);
        }
    }

    private async Task<OpenAiContentResponse> AnalyzeStructuredContentAsync(
        double durationSeconds,
        string apiKey,
        string analysisModel,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
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
                                        excerpt = new { type = "string" },
                                        confidence = new { type = "number" }
                                    },
                                    required = new[] { "startSeconds", "endSeconds", "label", "reason", "excerpt", "confidence" }
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
                Label: NormalizeIssueLabel(issue.Label, issue.Reason),
                Reason: string.IsNullOrWhiteSpace(issue.Reason) ? "Potential spoken mistake." : issue.Reason.Trim(),
                Excerpt: string.IsNullOrWhiteSpace(issue.Excerpt) ? string.Empty : issue.Excerpt.Trim(),
                Confidence: Math.Clamp(double.IsFinite(issue.Confidence) ? issue.Confidence : 0.5, 0, 1)))
            .Where(issue => issue.EndSeconds > issue.StartSeconds)
            .ToList()
            ?? [];

        return new OpenAiContentResponse(
            Summary: analysis.Summary,
            Issues: normalizedIssues);
    }

    private static IReadOnlyList<AiContentIssue> DetectChunkRetakeHeuristics(
        double durationSeconds,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks)
    {
        if (transcriptChunks.Count < 2)
        {
            return [];
        }

        var detectedIssues = new List<AiContentIssue>();

        for (var index = 0; index < transcriptChunks.Count - 1; index += 1)
        {
            var currentChunk = transcriptChunks[index];
            var nextChunk = transcriptChunks[index + 1];
            var gapBetweenChunks = Math.Max(0, nextChunk.StartSeconds - currentChunk.EndSeconds);
            if (gapBetweenChunks > 3.5)
            {
                continue;
            }

            var currentTokens = Tokenize(currentChunk.Text);
            var nextTokens = Tokenize(nextChunk.Text);
            if (currentTokens.Count < 2 || nextTokens.Count < 2)
            {
                continue;
            }

            var sharedPrefixLength = CountSharedPrefix(currentTokens, nextTokens);
            var similarity = CalculateTokenSimilarity(currentTokens, nextTokens);
            var currentIsShort = currentTokens.Count <= 8;
            var nextLooksLikeRetry = sharedPrefixLength >= 2 && nextTokens.Count >= currentTokens.Count;

            if (!nextLooksLikeRetry && !(sharedPrefixLength >= 1 && currentIsShort && similarity >= 0.45) && similarity < 0.62)
            {
                continue;
            }

            var candidate = new AiContentIssue(
                StartSeconds: Math.Max(0, currentChunk.StartSeconds),
                EndSeconds: Math.Min(durationSeconds, nextChunk.StartSeconds),
                Label: "Restart",
                Reason: "Nearby speech chunks restart with overlapping wording, which looks like an earlier bad take followed by a better retry.",
                Excerpt: $"{currentChunk.Text} / {nextChunk.Text}",
                Confidence: CalculateChunkRetakeConfidence(sharedPrefixLength, similarity, currentIsShort));

            if (candidate.EndSeconds <= candidate.StartSeconds)
            {
                continue;
            }

            if (OverlapsExistingIssue(candidate, detectedIssues, []))
            {
                continue;
            }

            detectedIssues.Add(candidate);
        }

        return detectedIssues;
    }

    private static List<AiContentIssue> MergeDetectedIssues(params IReadOnlyList<AiContentIssue>[] issueSets)
    {
        var mergedIssues = new List<AiContentIssue>();

        foreach (var issue in issueSets
                     .SelectMany(set => set)
                     .OrderByDescending(issue => issue.IsLearned)
                     .ThenByDescending(issue => issue.Confidence)
                     .ThenBy(issue => issue.StartSeconds)
                     .ThenBy(issue => issue.EndSeconds))
        {
            if (mergedIssues.Any(existingIssue => OverlapsMeaningfully(existingIssue, issue)))
            {
                continue;
            }

            mergedIssues.Add(issue);
        }

        return mergedIssues
            .OrderBy(issue => issue.StartSeconds)
            .ThenBy(issue => issue.EndSeconds)
            .ToList();
    }

    private static List<AiContentIssue> FilterSuppressedIssues(
        IReadOnlyList<AiContentIssue> issues,
        IReadOnlyList<AiContentFeedbackRange> suppressedRanges)
    {
        if (issues.Count == 0 || suppressedRanges.Count == 0)
        {
            return issues.ToList();
        }

        return issues
            .Where(issue => !suppressedRanges.Any(range => OverlapsMeaningfully(issue, range)))
            .ToList();
    }

    private static List<SpeechChunk> BuildSpeechChunks(
        double durationSeconds,
        IReadOnlyList<SilenceInterval> silenceIntervals)
    {
        var chunks = new List<SpeechChunk>();
        var orderedSilences = silenceIntervals
            .Where(interval => interval is not null && interval.EndSeconds > interval.StartSeconds)
            .OrderBy(interval => interval.StartSeconds)
            .ToList();

        var cursor = 0d;
        double? previousSilenceEnd = null;

        foreach (var silence in orderedSilences)
        {
            var speechEnd = Math.Clamp(silence.StartSeconds, 0, durationSeconds);
            if (speechEnd - cursor >= MinimumChunkDurationSeconds)
            {
                chunks.Add(new SpeechChunk(
                    StartSeconds: cursor,
                    EndSeconds: speechEnd,
                    GapBeforeSeconds: previousSilenceEnd is null ? 0 : Math.Max(0, cursor - previousSilenceEnd.Value),
                    GapAfterSeconds: Math.Max(0, silence.EndSeconds - silence.StartSeconds)));
            }

            previousSilenceEnd = silence.EndSeconds;
            cursor = Math.Clamp(silence.EndSeconds, 0, durationSeconds);
        }

        if (durationSeconds - cursor >= MinimumChunkDurationSeconds)
        {
            chunks.Add(new SpeechChunk(
                StartSeconds: cursor,
                EndSeconds: durationSeconds,
                GapBeforeSeconds: previousSilenceEnd is null ? 0 : Math.Max(0, cursor - previousSilenceEnd.Value),
                GapAfterSeconds: 0));
        }

        return chunks
            .Where(chunk => chunk.DurationSeconds >= MinimumChunkDurationSeconds && chunk.DurationSeconds <= MaximumChunkDurationSeconds)
            .ToList();
    }

    private static List<SpeechChunk> TrimChunkCount(List<SpeechChunk> chunks)
    {
        if (chunks.Count <= MaximumChunkTranscriptions)
        {
            return chunks;
        }

        return chunks
            .OrderByDescending(chunk => chunk.GapBeforeSeconds + chunk.GapAfterSeconds)
            .ThenByDescending(chunk => chunk.DurationSeconds)
            .Take(MaximumChunkTranscriptions)
            .OrderBy(chunk => chunk.StartSeconds)
            .ToList();
    }

    private static List<string> Tokenize(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int CountSharedPrefix(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var maxLength = Math.Min(left.Count, right.Count);
        var count = 0;

        while (count < maxLength && string.Equals(left[count], right[count], StringComparison.Ordinal))
        {
            count += 1;
        }

        return count;
    }

    private static double CalculateTokenSimilarity(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftSet = left.ToHashSet(StringComparer.Ordinal);
        var rightSet = right.ToHashSet(StringComparer.Ordinal);
        var unionCount = leftSet.Union(rightSet, StringComparer.Ordinal).Count();
        if (unionCount == 0)
        {
            return 0;
        }

        var intersectionCount = leftSet.Intersect(rightSet, StringComparer.Ordinal).Count();
        return (double)intersectionCount / unionCount;
    }

    private static double CalculateChunkRetakeConfidence(int sharedPrefixLength, double similarity, bool currentIsShort)
    {
        var score = 0.52 + Math.Min(0.28, sharedPrefixLength * 0.08) + Math.Min(0.16, similarity * 0.18);
        if (currentIsShort)
        {
            score += 0.06;
        }

        return Math.Round(Math.Clamp(score, 0, 0.94), 3);
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

    private static IReadOnlyList<AiContentIssue> InferDeliveryGapIssues(
        double durationSeconds,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiContentIssue> existingIssues)
    {
        if (transcriptSegments.Count < 2)
        {
            return [];
        }

        const double minimumGapSeconds = 0.9;
        var detectedIssues = new List<AiContentIssue>();

        for (var index = 0; index < transcriptSegments.Count - 1; index += 1)
        {
            var currentSegment = transcriptSegments[index];
            var nextSegment = transcriptSegments[index + 1];
            var gapStart = currentSegment.EndSeconds;
            var gapEnd = nextSegment.StartSeconds;
            var gapDuration = gapEnd - gapStart;

            if (gapDuration < minimumGapSeconds)
            {
                continue;
            }

            if (EndsSentence(currentSegment.Text))
            {
                continue;
            }

            var candidate = new AiContentIssue(
                StartSeconds: Math.Clamp(gapStart, 0, durationSeconds),
                EndSeconds: Math.Clamp(gapEnd, 0, durationSeconds),
                Label: "Long pause",
                Reason: "The transcript continues after a long silence without finishing the sentence, so this gap is a likely delivery stumble or restart pause.",
                Excerpt: $"{currentSegment.Text.Trim()} ... {nextSegment.Text.Trim()}",
                Confidence: CalculateDeliveryGapConfidence(gapDuration));

            if (candidate.EndSeconds <= candidate.StartSeconds)
            {
                continue;
            }

            if (OverlapsExistingIssue(candidate, existingIssues, detectedIssues))
            {
                continue;
            }

            detectedIssues.Add(candidate);
        }

        return detectedIssues;
    }

    private static bool EndsSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var lastCharacter = trimmed[^1];
        return lastCharacter is '.' or '!' or '?' or '…';
    }

    private static string NormalizeIssueLabel(string? label, string? reason)
    {
        var rawLabel = string.IsNullOrWhiteSpace(label)
            ? "Retake"
            : label.Trim();
        var searchText = $"{rawLabel} {reason ?? string.Empty}".ToLowerInvariant();

        if (ContainsAny(searchText, "false start", "lead-in", "lead in", "filler")) return "False start";
        if (ContainsAny(searchText, "garble", "garbled", "mumble", "stumble")) return "Garble";
        if (ContainsAny(searchText, "abandoned", "discarded", "stray")) return "Abandoned read";
        if (ContainsAny(searchText, "pause", "delivery gap")) return "Long pause";
        if (ContainsAny(searchText, "correction", "self-correction")) return "Correction";
        if (ContainsAny(searchText, "restart", "restarted", "retake", "retry", "repeated")) return "Restart";
        if (ContainsAny(searchText, "wrong word", "wrong phrase", "wrong line", "wrong sentence", "wrong closing", "incorrect", "misread")) return "Wrong line";
        if (ContainsAny(searchText, "bad take")) return "Retake";

        return ToDisplayLabel(rawLabel);
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string ToDisplayLabel(string value)
    {
        var tokens = value
            .Replace("/", " ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return "Retake";
        }

        return string.Join(
            " ",
            tokens.Select(token =>
                token.Length switch
                {
                    0 => string.Empty,
                    1 => token.ToUpperInvariant(),
                    _ => char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant()
                }));
    }

    private static bool OverlapsExistingIssue(
        AiContentIssue candidate,
        IReadOnlyList<AiContentIssue> existingIssues,
        IReadOnlyList<AiContentIssue> detectedIssues)
    {
        static bool HasOverlap(AiContentIssue left, AiContentIssue right) =>
            left.StartSeconds < right.EndSeconds && right.StartSeconds < left.EndSeconds;

        return existingIssues.Any(issue => HasOverlap(candidate, issue)) ||
               detectedIssues.Any(issue => HasOverlap(candidate, issue));
    }

    private static bool OverlapsMeaningfully(AiContentIssue issue, AiContentIssue otherIssue)
    {
        var overlap = Math.Min(issue.EndSeconds, otherIssue.EndSeconds) - Math.Max(issue.StartSeconds, otherIssue.StartSeconds);
        if (overlap <= 0)
        {
            return false;
        }

        var issueDuration = Math.Max(0.01, issue.EndSeconds - issue.StartSeconds);
        var otherDuration = Math.Max(0.01, otherIssue.EndSeconds - otherIssue.StartSeconds);
        var overlapRatio = overlap / Math.Min(issueDuration, otherDuration);

        return overlapRatio >= 0.45 ||
               (Math.Abs(issue.StartSeconds - otherIssue.StartSeconds) <= 0.35 &&
                Math.Abs(issue.EndSeconds - otherIssue.EndSeconds) <= 0.35);
    }

    private static bool OverlapsMeaningfully(AiContentIssue issue, AiContentFeedbackRange range)
    {
        var overlap = Math.Min(issue.EndSeconds, range.EndSeconds) - Math.Max(issue.StartSeconds, range.StartSeconds);
        if (overlap <= 0)
        {
            return false;
        }

        var issueDuration = Math.Max(0.01, issue.EndSeconds - issue.StartSeconds);
        var rangeDuration = Math.Max(0.01, range.EndSeconds - range.StartSeconds);
        var overlapRatio = overlap / Math.Min(issueDuration, rangeDuration);

        return overlapRatio >= 0.45 ||
               (Math.Abs(issue.StartSeconds - range.StartSeconds) <= 0.35 &&
                Math.Abs(issue.EndSeconds - range.EndSeconds) <= 0.35);
    }

    private static double CalculateDeliveryGapConfidence(double gapDuration)
    {
        var normalized = Math.Clamp((gapDuration - 0.9) / 4.0, 0, 1);
        return Math.Round(0.58 + (normalized * 0.32), 3);
    }

    private static string BuildCombinedSummary(
        string? transcriptSummary,
        string? chunkSummary,
        IReadOnlyList<AiContentIssue> combinedIssues,
        IReadOnlyList<AiContentIssue> deliveryGapIssues,
        IReadOnlyList<AiContentIssue> heuristicIssues,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks,
        AiContentFeedbackMemory feedbackMemory,
        bool useMemory)
    {
        var summaryParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(chunkSummary))
        {
            summaryParts.Add(chunkSummary.Trim());
        }

        if ((combinedIssues.Count == 0 || string.IsNullOrWhiteSpace(chunkSummary)) &&
            !string.IsNullOrWhiteSpace(transcriptSummary) &&
            !string.Equals(transcriptSummary.Trim(), chunkSummary?.Trim(), StringComparison.Ordinal))
        {
            summaryParts.Add(transcriptSummary.Trim());
        }

        if (heuristicIssues.Count > 0)
        {
            summaryParts.Add(
                heuristicIssues.Count == 1
                    ? "A2 heuristic pass found 1 restarted-line candidate from repeated chunk wording."
                    : $"A2 heuristic pass found {heuristicIssues.Count} restarted-line candidates from repeated chunk wording.");
        }

        if (transcriptChunks.Count > 0)
        {
            summaryParts.Add(
                transcriptChunks.Count == 1
                    ? "A2 segmented the audio into 1 speech chunk."
                    : $"A2 segmented the audio into {transcriptChunks.Count} speech chunks.");
        }

        if (deliveryGapIssues.Count > 0)
        {
            summaryParts.Add(
                deliveryGapIssues.Count == 1
                    ? "A2 also found 1 long mid-sentence delivery gap."
                    : $"A2 also found {deliveryGapIssues.Count} long mid-sentence delivery gaps.");
        }

        if (!useMemory)
        {
            summaryParts.Add("A2 ran in fresh mode with learned clip memory off.");
        }
        else if (feedbackMemory.MatchedFeedbackCount > 0)
        {
            summaryParts.Add(
                feedbackMemory.LearnedIssues.Count > 0 || feedbackMemory.SuppressedRanges.Count > 0
                    ? $"A2 also reused {feedbackMemory.MatchedFeedbackCount} learned feedback record{(feedbackMemory.MatchedFeedbackCount == 1 ? string.Empty : "s")} for this clip."
                    : $"A2 matched {feedbackMemory.MatchedFeedbackCount} older feedback record{(feedbackMemory.MatchedFeedbackCount == 1 ? string.Empty : "s")} but found no reusable learned cuts.");
        }

        if (combinedIssues.Count == 0)
        {
            summaryParts.Add("No clear bad takes were detected from the current chunk/transcript analysis.");
            return string.Join(" ", summaryParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        summaryParts.Add(
            combinedIssues.Count == 1
                ? "A2 found 1 likely bad-take range."
                : $"A2 found {combinedIssues.Count} likely bad-take ranges.");
        return string.Join(" ", summaryParts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildAutoCutSummary(
        string? targetSummary,
        bool usedProvidedTargetText,
        string? planSummary,
        string? verificationSummary,
        string? targetCoverageSummary,
        bool verificationChangedPlan,
        IReadOnlyList<AiContentIssue> heuristicIssues,
        IReadOnlyList<AiContentIssue> deliveryGapIssues,
        IReadOnlyList<AiContentIssue> combinedIssues,
        IReadOnlyList<AiTranscriptChunk> transcriptChunks)
    {
        var summaryParts = new List<string>();

        summaryParts.Add(
            usedProvidedTargetText
                ? "A3 used the provided target text."
                : string.IsNullOrWhiteSpace(targetSummary)
                    ? "A3 inferred a clean target transcript from the clip."
                    : targetSummary.Trim());

        if (!string.IsNullOrWhiteSpace(planSummary))
        {
            summaryParts.Add(planSummary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(verificationSummary))
        {
            summaryParts.Add(verificationSummary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(targetCoverageSummary))
        {
            summaryParts.Add(targetCoverageSummary.Trim());
        }

        if (transcriptChunks.Count > 0)
        {
            summaryParts.Add(
                transcriptChunks.Count == 1
                    ? "A3 segmented the audio into 1 speech chunk."
                    : $"A3 segmented the audio into {transcriptChunks.Count} speech chunks.");
        }

        if (heuristicIssues.Count > 0)
        {
            summaryParts.Add(
                heuristicIssues.Count == 1
                    ? "A3 also added 1 heuristic retake cut."
                    : $"A3 also added {heuristicIssues.Count} heuristic retake cuts.");
        }

        if (deliveryGapIssues.Count > 0)
        {
            summaryParts.Add(
                deliveryGapIssues.Count == 1
                    ? "A3 also added 1 long delivery-pause cut."
                    : $"A3 also added {deliveryGapIssues.Count} long delivery-pause cuts.");
        }

        if (verificationChangedPlan)
        {
            summaryParts.Add("A3 adjusted the first cut plan after listening to the rendered result.");
        }

        summaryParts.Add(
            combinedIssues.Count == 0
                ? "A3 found no final cut ranges."
                : combinedIssues.Count == 1
                    ? "A3 built 1 final cut range toward the clean target read."
                    : $"A3 built {combinedIssues.Count} final cut ranges toward the clean target read.");

        return string.Join(" ", summaryParts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static TargetCoverageReport AnalyzeTargetCoverage(
        string targetTranscript,
        string resultTranscript,
        bool enforceStrict)
    {
        var targetClauses = SplitTranscriptClauses(targetTranscript);
        var resultTokens = ExtractCoverageTokens(resultTranscript)
            .ToHashSet(StringComparer.Ordinal);
        var importantTargetTokens = ExtractImportantCoverageTokens(targetTranscript);
        var coveredImportantTokenCount = importantTargetTokens.Count(token => resultTokens.Contains(token));
        var coverageRatio = importantTargetTokens.Count == 0
            ? 1
            : (double)coveredImportantTokenCount / importantTargetTokens.Count;

        var missingClauses = targetClauses
            .Where(clause => ClauseLooksMissing(clause, resultTokens))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var missingTerms = importantTargetTokens
            .Where(token => !resultTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        var isHardFailure = enforceStrict && (missingClauses.Count > 0 || missingTerms.Count >= 3 || coverageRatio < 0.95);

        var summary = isHardFailure
            ? $"A3 target coverage check found missing script content ({coverageRatio:P0} key-term coverage)." +
              (missingClauses.Count > 0 ? $" Missing clauses: {string.Join(" | ", missingClauses.Take(3))}." : string.Empty) +
              (missingTerms.Count > 0 ? $" Missing terms: {string.Join(", ", missingTerms)}." : string.Empty)
            : enforceStrict
                ? $"A3 target coverage check kept the provided script intact enough ({coverageRatio:P0} key-term coverage)."
                : $"A3 target coverage check is at {coverageRatio:P0} key-term coverage.";

        return new TargetCoverageReport(
            CoverageRatio: coverageRatio,
            MissingClauses: missingClauses,
            MissingTerms: missingTerms,
            IsHardFailure: isHardFailure,
            Summary: summary);
    }

    private static IReadOnlyList<AiContentIssue> RestoreCutsForMissingTargetContent(
        IReadOnlyList<AiContentIssue> issues,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<string> missingClauses,
        IReadOnlyList<string> missingTerms)
    {
        if (issues.Count == 0 || transcriptSegments.Count == 0 || (missingClauses.Count == 0 && missingTerms.Count == 0))
        {
            return issues.ToList();
        }

        var protectedWindows = BuildProtectedWindowsForMissingTargetContent(transcriptSegments, missingClauses, missingTerms);
        if (protectedWindows.Count == 0)
        {
            return issues.ToList();
        }

        var restoredIssues = new List<AiContentIssue>();

        foreach (var issue in issues.OrderBy(issue => issue.StartSeconds).ThenBy(issue => issue.EndSeconds))
        {
            var remainingRanges = new List<(double StartSeconds, double EndSeconds)>
            {
                (issue.StartSeconds, issue.EndSeconds)
            };

            foreach (var window in protectedWindows)
            {
                if (remainingRanges.Count == 0)
                {
                    break;
                }

                var nextRanges = new List<(double StartSeconds, double EndSeconds)>();
                foreach (var range in remainingRanges)
                {
                    if (window.EndSeconds <= range.StartSeconds || window.StartSeconds >= range.EndSeconds)
                    {
                        nextRanges.Add(range);
                        continue;
                    }

                    if (window.StartSeconds > range.StartSeconds)
                    {
                        nextRanges.Add((range.StartSeconds, window.StartSeconds));
                    }

                    if (window.EndSeconds < range.EndSeconds)
                    {
                        nextRanges.Add((window.EndSeconds, range.EndSeconds));
                    }
                }

                remainingRanges = nextRanges
                    .Where(range => range.EndSeconds - range.StartSeconds > 0.05)
                    .ToList();
            }

            foreach (var range in remainingRanges)
            {
                restoredIssues.Add(issue with
                {
                    StartSeconds = range.StartSeconds,
                    EndSeconds = range.EndSeconds
                });
            }
        }

        return restoredIssues
            .OrderBy(issue => issue.StartSeconds)
            .ThenBy(issue => issue.EndSeconds)
            .ToList();
    }

    private static List<ProtectedWindow> BuildProtectedWindowsForMissingTargetContent(
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<string> missingClauses,
        IReadOnlyList<string> missingTerms)
    {
        var windows = new List<ProtectedWindow>();

        foreach (var clause in missingClauses)
        {
            var clauseTokens = ExtractImportantCoverageTokens(clause);
            if (clauseTokens.Count == 0)
            {
                clauseTokens = ExtractCoverageTokens(clause)
                    .Where(token => token.Length >= 3)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }

            if (clauseTokens.Count == 0)
            {
                continue;
            }

            var segmentMatches = transcriptSegments
                .Select(segment => new
                {
                    Segment = segment,
                    SharedCount = CountSharedTokens(clauseTokens, segment.Text),
                    Overlap = CalculateClauseTokenCoverage(clauseTokens, segment.Text)
                })
                .Where(match => match.SharedCount >= Math.Min(2, clauseTokens.Count) || match.Overlap >= 0.5)
                .Select(match => match.Segment)
                .ToList();

            if (segmentMatches.Count == 0)
            {
                var bestMatch = transcriptSegments
                    .Select(segment => new
                    {
                        Segment = segment,
                        SharedCount = CountSharedTokens(clauseTokens, segment.Text),
                        Overlap = CalculateClauseTokenCoverage(clauseTokens, segment.Text)
                    })
                    .OrderByDescending(match => match.SharedCount)
                    .ThenByDescending(match => match.Overlap)
                    .FirstOrDefault();

                if (bestMatch is not null && (bestMatch.SharedCount >= Math.Min(2, clauseTokens.Count) || bestMatch.Overlap >= 0.34))
                {
                    segmentMatches.Add(bestMatch.Segment);
                }
            }

            foreach (var segment in segmentMatches)
            {
                windows.Add(new ProtectedWindow(
                    StartSeconds: Math.Max(0, segment.StartSeconds - 0.12),
                    EndSeconds: segment.EndSeconds + 0.12));
            }
        }

        foreach (var term in missingTerms)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            foreach (var segment in transcriptSegments)
            {
                var segmentTokens = ExtractCoverageTokens(segment.Text);
                if (!segmentTokens.Contains(term, StringComparer.Ordinal))
                {
                    continue;
                }

                windows.Add(new ProtectedWindow(
                    StartSeconds: Math.Max(0, segment.StartSeconds - 0.1),
                    EndSeconds: segment.EndSeconds + 0.1));
            }
        }

        return MergeProtectedWindows(windows);
    }

    private static IReadOnlyList<AiContentIssue> KeepOnlyGapSafeIssues(
        IReadOnlyList<AiContentIssue> issues,
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiTranscriptWord> transcriptWords)
    {
        if (issues.Count == 0)
        {
            return issues.ToList();
        }

        var gapWindows = BuildSafeGapWindows(transcriptSegments, transcriptWords);
        if (gapWindows.Count == 0)
        {
            return [];
        }

        var safeIssues = new List<AiContentIssue>();
        foreach (var issue in issues.OrderBy(issue => issue.StartSeconds).ThenBy(issue => issue.EndSeconds))
        {
            foreach (var gapWindow in gapWindows)
            {
                var overlapStart = Math.Max(issue.StartSeconds, gapWindow.StartSeconds);
                var overlapEnd = Math.Min(issue.EndSeconds, gapWindow.EndSeconds);
                if (overlapEnd - overlapStart <= 0.08)
                {
                    continue;
                }

                var safeDuration = overlapEnd - overlapStart;
                var issueDuration = issue.EndSeconds - issue.StartSeconds;
                if (issueDuration <= 0 || safeDuration / issueDuration < 0.7)
                {
                    continue;
                }

                safeIssues.Add(new AiContentIssue(
                    StartSeconds: overlapStart,
                    EndSeconds: overlapEnd,
                    Label: "Long pause",
                    Reason: "A3 kept only the gap-only portion of this cleanup because the in-speech part could not be verified safely.",
                    Excerpt: issue.Excerpt,
                    Confidence: issue.Confidence,
                    IsLearned: issue.IsLearned));
                break;
            }
        }

        return safeIssues;
    }

    private static List<ProtectedWindow> BuildSafeGapWindows(
        IReadOnlyList<AiTranscriptSegment> transcriptSegments,
        IReadOnlyList<AiTranscriptWord> transcriptWords)
    {
        var windows = new List<ProtectedWindow>();
        windows.AddRange(BuildSpeechGapWindows(transcriptSegments));
        windows.AddRange(BuildWordGapWindows(transcriptWords));
        return MergeProtectedWindows(windows);
    }

    private static List<ProtectedWindow> BuildSpeechGapWindows(IReadOnlyList<AiTranscriptSegment> transcriptSegments)
    {
        var orderedSegments = transcriptSegments
            .OrderBy(segment => segment.StartSeconds)
            .ThenBy(segment => segment.EndSeconds)
            .ToList();
        var gapWindows = new List<ProtectedWindow>();

        for (var index = 0; index < orderedSegments.Count - 1; index += 1)
        {
            var current = orderedSegments[index];
            var next = orderedSegments[index + 1];
            var gapStart = current.EndSeconds;
            var gapEnd = next.StartSeconds;

            if (gapEnd - gapStart < 0.2)
            {
                continue;
            }

            gapWindows.Add(new ProtectedWindow(
                StartSeconds: gapStart + 0.05,
                EndSeconds: gapEnd - 0.05));
        }

        return gapWindows
            .Where(window => window.EndSeconds - window.StartSeconds > 0.08)
            .ToList();
    }

    private static List<ProtectedWindow> BuildWordGapWindows(IReadOnlyList<AiTranscriptWord> transcriptWords)
    {
        if (transcriptWords.Count < 2)
        {
            return [];
        }

        var orderedWords = transcriptWords
            .OrderBy(word => word.StartSeconds)
            .ThenBy(word => word.EndSeconds)
            .ToList();
        var gapWindows = new List<ProtectedWindow>();

        for (var index = 0; index < orderedWords.Count - 1; index += 1)
        {
            var current = orderedWords[index];
            var next = orderedWords[index + 1];
            var gapStart = current.EndSeconds;
            var gapEnd = next.StartSeconds;

            if (gapEnd - gapStart < 0.16)
            {
                continue;
            }

            gapWindows.Add(new ProtectedWindow(
                StartSeconds: gapStart + 0.02,
                EndSeconds: gapEnd - 0.02));
        }

        return gapWindows
            .Where(window => window.EndSeconds - window.StartSeconds > 0.06)
            .ToList();
    }

    private static bool IssueMetadataMatches(
        IReadOnlyList<AiContentIssue> left,
        IReadOnlyList<AiContentIssue> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var normalizedLeft = left
            .OrderBy(issue => issue.StartSeconds)
            .ThenBy(issue => issue.EndSeconds)
            .ToList();
        var normalizedRight = right
            .OrderBy(issue => issue.StartSeconds)
            .ThenBy(issue => issue.EndSeconds)
            .ToList();

        for (var index = 0; index < normalizedLeft.Count; index += 1)
        {
            if (!string.Equals(normalizedLeft[index].Label, normalizedRight[index].Label, StringComparison.Ordinal) ||
                !string.Equals(normalizedLeft[index].Reason, normalizedRight[index].Reason, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<ProtectedWindow> MergeProtectedWindows(IReadOnlyList<ProtectedWindow> windows)
    {
        if (windows.Count == 0)
        {
            return [];
        }

        var ordered = windows
            .OrderBy(window => window.StartSeconds)
            .ThenBy(window => window.EndSeconds)
            .ToList();
        var merged = new List<ProtectedWindow> { ordered[0] };

        foreach (var window in ordered.Skip(1))
        {
            var last = merged[^1];
            if (window.StartSeconds <= last.EndSeconds + 0.02)
            {
                merged[^1] = new ProtectedWindow(
                    StartSeconds: last.StartSeconds,
                    EndSeconds: Math.Max(last.EndSeconds, window.EndSeconds));
                continue;
            }

            merged.Add(window);
        }

        return merged;
    }

    private static bool ClauseLooksMissing(string clause, IReadOnlySet<string> resultTokens)
    {
        var clauseTokens = ExtractImportantCoverageTokens(clause);
        if (clauseTokens.Count == 0)
        {
            clauseTokens = ExtractCoverageTokens(clause)
                .Where(token => token.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (clauseTokens.Count == 0)
        {
            return false;
        }

        var coveredCount = clauseTokens.Count(token => resultTokens.Contains(token));
        var clauseCoverage = (double)coveredCount / clauseTokens.Count;
        var minimumCoverage = clauseTokens.Count >= 4 ? 0.72 : 0.6;
        return clauseCoverage < minimumCoverage;
    }

    private static double CalculateClauseTokenCoverage(IReadOnlyList<string> clauseTokens, string text)
    {
        if (clauseTokens.Count == 0)
        {
            return 0;
        }

        var textTokens = ExtractCoverageTokens(text)
            .ToHashSet(StringComparer.Ordinal);
        var coveredCount = clauseTokens.Count(token => textTokens.Contains(token));
        return (double)coveredCount / clauseTokens.Count;
    }

    private static int CountSharedTokens(IReadOnlyList<string> clauseTokens, string text)
    {
        if (clauseTokens.Count == 0)
        {
            return 0;
        }

        var textTokens = ExtractCoverageTokens(text)
            .ToHashSet(StringComparer.Ordinal);
        return clauseTokens.Count(token => textTokens.Contains(token));
    }

    private static List<string> SplitTranscriptClauses(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Replace('—', ' ')
            .Replace('–', ' ')
            .Split(['.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(clause => NormalizeTranscriptWhitespace(clause))
            .Where(clause => !string.IsNullOrWhiteSpace(clause))
            .Cast<string>()
            .ToList();
    }

    private static List<string> ExtractImportantCoverageTokens(string text)
    {
        return ExtractCoverageTokens(text)
            .Where(token => token.Length >= 3 && !CoverageStopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ExtractCoverageTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return builder.ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
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

    private static string? NormalizeTranscriptWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IssueSetsMatch(
        IReadOnlyList<AiContentIssue> left,
        IReadOnlyList<AiContentIssue> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var normalizedLeft = left
            .OrderBy(issue => issue.StartSeconds)
            .ThenBy(issue => issue.EndSeconds)
            .ToList();
        var normalizedRight = right
            .OrderBy(issue => issue.StartSeconds)
            .ThenBy(issue => issue.EndSeconds)
            .ToList();

        for (var index = 0; index < normalizedLeft.Count; index += 1)
        {
            if (Math.Abs(normalizedLeft[index].StartSeconds - normalizedRight[index].StartSeconds) > 0.05 ||
                Math.Abs(normalizedLeft[index].EndSeconds - normalizedRight[index].EndSeconds) > 0.05)
            {
                return false;
            }
        }

        return true;
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

    private sealed record PreparedMedia(
        double DurationSeconds,
        string UploadPath,
        string AudioPath);

    private sealed record AiTranscriptChunk(
        double StartSeconds,
        double EndSeconds,
        double GapBeforeSeconds,
        double GapAfterSeconds,
        string Text)
    {
        public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
    }

    private sealed record SpeechChunk(
        double StartSeconds,
        double EndSeconds,
        double GapBeforeSeconds,
        double GapAfterSeconds)
    {
        public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
    }

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
        string? Excerpt,
        double Confidence);

    private sealed record OpenAiAutoCutVerificationPayload(
        string? Summary,
        bool KeepCurrentPlan,
        IReadOnlyList<OpenAiContentIssueResponse>? Issues);

    private sealed record OpenAiAutoCutVerificationResponse(
        string Summary,
        bool KeepCurrentPlan,
        IReadOnlyList<AiContentIssue> Issues);

    private sealed record TargetCoverageReport(
        double CoverageRatio,
        IReadOnlyList<string> MissingClauses,
        IReadOnlyList<string> MissingTerms,
        bool IsHardFailure,
        string Summary);

    private sealed record ProtectedWindow(
        double StartSeconds,
        double EndSeconds);

    private sealed record OpenAiTargetTranscriptPayload(
        string? Summary,
        string? TargetTranscript);

    private sealed record OpenAiTargetTranscriptResponse(
        string Summary,
        string TargetTranscript);

    private sealed record OpenAiErrorResponse(OpenAiErrorBody? Error);

    private sealed record OpenAiErrorBody(string? Message);
}
