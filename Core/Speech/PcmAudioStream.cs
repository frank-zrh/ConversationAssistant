using System.Collections.Concurrent;

namespace ConversationAssistant.Core.Speech;

public sealed class PcmAudioStream : Stream
{
    private readonly BlockingCollection<byte[]> _chunks = new(100);
    private byte[]? _current;
    private int _offset;
    private long _position;
    private int _completed;
    private int _disposed;

    public void WriteChunk(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length == 0) throw new ArgumentException("Audio chunk must contain samples.", nameof(pcm));
        if (!_chunks.TryAdd(pcm))
            throw new InvalidOperationException("Speech engine fell behind microphone input.");
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _chunks.CompleteAdding();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count) throw new ArgumentException("Invalid buffer range.");
        if (count == 0) return 0;
        var read = 0;
        while (read < count)
        {
            if (_current is null || _offset == _current.Length)
            {
                if (!_chunks.TryTake(out _current, Timeout.Infinite)) break;
                _offset = 0;
            }
            var size = Math.Min(count - read, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset + read, size);
            _offset += size;
            read += size;
        }
        _position += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Complete();
            _chunks.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    // System.Speech's SpStreamWrapper uses -1 for a live stream of unknown length.
    public override long Length => -1;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (offset == 0 && origin == SeekOrigin.Current) return Position;
        if (offset == 0 && origin == SeekOrigin.Begin && Position == 0) return 0;
        throw new NotSupportedException("Live microphone audio cannot be rewound.");
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
