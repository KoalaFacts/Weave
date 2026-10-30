namespace Weave.Tools.Connectors;

// Read-only guard before decoding/materializing lines. The caller owns inner.
// At most one byte beyond the budget is requested to detect overflow at EOF.
internal sealed class McpResponseReadStream(Stream inner, int limit) : Stream
{
    private long _consumed;
    public bool LimitExceeded { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _consumed; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        Record(inner.Read(buffer, offset, Allowed(count)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Record(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Allowed(int requested)
    {
        if (LimitExceeded)
            throw new IOException("MCP response byte limit exceeded");
        return (int)Math.Min(requested, (long)limit - _consumed + 1);
    }

    private int Record(int read)
    {
        _consumed += read;
        if (_consumed > limit)
        {
            LimitExceeded = true;
            throw new IOException("MCP response byte limit exceeded");
        }
        return read;
    }
}
