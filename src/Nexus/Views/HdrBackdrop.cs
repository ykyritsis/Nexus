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
/// The still, blurred frame behind the overlay, correct on HDR screens. One frame of the monitor is
/// captured with Windows.Graphics.Capture in 16-bit float (scRGB: absolute brightness, 1.0 = 80 nits),
/// scaled so the user's SDR white becomes 1.0, blurred on the GPU with Win2D in linear light, then
/// encoded to sRGB in an ordinary 8-bit surface. Windows shows 8-bit surfaces at the SDR white
/// level, so the frame matches the game's brightness (a float surface was boosted a second time and
/// came out blown out). Only highlights brighter than SDR white are clipped, which a blur hides.
/// Over a game the blurred layer then stays live: the game's own window is captured (so the overlay
/// is never in the picture) and each new frame is re-blurred, at most 60 times a second.
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
    const float BlurAmount = 2.25f; // standard deviation at half size: a light frost

    readonly CanvasDevice _device;
    readonly CompositionGraphicsDevice _graphics;
    // Two layers from one capture: the frame as it is (shown the instant the overlay opens, so nothing
    // jumps) and the blurred one that fades in over it.
    readonly SpriteVisual _sharpVisual, _visual;
    CompositionDrawingSurface? _sharp, _surface;

    public HdrBackdrop(UIElement sharpHost, UIElement blurHost)
    {
        _device = CanvasDevice.GetSharedDevice();
        var compositor = ElementCompositionPreview.GetElementVisual(blurHost).Compositor;
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
        _sharpVisual = compositor.CreateSpriteVisual();
        _sharpVisual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(sharpHost, _sharpVisual);
        _visual = compositor.CreateSpriteVisual();
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(blurHost, _visual);
    }

    CompositionDrawingSurface Surface(ref CompositionDrawingSurface? surface, SpriteVisual visual, int w, int h)
    {
        if (surface is null || surface.SizeInt32.Width != w || surface.SizeInt32.Height != h)
        {
            surface?.Dispose();
            surface = _graphics.CreateDrawingSurface2(new SizeInt32(w, h), Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            var brush = visual.Compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            visual.Brush = brush;
        }
        return surface;
    }

    public static bool Supported => GraphicsCaptureSession.IsSupported();

    static bool _borderAsked;

    /// <summary>
    /// Windows outlines anything being captured unless the app has been granted borderless capture;
    /// setting IsBorderRequired = false alone is ignored without it. Asked once (desktop apps are
    /// granted without a prompt).
    /// </summary>
    public static async Task RequestBorderlessAsync()
    {
        if (_borderAsked) return;
        _borderAsked = true;
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Log.Info($"borderless capture: {status}");
        }
        catch (Exception ex) { Log.Warn($"borderless capture unavailable: {ex.Message}"); }
    }

    static void NoBorder(GraphicsCaptureSession session)
    {
        try { session.IsBorderRequired = false; }
        catch (Exception ex) { Log.Warn($"capture border can't be turned off: {ex.Message}"); }
    }

    /// <summary>Captures and blurs the monitor. False if anything fails (the caller falls back to GDI).</summary>
    /// <param name="sdrWhiteNits">The display's SDR white level in HDR mode, or 0 for an SDR display.</param>
    public async Task<bool> CaptureAsync(IntPtr monitor, double sdrWhiteNits, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long itemMs = 0, frameMs = 0;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            var item = ItemForMonitor(monitor);
            if (item is null) return false;
            itemMs = clock.ElapsedMilliseconds;
            var size = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.R16G16B16A16Float, 1, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            NoBorder(session);

            var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (p, _) => { var f = p.TryGetNextFrame(); if (f != null && !arrived.TrySetResult(f)) f.Dispose(); };
            session.StartCapture();
            var done = await Task.WhenAny(arrived.Task, Task.Delay(timeout));
            if (done != arrived.Task) { Log.Warn("HDR capture timed out"); return false; }

            using var frame = await arrived.Task;
            frameMs = clock.ElapsedMilliseconds;
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
            float exposure = ExposureFor(sdrWhiteNits);
            var sharp = Surface(ref _sharp, _sharpVisual, size.Width, size.Height);
            using (var ds = CanvasComposition.CreateDrawingSession(sharp))
                Render(ds, bitmap, exposure, 1f, blur: false);
            // Half resolution is plenty for a blur and quarters the work.
            var blurred = Surface(ref _surface, _visual, Math.Max(1, size.Width / 2), Math.Max(1, size.Height / 2));
            using (var ds = CanvasComposition.CreateDrawingSession(blurred))
                Render(ds, bitmap, exposure, 0.5f, blur: true);
            Log.Info($"HDR backdrop: item {itemMs} ms, first frame {frameMs} ms, total {clock.ElapsedMilliseconds} ms (SDR white {sdrWhiteNits:0} nits)");
            _visual.IsVisible = true;
            _sharpVisual.IsVisible = true;
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

    // ---------------- live ----------------

    Direct3D11CaptureFramePool? _livePool;
    GraphicsCaptureSession? _liveSession;
    Direct3D11CaptureFrame? _lastFrame;
    readonly object _liveGate = new();
    float _liveExposure = 1;
    long _lastDraw;
    bool _drawQueued, _liveDrawing;
    Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    /// <summary>Keeps the blurred layer moving with the game behind the overlay.</summary>
    public bool StartLive(IntPtr window, double sdrWhiteNits)
    {
        StopLive();
        try
        {
            var item = ItemForWindow(window);
            if (item is null) return false;
            _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _liveExposure = ExposureFor(sdrWhiteNits);
            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.R16G16B16A16Float, 2, item.Size);
            pool.FrameArrived += OnLiveFrame;
            var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            NoBorder(session);
            lock (_liveGate) { _livePool = pool; _liveSession = session; _liveDrawing = true; }
            session.StartCapture();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"live background unavailable: {ex.Message}");
            StopLive();
            return false;
        }
    }

    /// <summary>Capture thread: keep the newest frame, and ask the UI thread to draw it (at most 60 fps).</summary>
    void OnLiveFrame(Direct3D11CaptureFramePool pool, object _)
    {
        var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        lock (_liveGate)
        {
            if (!ReferenceEquals(pool, _livePool)) { frame.Dispose(); return; }
            var previous = _lastFrame;
            _lastFrame = frame;
            previous?.Dispose();
            if (!_liveDrawing || _drawQueued) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastDraw < System.Diagnostics.Stopwatch.Frequency / 60) return;
            _drawQueued = true;
        }
        _ui?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, DrawLive);
    }

    /// <summary>UI thread: re-blur the newest game frame into the blurred layer.</summary>
    void DrawLive()
    {
        try
        {
            lock (_liveGate)
            {
                _drawQueued = false;
                if (!_liveDrawing || _lastFrame is null || _surface is null) return;
                _lastDraw = System.Diagnostics.Stopwatch.GetTimestamp();
                DrawFrame(_lastFrame, _surface, blur: true);
            }
        }
        catch (Exception ex) { Log.Warn($"live background frame: {ex.Message}"); StopLive(); }
    }

    void DrawFrame(Direct3D11CaptureFrame frame, CompositionDrawingSurface target, bool blur)
    {
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
        var size = frame.ContentSize;
        float scale = Math.Min(target.SizeInt32.Width / (float)Math.Max(1, size.Width), target.SizeInt32.Height / (float)Math.Max(1, size.Height));
        using var ds = CanvasComposition.CreateDrawingSession(target);
        Render(ds, bitmap, _liveExposure, scale, blur);
    }

    /// <summary>
    /// Closing: stop updating the blur and put the newest game frame in the sharp layer, so the blur
    /// fades back to the game as it is now, not as it was when the overlay opened.
    /// </summary>
    public void FreezeForClose()
    {
        try
        {
            lock (_liveGate)
            {
                _liveDrawing = false;
                if (_lastFrame != null && _sharp != null) DrawFrame(_lastFrame, _sharp, blur: false);
            }
        }
        catch (Exception ex) { Log.Warn($"live background close frame: {ex.Message}"); }
    }

    public void StopLive()
    {
        lock (_liveGate)
        {
            _liveDrawing = false;
            _liveSession?.Dispose();
            _livePool?.Dispose();
            _lastFrame?.Dispose();
            _liveSession = null;
            _livePool = null;
            _lastFrame = null;
        }
    }

    static GraphicsCaptureItem? ItemForWindow(IntPtr window)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = CaptureItemIid;
        IntPtr ptr = interop.CreateForWindow(window, ref iid);
        if (ptr == IntPtr.Zero) return null;
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>scRGB 1.0 is 80 nits; on an HDR display SDR white sits at sdrWhiteNits, so divide by that.</summary>
    static float ExposureFor(double sdrWhiteNits) => sdrWhiteNits > 80 ? (float)(80 / sdrWhiteNits) : 1f;

    /// <summary>Scale, level to SDR white, blur in linear light, encode to sRGB, draw.</summary>
    static void Render(CanvasDrawingSession ds, ICanvasImage source, float exposure, float scale, bool blur = true)
    {
        // Hard borders: edge pixels are clamped instead of fading into transparent black, which left a
        // dark sliver along the top and left edges when shrinking for the blur.
        using var scaled = new ScaleEffect { Source = source, Scale = new Vector2(scale), InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Hard };
        // Level to SDR white and clip there *before* blurring, then encode to sRGB and blur that, like a
        // normal SDR blur. Blurring the raw HDR values first let very bright spots (up to ~10x white)
        // spread into wide glows: a bloom.
        using var levelled = new ColorMatrixEffect
        {
            Source = scaled,
            ColorMatrix = new Matrix5x4 { M11 = exposure, M22 = exposure, M33 = exposure, M44 = 1 },
            ClampOutput = true,
        };
        using var encoded = new ColorManagementEffect
        {
            Source = levelled,
            SourceColorProfile = new ColorManagementProfile(CanvasColorSpace.ScRgb),
            OutputColorProfile = new ColorManagementProfile(CanvasColorSpace.Srgb),
        };
        using var blurred = new GaussianBlurEffect { Source = encoded, BlurAmount = blur ? BlurAmount : 0, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
        ds.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float; // keep HDR values until the end
        ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        ds.DrawImage(blur ? blurred : encoded);
    }

    /// <summary>
    /// Nexus.exe --selftest-backdrop: runs the pipeline on known scRGB values, off screen, and logs
    /// the 8-bit result (SDR white should come out 255, mid grey about 124).
    /// </summary>
    public static void SelfTest()
    {
        var device = CanvasDevice.GetSharedDevice();
        foreach (var (label, value, white) in new[] { ("SDR white, HDR 240 nits", 3f, 240.0), ("mid grey, HDR 240 nits", 0.6f, 240.0), ("highlight, HDR 240 nits", 10f, 240.0), ("SDR white, SDR display", 1f, 0.0) })
        {
            using var src = new CanvasRenderTarget(device, 32, 32, 96, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
            using (var ds = src.CreateDrawingSession()) ds.Clear(new Vector4(value, value, value, 1));
            using var dst = new CanvasRenderTarget(device, 32, 32, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var ds = dst.CreateDrawingSession()) Render(ds, src, ExposureFor(white), 1f);
            var px = dst.GetPixelBytes(16, 16, 1, 1);
            Log.Info($"SELFTEST {label}: in {value} -> out B{px[0]} G{px[1]} R{px[2]} A{px[3]}");
        }
    }

    /// <summary>Hides the HDR frame (the GDI fallback is showing instead).</summary>
    public void Hide() { _visual.IsVisible = false; _sharpVisual.IsVisible = false; }

    /// <summary>Frees the GPU surface while the overlay is closed.</summary>
    public void Release()
    {
        StopLive();
        _visual.Brush = null;
        _sharpVisual.Brush = null;
        _surface?.Dispose();
        _surface = null;
        _sharp?.Dispose();
        _sharp = null;
    }

    // A capture item per monitor is reused across opens; it's dropped if the monitor goes away.
    static readonly Dictionary<IntPtr, GraphicsCaptureItem> Items = new();

    static GraphicsCaptureItem? ItemForMonitor(IntPtr monitor)
    {
        if (Items.TryGetValue(monitor, out var cached)) return cached;
        var item = CreateItem(monitor);
        if (item != null)
        {
            Items[monitor] = item;
            item.Closed += (_, _) => { lock (Items) Items.Remove(monitor); };
        }
        return item;
    }

    static GraphicsCaptureItem? CreateItem(IntPtr monitor)
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
