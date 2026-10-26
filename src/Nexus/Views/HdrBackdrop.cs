using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Nexus.Services;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

namespace Nexus.Views;

/// <summary>
/// The still, blurred frame behind the overlay, in HDR when the screen is HDR. One frame of the
/// monitor is captured with Windows.Graphics.Capture in 16-bit float (scRGB, so highlights above
/// SDR white survive), blurred on the GPU with Win2D, and drawn into a float composition surface
/// that Windows shows at the game's own brightness. It costs nothing while you navigate.
/// </summary>
public sealed class HdrBackdrop : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    const float BlurAmount = 9f; // in captured pixels at half size: a light frost

    readonly CanvasDevice _device;
    readonly CompositionGraphicsDevice _graphics;
    readonly SpriteVisual _visual;
    CompositionDrawingSurface? _surface;

    public HdrBackdrop(UIElement host)
    {
        _device = CanvasDevice.GetSharedDevice();
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
        _visual = compositor.CreateSpriteVisual();
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
    }

    public static bool Supported => GraphicsCaptureSession.IsSupported();

    /// <summary>Captures and blurs the monitor. False if anything fails (the caller falls back to GDI).</summary>
    public async Task<bool> CaptureAsync(IntPtr monitor, TimeSpan timeout)
    {
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            var item = ItemForMonitor(monitor);
            if (item is null) return false;
            var size = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.R16G16B16A16Float, 1, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; } catch { } // no yellow capture border where allowed

            var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (p, _) => { var f = p.TryGetNextFrame(); if (f != null && !arrived.TrySetResult(f)) f.Dispose(); };
            session.StartCapture();
            var done = await Task.WhenAny(arrived.Task, Task.Delay(timeout));
            if (done != arrived.Task) { Log.Warn("HDR capture timed out"); return false; }

            using var frame = await arrived.Task;
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
            // Half resolution is plenty for a blur and quarters the work.
            int w = Math.Max(1, size.Width / 2), h = Math.Max(1, size.Height / 2);
            if (_surface is null || _surface.SizeInt32.Width != w || _surface.SizeInt32.Height != h)
            {
                _surface?.Dispose();
                _surface = _graphics.CreateDrawingSurface2(new SizeInt32(w, h), Microsoft.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
                _visual.Brush = _visual.Compositor.CreateSurfaceBrush(_surface);
                ((CompositionSurfaceBrush)_visual.Brush).Stretch = CompositionStretch.Fill;
            }
            var scaled = new ScaleEffect { Source = bitmap, Scale = new Vector2(0.5f), InterpolationMode = CanvasImageInterpolation.Linear };
            using var blur = new GaussianBlurEffect { Source = scaled, BlurAmount = BlurAmount, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
            using (var ds = CanvasComposition.CreateDrawingSession(_surface))
            {
                ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                ds.DrawImage(blur);
            }
            scaled.Dispose();
            _visual.IsVisible = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"HDR capture failed, using GDI: {ex.Message}");
            return false;
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
        }
    }

    /// <summary>Hides the HDR frame (the GDI fallback is showing instead).</summary>
    public void Hide() => _visual.IsVisible = false;

    /// <summary>Frees the GPU surface while the overlay is closed.</summary>
    public void Release()
    {
        _visual.Brush = null;
        _surface?.Dispose();
        _surface = null;
    }

    static GraphicsCaptureItem? ItemForMonitor(IntPtr monitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = CaptureItemIid;
        IntPtr ptr = interop.CreateForMonitor(monitor, ref iid);
        if (ptr == IntPtr.Zero) return null;
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public void Dispose() => Release();
}
