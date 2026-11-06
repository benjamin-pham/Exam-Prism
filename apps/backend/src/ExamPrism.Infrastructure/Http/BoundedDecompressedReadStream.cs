namespace ExamPrism.Infrastructure.Http;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Common.Exceptions;
using Common.Telemetry;

public sealed class BoundedDecompressedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxAllowedBytes;
    private long _bytesRead;

    public BoundedDecompressedReadStream(Stream inner, long maxAllowedBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxAllowedBytes = maxAllowedBytes;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        CheckLimit(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        CheckLimit(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        CheckLimit(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        CheckLimit(read);
        return read;
    }

    private void CheckLimit(int bytesJustRead)
    {
        if (bytesJustRead <= 0) return;

        _bytesRead += bytesJustRead;
        if (_maxAllowedBytes > 0 && _bytesRead > _maxAllowedBytes)
        {
            ExamPrismDiagnostics.RecordDecompressionBombRejection();
            throw new PayloadTooLargeDecompressionException(_maxAllowedBytes);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

