namespace Strippr.Options;

public sealed class StripprOptions
{
    public const string SectionName = "Strippr";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string BundledFfmpegFolder { get; set; } = "tools/ffmpeg";

    public string FfmpegDownloadUrl { get; set; } =
        "https://github.com/GyanD/codexffmpeg/releases/download/8.0.1/ffmpeg-8.0.1-essentials_build.zip";

    public string StorageRoot { get; set; } = "App_Data";

    public string UploadsFolder { get; set; } = "uploads";

    public string OutputsFolder { get; set; } = "Strippr";

    public string TempFolder { get; set; } = "temp";

    public long MaxUploadMegabytes { get; set; } = 1024;

    public string DefaultNoiseThreshold { get; set; } = "-30dB";

    public double DefaultMinimumSilenceSeconds { get; set; } = 0.5;

    public double DefaultCrossfadeMilliseconds { get; set; } = 80;

    public int DefaultVideoCrossfadeFrames { get; set; } = 0;

    public double DefaultPauseSpeedMultiplier { get; set; } = 1;

    public double DefaultRetainedSilenceSeconds { get; set; } = 0;

    public double DefaultCutHandleMilliseconds { get; set; } = 120;

    public double MinimumKeepSegmentSeconds { get; set; } = 0.15;
}
