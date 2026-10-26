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
        // "--quit" with nothing running has nothing to close; don't start a copy just to open it.
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
            return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init =>
        {
            SynchronizationContext.SetSynchronizationContext(new SafeUiContext(DispatcherQueue.GetForCurrentThread()));
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn($"unobserved task error: {e.Exception.InnerException ?? e.Exception}"); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Warn($"fatal: {e.ExceptionObject}");
            _ = new App(args);
        });
        return 0;
    }
}
