using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Nexus.Providers;

/// <summary>Image sources for games, launchers and apps: picture files directly, exe icons via the shell.</summary>
public static class Icons
{
    static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<BitmapImage?> LoadAsync(string? path, uint size = 96)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            BitmapImage image;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                image = new BitmapImage(new Uri(path)) { DecodePixelWidth = (int)size };
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.UseCurrentScale);
                image = new BitmapImage();
                await image.SetSourceAsync(thumb);
            }
            Cache[path] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Error($"icon for {path}", ex);
            return null;
        }
    }
}
