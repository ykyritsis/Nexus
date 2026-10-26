using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Nexus.Views;

/// <summary>
/// A see-through window background with no blur: whatever is behind shows through untouched. The
/// overlay draws its own (still) blurred frame on top, so Windows only has to blend, never blur, and
/// there is no fallback colour to drop to.
/// </summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        target.SystemBackdrop = SystemCompositor.Get().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }

    /// <summary>
    /// The backdrop brush must come from the system (Windows.UI) compositor, which needs a
    /// Windows.System dispatcher queue on this thread; WinUI only creates its own kind.
    /// </summary>
    static class SystemCompositor
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

        [DllImport("CoreMessaging.dll")]
        static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

        static Windows.UI.Composition.Compositor? _compositor;
        static IntPtr _controller; // kept for the life of the app

        public static Windows.UI.Composition.Compositor Get()
        {
            if (_compositor != null) return _compositor;
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
            {
                var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /*current thread*/, apartmentType = 2 /*STA*/ };
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _controller));
            }
            return _compositor = new Windows.UI.Composition.Compositor();
        }
    }
}
