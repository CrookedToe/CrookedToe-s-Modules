using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace CrookedToe.Modules.Diagnostics;

/// <summary>
/// Process-wide, bounded diagnostics for investigating degradation that appears over time.
/// The callback path only updates counters; JSON and disk I/O happen on one background worker.
/// </summary>
internal static class BoundedDiagnostics
{
    internal const long MaxFileBytes = 16 * 1024 * 1024;
    internal const int RetainedFileCount = 8;
    private const int QueueCapacity = 1024;
    private static readonly object Gate = new();
    private static DiagnosticWriter? _writer;
    private static int _users;

    public static ModuleDiagnostics StartModule(string moduleName)
    {
        lock (Gate)
        {
            _writer ??= new DiagnosticWriter(ResolveDirectory(), QueueCapacity);
            _users++;
            var diagnostics = new ModuleDiagnostics(moduleName, _writer);
            diagnostics.Event("module_start");
            diagnostics.Event("module_build",
                $"moduleMvid={typeof(BoundedDiagnostics).Module.ModuleVersionId};" +
                $"hostVersion={typeof(VRCOSC.App.OSC.VRChat.VRChatOSCClient).Assembly.GetName().Version}");
            return diagnostics;
        }
    }

    internal static void StopModule(ModuleDiagnostics module)
    {
        lock (Gate)
        {
            module.Event("module_stop");
            _users = Math.Max(0, _users - 1);
            if (_users != 0 || _writer is null)
                return;

            _writer.Dispose();
            _writer = null;
        }
    }

    private static string ResolveDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("CROOKEDTOE_DIAGNOSTICS_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRCOSC", "diagnostics", "CrookedToe");
    }
}

internal sealed class ModuleDiagnostics : IDisposable
{
    private readonly DiagnosticWriter _writer;
    private int _disposed;

    internal ModuleDiagnostics(string moduleName, DiagnosticWriter writer)
    {
        ModuleName = moduleName;
        _writer = writer;
    }

    public string ModuleName { get; }
    public string LogDirectory => _writer.DirectoryPath;

    public DiagnosticProbe CreateProbe(string operation, double expectedIntervalMilliseconds = 0)
        => new(ModuleName, operation, expectedIntervalMilliseconds, _writer);

    public void Event(string name, string? detail = null)
        => _writer.TryWrite(DiagnosticRecord.Event(ModuleName, name, detail));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            BoundedDiagnostics.StopModule(this);
    }
}

internal sealed class DiagnosticProbe
{
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency * 10;
    private readonly string _module;
    private readonly string _operation;
    private readonly long _expectedIntervalTicks;
    private readonly DiagnosticWriter _writer;
    private long _nextReportTimestamp;
    private long _lastStartTimestamp;
    private long _count;
    private long _totalDurationTicks;
    private long _maximumDurationTicks;
    private long _maximumGapTicks;
    private long _lateStarts;
    private long _allocatedBytes;
    private long _failures;

    internal DiagnosticProbe(string module, string operation, double expectedIntervalMilliseconds, DiagnosticWriter writer)
    {
        _module = module;
        _operation = operation;
        _writer = writer;
        _expectedIntervalTicks = expectedIntervalMilliseconds <= 0
            ? 0
            : (long)(expectedIntervalMilliseconds / 1000d * Stopwatch.Frequency);
        _nextReportTimestamp = Stopwatch.GetTimestamp() + ReportIntervalTicks;
    }

    public DiagnosticScope Measure() => new(this);

    internal void Complete(long started, long durationTicks, long allocatedBytes, bool failed)
    {
        long priorStart = Interlocked.Exchange(ref _lastStartTimestamp, started);
        long gap = priorStart == 0 ? 0 : started - priorStart;
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalDurationTicks, durationTicks);
        Interlocked.Add(ref _allocatedBytes, Math.Max(0, allocatedBytes));
        if (failed)
            Interlocked.Increment(ref _failures);
        UpdateMaximum(ref _maximumDurationTicks, durationTicks);
        if (gap > 0)
            UpdateMaximum(ref _maximumGapTicks, gap);
        if (_expectedIntervalTicks > 0 && gap > _expectedIntervalTicks * 3 / 2)
            Interlocked.Increment(ref _lateStarts);

        TryReport(started + durationTicks);
    }

    private void TryReport(long now)
    {
        long due = Volatile.Read(ref _nextReportTimestamp);
        if (now < due || Interlocked.CompareExchange(ref _nextReportTimestamp, now + ReportIntervalTicks, due) != due)
            return;

        long count = Interlocked.Exchange(ref _count, 0);
        long total = Interlocked.Exchange(ref _totalDurationTicks, 0);
        long maximum = Interlocked.Exchange(ref _maximumDurationTicks, 0);
        long maxGap = Interlocked.Exchange(ref _maximumGapTicks, 0);
        long late = Interlocked.Exchange(ref _lateStarts, 0);
        long allocated = Interlocked.Exchange(ref _allocatedBytes, 0);
        long failures = Interlocked.Exchange(ref _failures, 0);
        if (count == 0)
            return;

        _writer.TryWrite(DiagnosticRecord.Probe(
            _module,
            _operation,
            count,
            TicksToMilliseconds(total) / count,
            TicksToMilliseconds(maximum),
            TicksToMilliseconds(maxGap),
            late,
            allocated,
            failures));
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static void UpdateMaximum(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}

internal readonly struct DiagnosticScope : IDisposable
{
    private readonly DiagnosticProbe _probe;
    private readonly long _started;
    private readonly long _allocatedAtStart;
    private readonly int _threadId;
    private readonly bool _valid;
    private readonly bool _failed;

    internal DiagnosticScope(DiagnosticProbe probe)
    {
        _probe = probe;
        _started = Stopwatch.GetTimestamp();
        _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        _threadId = Environment.CurrentManagedThreadId;
        _valid = true;
        _failed = false;
    }

    private DiagnosticScope(DiagnosticScope source, bool failed)
    {
        _probe = source._probe;
        _started = source._started;
        _allocatedAtStart = source._allocatedAtStart;
        _threadId = source._threadId;
        _valid = source._valid;
        _failed = failed;
    }

    public DiagnosticScope Failed() => new(this, failed: true);

    public void Dispose()
    {
        if (!_valid)
            return;

        long finished = Stopwatch.GetTimestamp();
        long allocated = _threadId == Environment.CurrentManagedThreadId
            ? GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart
            : 0;
        _probe.Complete(_started, finished - _started, allocated, _failed);
    }
}

internal sealed class DiagnosticWriter : IDisposable
{
    private static readonly TimeSpan RuntimeSampleInterval = TimeSpan.FromSeconds(10);
    private readonly Channel<DiagnosticRecord> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long _droppedRecords;

    internal DiagnosticWriter(string directoryPath, int capacity)
    {
        DirectoryPath = directoryPath;
        _channel = Channel.CreateBounded<DiagnosticRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(WriteLoopAsync);
    }

    public string DirectoryPath { get; }

    public void TryWrite(DiagnosticRecord record)
    {
        if (!_channel.Writer.TryWrite(record))
            Interlocked.Increment(ref _droppedRecords);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string activePath = Path.Combine(DirectoryPath, "crookedtoe-diagnostics.jsonl");
            await using var file = new RollingJsonFile(activePath);
            using var process = Process.GetCurrentProcess();
            TimeSpan priorCpu = process.TotalProcessorTime;
            long priorTimestamp = Stopwatch.GetTimestamp();
            long nextRuntimeSample = priorTimestamp + (long)(RuntimeSampleInterval.TotalSeconds * Stopwatch.Frequency);

            TryWrite(DiagnosticRecord.Session("session_start", Environment.ProcessId, Environment.Version.ToString()));
            while (await _channel.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out DiagnosticRecord? record))
                {
                    if (record is not null)
                        await file.WriteAsync(record).ConfigureAwait(false);
                }
                await file.FlushAsync().ConfigureAwait(false);

                long now = Stopwatch.GetTimestamp();
                if (now < nextRuntimeSample)
                    continue;

                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                double elapsedSeconds = (now - priorTimestamp) / (double)Stopwatch.Frequency;
                double cpuPercent = elapsedSeconds <= 0 ? 0 :
                    (cpu - priorCpu).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100d;
                priorCpu = cpu;
                priorTimestamp = now;
                nextRuntimeSample = now + (long)(RuntimeSampleInterval.TotalSeconds * Stopwatch.Frequency);
                await file.WriteAsync(DiagnosticRecord.Runtime(
                    process,
                    cpuPercent,
                    Interlocked.Exchange(ref _droppedRecords, 0))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch
        {
            // Diagnostics must never take down a module. A later module start creates a fresh writer.
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryWrite(DiagnosticRecord.Session("session_stop", Environment.ProcessId, null));
        _channel.Writer.Complete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
    }
}

internal sealed class RollingJsonFile : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly string _path;
    private FileStream _stream;

    public RollingJsonFile(string path)
    {
        _path = path;
        RotateIfNeeded();
        _stream = Open();
    }

    public async ValueTask WriteAsync(DiagnosticRecord record)
    {
        if (_stream.Length >= BoundedDiagnostics.MaxFileBytes)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            Rotate();
            _stream = Open();
        }

        await JsonSerializer.SerializeAsync(_stream, record, DiagnosticJsonContext.Default.DiagnosticRecord).ConfigureAwait(false);
        await _stream.WriteAsync(NewLine).ConfigureAwait(false);
    }

    public Task FlushAsync() => _stream.FlushAsync();

    private FileStream Open() => new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous);

    private void RotateIfNeeded()
    {
        if (File.Exists(_path) && new FileInfo(_path).Length >= BoundedDiagnostics.MaxFileBytes)
            Rotate();
    }

    private void Rotate()
    {
        string oldest = $"{_path}.{BoundedDiagnostics.RetainedFileCount - 1}";
        if (File.Exists(oldest))
            File.Delete(oldest);
        for (int i = BoundedDiagnostics.RetainedFileCount - 2; i >= 1; i--)
        {
            string source = $"{_path}.{i}";
            if (File.Exists(source))
                File.Move(source, $"{_path}.{i + 1}", overwrite: true);
        }
        if (File.Exists(_path))
            File.Move(_path, $"{_path}.1", overwrite: true);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

internal sealed record DiagnosticRecord
{
    public required DateTimeOffset Utc { get; init; }
    public required string Type { get; init; }
    public string? Module { get; init; }
    public string? Name { get; init; }
    public string? Detail { get; init; }
    public long? Count { get; init; }
    public double? MeanMs { get; init; }
    public double? MaxMs { get; init; }
    public double? MaxGapMs { get; init; }
    public long? LateStarts { get; init; }
    public long? AllocatedBytes { get; init; }
    public long? Failures { get; init; }
    public int? ProcessId { get; init; }
    public double? CpuPercent { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public long? ManagedHeapBytes { get; init; }
    public int? Gen0Collections { get; init; }
    public int? Gen1Collections { get; init; }
    public int? Gen2Collections { get; init; }
    public int? ThreadCount { get; init; }
    public int? ThreadPoolThreads { get; init; }
    public long? ThreadPoolPending { get; init; }
    public long? FinalizationPending { get; init; }
    public long? DroppedRecords { get; init; }
    public long? DroppedModuleLogs { get; init; }

    public static DiagnosticRecord Event(string module, string name, string? detail) =>
        new() { Utc = DateTimeOffset.UtcNow, Type = "event", Module = module, Name = name, Detail = detail };

    public static DiagnosticRecord Session(string name, int processId, string? detail) =>
        new() { Utc = DateTimeOffset.UtcNow, Type = "session", Name = name, ProcessId = processId, Detail = detail };

    public static DiagnosticRecord Probe(string module, string name, long count, double meanMs, double maxMs,
        double maxGapMs, long lateStarts, long allocatedBytes, long failures) => new()
    {
        Utc = DateTimeOffset.UtcNow, Type = "probe", Module = module, Name = name, Count = count,
        MeanMs = meanMs, MaxMs = maxMs, MaxGapMs = maxGapMs, LateStarts = lateStarts,
        AllocatedBytes = allocatedBytes, Failures = failures
    };

    public static DiagnosticRecord Runtime(Process process, double cpuPercent, long droppedRecords)
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new DiagnosticRecord
        {
            Utc = DateTimeOffset.UtcNow, Type = "runtime", ProcessId = process.Id, CpuPercent = cpuPercent,
            WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2),
            ThreadCount = process.Threads.Count, ThreadPoolThreads = ThreadPool.ThreadCount,
            ThreadPoolPending = ThreadPool.PendingWorkItemCount, FinalizationPending = gc.FinalizationPendingCount,
            DroppedRecords = droppedRecords, DroppedModuleLogs = RealtimeModuleLog.DroppedMessages
        };
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(DiagnosticRecord))]
internal partial class DiagnosticJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
