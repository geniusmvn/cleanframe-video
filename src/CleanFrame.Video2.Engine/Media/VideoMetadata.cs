namespace CleanFrame.Video2.Engine.Media;

public sealed record VideoMetadata(
    int Width,
    int Height,
    double Fps,
    double DurationSeconds,
    bool HasAudio,
    string VideoCodec,
    string? AudioCodec);
