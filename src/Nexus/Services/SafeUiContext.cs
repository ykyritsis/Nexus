using Microsoft.UI.Dispatching;

namespace Nexus.Services;

/// <summary>
/// The UI thread's SynchronizationContext, with a safety net. When an async handler (async void, or
/// a continuation after await) throws, the exception is posted back to the dispatcher and rethrown
/// there, outside Application.UnhandledException: WinUI ends the process (STATUS_STOWED_EXCEPTION in
/// CoreMessagingXP). Here those exceptions are logged with their stack and the overlay keeps running.
/// </summary>
public sealed class SafeUiContext : SynchronizationContext
{
    readonly DispatcherQueue _queue;

    public SafeUiContext(DispatcherQueue queue) => _queue = queue;

    public override void Post(SendOrPostCallback d, object? state) =>
        _queue.TryEnqueue(() => Run(d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_queue.HasThreadAccess) Run(d, state);
        else
        {
            using var done = new ManualResetEventSlim();
            _queue.TryEnqueue(() => { try { Run(d, state); } finally { done.Set(); } });
            done.Wait();
        }
    }

    public override SynchronizationContext CreateCopy() => new SafeUiContext(_queue);

    static void Run(SendOrPostCallback d, object? state)
    {
        try { d(state); }
        catch (Exception ex)
        {
            Log.Error("async UI work failed", ex);
            Log.Warn(ex.ToString());
        }
    }
}
