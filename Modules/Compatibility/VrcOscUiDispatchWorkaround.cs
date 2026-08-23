using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VRCOSC.App.OSC.VRChat;
using VRCOSC.App.UI.Views.ChatBox;

namespace CrookedToe.Modules.Compatibility;

/// <summary>
/// VRCOSC 2026 synchronously invokes OSC-sent observers. ChatBoxPreviewView then synchronously
/// invokes the UI dispatcher before checking the OSC address, coupling every module send to UI health.
/// Replace only that known handler with an address-filtered asynchronous dispatcher wrapper.
/// </summary>
internal static class VrcOscUiDispatchWorkaround
{
    private static readonly object Gate = new();
    private static readonly long ScanIntervalTicks = Stopwatch.Frequency;
    private static long _nextScanTimestamp;

    public static int ApplyIfDue(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force)
        {
            long due = Volatile.Read(ref _nextScanTimestamp);
            if (now < due || Interlocked.CompareExchange(ref _nextScanTimestamp, now + ScanIntervalTicks, due) != due)
                return 0;
        }
        else
        {
            Volatile.Write(ref _nextScanTimestamp, now + ScanIntervalTicks);
        }

        lock (Gate)
        {
            VRChatOSCClient? client = ResolveClient();
            if (client is null)
                return 0;
            Action<VRChatOSCMessage>? observers = client.OnVRChatOSCMessageSent;
            if (observers is null)
                return 0;

            int replaced = 0;
            foreach (Action<VRChatOSCMessage> observer in observers.GetInvocationList().Cast<Action<VRChatOSCMessage>>())
            {
                if (observer.Target is not ChatBoxPreviewView view)
                    continue;

                client.OnVRChatOSCMessageSent -= observer;
                Action<VRChatOSCMessage>? wrapper = null;
                wrapper = message =>
                {
                    if (!message.IsChatboxInput)
                        return;
                    if (view.Dispatcher.HasShutdownStarted || view.Dispatcher.HasShutdownFinished)
                    {
                        client.OnVRChatOSCMessageSent -= wrapper;
                        return;
                    }

                    _ = view.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => observer(message)));
                };
                client.OnVRChatOSCMessageSent += wrapper;
                Window? owner = Window.GetWindow(view);
                if (owner?.GetType().FullName == "VRCOSC.App.UI.Windows.ChatBox.ChatBoxPreviewWindow")
                    owner.Closed += RemoveWhenPopoutCloses;
                replaced++;

                void RemoveWhenPopoutCloses(object? sender, EventArgs args)
                {
                    client.OnVRChatOSCMessageSent -= wrapper;
                    if (owner is not null)
                        owner.Closed -= RemoveWhenPopoutCloses;
                }
            }

            return replaced;
        }
    }

    private static VRChatOSCClient? ResolveClient()
    {
        Type? appManagerType = typeof(VRChatOSCClient).Assembly.GetType("VRCOSC.App.AppManager");
        MethodInfo? getInstance = appManagerType?.GetMethod(
            "GetInstance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? appManager = getInstance?.Invoke(null, null);
        PropertyInfo? clientProperty = appManagerType?.GetProperty(
            "VRChatOscClient",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return clientProperty?.GetValue(appManager) as VRChatOSCClient;
    }
}
