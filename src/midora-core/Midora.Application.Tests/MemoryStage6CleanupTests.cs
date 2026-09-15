using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class MemoryStage6CleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryCleanupDetachesAndAttemptsEveryEntryOnce(bool multipleFailures)
    {
        using MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        List<int> order = [];
        InvalidOperationException firstFailure = new("First history cleanup failed.");
        IOException lastFailure = new("Last history cleanup failed.");
        for (int index = 0; index < 3; index++)
            document.Execute(new TrackedCommand(index, order,
                index == 0 ? firstFailure : index == 2 && multipleFailures ? lastFailure : null));

        Exception? failure = Record.Exception(document.Dispose);

        AssertFailures(failure, multipleFailures ? [firstFailure, lastFailure] : [firstFailure]);
        Assert.Empty(document.History);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal(new[] { 0, 1, 2 }, order);
        document.Dispose();
        Assert.Equal(new[] { 0, 1, 2 }, order);
        // The history owner does not own or destroy the still-live compiler.
        Assert.True(compilation.LastAttempt.IsConsumable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DetachedResourceListsKeepReverseOrderAndDoNotRetryFailures(bool complete, bool multipleFailures)
    {
        BoundedEditResources resources = new(temporaryRoot: Path.Combine(AppContext.BaseDirectory, ".tmp"));
        using BoundedEditResourceLease lease = resources.BeginResourceLease();
        List<int> order = [];
        InvalidOperationException firstFailure = new("Reverse-first resource cleanup failed.");
        IOException lastFailure = new("Reverse-last resource cleanup failed.");
        for (int index = 0; index < 3; index++)
            resources.TrackProvider(new TrackedResource(index, order,
                index == 2 ? firstFailure : index == 0 && multipleFailures ? lastFailure : null));
        IDisposable owner = complete ? lease.Complete() : lease;

        Exception? failure = Record.Exception(owner.Dispose);

        AssertFailures(failure, multipleFailures ? [firstFailure, lastFailure] : [firstFailure]);
        Assert.Equal(new[] { 2, 1, 0 }, order);
        owner.Dispose();
        lease.Dispose();
        Assert.Equal(new[] { 2, 1, 0 }, order);
        // A failed Dispose still ended the ambient scope correctly.
        using BoundedEditResourceLease next = resources.BeginResourceLease();
    }

    [Fact]
    public void PublishedResourcesAreNotDisposedByTheRetiredPreparationOwner()
    {
        BoundedEditResources resources = new(temporaryRoot: Path.Combine(AppContext.BaseDirectory, ".tmp"));
        using BoundedEditResourceLease lease = resources.BeginResourceLease();
        List<int> order = [];
        TrackedResource published = new(1, order, null);
        resources.TrackProvider(published);
        using BoundedEditPublicationResources publication = lease.Complete();
        publication.MarkPublished();

        publication.Dispose();
        lease.Dispose();

        Assert.Empty(order);
        published.Dispose();
        Assert.Equal(new[] { 1 }, order);
    }

    [Fact]
    public void SourceLifetimeTransferSurvivesAnotherProvisionalResourceCleanupFailure()
    {
        BoundedEditResources resources = new(temporaryRoot: Path.Combine(AppContext.BaseDirectory, ".tmp"));
        using BoundedEditResourceLease lease = resources.BeginResourceLease();
        List<int> order = [];
        TrackedResource source = new(1, order, null);
        resources.TrackProvider(source);
        BoundedEditResourceLease.RetainForSourceLifetime(source);
        InvalidOperationException injected = new("Unpublished resource failed.");
        resources.TrackProvider(new TrackedResource(2, order, injected));

        Assert.Same(injected, Assert.Throws<InvalidOperationException>(lease.Dispose));

        Assert.Equal(new[] { 2 }, order);
        source.Dispose();
        Assert.Equal(new[] { 2, 1 }, order);
    }

    private static void AssertFailures(Exception? actual, Exception[] expected)
    {
        if (expected.Length == 1) Assert.Same(expected[0], actual);
        else
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(actual);
            Assert.Equal(expected.Length, aggregate.InnerExceptions.Count);
            for (int index = 0; index < expected.Length; index++)
                Assert.Same(expected[index], aggregate.InnerExceptions[index]);
        }
    }

    private sealed class TrackedCommand(int id, List<int> order, Exception? failure) : IProjectEditCommand
    {
        public string Name => "Track history cleanup";
        public IPreparedProjectEdit Prepare(MidoraProject project) => new Prepared(id, order, failure);
        private sealed class Prepared(int id, List<int> order, Exception? failure) : IPreparedProjectEdit, IDisposable
        {
            public bool HasChanges => true;
            public ProjectChangeSet Changes { get; } = new();
            public void Apply(MidoraProject project) { }
            public void Undo(MidoraProject project) { }
            public void Dispose()
            {
                order.Add(id);
                if (failure is not null) throw failure;
            }
        }
    }

    private sealed class TrackedResource(int id, List<int> order, Exception? failure) : IDisposable
    {
        public void Dispose()
        {
            order.Add(id);
            if (failure is not null) throw failure;
        }
    }
}
