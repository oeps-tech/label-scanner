using System.IO;
using System.Windows.Media.Imaging;

namespace Scanner.Server.Services;

public static class ImageLoader
{
    public static BitmapSource? FromBase64(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return FromBytes(Convert.FromBase64String(text));
    }
    public static BitmapSource FromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
