using System.Text;

namespace Erasa.Video2.App.Services;

public static class MaskFileReader
{
    public static async Task<(byte[] Pixels, int Width, int Height)> ReadPgmAsync(string path, CancellationToken cancellationToken = default)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken);
        var index = 0;
        string ReadToken()
        {
            while (index < data.Length)
            {
                if (data[index] == '#')
                {
                    while (index < data.Length && data[index] != '\n') index++;
                }
                if (index < data.Length && char.IsWhiteSpace((char)data[index])) index++;
                else break;
            }
            var start = index;
            while (index < data.Length && !char.IsWhiteSpace((char)data[index])) index++;
            return Encoding.ASCII.GetString(data, start, index - start);
        }

        if (ReadToken() != "P5") throw new InvalidDataException("Mask không phải PGM P5.");
        if (!int.TryParse(ReadToken(), out var width) || !int.TryParse(ReadToken(), out var height))
            throw new InvalidDataException("Kích thước PGM không hợp lệ.");
        if (ReadToken() != "255") throw new InvalidDataException("PGM phải có max value 255.");
        if (index < data.Length && char.IsWhiteSpace((char)data[index])) index++;
        var expected = checked(width * height);
        if (data.Length - index < expected) throw new InvalidDataException("Dữ liệu PGM bị thiếu.");
        var pixels = new byte[expected];
        Buffer.BlockCopy(data, index, pixels, 0, expected);
        return (pixels, width, height);
    }
}
