using Midora.Audio;
using Midora.Common;
using Midora.Compiler;
using Midora.Domain;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Midora.Playback.Tests;

public sealed class PreparationStorageCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearReleasesChargeTableHighWaterBackingEvenWhileProductsRemainAlive(bool keepSmallFamily)
    {
        PreparationStorageCache cache = new(128L * 1024 * 1024, 8);
        ManyPartsProduct large = new(32_768);
        Product small = new(new byte[16]);
        cache.Add(0, "large", large);
        if (keepSmallFamily) cache.Add(1, "small", small);
        int before = cache.ChargeCapacity;
        Assert.True(before >= 32_768);
        WeakReference[] previousArrays = CaptureChargeTableBacking(cache);

        cache.Clear(0);

        if (keepSmallFamily)
        {
            Assert.True(cache.TryGet<Product>(1, "small", out Product? retained));
            Assert.Same(small, retained);
            Assert.InRange(cache.ChargeCapacity, 1, before / 4);
            Assert.True(cache.Snapshot.CachedBytes > 0);
        }
        else
        {
            Assert.Equal(0, cache.ChargeCapacity);
            Assert.Equal(0, cache.Snapshot.TotalRetainedBytes);
        }
        AssertBackingCollected(previousArrays);
        // A live product keeps its weak-table description alive intentionally;
        // only the obsolete accounting table backing must have been released.
        GC.KeepAlive(large);
        GC.KeepAlive(small);
        GC.KeepAlive(cache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasingLargeLeaseShrinksDirectoryAndPreservesOtherActiveOwnership(bool baseline)
    {
        PreparationStorageCache cache = new(128L * 1024 * 1024, 0);
        using IDisposable small = cache.Retain(new Product(new byte[16]));
        long smallBytes = cache.Snapshot.ActiveBytes;
        IDisposable large = cache.Retain(new ManyPartsProduct(16_384), baseline);
        int before = cache.ChargeCapacity;
        Assert.True(before >= 16_384);

        large.Dispose();
        large.Dispose();

        Assert.InRange(cache.ChargeCapacity, 1, before / 4);
        Assert.Equal(1, cache.Snapshot.ActiveLeases);
        Assert.Equal(smallBytes, cache.Snapshot.ActiveBytes);
        Assert.Equal(smallBytes, cache.Snapshot.TotalRetainedBytes);
        Assert.Equal(0, cache.Snapshot.BaselineBytes);
        small.Dispose();
        Assert.Equal(0, cache.ChargeCapacity);
        Assert.Equal(0, cache.Snapshot.TotalRetainedBytes);
    }

    [Fact]
    public void LruEvictionReleasesChargeTableHighWaterCapacityAtEndOfAdmission()
    {
        PreparationStorageCache cache = new(128L * 1024 * 1024, 1);
        cache.Add(0, "large", new ManyPartsProduct(16_384));
        int before = cache.ChargeCapacity;
        cache.Add(0, "small", new Product(new byte[16]));
        Assert.False(cache.TryGet<ManyPartsProduct>(0, "large", out _));
        Assert.True(cache.TryGet<Product>(0, "small", out _));
        Assert.Equal(1, cache.Snapshot.CachedEntries);
        Assert.InRange(cache.ChargeCapacity, 1, before / 4);
    }

    [Fact]
    public void OrdinaryPartialClearDoesNotCompactAboveQuarterOccupancy()
    {
        PreparationStorageCache cache = new(128L * 1024 * 1024, 8);
        cache.Add(0, "small", new ManyPartsProduct(4096));
        cache.Add(1, "large", new ManyPartsProduct(8192));
        int before = cache.ChargeCapacity;
        cache.Clear(0);
        Assert.Equal(before, cache.ChargeCapacity);
        Assert.True(cache.TryGet<ManyPartsProduct>(1, "large", out _));
        cache.Clear(1);
        Assert.Equal(0, cache.ChargeCapacity);
    }

    [Fact]
    public void CurrentCanonicalOwnershipIsNotChargedAgainAsExclusiveCacheStorage()
    {
        PreparationStorageCache cache = new(4096, 8);
        Product current = new(new byte[16384]);
        IDisposable canonical = cache.Retain(current, baseline: true);
        cache.Add(0, 1, current);
        Assert.Equal(1, cache.Snapshot.CachedEntries);
        Assert.InRange(cache.Snapshot.CachedBytes, 1, 4096);
        Assert.True(cache.Snapshot.SharedCacheAndBaselineBytes >= 16384);
        using IDisposable active = cache.Retain(current);
        canonical.Dispose();
        Assert.Equal(0, cache.Snapshot.CachedEntries);
        Assert.True(cache.Snapshot.ActiveBytes >= 16384);
        Assert.Equal(0, cache.Snapshot.BaselineBytes);
    }

    [Fact]
    public void SharedBackingIsChargedOnceAndActiveReaderSurvivesEvictionAndClear()
    {
        PreparationStorageCache cache = new(8 * 1024, 8);
        byte[] shared = new byte[4096];
        Product first = new(shared), second = new(shared);
        cache.Add(0, "first", first);
        cache.Add(1, "second", second);
        PreparationStorageSnapshot before = cache.Snapshot;
        Assert.InRange(before.CachedBytes, 4096, 6000);
        IDisposable lease = cache.Retain(first);
        Assert.True(cache.Snapshot.SharedCacheAndActiveBytes >= shared.Length);
        for (int i = 0; i < 1000; i++) cache.Add(2, i, new Product(new byte[4096]));
        Assert.False(cache.TryGet<Product>(0, "first", out _));
        Assert.True(cache.Snapshot.ActiveBytes >= shared.Length);
        Assert.InRange(cache.Snapshot.CachedBytes, 0, 8192);
        Assert.Equal(4096, first.Bytes.Length);
        cache.Clear(0); cache.Clear(1); cache.Clear(2);
        Assert.Equal(0, cache.Snapshot.CachedBytes);
        Assert.Equal(cache.Snapshot.ActiveBytes, cache.Snapshot.TotalRetainedBytes);
        lease.Dispose(); lease.Dispose();
        Assert.Equal(0, cache.Snapshot.TotalRetainedBytes);
        Assert.Equal(0, cache.Snapshot.ActiveLeases);
    }

    [Fact]
    public void OversizedLegalProductIsUncachedAndCanHaveMultipleIndependentReaders()
    {
        PreparationStorageCache cache = new(1024, 8);
        Product product = new(new byte[4096]);
        cache.Add(0, 1, product);
        Assert.Equal(1, cache.Snapshot.UncachedAdmissions);
        Assert.Equal(0, cache.Snapshot.CachedEntries);
        using IDisposable first = cache.Retain(product);
        long once = cache.Snapshot.ActiveBytes;
        using (cache.Retain(product))
        {
            Assert.Equal(once, cache.Snapshot.ActiveBytes);
            Assert.Equal(2, cache.Snapshot.ActiveLeases);
            cache.Clear(0);
            Assert.Equal(once, cache.Snapshot.ActiveBytes);
        }
        Assert.Equal(1, cache.Snapshot.ActiveLeases);
    }

    [Fact]
    public void FamiliesShareBudgetLruAndIndependentInvalidation()
    {
        PreparationStorageCache cache = new(100_000, 2);
        cache.Add(0, 1, new Product(new byte[16]));
        cache.Add(1, 2, new Product(new byte[16]));
        Assert.True(cache.TryGet<Product>(0, 1, out _));
        cache.Add(2, 3, new Product(new byte[16]));
        Assert.False(cache.TryGet<Product>(1, 2, out _));
        cache.Clear(2);
        Assert.True(cache.TryGet<Product>(0, 1, out _));
        Assert.Equal(1, cache.Snapshot.CachedEntries);
    }

    [Fact]
    public void ActualCanonicalAndMonitoringViewsShareStorageWithoutReinterpretingEvents()
    {
        using ProjectCompilationSession session = new(CreateProject());
        CanonicalCompiledResult full = session.LastAttempt;
        CanonicalCompiledResult view = session.CompileForPlayback(0, null);
        RetainedStorageCollector fullStorage = new();
        full.CollectRetainedStorage(fullStorage);
        long fullBytes = fullStorage.ToArray().Sum(value => value.Bytes);
        view.CollectRetainedStorage(fullStorage);
        Assert.InRange(fullStorage.ToArray().Sum(value => value.Bytes) - fullBytes, 0, 1024);
        MidiRenderPlan plan = session.GetOrCreateRealtimeRenderPlan(view, 48000,
            session.Project.Tracks.Select(value => value.Id).ToHashSet());
        MidiRenderPlan muted = plan.WithInitiallyDisabledSourceIndices([0]);
        using IDisposable first = session.RetainPreparationStorage(plan);
        long active = session.PreparationStorage.ActiveBytes;
        using (session.RetainPreparationStorage(muted))
            Assert.InRange(session.PreparationStorage.ActiveBytes - active, 0, 256);
        ScheduledMidiMessage[] events = plan.Ports[0].Events.ToArray();
        session.InvalidateSampleDomainCaches();
        Assert.Equal(events, plan.Ports[0].Events.ToArray());
        session.Dispose();
        Assert.Equal(events, plan.Ports[0].Events.ToArray());
        Assert.Equal(0, session.PreparationStorage.CachedBytes);
        Assert.True(session.PreparationStorage.ActiveBytes > 0);
        first.Dispose();
        Assert.Equal(0, session.PreparationStorage.TotalRetainedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControllerLeasesAreEstablishedBeforeBackendStartAndReleasedOnFailureOrStop(bool fail)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "preparation-storage-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.sf2");
        using FileStream font = new(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose);
        using ProjectCompilationSession session = new(CreateProject(), path);
        using InspectingBackend backend = new(session, fail);
        using PlaybackController controller = new(session, backend);
        if (fail)
        {
            Assert.Throws<InvalidOperationException>(() => controller.Start());
        }
        else
        {
            controller.Start();
            Assert.Equal(2, session.PreparationStorage.ActiveLeases);
            session.InvalidateSampleDomainCaches();
            backend.IsBuffering = true;
            controller.Update();
            Assert.Equal(2, session.PreparationStorage.ActiveLeases);
            controller.Stop();
        }
        Assert.Equal(0, session.PreparationStorage.ActiveLeases);
        Assert.Equal(0, session.PreparationStorage.ActiveBytes);
    }

    private sealed class Product(byte[] bytes) : IRetainedStorageSource
    {
        public byte[] Bytes => bytes;
        public void CollectRetainedStorage(RetainedStorageCollector collector)
        {
            collector.Add(this, 32);
            collector.Array(bytes);
        }
    }

    private sealed class ManyPartsProduct(int count) : IRetainedStorageSource
    {
        private readonly byte[][] _parts = Enumerable.Range(0, count).Select(_ => new byte[16]).ToArray();
        public void CollectRetainedStorage(RetainedStorageCollector collector)
        {
            collector.Add(this, 32);
            collector.Array(_parts);
            foreach (byte[] part in _parts) collector.Array(part);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CaptureChargeTableBacking(PreparationStorageCache cache)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object dictionary = typeof(PreparationStorageCache).GetField("_charges", flags)!.GetValue(cache)!;
        string[] fields = ["_buckets", "_entries"];
        return fields.Select(name => new WeakReference(
            dictionary.GetType().GetField(name, flags)!.GetValue(dictionary)!)).ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertBackingCollected(WeakReference[] arrays)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(arrays, reference => Assert.False(reference.IsAlive));
    }

    internal static MidoraProject CreateProject(int count = 32)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Piano", TemplateLengthTicks = 120 };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 60, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = count * 120L };
        for (int i = 0; i < count; i++) segment.Notes.Add(new LogicalNote(project)
            { StartTick = i * 120L, LengthTicks = 60, Note = 60, Velocity = 100 });
        track.Segments.Add(segment);
        return project;
    }

    private sealed class InspectingBackend(ProjectCompilationSession session, bool fail)
        : IRealtimePlaybackBackend, IBufferingRecoveryRealtimePlaybackBackend
    {
        public int ActualSampleRate => 48000;
        public long PositionFrames => 0;
        public long RenderPositionFrames => 0;
        public bool IsBuffering { get; set; }
        public bool IsCompleted => false;
        public bool IsFaulted => false;
        public string? FaultDescription => null;
        public bool OutputDeviceSelectionRequired => false;
        public string? OutputDeviceSelectionReason => null;
        public int Prepare() => ActualSampleRate;
        public bool HasBufferingRecoveryStorage => true;
        public void SetNextBufferingRecoveryStorage(AudioCacheSessionStore.AudioRecoverySpool? spool,
            long memoryFallbackFrameCapacity) => spool?.Dispose();
        public void BeginBufferingRecovery(long recoveryEndFrame) { }
        public void Start(MidiRenderPlan plan, string path, PlaybackMasterConfiguration master)
        {
            Assert.Equal(2, session.PreparationStorage.ActiveLeases);
            if (fail) throw new InvalidOperationException("Injected Start failure");
        }
        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands) { }
        public void Stop(bool flush) { }
        public void Reset() { }
        public void SelectOutputDevice(string? id) { }
        public void Dispose() { }
    }
}
