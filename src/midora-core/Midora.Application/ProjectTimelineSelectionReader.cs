using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectTimelineReadPreparation
{
    /// <summary>Read-only property projection. Never materializes a mutable
    /// facade or lets an arbitrary-size payload escape into a properties VM.</summary>
    public static OpaqueMidiEventPropertySnapshot? ReadOpaqueProperties(MidoraProject project,
        OpaqueMidiEventObjectSource source, MidoraId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        using var context = BulkEditPreparationContext.Enter(cancellationToken, project: project);
        context.Token.ThrowIfCancellationRequested();
        if (!source.TryFindOrdinalById(id, out int ordinal)) return null;
        var value = source.GetByOrdinal(ordinal);
        using var payload = context.Resources.BorrowPayload(value.Payload);
        string preview = Convert.ToHexString(value.Payload.Span[..Math.Min(value.Payload.Length, 256)]);
        if (value.Payload.Length > 256) preview += "…";
        context.Token.ThrowIfCancellationRequested();
        return new(value.Tick, value.Kind, value.MetaType, value.Payload.Length, preview);
    }
    /// <summary>
    /// Reads a frozen selection with bounded storage. Direct MIDI identities are
    /// resolved in page-sized batches, retaining the scalar returned by that
    /// lookup; other timeline sources resolve addresses once and read in page
    /// order. The enumeration must finish before a caller publishes a result.
    /// </summary>
    public static IEnumerable<T> ReadSelectedValues<T>(MidoraProject project,
        ITimelineObjectSource<T> source, IReadOnlyCollection<MidoraId> ids,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null,
        bool requireAll = true, bool preserveFormalOrder = true) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);
        using var context = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
        using var lease = context.Resources.BeginResourceLease();
        using var frozen = SelectionReadIds.Create(ids, context);
        long revision = source.SourceRevision;
        int count = source.Count;
        if (TryGetDenseIds(frozen, count) is { } dense && CanReadScalarId<T>())
        {
            foreach (var value in ReadDenseSelection(source, dense,
                static value => ReadScalarId(ref value), context, requireAll)) yield return value;
        }
        else if (source is DirectMidiNoteObjectSource notes)
        {
            foreach (var value in ReadDirectSelection<DirectMidiNoteSourceMatch, DirectMidiNoteValue>(notes.QueryByIds,
                static match => new SelectionReadValue<DirectMidiNoteValue>(match.Index, match.Value),
                frozen, context, requireAll, preserveFormalOrder))
            {
                DirectMidiNoteValue scalar = value;
                yield return Unsafe.As<DirectMidiNoteValue, T>(ref scalar);
            }
        }
        else if (source is DirectMidiChannelEventObjectSource events)
        {
            foreach (var value in ReadDirectSelection<DirectMidiChannelEventSourceMatch, DirectMidiChannelEventValue>(events.QueryByIds,
                static match => new SelectionReadValue<DirectMidiChannelEventValue>(match.Index, match.Value),
                frozen, context, requireAll, preserveFormalOrder))
            {
                DirectMidiChannelEventValue scalar = value;
                yield return Unsafe.As<DirectMidiChannelEventValue, T>(ref scalar);
            }
        }
        else
        {
            // This explicit builder is essential: ordinary shared roots must
            // not lazily create an unbounded in-memory ID directory in Find.
            source.PrepareOrdinalLookup(new SelectionReadIndexBuilder(context), context.Token);
            var sortProgress = new SelectionSortProgress(context, 0.65, 0.15);
            using var ordinals = BoundedEditSort.Sort(ResolveOrdinals(), Comparer<int>.Default,
                context.Resources, context.Token, sortProgress);
            foreach (T value in ReadOrdinalPages(source, ordinals, context)) yield return value;

            IEnumerable<int> ResolveOrdinals()
            {
                int visited = 0;
                foreach (MidoraId id in frozen.Values)
                {
                    context.Token.ThrowIfCancellationRequested();
                    bool found = source.TryFindOrdinalById(id, out int ordinal);
                    context.Checkpoint(++visited, frozen.Count, TimelineEditPreparationPhase.ResolvingSelection, 0.2, 0.45);
                    if (found) yield return ordinal;
                    else if (requireAll) throw UnknownSelectionId();
                }
                sortProgress.InputComplete = true;
            }
        }
        context.Token.ThrowIfCancellationRequested();
        if (source.SourceRevision != revision || source.Count != count)
            throw new InvalidOperationException("The timeline source changed while its selection was being read.");
    }

    // Opaque payloads contain managed memory and cannot enter scalar sort
    // storage. Resolve scalar addresses in batches, then borrow only one
    // payload at a time in formal order. No selection-sized payload list is
    // retained. The actual shared backing capacity remains visible in metrics.
    public static IEnumerable<OpaqueMidiEventValue> ReadSelectedOpaqueValues(MidoraProject project,
        OpaqueMidiEventObjectSource source, IReadOnlyCollection<MidoraId> ids,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null, bool requireAll = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);
        using var context = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
        using var lease = context.Resources.BeginResourceLease();
        using var frozen = SelectionReadIds.Create(ids, context);
        long revision = source.SourceRevision;
        int sourceCount = source.Count;
        if (TryGetDenseIds(frozen, sourceCount) is { } dense)
        {
            int found = 0;
            for (int ordinal = 0; ordinal < sourceCount; ordinal++)
            {
                context.Token.ThrowIfCancellationRequested();
                var value = source.GetByOrdinal(ordinal);
                using var payload = context.Resources.BorrowPayload(value.Payload);
                if (dense.Contains(value.Id)) { found++; yield return value; }
                context.Checkpoint(ordinal + 1, sourceCount, TimelineEditPreparationPhase.ReadingSelection, 0, 1);
            }
            if (requireAll && found != frozen.Count) throw UnknownSelectionId();
            if (source.SourceRevision != revision || source.Count != sourceCount)
                throw new InvalidOperationException("The timeline source changed while its selection was being read.");
            yield break;
        }
        var sortProgress = new SelectionSortProgress(context, 0.65, 0.15);
        using var ordinals = BoundedEditSort.Sort(Resolve(), Comparer<int>.Default,
            context.Resources, context.Token, sortProgress);
        int previous = -1, visited = 0;
        foreach (int ordinal in ordinals)
        {
            context.Token.ThrowIfCancellationRequested();
            if (ordinal <= previous || (uint)ordinal >= (uint)sourceCount)
                throw new InvalidOperationException("The timeline selection resolved to invalid or duplicate addresses.");
            previous = ordinal;
            var value = source.GetByOrdinal(ordinal);
            using var payload = context.Resources.BorrowPayload(value.Payload);
            context.Checkpoint(++visited, ordinals.Count, TimelineEditPreparationPhase.ReadingSelection, 0.8, 0.2);
            yield return value;
        }
        context.Token.ThrowIfCancellationRequested();
        if (source.SourceRevision != revision || source.Count != sourceCount)
            throw new InvalidOperationException("The timeline source changed while its selection was being read.");
        IEnumerable<int> Resolve()
        {
            int batchCapacity = ReadBatchCapacity(frozen.Count, context);
            using var working = context.Resources.ReserveWorking(batchCapacity * 64L);
            HashSet<MidoraId> batch = new(batchCapacity);
            int visited = 0;
            foreach (var id in frozen.Values)
            {
                context.Token.ThrowIfCancellationRequested();
                batch.Add(id);
                if (batch.Count < batchCapacity) continue;
                foreach (int ordinal in Flush()) yield return ordinal;
            }
            if (batch.Count != 0) foreach (int ordinal in Flush()) yield return ordinal;
            sortProgress.InputComplete = true;
            IEnumerable<int> Flush()
            {
                int found = 0;
                foreach (var match in source.QueryAddressesByIds(batch))
                { context.Token.ThrowIfCancellationRequested(); found++; yield return match.Index; }
                if (requireAll && found != batch.Count) throw UnknownSelectionId();
                visited += batch.Count;
                context.Checkpoint(visited, frozen.Count, TimelineEditPreparationPhase.ResolvingSelection, 0.2, 0.45);
                batch.Clear();
            }
        }
    }

    private static IEnumerable<TValue> ReadDirectSelection<TMatch, TValue>(
        Func<IReadOnlySet<MidoraId>, IEnumerable<TMatch>> query,
        Func<TMatch, SelectionReadValue<TValue>> project,
        SelectionReadIds ids, BulkEditPreparationContext context,
        bool requireAll, bool preserveFormalOrder) where TValue : unmanaged
    {
        var sortProgress = new SelectionSortProgress(context, 0.65, 0.15);
        if (!preserveFormalOrder)
        {
            foreach (var value in Resolve()) yield return value.Value;
            yield break;
        }
        using var ordered = BoundedEditSort.Sort(Resolve(),
            Comparer<SelectionReadValue<TValue>>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal)),
            context.Resources, context.Token, sortProgress);
        int count = 0;
        foreach (var value in ordered)
        {
            context.Checkpoint(++count, ordered.Count, TimelineEditPreparationPhase.ReadingSelection, 0.8, 0.2);
            yield return value.Value;
        }
        IEnumerable<SelectionReadValue<TValue>> Resolve()
        {
            int batchCapacity = ReadBatchCapacity(ids.Count, context);
            using var working = context.Resources.ReserveWorking(batchCapacity * 64L);
            HashSet<MidoraId> batch = new(batchCapacity);
            int visited = 0;
            foreach (var id in ids.Values)
            {
                context.Token.ThrowIfCancellationRequested();
                batch.Add(id);
                if (batch.Count < batchCapacity) continue;
                foreach (var value in Flush()) yield return value;
            }
            if (batch.Count != 0) foreach (var value in Flush()) yield return value;
            sortProgress.InputComplete = true;
            IEnumerable<SelectionReadValue<TValue>> Flush()
            {
                int found = 0;
                foreach (TMatch match in query(batch))
                { context.Token.ThrowIfCancellationRequested(); found++; yield return project(match); }
                if (requireAll && found != batch.Count) throw UnknownSelectionId();
                visited += batch.Count;
                context.Checkpoint(visited, ids.Count, TimelineEditPreparationPhase.ReadingSelection,
                    preserveFormalOrder ? 0.2 : 0, preserveFormalOrder ? 0.45 : 1);
                batch.Clear();
            }
        }
    }

    private static IEnumerable<T> ReadOrdinalPages<T>(ITimelineObjectSource<T> source,
        BoundedEditRecordStore<int> ordinals, BulkEditPreparationContext context)
    {
        int capacity = Math.Min(4096, source.PageCapacity);
        if (capacity <= 0) throw new InvalidOperationException("The timeline source has an invalid page size.");
        // This path is only for fixed-width values. Opaque uses a payload lease
        // per yielded event rather than holding 4096 arbitrary-size buffers.
        using var working = context.Resources.ReserveWorking(checked((long)capacity * Unsafe.SizeOf<T>()));
        TimelineObjectPage<T> page = default;
        bool hasPage = false;
        int previous = -1, visited = 0;
        foreach (int ordinal in ordinals)
        {
            context.Token.ThrowIfCancellationRequested();
            if (ordinal <= previous || (uint)ordinal >= (uint)source.Count)
                throw new InvalidOperationException("The timeline selection resolved to invalid or duplicate addresses.");
            previous = ordinal;
            if (!hasPage || ordinal < page.FirstOrdinal || ordinal >= (long)page.FirstOrdinal + page.Count)
            {
                int first = ordinal / capacity * capacity;
                if (!source.TryGetPageByOrdinal(first, Math.Min(capacity, source.Count - first), out page)
                    || page.FirstOrdinal != first || page.SourceRevision != source.SourceRevision
                    || page.Count == 0 || page.Count > capacity)
                    throw new InvalidOperationException("The timeline source returned an invalid selection page.");
                hasPage = true;
            }
            T value = page.Values[ordinal - page.FirstOrdinal];
            context.Checkpoint(++visited, ordinals.Count, TimelineEditPreparationPhase.ReadingSelection, 0.8, 0.2);
            yield return value;
        }
    }

    private static ArgumentException UnknownSelectionId() =>
        new("Every selected object must belong to the frozen timeline owner.", "ids");

    private static int ReadBatchCapacity(int count, BulkEditPreparationContext context) =>
        checked((int)Math.Max(1, Math.Min(Math.Min(4096, count), context.Resources.Budget.MaximumWorkingBytes / 256)));

    private static IReadOnlySet<MidoraId>? TryGetDenseIds(SelectionReadIds ids, int sourceCount)
        => ids.Count >= 4096 && (long)ids.Count * 8 >= sourceCount
            && ids.Values is IReadOnlySet<MidoraId> set
            && set is ITimelineInMemoryIdSet or HashSet<MidoraId> or FrozenSet<MidoraId> ? set : null;

    private static IEnumerable<T> ReadDenseSelection<T>(ITimelineObjectSource<T> source,
        IReadOnlySet<MidoraId> ids, Func<T, MidoraId> getId, BulkEditPreparationContext context, bool requireAll)
    {
        int capacity = Math.Min(4096, source.PageCapacity);
        if (capacity <= 0) throw new InvalidOperationException("The timeline source has an invalid page size.");
        using var working = context.Resources.ReserveWorking(checked((long)capacity * Unsafe.SizeOf<T>()));
        int matched = 0;
        for (int first = 0; first < source.Count;)
        {
            context.Token.ThrowIfCancellationRequested();
            if (!source.TryGetPageByOrdinal(first, Math.Min(capacity, source.Count - first), out var page)
                || page.FirstOrdinal != first || page.SourceRevision != source.SourceRevision
                || page.Count == 0 || page.Count > capacity)
                throw new InvalidOperationException("The timeline source returned an invalid selection page.");
            for (int index = 0; index < page.Count; index++)
            {
                if ((index & 255) == 0) context.Token.ThrowIfCancellationRequested();
                T value = page.Values[index];
                if (!ids.Contains(getId(value))) continue;
                matched++;
                yield return value;
            }
            first = checked(first + page.Count);
            context.Checkpoint(first, source.Count, TimelineEditPreparationPhase.ReadingSelection, 0, 1);
        }
        if (requireAll && matched != ids.Count) throw UnknownSelectionId();
    }

    private static bool CanReadScalarId<T>() => typeof(T) == typeof(LogicalNoteSnapshotValue)
        || typeof(T) == typeof(TemplateEventSnapshotValue) || typeof(T) == typeof(CurvePointSnapshotValue)
        || typeof(T) == typeof(DirectMidiNoteValue) || typeof(T) == typeof(DirectMidiChannelEventValue)
        || typeof(T) == typeof(MidoraId);

    private static MidoraId ReadScalarId<T>(ref T value)
    {
        if (typeof(T) == typeof(LogicalNoteSnapshotValue)) return Unsafe.As<T, LogicalNoteSnapshotValue>(ref value).Id;
        if (typeof(T) == typeof(TemplateEventSnapshotValue)) return Unsafe.As<T, TemplateEventSnapshotValue>(ref value).Id;
        if (typeof(T) == typeof(CurvePointSnapshotValue)) return Unsafe.As<T, CurvePointSnapshotValue>(ref value).Id;
        if (typeof(T) == typeof(DirectMidiNoteValue)) return Unsafe.As<T, DirectMidiNoteValue>(ref value).Id;
        if (typeof(T) == typeof(DirectMidiChannelEventValue)) return Unsafe.As<T, DirectMidiChannelEventValue>(ref value).Id;
        if (typeof(T) == typeof(MidoraId)) return Unsafe.As<T, MidoraId>(ref value);
        throw new NotSupportedException("The timeline value does not expose a supported scalar identity.");
    }

    private readonly record struct SelectionReadValue<T>(int Ordinal, T Value) where T : unmanaged;

    private sealed class SelectionSortProgress(BulkEditPreparationContext context, double start, double length)
        : IProgress<TimelineEditPreparationProgress>
    {
        public bool InputComplete { get; set; }
        public void Report(TimelineEditPreparationProgress value)
        {
            if (InputComplete) context.Report(value.InRange(start, length));
        }
    }

    private sealed class SelectionReadIndexBuilder(BulkEditPreparationContext context)
        : IImmutableTimelineOrdinalIndexBuilder
    {
        public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token)
        {
            var sortProgress = new SelectionSortProgress(context, 0.12, 0.06);
            var ordered = BoundedEditSort.Sort(CountValues(),
                Comparer<TimelineIdOrdinal>.Create(static (a, b) => a.Id.CompareTo(b.Id)),
                context.Resources, token, sortProgress);
            try
            {
                ordered.SpillResidentPages(token, context.ProgressInWorkRange(0.18, 0.02));
                var index = new BoundedTimelineOrdinalIndex(new BoundedImmutableValueSource<TimelineIdOrdinal>(ordered));
                return index;
            }
            catch { ordered.Dispose(); throw; }
            IEnumerable<TimelineIdOrdinal> CountValues()
            {
                int visited = 0;
                foreach (var value in values)
                {
                    // An edited root may delegate to a larger ancestor index;
                    // source.Count is not a truthful total for that work.
                    context.Checkpoint(++visited, 0, TimelineEditPreparationPhase.PreparingIndex, 0.02, 0.1);
                    yield return value;
                }
                context.Checkpoint(visited, visited, TimelineEditPreparationPhase.PreparingIndex, 0.02, 0.1);
                sortProgress.InputComplete = true;
            }
        }
    }

    private sealed class SelectionReadIds : IDisposable
    {
        private readonly BoundedEditRecordStore<MidoraId>? _owned;
        private SelectionReadIds(IEnumerable<MidoraId> values, int count, BoundedEditRecordStore<MidoraId>? owned = null)
        { Values = values; Count = count; _owned = owned; }
        public IEnumerable<MidoraId> Values { get; }
        public int Count { get; }
        public static SelectionReadIds Create(IReadOnlyCollection<MidoraId> ids, BulkEditPreparationContext context)
        {
            context.Token.ThrowIfCancellationRequested();
            if (ids is IReadOnlySet<MidoraId> set)
            {
                if (set.Contains(default)) throw new ArgumentException("Selection IDs must be valid.", nameof(ids));
                return new(set, set.Count);
            }
            var ordered = BoundedEditSort.Sort(ids, Comparer<MidoraId>.Default, context.Resources, context.Token,
                context.ProgressInRange(0, 0.02));
            try
            {
                MidoraId previous = default;
                foreach (var id in ordered)
                {
                    context.Token.ThrowIfCancellationRequested();
                    if (id == default || id == previous)
                        throw new ArgumentException("Selection IDs must be distinct and valid.", nameof(ids));
                    previous = id;
                }
                return new(ordered, ordered.Count, ordered);
            }
            catch { ordered.Dispose(); throw; }
        }
        public void Dispose() => _owned?.Dispose();
    }
}

public sealed record OpaqueMidiEventPropertySnapshot(long Tick, OpaqueMidiEventKind Kind,
    byte MetaType, int PayloadLength, string PayloadHexPreview);
