namespace Erasa.Video2.Core.Processing;

public static class PixelCompositor
{
    public static byte[] CompositeBgr(ReadOnlySpan<byte> original, ReadOnlySpan<byte> replacement, ReadOnlySpan<byte> mask)
    {
        if (original.Length != replacement.Length) throw new ArgumentException("Image sizes differ.", nameof(replacement));
        if (original.Length % 3 != 0 || mask.Length != original.Length / 3) throw new ArgumentException("Mask size mismatch.", nameof(mask));
        var result = original.ToArray();
        for (var pixel = 0; pixel < mask.Length; pixel++)
        {
            var alpha = mask[pixel] / 255d;
            if (alpha <= 0) continue;
            var offset = pixel * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                result[offset + channel] = (byte)Math.Clamp(
                    Math.Round(original[offset + channel] * (1 - alpha) + replacement[offset + channel] * alpha), 0, 255);
            }
        }
        return result;
    }
}
