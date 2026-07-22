namespace CleanFrame.Video2.Core.Processing;

public static class RawMaskCompositor
{
    public static byte[] CompositeBgr(byte[] original, byte[] reconstructed, float[] alpha)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(reconstructed);
        ArgumentNullException.ThrowIfNull(alpha);
        if (original.Length != reconstructed.Length || original.Length != alpha.Length * 3)
            throw new ArgumentException("Buffer dimensions do not match.");

        var output = new byte[original.Length];
        for (var i = 0; i < alpha.Length; i++)
        {
            var a = Math.Clamp(alpha[i], 0, 1);
            var offset = i * 3;
            for (var c = 0; c < 3; c++)
            {
                output[offset + c] = a <= 0
                    ? original[offset + c]
                    : (byte)Math.Clamp(Math.Round(original[offset + c] * (1 - a) + reconstructed[offset + c] * a), 0, 255);
            }
        }
        return output;
    }
}
