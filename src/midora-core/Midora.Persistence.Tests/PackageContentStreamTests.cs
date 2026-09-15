using System.Security.Cryptography;

namespace Midora.Persistence.Tests;

public sealed class PackageContentStreamTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 200_001)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 200_001)]
    public void StreamingDigestAndExactComparisonPreserveBytes(bool compare, int length)
    {
        byte[] data = new byte[length];
        new Random(17).NextBytes(data);
        using MemoryStream file = compare ? new(data, writable: false) : new();
        using (PackageContentStreamV1 stream = new(file, compare, default))
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int count = Math.Min(997, data.Length - offset);
                stream.Write(data, offset, count);
                offset += count;
            }
            var result = stream.Complete();
            Assert.Equal(length, result.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), result.Sha256);
            Assert.False(stream.CanWrite);
            Assert.Throws<InvalidOperationException>(() => stream.Complete());
            Assert.Throws<InvalidOperationException>(() => stream.WriteByte(1));
        }
        // The transaction, not the wrapper, owns the file and its publish/flush lifecycle.
        Assert.True(file.CanRead);
        if (!compare) Assert.Equal(data, file.ToArray());
    }

    [Fact]
    public void ComparisonRejectsLateMismatchRatherThanTrustingOnlyLength()
    {
        byte[] data = new byte[200_001];
        using MemoryStream file = new(data, writable: false);
        using PackageContentStreamV1 stream = new(file, compare: true, default);
        stream.Write(data.AsSpan(0, data.Length - 1));
        Assert.Throws<InvalidDataException>(() => stream.WriteByte(1));
    }

    [Fact]
    public void ComparisonRejectsExtraWriterBytes()
    {
        using MemoryStream file = new([1, 2], writable: false);
        using PackageContentStreamV1 stream = new(file, compare: true, default);
        Assert.Throws<InvalidDataException>(() => stream.Write([1, 2, 3]));
    }

    [Fact]
    public void ComparisonRejectsUnconsumedSourceBytes()
    {
        using MemoryStream file = new([1, 2, 3], writable: false);
        using PackageContentStreamV1 stream = new(file, compare: true, default);
        stream.Write([1, 2]);
        Assert.Throws<InvalidDataException>(() => stream.Complete());
    }

    [Fact]
    public void ComparisonSupportsNonSeekableShortReads()
    {
        byte[] data = new byte[200_001];
        new Random(31).NextBytes(data);
        using ShortReadStream file = new(data);
        using PackageContentStreamV1 stream = new(file, compare: true, default);
        stream.Write(data);
        Assert.Equal(data.Length, stream.Complete().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDoesNotWriteAnotherByte(bool compare)
    {
        using CancellationTokenSource cancellation = new();
        using MemoryStream file = new();
        using PackageContentStreamV1 stream = new(file, compare, cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => stream.WriteByte(1));
        Assert.Throws<OperationCanceledException>(() => stream.Complete());
        Assert.Equal(0, file.Length);
    }

    [Fact]
    public void StreamingFileFailureIsNotSwallowed()
    {
        using MemoryStream file = new([1], writable: false);
        using PackageContentStreamV1 stream = new(file, compare: false, default);
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    private sealed class ShortReadStream(byte[] content) : Stream
    {
        private int _position;
        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(3, Math.Min(buffer.Length, content.Length - _position));
            content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
