namespace CrookedToe.Modules.OSCAudioReaction;

internal static class AudioCaptureShutdown
{
    public static Task RunAsync(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        // Always use the default scheduler so a caller running on VRCOSC's WPF
        // dispatcher cannot synchronously join the WASAPI capture thread.
        return Task.Factory.StartNew(
            cleanup,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }
}
