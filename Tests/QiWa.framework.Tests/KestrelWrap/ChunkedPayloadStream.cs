#pragma warning disable CS1591
namespace Tests.KestrelWrap;

/// <summary>
/// 为 ReadRequest 测试准备的可分块输出流。
/// 既可以按固定 chunk 大小吐出数据，也可以通过 totalBytes 伪造大请求和 chunked 请求。
/// </summary>
internal sealed class ChunkedPayloadStream : Stream
{
    private readonly byte[] payload;
    private readonly int chunkSize;
    private readonly int totalBytes;
    private readonly bool cancelReads;
    private int position;

    public ChunkedPayloadStream(byte[] payload, int chunkSize, int? totalBytes = null, bool cancelReads = false)
    {
        this.payload = payload.Length == 0 ? new byte[] { 0 } : payload;
        this.chunkSize = chunkSize;
        this.totalBytes = totalBytes ?? payload.Length;
        this.cancelReads = cancelReads;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (cancelReads)
        {
            throw new OperationCanceledException();
        }
        return FillBuffer(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (cancelReads)
        {
            throw new OperationCanceledException();
        }
        return FillBuffer(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancelReads || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }
        return ValueTask.FromResult(FillBuffer(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancelReads || cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }
        return Task.FromResult(FillBuffer(buffer.AsSpan(offset, count)));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int FillBuffer(Span<byte> destination)
    {
        if (position >= totalBytes)
        {
            return 0;
        }

        int count = Math.Min(Math.Min(destination.Length, chunkSize), totalBytes - position);
        for (int i = 0; i < count; i++)
        {
            destination[i] = payload[(position + i) % payload.Length];
        }
        position += count;
        return count;
    }
}
