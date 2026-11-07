namespace FC.SDK.Transport;

/// <summary>
/// Fails a read that sees no byte for <paramref name="idle"/>. A deadline on the whole reply would cut
/// a 25 MB download short over a slow link; silence is what marks a camera that stopped answering.
/// </summary>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idle) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(idle);
        try
        {
            return await inner.ReadAsync(buffer, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no byte received for {idle.TotalSeconds:0.#} s");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        inner.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        inner.WriteAsync(buffer, offset, count, ct);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Synchronous reads have no idle deadline; use ReadAsync.");

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
