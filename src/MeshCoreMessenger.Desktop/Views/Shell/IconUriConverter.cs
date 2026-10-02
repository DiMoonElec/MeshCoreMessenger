using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MeshCoreMessenger.Desktop.Views.Shell;

public sealed class IconUriConverter : IValueConverter
{
    public static readonly IconUriConverter Instance = new();
    private static readonly Dictionary<string, Bitmap> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uri)
        {
            return null;
        }

        if (!Cache.TryGetValue(uri, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            bitmap = new Bitmap(stream);
            Cache[uri] = bitmap;
        }

        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
