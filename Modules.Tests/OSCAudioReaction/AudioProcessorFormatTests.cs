using CrookedToe.Modules.OSCAudioReaction;

namespace CrookedToesModules.Tests.OSCAudioReaction;

[TestClass]
public sealed class AudioProcessorFormatTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(6)]
    public void FloatCaptureUsesFramesAndHandlesDeviceChannelCount(int channels)
    {
        var settings = new AudioSettings
        {
            SampleRate = 48_000,
            Channels = channels,
            EnableAGC = false,
            Gain = 1f
        };
        using var processor = new AudioProcessor(settings);
        var samples = new float[512 * channels];
        Array.Fill(samples, 0.25f);
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        AudioProcessingResult result = processor.ProcessAudio(bytes, bytes.Length);

        Assert.IsTrue(float.IsFinite(result.Volume));
        Assert.IsTrue(float.IsFinite(result.Direction));
        Assert.IsTrue(result.Volume > 0f);
        Assert.AreEqual(0.5f, result.Direction, 0.0001f);
        Assert.IsTrue(result.FrequencyBands.All(float.IsFinite));
    }
}
