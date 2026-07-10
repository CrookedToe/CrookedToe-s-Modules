using System.IO;
using System.Text.Json;

namespace CrookedToe.Modules.OSCLeash;

internal sealed class LeashTraceRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private StreamWriter? _writer;
    private DateTime _lastSampleAt;
    private DateTime _lastFlushAt;
    private bool _failed;

    public void SetEnabled(bool enabled, Action<string> log)
    {
        if (enabled)
        {
            EnsureOpen(log);
            return;
        }

        Close(log);
        _failed = false;
    }

    public void Record(object frame, Action<string> log)
    {
        if (_writer is null)
            return;

        DateTime now = DateTime.UtcNow;
        if (now - _lastSampleAt < SampleInterval)
            return;

        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(frame, JsonOptions));
            _lastSampleAt = now;
            if (now - _lastFlushAt >= FlushInterval)
            {
                _writer.Flush();
                _lastFlushAt = now;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            log($"Warning: OSC Leash trace failed: {ex.Message}");
            Close(log);
        }
    }

    public void Dispose() => Close(null);

    private void EnsureOpen(Action<string> log)
    {
        if (_writer is not null || _failed)
            return;

        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.CurrentDirectory;

            string directory = Path.Combine(root, "VRCOSC", "OSCLeash", "Debug");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"osc-leash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
            _writer = new StreamWriter(path, append: false);
            _lastSampleAt = DateTime.MinValue;
            _lastFlushAt = DateTime.UtcNow;
            log($"OSC Leash trace recording to {path}");
        }
        catch (Exception ex)
        {
            _failed = true;
            log($"Warning: OSC Leash trace could not start: {ex.Message}");
        }
    }

    private void Close(Action<string>? log)
    {
        if (_writer is null)
            return;

        try
        {
            _writer.Dispose();
            log?.Invoke("OSC Leash trace stopped");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: OSC Leash trace could not close cleanly: {ex.Message}");
        }
        finally
        {
            _writer = null;
        }
    }
}
