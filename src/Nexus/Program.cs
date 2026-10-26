using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Nexus.Services;

namespace Nexus;

/// <summary>
/// Entry point. Only one Nexus runs at a time: starting it again (for example from a StreamDock key
/// with --toggle) just tells the running copy what to do and exits.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!SingleInstance.TryBecomePrimary(args))
            return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }
}
