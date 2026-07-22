namespace LaMaStudio.Core;

public static class PgmMask
{
    public static void Save(string path, int width, int height, ReadOnlySpan<byte> alpha)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (alpha.Length != width * height) throw new ArgumentException("Mask size does not match dimensions.", nameof(alpha));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        writer.Write($"P5\n{width} {height}\n255\n");
        writer.Flush();
        stream.Write(alpha);
    }

    public static (int Width, int Height, byte[] Alpha) Load(string path)
    {
        using var stream = File.OpenRead(path);
        string ReadToken()
        {
            var bytes = new List<byte>();
            int value;
            do { value = stream.ReadByte(); } while (value >= 0 && char.IsWhiteSpace((char)value));
            while (value >= 0 && !char.IsWhiteSpace((char)value)) { bytes.Add((byte)value); value = stream.ReadByte(); }
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }
        if (ReadToken() != "P5") throw new InvalidDataException("Only binary PGM P5 is supported.");
        var width = int.Parse(ReadToken(), System.Globalization.CultureInfo.InvariantCulture);
        var height = int.Parse(ReadToken(), System.Globalization.CultureInfo.InvariantCulture);
        if (ReadToken() != "255") throw new InvalidDataException("Only 8-bit PGM is supported.");
        var alpha = new byte[checked(width * height)];
        stream.ReadExactly(alpha);
        return (width, height, alpha);
    }
}
