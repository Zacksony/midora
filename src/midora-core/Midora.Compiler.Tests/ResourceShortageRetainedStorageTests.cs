using Midora.Common;
using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class ResourceShortageRetainedStorageTests
{
    [Fact]
    public void ShortageAccountsForItsObjectAndAllFiveFrozenIdArrays()
    {
        ResourceShortageDetails shortage = CreateShortage();
        RetainedStorageCollector storage = new();

        shortage.CollectRetainedStorage(storage);

        RetainedStoragePart[] parts = storage.ToArray();
        Assert.Equal(6, parts.Length);
        Assert.Equal(80, Assert.Single(parts, part => ReferenceEquals(part.Identity, shortage)).Bytes);
        MidoraId[][] expected =
        [
            shortage.TrackIds.ToArray(),
            shortage.SegmentIds.ToArray(),
            shortage.LogicalNoteIds.ToArray(),
            shortage.EventInstrumentIds.ToArray(),
            shortage.SubVoiceIds.ToArray()
        ];
        foreach (MidoraId[] values in expected)
        {
            RetainedStoragePart part = Assert.Single(parts, part =>
                part.Identity is MidoraId[] array && array.AsSpan().SequenceEqual(values));
            Assert.Equal(24L + values.Length * sizeof(long), part.Bytes);
        }
        Assert.Equal(320, parts.Sum(part => part.Bytes));

        // A fresh traversal must report the same backing arrays, not copies.
        RetainedStorageCollector repeated = new();
        shortage.CollectRetainedStorage(repeated);
        foreach (RetainedStoragePart part in parts)
            Assert.Equal(part.Bytes, Assert.Single(repeated.ToArray(), value =>
                ReferenceEquals(value.Identity, part.Identity)).Bytes);
    }

    [Fact]
    public void CanonicalOwnersCollectSharedShortageStorageOnlyOnce()
    {
        ResourceShortageDetails shortage = CreateShortage();
        CanonicalCompiledResult first = CreateFailure(shortage);
        CanonicalCompiledResult second = CreateFailure(shortage);
        RetainedStorageCollector expected = new();
        shortage.CollectRetainedStorage(expected);
        RetainedStoragePart[] shortageParts = expected.ToArray();
        RetainedStorageCollector storage = new();

        first.CollectRetainedStorage(storage);
        AssertCollectedOnce();
        second.CollectRetainedStorage(storage);
        AssertCollectedOnce();
        RetainedStoragePart[] once = storage.ToArray();
        first.CollectRetainedStorage(storage);
        second.CollectRetainedStorage(storage);
        Assert.Equal(once, storage.ToArray());

        void AssertCollectedOnce()
        {
            RetainedStoragePart[] actual = storage.ToArray();
            foreach (RetainedStoragePart part in shortageParts)
                Assert.Equal(part.Bytes, Assert.Single(actual, value =>
                    ReferenceEquals(value.Identity, part.Identity)).Bytes);
            Assert.Equal(5, actual.Count(part => part.Identity is MidoraId[] { Length: > 0 }));
        }
    }

    private static ResourceShortageDetails CreateShortage() => new(
        new TickRange(10, 20), 257, 256,
        [new(1)],
        [new(2), new(3)],
        [new(4), new(5), new(6)],
        [new(7), new(8), new(9), new(10)],
        [new(11), new(12), new(13), new(14), new(15)]);

    private static CanonicalCompiledResult CreateFailure(ResourceShortageDetails shortage) => new(
        480,
        new CompilationContextSummary(new CompilationRequest(), 20, CompilationEndTickSource.NaturalContent),
        [], new CanonicalConductor([], [], [], [], [], null), [], [],
        true, false, CompilationFailureStage.ResourceAllocation, 0,
        new CompilationStatistics(1, 1, 0, 257) { ResourceShortage = shortage });
}
