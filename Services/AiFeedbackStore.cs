using System.Text.Json;
using System.Text.RegularExpressions;
using Strippr.Models;

namespace Strippr.Services;

public sealed class AiFeedbackStore
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept",
        "added",
        "reject",
        "learn",
        "trim",
        "split",
        "wrong_reason"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly WorkspaceService _workspaceService;

    public AiFeedbackStore(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    public async Task AppendContentFeedbackAsync(
        AiContentFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        var suggestion = request.Suggestion
            ?? throw new InvalidOperationException("Suggestion feedback is required.");

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new InvalidOperationException("Feedback session id is required.");
        }

        var normalizedAction = suggestion.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AllowedActions.Contains(normalizedAction))
        {
            throw new InvalidOperationException("Unknown feedback action.");
        }

        var normalizedRanges = (suggestion.CurrentRanges ?? [])
            .Where(range =>
                range is not null &&
                double.IsFinite(range.StartSeconds) &&
                double.IsFinite(range.EndSeconds) &&
                range.EndSeconds > range.StartSeconds)
            .Select(range => new AiContentFeedbackRange(
                StartSeconds: Math.Round(range.StartSeconds, 3),
                EndSeconds: Math.Round(range.EndSeconds, 3)))
            .ToList();

        var transcriptSegments = (request.TranscriptSegments ?? [])
            .Where(segment =>
                segment is not null &&
                double.IsFinite(segment.StartSeconds) &&
                double.IsFinite(segment.EndSeconds) &&
                segment.EndSeconds > segment.StartSeconds &&
                !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => new AiTranscriptSegment(
                StartSeconds: Math.Round(segment.StartSeconds, 3),
                EndSeconds: Math.Round(segment.EndSeconds, 3),
                Text: segment.Text.Trim()))
            .ToList();

        var record = new AiContentFeedbackRecord(
            CreatedUtc: DateTime.UtcNow,
            SessionId: request.SessionId.Trim(),
            ClientFileName: string.IsNullOrWhiteSpace(request.ClientFileName)
                ? "unknown"
                : request.ClientFileName.Trim(),
            SpokenLanguage: string.IsNullOrWhiteSpace(request.SpokenLanguage)
                ? "auto"
                : request.SpokenLanguage.Trim(),
            TranscriptionModel: string.IsNullOrWhiteSpace(request.TranscriptionModel)
                ? "unknown"
                : request.TranscriptionModel.Trim(),
            AnalysisModel: string.IsNullOrWhiteSpace(request.AnalysisModel)
                ? "unknown"
                : request.AnalysisModel.Trim(),
            TranscriptText: request.TranscriptText?.Trim() ?? string.Empty,
            TranscriptSegments: transcriptSegments,
            Suggestion: new AiContentFeedbackSuggestion(
                SuggestionId: string.IsNullOrWhiteSpace(suggestion.SuggestionId)
                    ? Guid.NewGuid().ToString("N")
                    : suggestion.SuggestionId.Trim(),
                Action: normalizedAction,
                Label: suggestion.Label?.Trim(),
                CorrectedLabel: suggestion.CorrectedLabel?.Trim(),
                Reason: suggestion.Reason?.Trim(),
                Excerpt: suggestion.Excerpt?.Trim(),
                Confidence: ClampConfidence(suggestion.Confidence),
                OriginalStartSeconds: Math.Round(Math.Max(0, suggestion.OriginalStartSeconds), 3),
                OriginalEndSeconds: Math.Round(Math.Max(0, suggestion.OriginalEndSeconds), 3),
                CurrentRanges: normalizedRanges,
                Note: string.IsNullOrWhiteSpace(suggestion.Note) ? null : suggestion.Note.Trim()));

        var logPath = _workspaceService.GetAiContentFeedbackLogPath();
        var payload = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(logPath, payload, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AiContentFeedbackMemory> GetRememberedContentFeedbackAsync(
        string clientFileName,
        string transcriptText,
        CancellationToken cancellationToken)
    {
        var logPath = _workspaceService.GetAiContentFeedbackLogPath();
        if (!File.Exists(logPath))
        {
            return EmptyMemory();
        }

        var normalizedFileName = NormalizeForComparison(clientFileName);
        var normalizedTranscript = NormalizeForComparison(transcriptText);
        if (normalizedFileName.Length == 0 && normalizedTranscript.Length == 0)
        {
            return EmptyMemory();
        }

        var matchedRecords = new List<AiContentFeedbackRecord>();

        using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AiContentFeedbackRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<AiContentFeedbackRecord>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (record is null || !IsMatchingRecord(record, normalizedFileName, normalizedTranscript))
            {
                continue;
            }

            matchedRecords.Add(record);
        }

        if (matchedRecords.Count == 0)
        {
            return EmptyMemory();
        }

        var latestBySuggestion = matchedRecords
            .OrderBy(record => record.CreatedUtc)
            .GroupBy(record => record.Suggestion.SuggestionId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        var learnedCandidates = new List<(DateTime CreatedUtc, AiContentIssue Issue)>();
        var suppressedRanges = new List<AiContentFeedbackRange>();

        foreach (var record in latestBySuggestion)
        {
            var action = record.Suggestion.Action?.Trim().ToLowerInvariant() ?? string.Empty;
            var currentRanges = NormalizeRanges(record.Suggestion.CurrentRanges);

            if (action == "reject")
            {
                if (currentRanges.Count > 0)
                {
                    suppressedRanges.AddRange(currentRanges);
                }
                else if (record.Suggestion.OriginalEndSeconds > record.Suggestion.OriginalStartSeconds)
                {
                    suppressedRanges.Add(new AiContentFeedbackRange(
                        Math.Round(record.Suggestion.OriginalStartSeconds, 3),
                        Math.Round(record.Suggestion.OriginalEndSeconds, 3)));
                }

                continue;
            }

            if (currentRanges.Count == 0)
            {
                continue;
            }

            var baseLabel = string.IsNullOrWhiteSpace(record.Suggestion.Label)
                ? "Learned cut"
                : record.Suggestion.Label.Trim();
            var correctedLabel = string.IsNullOrWhiteSpace(record.Suggestion.CorrectedLabel)
                ? null
                : record.Suggestion.CorrectedLabel.Trim();
            var baseReason = BuildLearnedReason(record);
            var baseExcerpt = record.Suggestion.Excerpt?.Trim() ?? string.Empty;
            var baseConfidence = Math.Max(0.95, ClampConfidence(record.Suggestion.Confidence));

            foreach (var range in currentRanges)
            {
                learnedCandidates.Add((
                    record.CreatedUtc,
                    new AiContentIssue(
                        StartSeconds: range.StartSeconds,
                        EndSeconds: range.EndSeconds,
                        Label: correctedLabel ?? baseLabel,
                        Reason: baseReason,
                        Excerpt: baseExcerpt,
                        Confidence: baseConfidence,
                        IsLearned: true)));
            }
        }

        var filteredLearnedIssues = learnedCandidates
            .Where(entry => !suppressedRanges.Any(range => OverlapsMeaningfully(entry.Issue, range)))
            .OrderByDescending(entry => entry.CreatedUtc)
            .ThenByDescending(entry => entry.Issue.Confidence)
            .ToList();
        var deduplicatedLearnedIssues = new List<AiContentIssue>();

        foreach (var entry in filteredLearnedIssues)
        {
            if (deduplicatedLearnedIssues.Any(existingIssue => OverlapsMeaningfully(existingIssue, entry.Issue)))
            {
                continue;
            }

            deduplicatedLearnedIssues.Add(entry.Issue);
        }

        return new AiContentFeedbackMemory(
            LearnedIssues: deduplicatedLearnedIssues
                .OrderBy(issue => issue.StartSeconds)
                .ThenBy(issue => issue.EndSeconds)
                .ToList(),
            SuppressedRanges: suppressedRanges
                .OrderBy(range => range.StartSeconds)
                .ThenBy(range => range.EndSeconds)
                .ToList(),
            MatchedFeedbackCount: latestBySuggestion.Count);
    }

    public async Task<int> ForgetRememberedContentFeedbackAsync(
        string clientFileName,
        string? transcriptText,
        CancellationToken cancellationToken)
    {
        var normalizedFileName = NormalizeForComparison(clientFileName);
        var normalizedTranscript = NormalizeForComparison(transcriptText);
        if (normalizedFileName.Length == 0 && normalizedTranscript.Length == 0)
        {
            throw new InvalidOperationException("Clip memory reset needs a file name or transcript.");
        }

        var logPath = _workspaceService.GetAiContentFeedbackLogPath();
        if (!File.Exists(logPath))
        {
            return 0;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var keptLines = new List<string>();
            var removedCount = 0;

            using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AiContentFeedbackRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<AiContentFeedbackRecord>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    keptLines.Add(line);
                    continue;
                }

                if (record is not null && IsMatchingRecord(record, normalizedFileName, normalizedTranscript))
                {
                    removedCount += 1;
                    continue;
                }

                keptLines.Add(line);
            }

            var content = keptLines.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, keptLines) + Environment.NewLine;
            await File.WriteAllTextAsync(logPath, content, cancellationToken);
            return removedCount;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static double ClampConfidence(double confidence)
    {
        if (!double.IsFinite(confidence))
        {
            return 0.5;
        }

        return Math.Round(Math.Clamp(confidence, 0, 1), 3);
    }

    private static AiContentFeedbackMemory EmptyMemory()
    {
        return new AiContentFeedbackMemory([], [], 0);
    }

    private static List<AiContentFeedbackRange> NormalizeRanges(IReadOnlyList<AiContentFeedbackRange>? ranges)
    {
        return (ranges ?? [])
            .Where(range =>
                range is not null &&
                double.IsFinite(range.StartSeconds) &&
                double.IsFinite(range.EndSeconds) &&
                range.EndSeconds > range.StartSeconds)
            .Select(range => new AiContentFeedbackRange(
                StartSeconds: Math.Round(range.StartSeconds, 3),
                EndSeconds: Math.Round(range.EndSeconds, 3)))
            .ToList();
    }

    private static bool IsMatchingRecord(
        AiContentFeedbackRecord record,
        string normalizedFileName,
        string normalizedTranscript)
    {
        var recordFileName = NormalizeForComparison(record.ClientFileName);
        var recordTranscript = NormalizeForComparison(record.TranscriptText);

        var sameFile = normalizedFileName.Length > 0 &&
            recordFileName.Equals(normalizedFileName, StringComparison.Ordinal);
        var transcriptSimilarity = ComputeTranscriptSimilarity(normalizedTranscript, recordTranscript);
        var sameTranscript = transcriptSimilarity >= 0.78;

        if (sameFile && sameTranscript)
        {
            return true;
        }

        if (sameTranscript && normalizedTranscript.Length > 80)
        {
            return true;
        }

        return sameFile && normalizedTranscript.Length == 0;
    }

    private static string BuildLearnedReason(AiContentFeedbackRecord record)
    {
        var action = record.Suggestion.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        var baseReason = string.IsNullOrWhiteSpace(record.Suggestion.Reason)
            ? "Reused from local A2 feedback."
            : record.Suggestion.Reason.Trim();

        return action switch
        {
            "added" => $"Reused manual A2 feedback for this clip. {baseReason}",
            "accept" => $"Reused accepted A2 feedback for this clip. {baseReason}",
            "split" => $"Reused split A2 feedback for this clip. {baseReason}",
            "wrong_reason" => string.IsNullOrWhiteSpace(record.Suggestion.CorrectedLabel)
                ? $"Reused corrected A2 feedback for this clip. {baseReason}"
                : $"Reused corrected A2 feedback for this clip as '{record.Suggestion.CorrectedLabel.Trim()}'. {baseReason}",
            _ => $"Reused learned A2 feedback for this clip. {baseReason}"
        };
    }

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        return Regex.Replace(normalized, @"\s+", " ");
    }

    private static double ComputeTranscriptSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (left.Equals(right, StringComparison.Ordinal))
        {
            return 1;
        }

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        var leftSet = new HashSet<string>(leftTokens, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(rightTokens, StringComparer.Ordinal);
        var intersectionCount = leftSet.Intersect(rightSet, StringComparer.Ordinal).Count();
        var unionCount = leftSet.Union(rightSet, StringComparer.Ordinal).Count();
        return unionCount == 0 ? 0 : (double)intersectionCount / unionCount;
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

    private static bool OverlapsMeaningfully(AiContentIssue left, AiContentIssue right)
    {
        var overlap = Math.Min(left.EndSeconds, right.EndSeconds) - Math.Max(left.StartSeconds, right.StartSeconds);
        if (overlap <= 0)
        {
            return false;
        }

        var leftDuration = Math.Max(0.01, left.EndSeconds - left.StartSeconds);
        var rightDuration = Math.Max(0.01, right.EndSeconds - right.StartSeconds);
        var overlapRatio = overlap / Math.Min(leftDuration, rightDuration);

        return overlapRatio >= 0.45 ||
               (Math.Abs(left.StartSeconds - right.StartSeconds) <= 0.35 &&
                Math.Abs(left.EndSeconds - right.EndSeconds) <= 0.35);
    }
}
