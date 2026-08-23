using System.Text.Json;
using CrookedToe.Modules.Diagnostics;

namespace CrookedToesModules.Tests.Diagnostics;

[TestClass]
public sealed class BoundedDiagnosticsTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"crookedtoe-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void WriterFlushesQueuedRecordsDuringDispose()
    {
        using (var writer = new DiagnosticWriter(_directory, capacity: 4))
            writer.TryWrite(DiagnosticRecord.Event("test-module", "test-event", "detail"));

        string path = Path.Combine(_directory, "crookedtoe-diagnostics.jsonl");
        Assert.IsTrue(File.Exists(path));
        string[] lines = File.ReadAllLines(path);
        Assert.IsTrue(lines.Any(line =>
        {
            using JsonDocument json = JsonDocument.Parse(line);
            return json.RootElement.TryGetProperty("Name", out JsonElement name) &&
                   name.GetString() == "test-event";
        }));
    }

    [TestMethod]
    public async Task FullFileRotatesBeforeWritingNextRecord()
    {
        string path = Path.Combine(_directory, "crookedtoe-diagnostics.jsonl");
        using (FileStream stream = File.Create(path))
            stream.SetLength(BoundedDiagnostics.MaxFileBytes);

        await using (var file = new RollingJsonFile(path))
            await file.WriteAsync(DiagnosticRecord.Event("test-module", "after-rotation", null));

        Assert.AreEqual(BoundedDiagnostics.MaxFileBytes, new FileInfo($"{path}.1").Length);
        Assert.IsTrue(new FileInfo(path).Length < BoundedDiagnostics.MaxFileBytes);
        StringAssert.Contains(File.ReadAllText(path), "after-rotation");
    }
}
