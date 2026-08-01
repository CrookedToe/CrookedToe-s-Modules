using System.Diagnostics;

namespace CrookedToe.Modules.OSCAudioReaction;

internal sealed class LatestAudioFrameBuffer
{
    public const int MaximumCapacity = 1024 * 1024;
    private const int MinimumCapacity = 64 * 1024;

    private readonly object _sync = new();
    private readonly byte[] _buffer;
    private bool _hasUnreadFrame;
    private int _bytesRecorded;
    private long _timestamp;

    public LatestAudioFrameBuffer(int requestedCapacity, int blockAlign)
    {
        BlockAlign = Math.Max(1, blockAlign);
        int alignedMinimum = AlignDown(Math.Max(MinimumCapacity, requestedCapacity), BlockAlign);
        Capacity = Math.Clamp(alignedMinimum, BlockAlign, AlignDown(MaximumCapacity, BlockAlign));
        _buffer = new byte[Capacity];
    }

    public int BlockAlign { get; }
    public int Capacity { get; }
    public long ReceivedFrames { get; private set; }
    public long CoalescedFrames { get; private set; }
    public long TruncatedFrames { get; private set; }

    public void Write(byte[] source, int bytesRecorded, long? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        int available = Math.Clamp(bytesRecorded, 0, source.Length);
        int bytesToCopy = AlignDown(Math.Min(available, Capacity), BlockAlign);
        int sourceOffset = AlignDown(available - bytesToCopy, BlockAlign);

        lock (_sync)
        {
            ReceivedFrames++;
            if (_hasUnreadFrame)
                CoalescedFrames++;
            if (available > Capacity)
                TruncatedFrames++;

            if (bytesToCopy > 0)
                Buffer.BlockCopy(source, sourceOffset, _buffer, 0, bytesToCopy);

            _bytesRecorded = bytesToCopy;
            _timestamp = timestamp ?? Stopwatch.GetTimestamp();
            _hasUnreadFrame = bytesToCopy > 0;
        }
    }

    public bool TryReadLatest(byte[] destination, out int bytesRecorded, out long timestamp)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < Capacity)
            throw new ArgumentException($"Destination must be at least {Capacity} bytes.", nameof(destination));

        lock (_sync)
        {
            if (!_hasUnreadFrame)
            {
                bytesRecorded = 0;
                timestamp = 0;
                return false;
            }

            Buffer.BlockCopy(_buffer, 0, destination, 0, _bytesRecorded);
            bytesRecorded = _bytesRecorded;
            timestamp = _timestamp;
            _hasUnreadFrame = false;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _hasUnreadFrame = false;
            _bytesRecorded = 0;
            _timestamp = 0;
        }
    }

    private static int AlignDown(int value, int alignment)
        => value - (value % alignment);
}
