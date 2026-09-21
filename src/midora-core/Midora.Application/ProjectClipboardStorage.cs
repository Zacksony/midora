using System.Collections;
using Midora.Domain;

namespace Midora.Application;

/// <summary>Owns clipboard pages until the clipboard and every prepared paste release them.</summary>
internal sealed class ClipboardStorageOwner(IEnumerable<IDisposable> resources, long metadataBytes = 0)
{
    private readonly IDisposable[] _resources = resources.ToArray();
    private int _references = 1;
    public long MetadataBytes { get; } = metadataBytes;
    public IDisposable Acquire()
    {
        int count;
        do
        {
            count = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(count == 0, this);
        } while (Interlocked.CompareExchange(ref _references, checked(count + 1), count) != count);
        return new Lease(this);
    }
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        foreach (IDisposable resource in _resources) resource.Dispose();
    }
    private sealed class Lease(ClipboardStorageOwner owner) : IDisposable
    {
        private ClipboardStorageOwner? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

internal sealed class ClipboardCaptureScope : IDisposable
{
    private static readonly AsyncLocal<ClipboardCaptureScope?> Ambient = new();
    private readonly ClipboardCaptureScope? _previous;
    private readonly List<IDisposable> _resources = [];
    private long _metadataBytes;
    private readonly BulkEditPreparationContext? _ownedContext;
    private ClipboardCaptureScope()
    {
        _previous = Ambient.Value;
        if (BulkEditPreparationContext.Current is null)
            _ownedContext = BulkEditPreparationContext.Enter();
        Ambient.Value = this;
    }
    public static ClipboardCaptureScope Enter() => new();
    // Catalog objects remain managed records; reserve a conservative allowance
    // before allocating their snapshots, sort arrays and dependency maps. The
    // reservation follows the clipboard lease, not merely the Copy call.
    public static void ReserveMetadata(long count, int bytesPerRecord = 512)
    {
        if (count == 0) return;
        BulkEditPreparationContext context = BulkEditPreparationContext.Current!;
        context.Token.ThrowIfCancellationRequested();
        long bytes = checked(count * bytesPerRecord);
        Own(context.Resources.ReserveWorking(bytes));
        Ambient.Value!._metadataBytes = checked(Ambient.Value._metadataBytes + bytes);
    }
    public static ClipboardStorageOwner TakeStorage()
    {
        ClipboardCaptureScope? scope = Ambient.Value;
        ClipboardStorageOwner result = new(scope?._resources ?? [], scope?._metadataBytes ?? 0);
        scope?._resources.Clear();
        if (scope is not null) scope._metadataBytes = 0;
        return result;
    }
    public static BoundedEditRecordStore<T> Capture<T>(IEnumerable<T> values, long total = 0,
        bool reportReadProgress = true)
        where T : unmanaged
    {
        ClipboardCaptureScope scope = Ambient.Value
            ?? throw new InvalidOperationException("Clipboard capture requires an ownership scope.");
        BulkEditPreparationContext context = BulkEditPreparationContext.Current!;
        var result = new BoundedEditRecordStore<T>(context.Resources);
        scope._resources.Add(result);
        long count = 0;
        foreach (T value in values)
        {
            result.Add(value, context.Token);
            if ((++count & 255) == 0)
            {
                context.Token.ThrowIfCancellationRequested();
                if (reportReadProgress)
                    context.Checkpoint(count, total, TimelineEditPreparationPhase.ReadingSelection, 0, 0.75);
            }
        }
        result.Seal();
        if (reportReadProgress)
            context.Checkpoint(count, total == 0 ? count : total, TimelineEditPreparationPhase.ReadingSelection, 0, 0.75);
        result.SpillResidentPages(context.Token, context.ProgressInWorkRange(0.75, 0.24));
        return result;
    }
    public static BoundedEditRecordStore<T> Sort<T>(IEnumerable<T> values, IComparer<T> comparer,
        bool reportSelectionProgress = false)
        where T : unmanaged
    {
        ClipboardCaptureScope scope = Ambient.Value
            ?? throw new InvalidOperationException("Clipboard capture requires an ownership scope.");
        BulkEditPreparationContext context = BulkEditPreparationContext.Current!;
        var sortProgress = reportSelectionProgress ? new CaptureSortProgress(context) : null;
        BoundedEditRecordStore<T> result = BoundedEditSort.Sort(
            sortProgress is null ? values : CountInput(), comparer, context.Resources, context.Token, sortProgress);
        scope._resources.Add(result);
        result.SpillResidentPages(context.Token, reportSelectionProgress ? context.ProgressInWorkRange(0.9, 0.09) : null);
        return result;
        IEnumerable<T> CountInput()
        {
            foreach (T value in values) yield return value;
            sortProgress!.InputComplete = true;
        }
    }
    private sealed class CaptureSortProgress(BulkEditPreparationContext context)
        : IProgress<TimelineEditPreparationProgress>
    {
        public bool InputComplete { get; set; }
        public void Report(TimelineEditPreparationProgress value)
        { if (InputComplete) context.Report(value.InRange(0.75, 0.15)); }
    }
    public static T Own<T>(T resource) where T : IDisposable
    {
        ClipboardCaptureScope scope = Ambient.Value
            ?? throw new InvalidOperationException("Clipboard capture requires an ownership scope.");
        scope._resources.Add(resource);
        return resource;
    }
    public void Dispose()
    {
        Ambient.Value = _previous;
        foreach (IDisposable resource in _resources) resource.Dispose();
        _ownedContext?.Dispose();
    }
}

internal sealed class OpaqueClipboardList : IReadOnlyList<OpaqueMidiEventClipboardSnapshot>, IDisposable
{
    private readonly BoundedEditRecordStore<OpaqueRecord> _records;
    private readonly BoundedEditRecordStore<byte> _bytes;
    private long _minimumTick = long.MaxValue;
    private OpaqueClipboardList(BoundedEditResources resources)
    {
        _records = new(resources);
        try { _bytes = new(resources); }
        catch { _records.Dispose(); throw; }
    }
    public static OpaqueClipboardList Capture(IEnumerable<OpaqueMidiEvent> source) => Capture(
        source.Select(static item => new OpaqueMidiEventValue(item.Id, item.Tick, item.Kind,
            item.MetaType, item.Payload, item.Order)));
    public static OpaqueClipboardList Capture(IEnumerable<OpaqueMidiEventValue> source, bool reportReadProgress = true)
    {
        BulkEditPreparationContext context = BulkEditPreparationContext.Current!;
        var result = ClipboardCaptureScope.Own(new OpaqueClipboardList(context.Resources));
        foreach (OpaqueMidiEventValue item in source)
        {
            context.Token.ThrowIfCancellationRequested();
            using var payloadLease = context.Resources.BorrowPayload(item.Payload);
            result._minimumTick = Math.Min(result._minimumTick, item.Tick);
            int offset = result._bytes.Count;
            foreach (byte value in item.Payload.Span) result._bytes.Add(value, context.Token);
            result._records.Add(new(item.Tick, item.Kind, item.MetaType, item.Order,
                offset, item.Payload.Length), context.Token);
            if (reportReadProgress)
                context.Checkpoint(result.Count, 0, TimelineEditPreparationPhase.ReadingSelection, 0, 0.75);
        }
        result._records.Seal();
        result._bytes.Seal();
        result._records.SpillResidentPages(context.Token, context.ProgressInWorkRange(0.75, 0.05));
        result._bytes.SpillResidentPages(context.Token, context.ProgressInWorkRange(0.8, 0.19));
        return result;
    }
    public int Count => _records.Count;
    internal long PayloadBytes => _bytes.Count;
    public long MinimumTick => Count == 0 ? 0 : _minimumTick;
    public OpaqueMidiEventClipboardSnapshot this[int index]
    {
        get
        {
            OpaqueRecord record = _records[index];
            byte[] payload = new byte[record.Length];
            using var payloadLease = BulkEditPreparationContext.Current?.Resources.BorrowPayload(payload);
            using var pageLease = BulkEditPreparationContext.Current?.Resources.ReserveWorking(_bytes.PageCapacity);
            byte[] page = new byte[_bytes.PageCapacity];
            int copied = 0;
            while (copied < payload.Length)
            {
                BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                int offset = checked(record.Offset + copied);
                int withinPage = offset % _bytes.PageCapacity;
                int take = Math.Min(_bytes.PageCapacity - withinPage, payload.Length - copied);
                _bytes.CopyPage(offset / _bytes.PageCapacity, page);
                page.AsSpan(withinPage, take).CopyTo(payload.AsSpan(copied));
                copied += take;
            }
            return new(record.Tick, record.Kind, record.MetaType, payload, record.Order);
        }
    }
    public IEnumerator<OpaqueMidiEventClipboardSnapshot> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            var value = this[index];
            using var payloadLease = BulkEditPreparationContext.Current?.Resources.BorrowPayload(value.Payload);
            yield return value;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { _records.Dispose(); _bytes.Dispose(); }
    private readonly record struct OpaqueRecord(long Tick, OpaqueMidiEventKind Kind,
        byte MetaType, long Order, int Offset, int Length);
}

internal sealed class ProjectedClipboardList<TSource, TResult>(
    IReadOnlyList<TSource> source, Func<TSource, TResult> project) : IReadOnlyList<TResult>
{
    public int Count => source.Count;
    public TResult this[int index] => project(source[index]);
    public IEnumerator<TResult> GetEnumerator()
    {
        foreach (TSource value in source)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            yield return project(value);
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ClipboardIdSet(BoundedEditRecordStore<MidoraId> values)
    : IReadOnlySet<MidoraId>
{
    public int Count => values.Count;
    public bool Contains(MidoraId value)
    {
        int low = 0, high = Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = values[middle].CompareTo(value);
            if (comparison == 0) return true;
            if (comparison < 0) low = middle + 1; else high = middle - 1;
        }
        return false;
    }
    public IEnumerator<MidoraId> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Overlaps(IEnumerable<MidoraId> other) => other.Any(Contains);
    public bool IsSubsetOf(IEnumerable<MidoraId> other) => Compare(other).Matched == Count;
    public bool IsProperSubsetOf(IEnumerable<MidoraId> other)
    { var result = Compare(other); return result.Matched == Count && result.OtherOnly != 0; }
    public bool IsSupersetOf(IEnumerable<MidoraId> other) => other.All(Contains);
    public bool IsProperSupersetOf(IEnumerable<MidoraId> other)
    { var result = Compare(other); return result.OtherOnly == 0 && result.Matched < Count; }
    public bool SetEquals(IEnumerable<MidoraId> other)
    { var result = Compare(other); return result.OtherOnly == 0 && result.Matched == Count; }
    private (int Matched, int OtherOnly) Compare(IEnumerable<MidoraId> other)
    {
        using var context = BulkEditPreparationContext.Enter();
        using var sorted = BoundedEditSort.Sort(other, Comparer<MidoraId>.Default,
            context.Resources, context.Token);
        int matched = 0, otherOnly = 0;
        MidoraId? previous = null;
        foreach (MidoraId id in sorted)
        {
            if (previous == id) continue;
            previous = id;
            if (Contains(id)) matched++; else otherOnly++;
        }
        return (matched, otherOnly);
    }
}

public static partial class ProjectObjectClipboard
{
    private static IEnumerable<T> EnumerateClipboardSource<T>(ITimelineObjectSource<T> source)
    {
        if (source is OpaqueMidiEventObjectSource opaque)
        {
            for (int index = 0; index < opaque.Count; index++)
            {
                var context = BulkEditPreparationContext.Current!;
                context.Token.ThrowIfCancellationRequested();
                var value = opaque.GetByOrdinal(index);
                using var payload = context.Resources.BorrowPayload(value.Payload);
                yield return (T)(object)value;
            }
            yield break;
        }
        for (int ordinal = 0; ordinal < source.Count;)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            if (!source.TryGetPageByOrdinal(ordinal, Math.Min(source.PageCapacity, source.Count - ordinal), out var page)
                || page.Count == 0)
                throw new InvalidOperationException("The clipboard source could not supply its next immutable page.");
            foreach (T value in page.Values) yield return value;
            ordinal = checked(ordinal + page.Count);
        }
    }

    private static IEnumerable<T> EnumerateClipboardSelection<T>(MidoraProject project,
        ITimelineObjectSource<T> source, IReadOnlySet<MidoraId> ids) where T : unmanaged =>
        ProjectTimelineReadPreparation.ReadSelectedValues(project, source, ids,
            progress: BulkEditPreparationContext.Current!.ProgressInRange(0, 0.75));

    private static IEnumerable<OpaqueMidiEventValue> EnumerateClipboardSelection(MidoraProject project,
        OpaqueMidiEventObjectSource source, IReadOnlySet<MidoraId> ids) =>
        ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(project, source, ids,
            progress: BulkEditPreparationContext.Current!.ProgressInRange(0, 0.75));

    private static IProjectEditCommand KeepClipboardAlive(ProjectObjectClipboardPayload payload,
        IProjectEditCommand command, ProjectClipboardPasteTarget? pasteTarget = null,
        bool independentlyPreparedContent = false)
    {
        // Note paste already prepares a detached owner root and fully consumes
        // its input into independently retained pages. Structural commands still
        // require the whole-Project transaction and its source clipboard lease.
        IProjectEditCommand detached = independentlyPreparedContent ? command
            : new SequentialProjectEditCommand(command.Name, [_ => command]);
        ProjectClipboardPasteTarget target = pasteTarget ?? new(payload.Kind);
        return command is ITimelineSelectionResultEditCommand selection
            ? new SelectionClipboardCommand(payload, detached, selection, target, independentlyPreparedContent)
            : new ClipboardCommand(payload, detached, target, independentlyPreparedContent);
    }

    private class ClipboardCommand(ProjectObjectClipboardPayload payload, IProjectEditCommand command,
        ProjectClipboardPasteTarget pasteTarget, bool independentlyPreparedContent)
        : IProgressReportingProjectEditCommand, IProjectClipboardPasteCommand, IDisposable
    {
        private readonly ClipboardStorageOwner _storage = payload.StorageOwner;
        private readonly ProjectObjectClipboardData _data = payload.Data;
        private IDisposable? _lease = payload.AcquireStorageLease();
        private ProjectClipboardPasteSelectionKinds? _selectionKinds;
        public string Name => command.Name;
        public ProjectClipboardPasteTarget PasteTarget { get; } = pasteTarget;
        public ProjectClipboardPasteSelectionKinds GetPasteSelectionKinds(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_lease is null, this);
            cancellationToken.ThrowIfCancellationRequested();
            return _selectionKinds ??= DescribePasteSelection(
                _data, PasteTarget.MidiTarget, cancellationToken);
        }
        public IPreparedProjectEdit Prepare(MidoraProject project) => Prepare(project, default, null);
        public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) => Prepare(project, token, null);
        public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token,
            IProgress<TimelineEditPreparationProgress>? progress)
        {
            ObjectDisposedException.ThrowIf(_lease is null, this);
            IDisposable lease = _storage.Acquire();
            IDisposable? metadata = null;
            try
            {
                using var context = BulkEditPreparationContext.Enter(token, progress, project: project);
                metadata = context.Resources.ReserveWorking(_storage.MetadataBytes);
                if (_selectionKinds is null
                    && _data is DirectMidiEventClipboardData or SubVoiceTimelineEventsClipboardData)
                    progress?.Report(new(TimelineEditPreparationPhase.ReadingSelection, 0, 0));
                _ = GetPasteSelectionKinds(token);
                IPreparedProjectEdit prepared = command switch
                {
                    IProgressReportingProjectEditCommand reporting => reporting.Prepare(project, token, progress),
                    ICancellableProjectEditCommand cancellable => cancellable.Prepare(project, token),
                    _ => command.Prepare(project)
                };
                if (independentlyPreparedContent)
                {
                    // Only explicitly audited Note commands opt in: the returned
                    // edit owns its output pages and selection, not input iterators.
                    // A command can still be prepared again while its own lease lives.
                    metadata.Dispose();
                    metadata = null;
                    lease.Dispose();
                    return prepared;
                }
                return prepared is IPreparedTimelineSelectionEdit { HasPreparedSelection: true } selection
                    ? new SelectionClipboardPrepared(prepared, lease, metadata, selection.PreparedSelection)
                    : new ClipboardPrepared(prepared, lease, metadata);
            }
            catch { metadata?.Dispose(); lease.Dispose(); throw; }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            GC.SuppressFinalize(this);
        }
        ~ClipboardCommand()
        {
            try { Interlocked.Exchange(ref _lease, null)?.Dispose(); }
            catch { /* Abandoned command cleanup must never crash the process. */ }
        }
    }
    private sealed class SelectionClipboardCommand(ProjectObjectClipboardPayload payload,
        IProjectEditCommand command, ITimelineSelectionResultEditCommand selection,
        ProjectClipboardPasteTarget pasteTarget, bool independentlyPreparedContent)
        : ClipboardCommand(payload, command, pasteTarget, independentlyPreparedContent), ITimelineSelectionResultEditCommand
    {
        public IReadOnlyList<MidoraId> ResultSelectionIds => selection.ResultSelectionIds;
    }
    private class ClipboardPrepared(IPreparedProjectEdit prepared, IDisposable lease, IDisposable metadata)
        : IPreparedProjectEdit, IPreparedProjectEditPublicationGate, IPreparedSegmentIdentityTransfers, IDisposable
    {
        private IDisposable? _lease = lease;
        public bool HasChanges => prepared.HasChanges;
        public ProjectChangeSet Changes => prepared.Changes;
        public IReadOnlyList<SegmentIdentityTransfer> SegmentIdentityTransfers =>
            (prepared as IPreparedSegmentIdentityTransfers)?.SegmentIdentityTransfers ?? [];
        public void Apply(MidoraProject project) => prepared.Apply(project);
        public void Undo(MidoraProject project) => prepared.Undo(project);
        public void ValidateForPublication(MidoraProject project)
        {
            if (prepared is IPreparedProjectEditPublicationGate gate) gate.ValidateForPublication(project);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _lease, null) is not IDisposable owned) return;
            try { if (prepared is IDisposable disposable) disposable.Dispose(); }
            finally { metadata.Dispose(); owned.Dispose(); }
        }
    }
    private sealed class SelectionClipboardPrepared(IPreparedProjectEdit prepared, IDisposable lease, IDisposable metadata,
        PreparedTimelineSelection selection) : ClipboardPrepared(prepared, lease, metadata), IPreparedTimelineSelectionEdit
    {
        public PreparedTimelineSelection PreparedSelection => selection;
    }
}
