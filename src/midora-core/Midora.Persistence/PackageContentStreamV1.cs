using System.Buffers;
using System.Security.Cryptography;

namespace Midora.Persistence;

/// <summary>One transaction-owned file; self-validation compares bytes, not only digests.</summary>
internal sealed class PackageContentStreamV1 : Stream
{
    private readonly Stream _file;
    private readonly bool _compare;
    private readonly CancellationToken _cancellationToken;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _comparisonBuffer;
    private long _length;
    private bool _completed;

    public PackageContentStreamV1(Stream file, bool compare, CancellationToken cancellationToken)
    {
        _file = file;
        _compare = compare;
        _cancellationToken = cancellationToken;
        if (compare) _comparisonBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    }

    public (long Length, string Sha256) Complete()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_completed) throw new InvalidOperationException("The package content stream was already completed.");
        if (_compare && _file.ReadByte() != -1)
            throw new InvalidDataException("Self-validation produced a shorter structural file.");
        _completed = true;
        return (_length, Convert.ToHexStringLower(_hash.GetHashAndReset()));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_completed) throw new InvalidOperationException("The package content stream is complete.");
        if (_compare)
        {
            ReadOnlySpan<byte> remaining = buffer;
            while (!remaining.IsEmpty)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Span<byte> expected = _comparisonBuffer.AsSpan(0, Math.Min(remaining.Length, _comparisonBuffer!.Length));
                int read = 0;
                while (read < expected.Length)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int count = _file.Read(expected[read..]);
                    if (count == 0)
                        throw new InvalidDataException("Self-validation produced a longer structural file.");
                    read += count;
                }
                if (!remaining[..expected.Length].SequenceEqual(expected))
                    throw new InvalidDataException("Self-validation changed structural file bytes.");
                remaining = remaining[expected.Length..];
            }
        }
        else _file.Write(buffer);
        _hash.AppendData(buffer);
        _length = checked(_length + buffer.Length);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Flush() { if (!_compare) _file.Flush(); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_completed;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_comparisonBuffer is { } buffer)
            {
                _comparisonBuffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
            _hash.Dispose();
        }
        // The transaction owns the file separately (and flushes it to disk before publishing).
        base.Dispose(disposing);
    }
}
