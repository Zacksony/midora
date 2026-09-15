using System.Collections;
using Midora.Domain;

namespace Midora.Application;

/// <summary>A discrete Tempo sample. Enumeration order determines later-wins collisions.</summary>
public readonly record struct ConductorTempoPoint(long Tick, decimal BeatsPerMinute);

public static partial class ProjectDomainEditCommands
{
    public static ITimelineSelectionResultEditCommand DrawTempoPoints(IEnumerable<ConductorTempoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return DrawTempoPoints(_ => points,
            points.TryGetNonEnumeratedCount(out int count) ? count : null);
    }

    /// <summary>
    /// Lazily samples a gesture under the preparation's cancellation token.
    /// An omitted count reports real processed candidates with unknown total.
    /// </summary>
    public static ITimelineSelectionResultEditCommand DrawTempoPoints(
        Func<CancellationToken, IEnumerable<ConductorTempoPoint>> points, long? candidateCount = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (candidateCount < 0) throw new ArgumentOutOfRangeException(nameof(candidateCount));
        return ConductorMutation("Draw tempo points", incoming: token => points(token).Select(static point =>
            new ConductorRequest(ConductorKind.Tempo, point.Tick, Bpm: point.BeatsPerMinute)),
            incomingCount: candidateCount);
    }

    public static ITimelineSelectionResultEditCommand MoveConductorEvents(
        IReadOnlyCollection<MidoraId> eventIds, long tickDelta, decimal tempoDelta = 0, bool duplicate = false)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        return ConductorMutation(duplicate ? "Copy and move conductor events" : "Move conductor events", eventIds,
            row => row with
            {
                Tick = checked(row.Tick + tickDelta),
                Bpm = row.Kind == ConductorKind.Tempo ? checked(row.Bpm + tempoDelta) : row.Bpm
            }, duplicate: duplicate);
    }

    private static ITimelineSelectionResultEditCommand DeleteConductorSelection(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ConductorMutation("Delete conductor events", ids, static _ => null);
    }

    private static ITimelineSelectionResultEditCommand DeleteConductorValue(ConductorKind kind, MidoraId id) =>
        ConductorMutation("Delete conductor event", [id], row =>
        {
            if (row.Kind != kind) throw new ArgumentOutOfRangeException(nameof(id));
            return null;
        });

    private static ITimelineSelectionResultEditCommand SetConductorValue(string name, ConductorKind kind,
        MidoraId id, long tick, decimal bpm = 0, int primary = 0, int secondary = 0, bool flag = false,
        string? text = null) => ConductorMutation(name, [id], row =>
        {
            if (row.Kind != kind) throw new ArgumentOutOfRangeException(nameof(id));
            return row with { Tick = tick, Bpm = bpm, Primary = primary, Secondary = secondary, Flag = flag };
        }, replacementText: text);

    private static ITimelineSelectionResultEditCommand CreateConductorValue(string name, ConductorKind kind,
        long tick, decimal bpm = 0, int primary = 0, int secondary = 0, bool flag = false, string? text = null) =>
        ConductorMutation(name, incoming: _ => [new(kind, tick, bpm, primary, secondary, flag, text)], incomingCount: 1);

    private enum ConductorKind { Tempo, TimeSignature, KeySignature, Marker, End }
    private readonly record struct ConductorRequest(ConductorKind Kind, long Tick, decimal Bpm = 0,
        int Primary = 0, int Secondary = 0, bool Flag = false, string? Text = null);
    // All operation-sized storage is fixed-width and spillable. Text is held in
    // a separate bounded UTF-16 stream, not an unbounded per-record object graph.
    private readonly record struct ConductorRow(ConductorKind Kind, MidoraId Id, long Tick,
        decimal Bpm, int Primary, int Secondary, bool Flag, int TextOffset, int TextLength,
        int Ordinal = -1, long Sequence = 0);
    private readonly record struct ConductorAddress(ConductorKind Kind, int Ordinal);

    private static ITimelineSelectionResultEditCommand ConductorMutation(string name,
        IReadOnlyCollection<MidoraId>? selectedIds = null, Func<ConductorRow, ConductorRow?>? transform = null,
        Func<CancellationToken, IEnumerable<ConductorRequest>>? incoming = null, bool duplicate = false,
        string? replacementText = null, long? incomingCount = null) =>
        ResultCommand(name, (project, publish, token, progress) =>
        {
            bool smallKnownEdit = (selectedIds?.Count ?? 0) <= 256
                && (incoming is null || incomingCount is >= 0 and <= 256)
                && (selectedIds?.Count ?? 0) + (incomingCount ?? 0) <= 256;
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project,
                preferredPageRecordCount: smallKnownEdit ? 64 : null);
            token = scope.Token;
            using var lease = scope.Resources.BeginResourceLease();
            ConductorTrack original = project.Conductor;
            ConductorTrack frozen = original.CloneFrozen();
            ConductorTrack result = frozen.CloneFrozen();
            long firstId = project.NextStableId, nextId = firstId;
            var text = new BoundedEditRecordStore<char>(scope.Resources);
            ProjectTimeSignatureMap.WarmLease? warmMap = null;
            // Register the text source as soon as it becomes immutable below.
            try
            {
                ConductorView[] views = CreateConductorViews(frozen, text, scope, selectedIds?.Count ?? 0);
                using var selected = BoundedEditSort.Sort(ResolveSelection(), ConductorFormalComparer,
                    scope.Resources, token, scope.ProgressInWorkRange(0, .12));
                using var candidates = BoundedEditSort.Sort(Candidates(), ConductorCandidateComparer,
                    scope.Resources, token, scope.ProgressInWorkRange(.23, .12));
                if (selectedIds is null && candidates.Count == 0)
                    throw new ArgumentException("At least one Conductor event must be created.", nameof(incoming));
                int[] winnerCounts = new int[views.Length];
                using var winners = BoundedEditSort.Sort(Winners(), ConductorFormalComparer,
                    scope.Resources, token, scope.ProgressInWorkRange(.35, .1));
                using var removals = BoundedEditSort.Sort(Removals(), Comparer<ConductorAddress>.Create(static (a, b) =>
                {
                    int order = a.Kind.CompareTo(b.Kind);
                    return order != 0 ? order : a.Ordinal.CompareTo(b.Ordinal);
                }), scope.Resources, token, scope.ProgressInWorkRange(.45, .1));
                bool changed = false;
                for (int kind = 0; kind < views.Length; kind++)
                {
                    token.ThrowIfCancellationRequested();
                    ConductorKind type = (ConductorKind)kind;
                    using var deleted = new BoundedEditRecordStore<int>(scope.Resources);
                    int previous = -1;
                    foreach (ConductorAddress address in removals)
                    {
                        if (address.Kind != type || address.Ordinal == previous) continue;
                        deleted.Add(address.Ordinal, token); previous = address.Ordinal;
                    }
                    deleted.Seal();
                    using var added = new BoundedEditRecordStore<ConductorRow>(scope.Resources);
                    foreach (ConductorRow winner in winners)
                        if (winner.Kind == type) added.Add(winner, token);
                    added.Seal();
                    if (deleted.Count == 0 && added.Count == 0) continue;
                    changed |= views[kind].Apply(result, deleted, added);
                    scope.Checkpoint(kind + 1, views.Length, TimelineEditPreparationPhase.BuildingResult, .55, .35);
                }
                foreach (var row in selected)
                {
                    if (row.Kind != ConductorKind.End || duplicate) continue;
                    ConductorRow? newValue = transform!(row);
                    result.EndMarker = newValue is { } end ? new ProjectEndMarker(row.Id, end.Tick) : null;
                    changed |= newValue is null || newValue.Value.Tick != row.Tick;
                }
                // A Project End Marker cannot be duplicated: it is a singleton,
                // not an ordinary clipboard event.
                if (duplicate && selected.Any(static row => row.Kind == ConductorKind.End))
                    throw new InvalidOperationException("The Project End Marker cannot be copied.");
                text.Seal();
                var textSource = new BoundedImmutableValueSource<char>(text);
                foreach (ConductorView view in views) view.FinishText(textSource);
                changed = !ConductorUnchanged(result, frozen);
                if (!ReferenceEquals(result.TimeSignatures.CreateQuerySnapshot(), frozen.TimeSignatures.CreateQuerySnapshot()))
                    warmMap = ProjectTimeSignatureMap.AcquireWarmLease(project.TicksPerQuarterNote,
                        result.TimeSignatures.CreateQuerySnapshot(), token);
                var before = FreezeConductorIds(selected.Select(static row => row.Id), scope);
                var after = FreezeConductorIds(winners.Select(static row => row.Id)
                    .Concat(selected.Where(static row => row.Kind == ConductorKind.End)
                        .Where(_ => result.EndMarker is not null).Select(static row => row.Id)), scope);
                BoundedEditPublicationResources resources = lease.Complete(token, scope.ProgressInWorkRange(.9, .09));
                var root = new ConductorRootEdit(project, original, frozen, result, firstId, nextId, changed, resources,
                    warmMap);
                warmMap = null;
                try
                {
                    scope.Checkpoint(1, 1, TimelineEditPreparationPhase.Ready);
                    return WithSelectionPublication(root, before, after, publish);
                }
                catch { root.Dispose(); throw; }

                IEnumerable<ConductorRow> ResolveSelection()
                {
                    if (selectedIds is null) yield break;
                    if (selectedIds.Count == 0) throw new ArgumentException("At least one Conductor event must be selected.", nameof(selectedIds));
                    using var ids = BoundedEditSort.Sort(selectedIds, Comparer<MidoraId>.Default, scope.Resources, token);
                    MidoraId last = default;
                    int processed = 0;
                    foreach (MidoraId id in ids)
                    {
                        if (id == default || id == last) throw new ArgumentException("The Conductor selection contains invalid or duplicate IDs.", nameof(selectedIds));
                        last = id;
                        bool found = false;
                        foreach (ConductorView view in views)
                        {
                            if (view.Find(id) is not int ordinal) continue;
                            yield return view.Read(ordinal);
                            found = true; break;
                        }
                        if (!found && frozen.EndMarker is { } end && end.Id == id)
                        {
                            yield return new(ConductorKind.End, id, end.Tick, 0, 0, 0, false, 0, 0);
                            found = true;
                        }
                        if (!found) throw new ArgumentOutOfRangeException(nameof(selectedIds), "The selected Conductor event no longer exists.");
                        if (++processed % 256 == 0) scope.Checkpoint(processed, ids.Count, TimelineEditPreparationPhase.ResolvingSelection, 0, .1);
                    }
                }

                IEnumerable<ConductorRow> Candidates()
                {
                    long sequence = 0;
                    long processed = 0;
                    long total = selected.Count + (incomingCount ?? 0);
                    bool knownTotal = incoming is null || incomingCount.HasValue;
                    scope.Checkpoint(0, knownTotal ? total : 0, TimelineEditPreparationPhase.Planning, .12, .11);
                    foreach (ConductorRow row in selected)
                    {
                        token.ThrowIfCancellationRequested();
                        ReportCandidate();
                        ConductorRow? replacement = transform!(row);
                        if (!duplicate && row.Tick == 0 && row.Kind is ConductorKind.Tempo or ConductorKind.TimeSignature
                            && (replacement is null || replacement.Value.Tick != 0))
                            throw new InvalidOperationException("The required tick 0 Tempo and Time Signature cannot be moved or deleted.");
                        if (replacement is not { } value) continue;
                        if (replacementText is not null && row.Kind == ConductorKind.Marker)
                        {
                            string normalized = ProjectTextRules.NormalizeShortText(replacementText, true, nameof(replacementText));
                            int offset = text.Count;
                            foreach (char character in normalized) text.Add(character, token);
                            value = value with { TextOffset = offset, TextLength = normalized.Length };
                        }
                        ValidateConductorRow(project, value);
                        if (value.Kind == ConductorKind.End) continue;
                        if (duplicate) value = value with { Id = Allocate(), Ordinal = -1 };
                        yield return NormalizeRequired(value with { Sequence = sequence++ });
                    }
                    if (incoming is not null)
                    foreach (ConductorRequest request in incoming(token))
                    {
                        token.ThrowIfCancellationRequested();
                        ReportCandidate();
                        string normalized = request.Kind == ConductorKind.Marker
                            ? ProjectTextRules.NormalizeShortText(request.Text ?? string.Empty, true, nameof(incoming)) : string.Empty;
                        int offset = text.Count;
                        foreach (char character in normalized) text.Add(character, token);
                        var value = new ConductorRow(request.Kind, Allocate(), request.Tick, request.Bpm,
                            request.Primary, request.Secondary, request.Flag, offset, normalized.Length, Sequence: sequence++);
                        ValidateConductorRow(project, value);
                        yield return NormalizeRequired(value);
                    }
                    // Unknown totals become determinate only after the lazy
                    // stream has ended. This is actual work, not timer progress.
                    scope.Checkpoint(processed, processed, TimelineEditPreparationPhase.Planning, .12, .11);
                    void ReportCandidate()
                    {
                        processed = checked(processed + 1);
                        if (processed % scope.Resources.Budget.CancellationCheckInterval == 0)
                            scope.Checkpoint(processed, knownTotal && processed <= total ? total : 0,
                                TimelineEditPreparationPhase.Planning, .12, .11);
                    }
                    MidoraId Allocate() { MidoraId value = new(nextId); nextId = checked(nextId + 1); return value; }
                    ConductorRow NormalizeRequired(ConductorRow value)
                    {
                        if (value.Tick != 0 || value.Kind is not (ConductorKind.Tempo or ConductorKind.TimeSignature)) return value;
                        ConductorView view = views[(int)value.Kind];
                        if (view.Count != 0 && view.Read(0) is { Tick: 0 } initial)
                            return value with { Id = initial.Id };
                        return value;
                    }
                }

                IEnumerable<ConductorRow> Winners()
                {
                    ConductorRow? pending = null;
                    foreach (ConductorRow value in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        if (pending is { } old && (old.Kind != value.Kind || old.Tick != value.Tick || old.Kind == ConductorKind.Marker))
                        { winnerCounts[(int)old.Kind]++; yield return old; }
                        pending = value;
                    }
                    if (pending is { } final) { winnerCounts[(int)final.Kind]++; yield return final; }
                }
                IEnumerable<ConductorAddress> Removals()
                {
                    if (!duplicate)
                        foreach (var value in selected)
                            if (value.Kind != ConductorKind.End) yield return new(value.Kind, value.Ordinal);
                    for (int kind = 0; kind < (int)ConductorKind.Marker; kind++)
                    {
                        if (winnerCounts[kind] == 0) continue;
                        ConductorKind type = (ConductorKind)kind;
                        foreach (int ordinal in views[kind].AtTicks(
                            winners.Where(row => row.Kind == type).Select(static row => row.Tick), winnerCounts[kind], token))
                            yield return new(type, ordinal);
                    }
                }
            }
            catch { warmMap?.Dispose(); text.Dispose(); throw; }
        });

    private static readonly IComparer<ConductorRow> ConductorFormalComparer = Comparer<ConductorRow>.Create(static (a, b) =>
    {
        int order = a.Kind.CompareTo(b.Kind);
        if (order == 0) order = a.Tick.CompareTo(b.Tick);
        return order != 0 ? order : a.Id.CompareTo(b.Id);
    });
    private static readonly IComparer<ConductorRow> ConductorCandidateComparer = Comparer<ConductorRow>.Create(static (a, b) =>
    {
        int order = a.Kind.CompareTo(b.Kind);
        if (order == 0) order = a.Tick.CompareTo(b.Tick);
        return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
    });

    private static void ValidateConductorRow(MidoraProject project, ConductorRow value)
    {
        ValidateConductorTick(value.Tick, nameof(value.Tick));
        switch (value.Kind)
        {
            case ConductorKind.Tempo: ValidateTempo(value.Bpm); break;
            case ConductorKind.TimeSignature:
                if (value.Primary is < 1 or > 99 || value.Secondary is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
                    throw new ArgumentOutOfRangeException(nameof(value), "The Time Signature is invalid.");
                ProjectTimeSignatureRules.ValidateCompatibility(project.TicksPerQuarterNote, value.Secondary, nameof(value.Secondary));
                break;
            case ConductorKind.KeySignature:
                if (value.Primary is < -7 or > 7) throw new ArgumentOutOfRangeException(nameof(value));
                break;
            case ConductorKind.Marker: case ConductorKind.End: break;
            default: throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static BoundedImmutableValueSource<MidoraId> FreezeConductorIds(IEnumerable<MidoraId> ids, BulkEditPreparationContext scope)
    {
        var store = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        try { store.AddRange(ids, scope.Token); store.Seal(); return new(store); }
        catch { store.Dispose(); throw; }
    }

    private sealed class ConductorView(int count, Func<MidoraId, int?> find,
        Func<int, ConductorRow> read, Func<ConductorTrack, BoundedEditRecordStore<int>, BoundedEditRecordStore<ConductorRow>, bool> apply,
        Action<BoundedImmutableValueSource<char>> finishText)
    {
        public int Count => count;
        public int? Find(MidoraId id) => find(id);
        public ConductorRow Read(int ordinal) => read(ordinal);
        public bool Apply(ConductorTrack target, BoundedEditRecordStore<int> deleted, BoundedEditRecordStore<ConductorRow> added) => apply(target, deleted, added);
        public void FinishText(BoundedImmutableValueSource<char> source) => finishText(source);
        public IEnumerable<int> AtTick(long tick)
        {
            int lo = 0, hi = count;
            while (lo < hi) { int middle = lo + (hi - lo) / 2; if (read(middle).Tick < tick) lo = middle + 1; else hi = middle; }
            for (int index = lo; index < count && read(index).Tick == tick; index++) yield return index;
        }
        public IEnumerable<int> AtTicks(IEnumerable<long> ticks, int candidateCount, CancellationToken token)
        {
            if (candidateCount < Math.Max(256, count / 32))
            {
                foreach (long tick in ticks)
                    foreach (int ordinal in AtTick(tick)) yield return ordinal;
                yield break;
            }
            // Dense target keys form an ordered merge. Repeated binary queries
            // would re-project log(N) record objects per edited point.
            int index = 0;
            long currentTick = count == 0 ? long.MaxValue : read(0).Tick;
            foreach (long tick in ticks)
            {
                while (index < count && currentTick < tick) Advance();
                while (index < count && currentTick == tick)
                { yield return index; Advance(); }
            }
            void Advance()
            {
                index++;
                if (index % 256 == 0) token.ThrowIfCancellationRequested();
                if (index < count) currentTick = read(index).Tick;
            }
        }
    }

    private static ConductorView[] CreateConductorViews(ConductorTrack frozen, BoundedEditRecordStore<char> text,
        BulkEditPreparationContext scope, int selectionCount) =>
    [
        CreateConductorView(frozen.Tempos, static c => c.Tempos, static (v, ordinal) => new(ConductorKind.Tempo, v.Id, v.Tick, v.BeatsPerMinute, 0, 0, false, 0, 0, ordinal),
            static (v, _) => new TempoChange(v.Id, v.Tick, v.Bpm), text, scope, selectionCount),
        CreateConductorView(frozen.TimeSignatures, static c => c.TimeSignatures, static (v, ordinal) => new(ConductorKind.TimeSignature, v.Id, v.Tick, 0, v.Numerator, v.Denominator, false, 0, 0, ordinal),
            static (v, _) => new TimeSignatureChange(v.Id, v.Tick, v.Primary, v.Secondary), text, scope, selectionCount),
        CreateConductorView(frozen.KeySignatures, static c => c.KeySignatures, static (v, ordinal) => new(ConductorKind.KeySignature, v.Id, v.Tick, 0, v.SharpsFlats, 0, v.IsMinor, 0, 0, ordinal),
            static (v, _) => new KeySignatureChange(v.Id, v.Tick, v.Primary, v.Flag), text, scope, selectionCount),
        CreateConductorView(frozen.Markers, static c => c.Markers, (v, ordinal) =>
        {
            int offset = text.Count;
            foreach (char character in v.Name) text.Add(character, scope.Token);
            return new(ConductorKind.Marker, v.Id, v.Tick, 0, 0, 0, false, offset, v.Name.Length, ordinal);
        }, static (v, getText) => new ProjectMarker(v.Id, v.Tick, getText(v.TextOffset, v.TextLength)), text, scope, selectionCount)
    ];

    private static ConductorView CreateConductorView<T>(ConductorCollection<T> collection,
        Func<ConductorTrack, ConductorCollection<T>> target, Func<T, int, ConductorRow> read,
        Func<ConductorRow, Func<int, int, string>, T> create, BoundedEditRecordStore<char> text,
        BulkEditPreparationContext scope, int selectionCount) where T : class
    {
        var snapshot = collection.CreateQuerySnapshot();
        var textReader = new ConductorTextReader();
        Action? publish = null;
        bool lookupPrepared = false;
        return new(snapshot.Count,
            id =>
            {
                // The shared root's stable-ID directory can reject other
                // event kinds without building their full ordinal indexes.
                if (!snapshot.TryGetById(id, out _)) return null;
                if (!lookupPrepared && selectionCount >= Math.Max(256, snapshot.Count / 32))
                {
                    snapshot.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, scope.Token);
                    lookupPrepared = true;
                }
                return snapshot.TryFindOrdinalById(id, out int ordinal) ? ordinal : null;
            },
            ordinal => read(snapshot.GetByOrdinal(ordinal), ordinal), Apply, source =>
            {
                // Scalar-only roots must not retain a large Marker text
                // stream merely because the same mixed edit changed both.
                if (typeof(T) == typeof(ProjectMarker)) textReader.Source = source;
                publish?.Invoke();
            });

        bool Apply(ConductorTrack owner, BoundedEditRecordStore<int> deleted, BoundedEditRecordStore<ConductorRow> added)
        {
            if (deleted.Count == added.Count && Enumerable.Range(0, deleted.Count).All(index =>
                (read(snapshot.GetByOrdinal(deleted[index]), deleted[index]) with { Ordinal = 0, Sequence = 0 })
                == (added[index] with { Ordinal = 0, Sequence = 0 }))) return false;
            var rows = new BoundedEditRecordStore<ConductorRow>(scope.Resources);
            try
            {
                if (deleted.Count + added.Count <= 256)
                {
                    rows.AddRange(added, scope.Token); rows.Seal();
                    var scalarSource = new BoundedImmutableValueSource<ConductorRow>(rows);
                    var source = new ConductorObjectSource<T>(scalarSource, create, textReader);
                    // This small immutable deletion directory is deliberately
                    // bounded; large selections use the streaming merge below.
                    TimelineValueEdit<T>[] edits = deleted.Select(static ordinal => new TimelineValueEdit<T>(ordinal, true, default!)).ToArray();
                    publish = () =>
                    {
                        if (edits.Length == source.Count && Enumerable.Range(0, edits.Length).All(index =>
                            Equals(snapshot.GetByOrdinal(edits[index].Ordinal), source[index]))) return;
                        target(owner).AdoptEditedSnapshot(snapshot, new ConductorArraySource<TimelineValueEdit<T>>(edits), source, scope.Token);
                    };
                }
                else
                {
                    using var removals = deleted.GetEnumerator(); bool hasRemoval = removals.MoveNext();
                    using var additions = added.GetEnumerator(); bool hasAddition = additions.MoveNext();
                    for (int ordinal = 0; ordinal < snapshot.Count; ordinal++)
                    {
                        if (ordinal % 256 == 0) scope.Token.ThrowIfCancellationRequested();
                        if (hasRemoval && removals.Current == ordinal) { hasRemoval = removals.MoveNext(); continue; }
                        ConductorRow old = read(snapshot.GetByOrdinal(ordinal), ordinal);
                        while (hasAddition && ConductorFormalComparer.Compare(additions.Current, old) < 0)
                        { rows.Add(additions.Current, scope.Token); hasAddition = additions.MoveNext(); }
                        rows.Add(old, scope.Token);
                    }
                    while (hasAddition) { rows.Add(additions.Current, scope.Token); hasAddition = additions.MoveNext(); }
                    rows.Seal();
                    var scalarSource = new BoundedImmutableValueSource<ConductorRow>(rows);
                    var index = BoundedTimelineOrdinalIndexBuilder.Instance.CreateOrdinalIndex(
                        scalarSource.Select(static (row, ordinal) => new TimelineIdOrdinal(row.Id, ordinal)), scope.Token);
                    var source = new ConductorObjectSource<T>(scalarSource, create, textReader, index);
                    publish = () => target(owner).AdoptSource(source, scope.Token);
                }
                return true;
            }
            catch { rows.Dispose(); throw; }
        }
    }

    // Keep the published provider independent from preparation's closure:
    // capturing the planner would pin every preceding Conductor root/project.
    private sealed class ConductorTextReader
    {
        public BoundedImmutableValueSource<char>? Source { get; set; }
        public string Read(int offset, int length) => string.Create(length, (offset, Source), static (span, state) =>
        {
            var source = state.Source ?? throw new InvalidOperationException("The detached Conductor text source is not sealed.");
            for (int copied = 0; copied < span.Length;)
            {
                int position = state.offset + copied;
                int local = position % source.PageCapacity;
                int take = Math.Min(span.Length - copied, source.PageCapacity - local);
                source.ReadPage(position / source.PageCapacity).Span.Slice(local, take).CopyTo(span[copied..]);
                copied += take;
            }
        });
    }

    private sealed class ConductorObjectSource<T>(BoundedImmutableValueSource<ConductorRow> source,
        Func<ConductorRow, Func<int, int, string>, T> create, ConductorTextReader text,
        IImmutableTimelineIdIndex? index = null)
        : IIndexedImmutableTimelineValueSource<T>
    {
        private readonly Func<int, int, string> _readText = text.Read;
        public int Count => source.Count;
        // Domain leaves contain 128 records. Project just this leaf rather
        // than rehydrating an entire (much larger) scalar spill page per leaf.
        public int PageCapacity => 128;
        public IImmutableTimelineIdIndex? DetachedIdIndex => index;
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        {
            if (index is not null) return index.TryFindOrdinalById(id, out ordinal);
            // Only the <=256-record small append source has no separate index.
            for (int i = 0; i < source.Count; i++)
                if (source[i].Id == id) { ordinal = i; return true; }
            ordinal = -1;
            return false;
        }
        public T this[int index] => create(source[index], _readText);
        public bool TryReadCachedValue(int index, out T value)
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            try
            {
                if (source.TryReadCachedPage(index / source.PageCapacity, out var rows))
                { value = create(rows.Span[index % source.PageCapacity], _readText); return true; }
            }
            catch (TimelineValueReadPendingException) { }
            value = default!; return false;
        }
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values)
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            try { values = ReadPage(pageIndex); return true; }
            catch (TimelineValueReadPendingException) { values = default; return false; }
        }
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            int first = checked(pageIndex * PageCapacity);
            if (first < 0 || first > Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            T[] result = new T[Math.Min(PageCapacity, Count - first)];
            for (int i = 0; i < result.Length; i++) result[i] = create(source[first + i], _readText);
            return result;
        }
        public IEnumerator<T> GetEnumerator() { foreach (var row in source) yield return create(row, _readText); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ConductorArraySource<T>(T[] values) : IImmutableTimelineValueSource<T>
    {
        public int Count => values.Length;
        public int PageCapacity => 4096;
        public T this[int index] => values[index];
        public ReadOnlyMemory<T> ReadPage(int pageIndex) => values.AsMemory(pageIndex * PageCapacity, Math.Min(PageCapacity, Count - pageIndex * PageCapacity));
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ConductorRootEdit(MidoraProject owner, ConductorTrack original, ConductorTrack frozen,
        ConductorTrack result, long firstId, long nextId, bool changed, BoundedEditPublicationResources resources,
        ProjectTimeSignatureMap.WarmLease? warmMap)
        : IPreparedProjectEdit, IPreparedProjectEditPublicationGate, IDisposable
    {
        private bool _published;
        public bool HasChanges => changed;
        public ProjectChangeSet Changes => ConductorChange();
        public void ValidateForPublication(MidoraProject project)
        {
            if (!ReferenceEquals(owner, project) || !ReferenceEquals(project.Conductor, original)
                || project.NextStableId != firstId || !ConductorUnchanged(original, frozen))
                throw new InvalidOperationException("The Conductor changed while the edit was being prepared.");
        }
        public void Apply(MidoraProject project)
        {
            if (!_published) ValidateForPublication(project);
            else if (!ReferenceEquals(owner, project) || !ReferenceEquals(project.Conductor, original))
                throw new InvalidOperationException("The Conductor no longer matches the Undo history.");
            resources.MarkPublished(); _published = true;
            if (changed) project.Conductor = result;
            project.AdvanceNextStableId(nextId);
        }
        public void Undo(MidoraProject project)
        {
            if (!ReferenceEquals(owner, project) || changed && !ReferenceEquals(project.Conductor, result))
                throw new InvalidOperationException("The Conductor no longer matches the Undo history.");
            if (changed) project.Conductor = original;
        }
        public void Dispose() { resources.Dispose(); warmMap?.Dispose(); }
    }

    private static bool ConductorUnchanged(ConductorTrack a, ConductorTrack b) =>
        ReferenceEquals(a.Tempos.CreateQuerySnapshot(), b.Tempos.CreateQuerySnapshot())
        && ReferenceEquals(a.TimeSignatures.CreateQuerySnapshot(), b.TimeSignatures.CreateQuerySnapshot())
        && ReferenceEquals(a.KeySignatures.CreateQuerySnapshot(), b.KeySignatures.CreateQuerySnapshot())
        && ReferenceEquals(a.Markers.CreateQuerySnapshot(), b.Markers.CreateQuerySnapshot())
        && a.EndMarker?.Id == b.EndMarker?.Id && a.EndMarker?.Tick == b.EndMarker?.Tick;
}
