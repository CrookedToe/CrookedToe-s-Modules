using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CrookedToe.Modules.Compatibility;
using FastOSC;
using VRCOSC.App.OSC.VRChat;
using VRCOSC.App.UI.Views.ChatBox;

namespace CrookedToesModules.Tests.Compatibility;

[TestClass]
public sealed class VrcOscUiDispatchWorkaroundTests
{
    [TestMethod]
    public void ResolvesTheActualSdkClientField()
    {
        Type managerType = typeof(VRChatOSCClient).Assembly.GetType("VRCOSC.App.AppManager", throwOnError: true)!;
        object manager = RuntimeHelpers.GetUninitializedObject(managerType);
        var client = new VRChatOSCClient();
        managerType.GetField("VRChatOscClient")!.SetValue(manager, client);

        Assert.AreSame(client, VrcOscUiDispatchWorkaround.ResolveClient(manager));
        Assert.AreSame(client, VrcOscUiDispatchWorkaround.ResolveClient(new PropertyManager(client)));
        Assert.IsNull(VrcOscUiDispatchWorkaround.ResolveClient(new object()));
    }

    [TestMethod]
    public void RealSdkPreviewIsPatchedOffThreadAndMovementNeverWaitsForUi()
    {
        OnSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // Avoid starting the entire host from the view constructor. Only its dispatcher
            // and actual SDK message handler are used; non-chat traffic must never call it.
            var view = (ChatBoxPreviewView)RuntimeHelpers.GetUninitializedObject(typeof(ChatBoxPreviewView));
            typeof(DispatcherObject).GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(view, dispatcher);
            var handler = view.GetType().GetMethod("OnVRChatOSCMessageSent", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<VRChatOSCMessage>>(view);
            var client = new VRChatOSCClient();
            int otherCalls = 0;
            client.OnVRChatOSCMessageSent += handler;
            client.OnVRChatOSCMessageSent += _ => Interlocked.Increment(ref otherCalls);
            int queued = 0;
            dispatcher.Hooks.OperationPosted += (_, _) => Interlocked.Increment(ref queued);

            // The UI thread is deliberately not pumping during the entire send workload.
            Task worker = Task.Run(() =>
            {
                Assert.AreEqual(1, VrcOscUiDispatchWorkaround.PatchObservers(client));
                for (int i = 0; i < 100_000; i++)
                    client.OnVRChatOSCMessageSent(new VRChatOSCMessage(new OSCMessage("/input/Horizontal", 0.5f)));
                Assert.AreEqual(0, VrcOscUiDispatchWorkaround.PatchObservers(client));
            });
            Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(10)), "Movement waited on a blocked UI dispatcher.");
            Assert.AreEqual(100_000, otherCalls);
            Assert.AreEqual(0, queued);
            GC.KeepAlive(view);
        });
    }

    [TestMethod]
    public void DeadSdkPreviewSubscriptionsArePrunedAcrossRepeatedViews()
    {
        OnSta(() =>
        {
            var client = new VRChatOSCClient();
            for (int i = 0; i < 100; i++)
                AddAbandonedSdkPreview(client);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.AreEqual(0, VrcOscUiDispatchWorkaround.PatchObservers(client));
            Assert.IsNull(client.OnVRChatOSCMessageSent);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddAbandonedSdkPreview(VRChatOSCClient client)
    {
        var view = (ChatBoxPreviewView)RuntimeHelpers.GetUninitializedObject(typeof(ChatBoxPreviewView));
        typeof(DispatcherObject).GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, Dispatcher.CurrentDispatcher);
        client.OnVRChatOSCMessageSent += typeof(ChatBoxPreviewView)
            .GetMethod("OnVRChatOSCMessageSent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<VRChatOSCMessage>>(view);
        Assert.AreEqual(1, VrcOscUiDispatchWorkaround.PatchObservers(client));
        GC.KeepAlive(view);
    }

    [TestMethod]
    public void BlockedUiQueuesOnePreviewAndDeliversOnlyTheLatestValue()
    {
        OnSta(() =>
        {
            var target = new PreviewTarget();
            var observer = new LatestDispatcherObserver<PreviewTarget, string>(target, static (view, value) => view.Values.Add(value));
            int queued = 0;
            target.Dispatcher.Hooks.OperationPosted += (_, _) => queued++;
            Task worker = Task.Run(() =>
            {
                for (int i = 0; i < 100_000; i++)
                    observer.Publish(i.ToString());
            });
            Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1, queued);
            Assert.AreEqual(0, target.Values.Count);
            Pump(target.Dispatcher);
            CollectionAssert.AreEqual(new[] { "99999" }, target.Values);

            observer.Publish("next");
            Pump(target.Dispatcher);
            CollectionAssert.AreEqual(new[] { "99999", "next" }, target.Values);
            target.Dispatcher.InvokeShutdown();
            Assert.IsFalse(observer.IsAlive);
            observer.Publish("after shutdown");
            Assert.AreEqual(2, target.Values.Count);
        });
    }

    [TestMethod]
    public void QueuedPreviewDoesNotKeepClosedViewsAlive()
    {
        OnSta(() =>
        {
            var observer = CreateAbandonedPreview();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(observer.IsAlive);
            Pump(Dispatcher.CurrentDispatcher);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LatestDispatcherObserver<PreviewTarget, string> CreateAbandonedPreview()
    {
        var observer = new LatestDispatcherObserver<PreviewTarget, string>(new PreviewTarget(), static (_, _) => Assert.Fail("Dead view invoked"));
        observer.Publish("queued");
        return observer;
    }

    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "STA test did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class PropertyManager(VRChatOSCClient client)
    {
        public VRChatOSCClient VRChatOscClient { get; } = client;
    }

    private sealed class PreviewTarget : DispatcherObject
    {
        public List<string> Values { get; } = [];
    }
}
