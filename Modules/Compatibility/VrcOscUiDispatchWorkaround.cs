using System.Diagnostics;
using System.Reflection;
using VRCOSC.App.OSC.VRChat;
using VRCOSC.App.UI.Views.ChatBox;

namespace CrookedToe.Modules.Compatibility;

/// <summary>
/// VRCOSC invokes sent observers synchronously. Its preview dispatches to the UI even for
/// movement packets. Filter those packets before dispatch and keep only the latest preview.
/// </summary>
internal static class VrcOscUiDispatchWorkaround
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly object Gate = new();
    private static readonly Type? ManagerType = typeof(VRChatOSCClient).Assembly.GetType("VRCOSC.App.AppManager");
    private static readonly MethodInfo? GetManager = ManagerType?.GetMethod(
        "GetInstance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    private static long _nextScanTimestamp;

    public static string Status { get; private set; } = "not_scanned";

    public static int ApplyIfDue(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force)
        {
            long due = Volatile.Read(ref _nextScanTimestamp);
            if (now < due || Interlocked.CompareExchange(ref _nextScanTimestamp, now + Stopwatch.Frequency, due) != due)
                return 0;
        }
        else
            Volatile.Write(ref _nextScanTimestamp, now + Stopwatch.Frequency);

        lock (Gate)
        {
            object? manager = GetManager?.Invoke(null, null);
            VRChatOSCClient? client = manager is null ? null : ResolveClient(manager);
            if (client is null)
            {
                Status = "client_unavailable";
                return 0;
            }

            int replaced = PatchObservers(client);
            Status = $"client_resolved;patchedObservers={replaced}";
            return replaced;
        }
    }

    // This is a FIELD in both the SDK and the installed host. Support a property too,
    // but never silently turn the protection off by assuming one member shape.
    internal static VRChatOSCClient? ResolveClient(object manager)
    {
        Type type = manager.GetType();
        return (type.GetField("VRChatOscClient", InstanceFlags)?.GetValue(manager)
            ?? type.GetProperty("VRChatOscClient", InstanceFlags)?.GetValue(manager)) as VRChatOSCClient;
    }

    internal static int PatchObservers(VRChatOSCClient client)
    {
        Action<VRChatOSCMessage>? observers = client.OnVRChatOSCMessageSent;
        if (observers is null)
            return 0;

        int replaced = 0;
        foreach (Action<VRChatOSCMessage> observer in observers.GetInvocationList())
        {
            if (observer.Target is PreviewSubscription previous && !previous.IsAlive)
            {
                ReplaceObserver(client, observer, null);
                continue;
            }
            if (observer.Target is not ChatBoxPreviewView view || observer.Method.Name != "OnVRChatOSCMessageSent")
                continue;

            // An open delegate does not retain the view. No Window.GetWindow or other
            // dependency-property access is allowed on this module worker thread.
            var callback = observer.Method.CreateDelegate<Action<ChatBoxPreviewView, VRChatOSCMessage>>();
            var subscription = new PreviewSubscription(view, callback);
            ReplaceObserver(client, observer, subscription.Send);
            replaced++;
        }
        return replaced;
    }

    private static void ReplaceObserver(VRChatOSCClient client, Action<VRChatOSCMessage> oldObserver,
        Action<VRChatOSCMessage>? newObserver)
    {
        Action<VRChatOSCMessage>? before;
        Action<VRChatOSCMessage>? after;
        do
        {
            before = client.OnVRChatOSCMessageSent;
            after = (Action<VRChatOSCMessage>?)Delegate.Remove(before, oldObserver);
            if (newObserver is not null)
                after = (Action<VRChatOSCMessage>?)Delegate.Combine(after, newObserver);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref client.OnVRChatOSCMessageSent, after, before), before));
    }

    private sealed class PreviewSubscription(ChatBoxPreviewView view, Action<ChatBoxPreviewView, VRChatOSCMessage> callback)
    {
        private readonly LatestDispatcherObserver<ChatBoxPreviewView, VRChatOSCMessage> _observer = new(view, callback);
        public bool IsAlive => _observer.IsAlive;
        public void Send(VRChatOSCMessage message)
        {
            if (message.IsChatboxInput)
                _observer.Publish(message);
        }
    }
}
