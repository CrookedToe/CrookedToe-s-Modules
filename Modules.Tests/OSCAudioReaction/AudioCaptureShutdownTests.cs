using CrookedToe.Modules.OSCAudioReaction;

namespace CrookedToesModules.Tests.OSCAudioReaction;

[TestClass]
public sealed class AudioCaptureShutdownTests
{
    [TestMethod]
    public async Task RunAsync_LeavesCallerFreeToReleaseCaptureCallback()
    {
        var dispatchRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatchCompleted = new ManualResetEventSlim();
        using var captureExited = new ManualResetEventSlim();

        var captureThread = new Thread(() =>
        {
            dispatchRequested.TrySetResult(true);
            dispatchCompleted.Wait();
            captureExited.Set();
        });

        captureThread.Start();

        try
        {
            await dispatchRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Models WasapiCapture.Dispose joining a callback which is waiting for
            // synchronous work on VRCOSC's dispatcher.
            Task shutdown = AudioCaptureShutdown.RunAsync(captureThread.Join);

            Assert.IsFalse(shutdown.IsCompleted, "Shutdown should be waiting for the in-flight capture callback.");

            // The caller remains free to service the callback instead of deadlocking.
            dispatchCompleted.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(captureExited.IsSet);
        }
        finally
        {
            dispatchCompleted.Set();
            captureThread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
