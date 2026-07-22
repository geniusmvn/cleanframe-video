using System.Buffers.Binary;
using System.IO.Compression;

namespace Erasa.Video.Core.Processing;

public static class MaskPngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task WriteGrayscaleAsync(string path, int width, int height, byte[] pixels, CancellationToken ct = default)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Mask size mismatch.", nameof(pixels));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await stream.WriteAsync(Signature, ct);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; ihdr[9] = 0; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        await WriteChunkAsync(stream, "IHDR", ihdr, ct);
        await using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(pixels, y * width, width);
        }
        raw.Position = 0;
        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await raw.CopyToAsync(zlib, ct);
        await WriteChunkAsync(stream, "IDAT", compressed.ToArray(), ct);
        await WriteChunkAsync(stream, "IEND", [], ct);
    }

    private static async Task WriteChunkAsync(Stream stream, string type, byte[] data, CancellationToken ct)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(typeBytes, ct);
        await stream.WriteAsync(data, ct);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0); data.CopyTo(crcInput, typeBytes.Length);
        var crc = Crc32(crcInput);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        await stream.WriteAsync(crcBytes, ct);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
