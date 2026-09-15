using System.Collections;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class CompactMidoraIdListTests
{
    [Fact]
    public void MillionConsecutiveIdsUseOneRunAndEnumerateInOrder()
    {
        const int count = 1_000_000;
        SequentialIds source = new(10_000, count);

        CompactMidoraIdList compact = Assert.IsType<CompactMidoraIdList>(
            CompactMidoraIdList.Freeze(source));

        Assert.Equal(count, compact.Count);
        Assert.Equal(1, compact.SegmentCount);
        Assert.Equal(0, compact.EncodedDeltaByteCount);
        Assert.Equal(MidoraId.FromSequence(10_000), compact[0]);
        Assert.Equal(MidoraId.FromSequence(509_999), compact[499_999]);
        Assert.Equal(MidoraId.FromSequence(1_009_999), compact[^1]);

        long expected = 10_000;
        foreach (MidoraId id in compact)
            Assert.Equal(expected++, id.Value);
        Assert.Equal(1_010_000, expected);
    }

    [Fact]
    public void ArbitraryOrderDuplicatesAndBoundaryValuesRoundTripExactly()
    {
        MidoraId[] source =
        [
            MidoraId.FromSequence(9),
            MidoraId.FromSequence(3),
            MidoraId.FromSequence(3),
            MidoraId.FromSequence(10),
            MidoraId.FromSequence(long.MaxValue),
            MidoraId.FromSequence(1),
            MidoraId.FromSequence(2),
            MidoraId.FromSequence(1),
            default,
            MidoraId.FromSequence(long.MaxValue)
        ];

        IReadOnlyList<MidoraId> compact = CompactMidoraIdList.Freeze(source);

        Assert.Equal(source, compact);
        for (int index = 0; index < source.Length; index++)
            Assert.Equal(source[index], compact[index]);
        Assert.Equal(source.Reverse(), compact.Reverse());
    }

    [Fact]
    public void LiteralBlockBoundariesRunsAndEnumerationHaveIdenticalContent()
    {
        List<MidoraId> source = new(1_100);
        for (int index = 0; index < 513; index++)
        {
            long value = index % 2 == 0
                ? 8_000_000_000L - (index * 37L)
                : 200 + (index * 91L);
            source.Add(MidoraId.FromSequence(value));
        }
        for (int index = 0; index < 500; index++)
            source.Add(MidoraId.FromSequence(20_000 + index));
        source.AddRange(
        [
            MidoraId.FromSequence(7),
            MidoraId.FromSequence(7),
            MidoraId.FromSequence(7),
            MidoraId.FromSequence(99)
        ]);

        IReadOnlyList<MidoraId> compact = CompactMidoraIdList.Freeze(source);

        Assert.Equal(source.Count, compact.Count);
        Assert.Equal(source, compact);
        Assert.Equal(source, ((IEnumerable<MidoraId>)compact).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = compact[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = compact[compact.Count]);
    }

    [Fact]
    public void FreezeDetachesFromMutableInputAndReusesAnAlreadyFrozenInstance()
    {
        MidoraId[] source =
        [
            MidoraId.FromSequence(100),
            MidoraId.FromSequence(101),
            MidoraId.FromSequence(102),
            MidoraId.FromSequence(50)
        ];
        IReadOnlyList<MidoraId> compact = CompactMidoraIdList.Freeze(source);

        source[0] = MidoraId.FromSequence(999);

        Assert.Equal(MidoraId.FromSequence(100), compact[0]);
        Assert.Same(compact, CompactMidoraIdList.Freeze(compact));
        Assert.Same(
            CompactMidoraIdList.Freeze(Array.Empty<MidoraId>()),
            CompactMidoraIdList.Freeze(Array.Empty<MidoraId>()));
    }

    [Fact]
    public void FrozenPartsConcatenateWithoutChangingSequenceSemantics()
    {
        IReadOnlyList<MidoraId>[] sourceParts =
        [
            CompactMidoraIdList.Freeze(new SequentialIds(100, 4)),
            CompactMidoraIdList.Freeze(Array.Empty<MidoraId>()),
            CompactMidoraIdList.Freeze(
            [
                MidoraId.FromSequence(900),
                MidoraId.FromSequence(12),
                MidoraId.FromSequence(12),
                default
            ]),
            CompactMidoraIdList.Freeze(new SequentialIds(1_000, 5))
        ];
        MidoraId[] expected = sourceParts.SelectMany(static value => value).ToArray();

        CompactMidoraIdList concatenated = Assert.IsType<CompactMidoraIdList>(
            CompactMidoraIdList.FreezeConcatenated(sourceParts));

        Assert.Equal(expected, concatenated);
        Assert.Equal(expected, ((IEnumerable<MidoraId>)concatenated).ToArray());
        Assert.Equal(expected[0], concatenated[0]);
        Assert.Equal(expected[^1], concatenated[^1]);
    }

    private sealed class SequentialIds(long first, int count) : IReadOnlyList<MidoraId>
    {
        public int Count { get; } = count;

        public MidoraId this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                return MidoraId.FromSequence(checked(first + index));
            }
        }

        public IEnumerator<MidoraId> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
