namespace Gitfs.Core.Objects;

/// <summary>Read-only обёртка, не дающая прочитать больше заявленного размера
/// объекта: zlib-поток пака физически продолжается следующими объектами.</summary>
internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public LimitedReadStream(Stream inner, long limit)
    {
        _inner = inner;
        _remaining = limit;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        var slice = _remaining < buffer.Length ? buffer[..(int)_remaining] : buffer;
        var n = _inner.Read(slice);
        _remaining -= n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
