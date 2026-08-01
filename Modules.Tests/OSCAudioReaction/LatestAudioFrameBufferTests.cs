using CrookedToe.Modules.OSCAudioReaction;

namespace CrookedToesModules.Tests.OSCAudioReaction;

[TestClass]
public sealed class LatestAudioFrameBufferTests
{
    [TestMethod]
    public void MultipleWritesCoalesceToTheNewestCompleteFrame()
    {
        var mailbox = new LatestAudioFrameBuffer(64 * 1024, blockAlign: 8);
        var destination = new byte[mailbox.Capacity];

        mailbox.Write([1, 2, 3, 4, 5, 6, 7, 8], 8, timestamp: 10);
        mailbox.Write([9, 10, 11, 12, 13, 14, 15, 16], 8, timestamp: 20);

        Assert.IsTrue(mailbox.TryReadLatest(destination, out int bytesRecorded, out long timestamp));
        CollectionAssert.AreEqual(
            new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 },
            destination[..bytesRecorded]);
        Assert.AreEqual(20, timestamp);
        Assert.AreEqual(2, mailbox.ReceivedFrames);
        Assert.AreEqual(1, mailbox.CoalescedFrames);
        Assert.IsFalse(mailbox.TryReadLatest(destination, out _, out _));
    }

    [TestMethod]
    public void OversizedFramesRemainBoundedAndBlockAligned()
    {
        var mailbox = new LatestAudioFrameBuffer(LatestAudioFrameBuffer.MaximumCapacity, blockAlign: 12);
        var source = new byte[LatestAudioFrameBuffer.MaximumCapacity + 97];
        for (int i = 0; i < source.Length; i++)
            source[i] = (byte)(i % 251);
        var destination = new byte[mailbox.Capacity];

        mailbox.Write(source, source.Length);

        Assert.IsTrue(mailbox.TryReadLatest(destination, out int bytesRecorded, out _));
        Assert.IsTrue(bytesRecorded <= LatestAudioFrameBuffer.MaximumCapacity);
        Assert.AreEqual(0, bytesRecorded % 12);
        Assert.AreEqual(1, mailbox.TruncatedFrames);
    }

    [TestMethod]
    public void ProlongedProducerOverrunRetainsOnlyOneLatestFrame()
    {
        var mailbox = new LatestAudioFrameBuffer(64 * 1024, blockAlign: 8);
        var source = new byte[8];
        var destination = new byte[mailbox.Capacity];

        for (int frame = 1; frame <= 100_000; frame++)
        {
            BitConverter.TryWriteBytes(source, frame);
            mailbox.Write(source, source.Length, timestamp: frame);
        }

        Assert.IsTrue(mailbox.TryReadLatest(destination, out int bytesRecorded, out long timestamp));
        Assert.AreEqual(8, bytesRecorded);
        Assert.AreEqual(100_000, timestamp);
        Assert.AreEqual(100_000, mailbox.ReceivedFrames);
        Assert.AreEqual(99_999, mailbox.CoalescedFrames);
        Assert.AreEqual(100_000, BitConverter.ToInt32(destination));
        Assert.IsFalse(mailbox.TryReadLatest(destination, out _, out _));
    }
}
