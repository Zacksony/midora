using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class CompressedMidoraIdSetTests
{
    [Fact]
    public void SetOperationsMatchHashSetOracle()
    {
        Random random = new(0x4d49444f);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            HashSet<MidoraId> left = RandomSet(random);
            HashSet<MidoraId> right = RandomSet(random);
            CompressedMidoraIdSet compressedLeft = CompressedMidoraIdSet.Create(left);
            CompressedMidoraIdSet compressedRight = CompressedMidoraIdSet.Create(right);

            Assert.Equal(left.Order().ToArray(), compressedLeft.ToArray());
            Assert.Equal(
                left.Union(right).Order().ToArray(),
                compressedLeft.Union(compressedRight).ToArray());
            Assert.Equal(
                left.Except(right).Order().ToArray(),
                compressedLeft.Except(compressedRight).ToArray());
            Assert.Equal(
                left.Intersect(right).Order().ToArray(),
                compressedLeft.Intersect(compressedRight).ToArray());
            Assert.Equal(
                left.SymmetricExcept(right).Order().ToArray(),
                compressedLeft.SymmetricExcept(compressedRight).ToArray());
            Assert.Equal(left.SetEquals(right), compressedLeft.SetEquals(compressedRight));
            Assert.Equal(left.IsSubsetOf(right), compressedLeft.IsSubsetOf(compressedRight));
            Assert.Equal(left.IsSupersetOf(right), compressedLeft.IsSupersetOf(compressedRight));
            Assert.Equal(left.Overlaps(right), compressedLeft.Overlaps(compressedRight));
        }
    }

    [Fact]
    public void BuilderDeduplicatesAndFreezesItsResult()
    {
        CompressedMidoraIdSet.Builder builder = CompressedMidoraIdSet.CreateBuilder();
        Assert.True(builder.Add(new MidoraId(1)));
        Assert.False(builder.Add(new MidoraId(1)));
        Assert.True(builder.Add(new MidoraId(4_097)));

        CompressedMidoraIdSet result = builder.Build();

        Assert.Equal(2, result.Count);
        Assert.True(result.Contains(new MidoraId(1)));
        Assert.True(result.Contains(new MidoraId(4_097)));
        Assert.Throws<InvalidOperationException>(() => builder.Add(new MidoraId(2)));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void MillionContiguousIdsUseBoundedPagePayload()
    {
        CompressedMidoraIdSet selection = CompressedMidoraIdSet.Create(
            Enumerable.Range(1, 1_000_000).Select(static value => new MidoraId(value)));

        Assert.Equal(1_000_000, selection.Count);
        Assert.Equal(245, selection.PageCount);
        Assert.InRange(selection.EstimatedPayloadBytes, 120_000, 130_000);
        Assert.True(selection.TryGetMinimum(out MidoraId minimum));
        Assert.Equal(new MidoraId(1), minimum);
    }

    [Fact]
    public void SparseAndDensePagesRemainOrderedAcrossMutation()
    {
        CompressedMidoraIdSet source = CompressedMidoraIdSet.Create(
            Enumerable.Range(1, 100).Select(static value => new MidoraId(value))
                .Append(new MidoraId(10_000)));

        CompressedMidoraIdSet result = source
            .Remove(new MidoraId(50))
            .Add(new MidoraId(9_999));

        Assert.Equal(101, result.Count);
        Assert.DoesNotContain(new MidoraId(50), result);
        Assert.Equal(result.Order().ToArray(), result.ToArray());
    }

    private static HashSet<MidoraId> RandomSet(Random random)
    {
        HashSet<MidoraId> result = [];
        int count = random.Next(0, 2_000);
        for (int index = 0; index < count; index++)
            result.Add(new MidoraId(random.NextInt64(1, 1_000_000)));
        return result;
    }
}

internal static class HashSetTestExtensions
{
    public static IEnumerable<T> SymmetricExcept<T>(this HashSet<T> source, HashSet<T> other)
    {
        HashSet<T> result = new(source);
        result.SymmetricExceptWith(other);
        return result;
    }
}
