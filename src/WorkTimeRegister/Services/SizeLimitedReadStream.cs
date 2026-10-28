namespace WorkTimeRegister.Services;

internal sealed class SizeLimitedReadStream(Stream source, long maximumBytes) : Stream
{
    private long bytesRead;

    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => bytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(source.Read(buffer, offset, Limit(count)));

    public override int Read(Span<byte> buffer) => Count(source.Read(buffer[..Limit(buffer.Length)]));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await source.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Limit(int requested) => (int)Math.Min(requested, maximumBytes - bytesRead + 1);

    private int Count(int count)
    {
        bytesRead += count;
        if (bytesRead > maximumBytes)
            throw new InvalidDataException("JSON-файл превышает допустимый размер для импорта.");
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
