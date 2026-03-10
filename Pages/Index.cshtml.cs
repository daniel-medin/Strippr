using System.ComponentModel.DataAnnotations;
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ApplyDefaults();
        await LoadHealthAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadHealthAsync(cancellationToken);

        if (Input.Video is null)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Video)}", "Choose a video file first.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Result = await _videoProcessingService.ProcessAsync(
            Input.Video!,
            Input.NoiseThreshold,
            Input.MinimumSilenceSeconds,
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
            NoiseThreshold = _options.DefaultNoiseThreshold,
            MinimumSilenceSeconds = _options.DefaultMinimumSilenceSeconds
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

        [Required]
        [RegularExpression(@"^-?\d+(?:\.\d+)?dB$", ErrorMessage = "Use a value like -30dB.")]
        public string NoiseThreshold { get; set; } = string.Empty;

        [Range(0.1, 10)]
        public double MinimumSilenceSeconds { get; set; }
    }
}
