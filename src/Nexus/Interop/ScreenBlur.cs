using System.Runtime.InteropServices;

namespace Nexus.Interop;

/// <summary>
/// The overlay's background: one frame of the screen, captured as the overlay opens, shrunk and
/// blurred on the CPU (it is tiny by then, so this takes a few milliseconds) and then stretched back
/// up, which smooths it further. Consoles do the same: a still, blurred frame costs nothing while
/// you navigate, unlike a live blur that has to be redrawn every frame the game draws.
/// </summary>
internal static class ScreenBlur
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);

    const int HALFTONE = 4;
    const uint SRCCOPY = 0x00CC0020;

    /// <summary>A blurred BGRA image of a screen area, about <paramref name="width"/> pixels wide; null if capture fails.</summary>
    public static (byte[] pixels, int width, int height)? Capture(Native.RECT area, int width = 320, int radius = 4)
    {
        int sw = area.Right - area.Left, sh = area.Bottom - area.Top;
        if (sw <= 0 || sh <= 0) return null;
        int w = Math.Min(width, sw), h = Math.Max(1, (int)Math.Round(sh * (double)w / sw));

        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // negative: top-down rows
                biPlanes = 1, biBitCount = 32,
            };
            dib = CreateDIBSection(mem, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return null;
            old = SelectObject(mem, dib);
            // HALFTONE averages the source pixels while shrinking (no shimmer), which is half the blur already.
            SetStretchBltMode(mem, HALFTONE);
            SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
            if (!StretchBlt(mem, 0, 0, w, h, screen, area.Left, area.Top, sw, sh, SRCCOPY)) return null;

            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            // Three box blurs approximate a gaussian.
            var tmp = new byte[px.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(px, tmp, w, h, radius, horizontal: true);
                BoxBlur(tmp, px, w, h, radius, horizontal: false);
            }
            return (px, w, h);
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Running-sum box blur along one axis, edges clamped.</summary>
    static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4, lineStep = horizontal ? w * 4 : 4;
        int span = r * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = line * lineStep;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[start + Math.Clamp(k, 0, length - 1) * step + c];
                for (int i = 0; i < length; i++)
                {
                    dst[start + i * step + c] = (byte)(sum / span);
                    int add = Math.Min(i + r + 1, length - 1), remove = Math.Max(i - r, 0);
                    sum += src[start + add * step + c] - src[start + remove * step + c];
                }
            }
            for (int i = 0; i < length; i++) dst[start + i * step + 3] = 255;
        }
    }
}
