using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace Erasa.Video.App.Services;

public sealed class PathBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string path && File.Exists(path) ? new Bitmap(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
