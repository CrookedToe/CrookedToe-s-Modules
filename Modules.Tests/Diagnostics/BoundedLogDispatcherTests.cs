using CrookedToe.Modules.Diagnostics;

namespace CrookedToesModules.Tests.Diagnostics;

[TestClass]
public sealed class BoundedLogDispatcherTests
{
    [TestMethod]
    public void StalledHostLoggingCannotBlockProducersOrGrowTheQueue()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var drained = new CountdownEvent(4);
        using var dispatcher = new BoundedLogDispatcher(4);
        Assert.IsTrue(dispatcher.TryPost(() => { entered.Set(); release.Wait(); }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Task producer = Task.Run(() =>
            {
                for (int i = 0; i < 100_000; i++)
                    dispatcher.TryPost(() => drained.Signal());
            });
            Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(5)), "Logging blocked the control thread.");
            Assert.AreEqual(4, dispatcher.PendingCount);
            Assert.AreEqual(99_996L, dispatcher.DroppedMessages);
            dispatcher.Dispose(); // Must return even while the host callback is blocked.
            Assert.IsFalse(dispatcher.TryPost(() => Assert.Fail("Post after shutdown")));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(drained.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public void FailedHostLogDoesNotKillDeliveryOfLaterMessages()
    {
        using var delivered = new ManualResetEventSlim();
        using var dispatcher = new BoundedLogDispatcher(4);
        dispatcher.TryPost(() => throw new InvalidOperationException("host failure"));
        dispatcher.TryPost(() => delivered.Set());
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1L, dispatcher.FailedMessages);
    }
}
