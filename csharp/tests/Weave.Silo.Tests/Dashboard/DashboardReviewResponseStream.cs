namespace Weave.Silo.Tests.Dashboard;

internal sealed class DashboardReviewResponseStream(long byteCount, bool failRead = false) : Stream
{
    public long BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadBytes(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadBytes(buffer.Span));
    }

    private int ReadBytes(Span<byte> buffer)
    {
        if (failRead)
            throw new IOException("private-stream-failure-marker");
        var count = (int)Math.Min(buffer.Length, byteCount - BytesRead);
        buffer[..count].Fill((byte)'x');
        BytesRead += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
