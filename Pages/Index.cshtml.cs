using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Strippr.Models;
using Strippr.Options;
using Strippr.Services;

namespace Strippr.Pages;

public sealed class IndexModel : PageModel
{
    private readonly FfmpegService _ffmpegService;
    private readonly StripprOptions _options;
    private readonly VideoProcessingService _videoProcessingService;

    public IndexModel(
        FfmpegService ffmpegService,
        IOptions<StripprOptions> options,
        VideoProcessingService videoProcessingService)
    {
        _ffmpegService = ffmpegService;
        _options = options.Value;
        _videoProcessingService = videoProcessingService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public VideoProcessingResult? Result { get; private set; }

    public bool FfmpegAvailable { get; private set; }

    public string FfmpegStatusMessage { get; private set; } = string.Empty;

    public bool HasConfiguredOpenAiApiKey => !string.IsNullOrWhiteSpace(_options.OpenAiApiKey);

    public string DefaultOpenAiModel => string.IsNullOrWhiteSpace(_options.DefaultOpenAiModel)
        ? "whisper-1"
        : _options.DefaultOpenAiModel;

    public string DefaultNoiseThresholdDbText =>
        TryParseNoiseThreshold(_options.DefaultNoiseThreshold)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-30";

    public string DefaultMinimumSilenceSecondsText =>
        _options.DefaultMinimumSilenceSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    public string DefaultRetainedSilenceSecondsText =>
        _options.DefaultRetainedSilenceSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    public string DefaultCutHandleMillisecondsText =>
        _options.DefaultCutHandleMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

    public string DefaultCrossfadeMillisecondsText =>
        _options.DefaultCrossfadeMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

    public string DefaultVideoCrossfadeFramesText =>
        _options.DefaultVideoCrossfadeFrames.ToString(CultureInfo.InvariantCulture);

    public string DefaultPauseSpeedMultiplierText =>
        _options.DefaultPauseSpeedMultiplier.ToString("0.###", CultureInfo.InvariantCulture);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ApplyDefaults();
        await LoadHealthAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadHealthAsync(cancellationToken);
        var manualCutRanges = ParseCutRanges(Input.ManualCutRangesJson);
        var aiCutRanges = ParseCutRanges(Input.AiCutRangesJson);

        if (Input.Video is null)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Video)}", "Choose a video file first.");
        }

        if (manualCutRanges is null)
        {
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.ManualCutRangesJson)}",
                "The manual cut markers could not be read. Refresh the page and try again.");
        }

        if (aiCutRanges is null)
        {
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.AiCutRangesJson)}",
                "The AI cut ranges could not be read. Run the AI analysis again.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var explicitCutRanges = manualCutRanges!
            .Concat(aiCutRanges!)
            .ToList();

        var automaticSilenceEnabled = Input.EnableNoiseThreshold && Input.EnableMinimumSilence;
        var retainedSilenceSeconds = Input.EnableRetainedSilence
            ? Input.RetainedSilenceSeconds
            : 0;
        var cutHandleMilliseconds = Input.EnableCutHandles
            ? Input.CutHandleMilliseconds
            : 0;
        var crossfadeMilliseconds = Input.EnableCrossfade
            ? Input.CrossfadeMilliseconds
            : 0;
        var videoCrossfadeFrames = Input.EnableVideoCrossfade
            ? Input.VideoCrossfadeFrames
            : 0;
        var pauseSpeedMultiplier = Input.EnablePauseSpeed
            ? Input.PauseSpeedMultiplier
            : 1;

        Result = await _videoProcessingService.ProcessAsync(
            Input.Video!,
            automaticSilenceEnabled,
            Input.NoiseThreshold,
            Input.MinimumSilenceSeconds,
            retainedSilenceSeconds,
            cutHandleMilliseconds,
            crossfadeMilliseconds,
            videoCrossfadeFrames,
            pauseSpeedMultiplier,
            explicitCutRanges,
            cancellationToken);

        if (!Result.Success)
        {
            ModelState.AddModelError(string.Empty, Result.Message);
        }

        return Page();
    }

    public string FormatSeconds(double seconds)
    {
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.ff");
    }

    private void ApplyDefaults()
    {
        Input = new InputModel
        {
            EnableNoiseThreshold = false,
            NoiseThreshold = _options.DefaultNoiseThreshold,
            EnableMinimumSilence = false,
            MinimumSilenceSeconds = _options.DefaultMinimumSilenceSeconds,
            EnableRetainedSilence = false,
            RetainedSilenceSeconds = _options.DefaultRetainedSilenceSeconds,
            EnableCutHandles = false,
            CutHandleMilliseconds = _options.DefaultCutHandleMilliseconds,
            EnableCrossfade = false,
            CrossfadeMilliseconds = _options.DefaultCrossfadeMilliseconds,
            EnableVideoCrossfade = false,
            VideoCrossfadeFrames = _options.DefaultVideoCrossfadeFrames,
            EnablePauseSpeed = false,
            PauseSpeedMultiplier = _options.DefaultPauseSpeedMultiplier,
            ManualCutRangesJson = "[]",
            AiCutRangesJson = "[]"
        };
    }

    private async Task LoadHealthAsync(CancellationToken cancellationToken)
    {
        var health = await _ffmpegService.GetHealthStatusAsync(cancellationToken);
        FfmpegAvailable = health.IsAvailable;
        FfmpegStatusMessage = health.Message;
    }

    public sealed class InputModel
    {
        [Required]
        public IFormFile? Video { get; set; }

        public bool EnableNoiseThreshold { get; set; } = true;

        [Required]
        [RegularExpression(@"^-?\d+(?:\.\d+)?dB$", ErrorMessage = "Use a value like -30dB.")]
        public string NoiseThreshold { get; set; } = string.Empty;

        public bool EnableMinimumSilence { get; set; } = true;

        [Range(0.1, 10)]
        public double MinimumSilenceSeconds { get; set; }

        public bool EnableRetainedSilence { get; set; } = true;

        [Range(0, 1)]
        public double RetainedSilenceSeconds { get; set; }

        public bool EnableCutHandles { get; set; } = true;

        [Range(0, 500)]
        public double CutHandleMilliseconds { get; set; }

        public bool EnableCrossfade { get; set; } = true;

        [Range(0, 500)]
        public double CrossfadeMilliseconds { get; set; }

        public bool EnableVideoCrossfade { get; set; } = true;

        [Range(0, 48)]
        public int VideoCrossfadeFrames { get; set; }

        public bool EnablePauseSpeed { get; set; } = true;

        [Range(1, 10)]
        public double PauseSpeedMultiplier { get; set; }

        public string ManualCutRangesJson { get; set; } = "[]";

        public string AiCutRangesJson { get; set; } = "[]";
    }

    private static IReadOnlyList<SilenceInterval>? ParseCutRanges(string? cutRangesJson)
    {
        if (string.IsNullOrWhiteSpace(cutRangesJson))
        {
            return [];
        }

        try
        {
            var ranges = JsonSerializer.Deserialize<List<ManualCutRangePayload>>(
                cutRangesJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            if (ranges is null)
            {
                return [];
            }

            return ranges
                .Select(range => new SilenceInterval(range.StartSeconds, range.EndSeconds))
                .Where(range =>
                    double.IsFinite(range.StartSeconds) &&
                    double.IsFinite(range.EndSeconds) &&
                    range.EndSeconds > range.StartSeconds)
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? TryParseNoiseThreshold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private sealed record ManualCutRangePayload(double StartSeconds, double EndSeconds);
}
