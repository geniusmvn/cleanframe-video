namespace LaMaStudio.Core;

public enum MediaKind { Image, Video }

public static class MediaKinds
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };

    public static bool TryFromPath(string path, out MediaKind kind)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) { kind = MediaKind.Image; return true; }
        if (Videos.Contains(ext)) { kind = MediaKind.Video; return true; }
        kind = default; return false;
    }
}
