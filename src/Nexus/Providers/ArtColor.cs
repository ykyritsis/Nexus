using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Nexus.Providers;

/// <summary>A vivid "mood" colour for a piece of art, used to tint the glow behind the overlay.</summary>
public static class ArtColor
{
    static readonly Dictionary<string, Color> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<Color?> FromFileAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = 24, ScaledHeight = 24, InterpolationMode = BitmapInterpolationMode.Linear },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var data = pixels.DetachPixelData();

            // Weight each pixel by how colourful it is, so the result is the art's accent, not grey mush.
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                double pb = data[i], pg = data[i + 1], pr = data[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double w = (max - min) / 255.0 * (max / 255.0) + 0.02;
                r += pr * w; g += pg * w; b += pb * w; weight += w;
            }
            var color = Vivid(r / weight, g / weight, b / weight);
            Cache[path] = color;
            return color;
        }
        catch { return null; }
    }

    /// <summary>Pushes saturation and brightness up so the glow reads as colour, not shadow.</summary>
    static Color Vivid(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 1) return Color.FromArgb(255, 90, 200, 250);
        double scale = 235 / max;
        r *= scale; g *= scale; b *= scale; min *= scale;
        // stretch away from the minimum channel for more saturation
        double lift = 0.35;
        r = r - (min * lift) * (r < 235 ? 1 : 0);
        g = g - (min * lift) * (g < 235 ? 1 : 0);
        b = b - (min * lift) * (b < 235 ? 1 : 0);
        static byte C(double v) => (byte)Math.Clamp(v, 0, 255);
        return Color.FromArgb(255, C(r), C(g), C(b));
    }
}
