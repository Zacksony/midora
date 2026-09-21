using System.Globalization;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

public enum TimelineObjectListOwnerKind { LogicalSegment, MidiSegment, SubVoice }

/// <summary>Only scalar identity/value metadata, never a WPF row or an opaque payload.</summary>
public readonly record struct TimelineObjectListRow(
    MidoraId Id, TimelineItemKind Kind, long Tick, long LengthTicks = 0,
    int Key = 0, int Velocity = 0, double Value = 0,
    MidoraId ParameterId = default, MidoraId LaneId = default,
    TemplateEventKind TemplateKind = default, DirectMidiChannelEventKind DirectKind = default,
    int Number = 0, int SecondaryValue = 0, long Order = 0,
    OpaqueMidiEventKind OpaqueKind = default, byte MetaType = 0, int PayloadLength = 0,
    bool HasBankMsb = false, bool HasBankLsb = false, InstrumentChangeValue InstrumentChange = default)
{
    public bool IsInstrumentChange => InstrumentChange.Id != default;
    public IEnumerable<MidoraId> SelectionIds => IsInstrumentChange ? InstrumentChangeLane.MemberIds(InstrumentChange) : [Id];
    public bool IsSelected(IReadOnlySet<MidoraId> ids) => IsInstrumentChange
        ? ids.Contains(InstrumentChange.BankEventId) && ids.Contains(InstrumentChange.ProgramEventId)
            && (InstrumentChange.BankLsbEventId is not { } lsb || ids.Contains(lsb)) : ids.Contains(Id);
    public bool IsNote => Kind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote;
    public bool IsOpaque => Kind == TimelineItemKind.OpaqueMidiEvent;
    public DirectMidiEventLaneTarget? DirectMidiTarget => Kind == TimelineItemKind.DirectMidiEvent
        ? new(DirectKind, DirectKind is DirectMidiChannelEventKind.ControlChange or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff ? Number : 0) : null;
    // A navigation target, not a declaration that this entire object supports
    // single-value numeric editing (Bank/Pitch Bend Range may expose two values).
    public TemplateEventMappingTarget? MidiTarget => Kind == TimelineItemKind.TemplateEvent
        ? TemplateEventMappingTarget.Create(TemplateKind, Number,
            TemplateKind == TemplateEventKind.Bank && !HasBankMsb && HasBankLsb
                ? TemplateEventMappingParameter.SecondaryValue : TemplateEventMappingParameter.Value) : null;
}

/// <summary>
/// Immutable owner revision plus a lazily built, bounded, on-disk chronological
/// directory. Construction/identity checks never enumerate object content. The
/// first 256 rows use a separate bounded top-K pass and need not wait for sorting.
/// </summary>
public sealed class TimelineObjectListSource : IDisposable
{
    private const int FirstPageSize = 256;
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);
    private readonly MidoraProject _project;
    private readonly Component[] _components;
    private readonly IReadOnlyDictionary<MidoraId, string> _parameterNames;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _storageLock = new();
    private readonly Lazy<Task<TimelineObjectListRow[]>> _firstPage;
    private readonly Lazy<Task<TimelineReadOnlySortedStore<TimelineObjectListRow>>> _directory;
    private TimelineReadOnlySortedStore<TimelineObjectListRow>? _ownedDirectory;
    private int _activeReaders;
    private bool _disposed;

    private TimelineObjectListSource(MidoraProject project, MidoraId ownerId,
        TimelineObjectListOwnerKind kind, MidoraId instrumentId, Component[] components)
    {
        _project = project;
        OwnerId = ownerId; OwnerKind = kind; InstrumentId = instrumentId;
        _components = components;
        _parameterNames = components.Where(value => value.LaneId != default)
            .ToDictionary(value => value.LaneId, value => value.Label ?? "Broken parameter");
        Count = checked(components.Sum(value => value.Count));
        _firstPage = new(() => Task.Run(ReadFirstPage));
        _directory = new(() => Task.Run(BuildDirectoryAsync));
    }

    public MidoraId OwnerId { get; }
    public MidoraId InstrumentId { get; }
    public TimelineObjectListOwnerKind OwnerKind { get; }
    public int Count { get; }
    public bool IsDisposed => _disposed;
    internal bool IsActivated => _firstPage.IsValueCreated || _directory.IsValueCreated;
    internal bool DirectoryReady => _directory.IsValueCreated && _directory.Value.IsCompletedSuccessfully;
    internal Task PrepareAsync() => _directory.Value;
    internal long RetainedScalarBytes => DirectoryReady ? _directory.Value.Result.ResidentBytes : 0;
    internal long DirectorySpillBytes => DirectoryReady ? _directory.Value.Result.SpillBytes : 0;

    internal TimelineObjectListSource(MidoraProject project, ITimelineObjectSource<LogicalNoteSnapshotValue> source, bool useRangeCandidates = false)
        : this(project, new MidoraId(1), TimelineObjectListOwnerKind.LogicalSegment, default,
            [Make(source, source, static value => new(value.Id, TimelineItemKind.LogicalNote,
                value.StartTick, value.LengthTicks, value.Note, value.Velocity),
                candidates: useRangeCandidates ? (end, _) => source.QueryTickRange(new(0, end)) : null)]) { }

    public static TimelineObjectListSource CreateLogical(MidoraProject project, Segment segment)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(segment);
        // The owner already owns lane metadata. Refuse an unbounded second
        // directory before allocating its component array.
        if (segment.ParameterLanes.Count > 65536) throw new InvalidOperationException("The object list exceeds its lane-directory budget.");
        var wantedParameters = segment.ParameterLanes.Select(value => value.ParameterId).ToHashSet();
        Dictionary<MidoraId, string> names = [];
        foreach (var instrument in project.EventInstruments)
            foreach (var definition in instrument.LogicalParameters)
                if (wantedParameters.Contains(definition.Id)) names[definition.Id] = definition.Name;
        var noteSnapshot = segment.Notes.CreateQuerySnapshot();
        List<Component> components = [Make(noteSnapshot, segment.Notes,
            static value => new(value.Id, TimelineItemKind.LogicalNote, value.StartTick, value.LengthTicks, value.Note, value.Velocity),
            candidates: noteSnapshot.EnumerateListCandidates)];
        foreach (var lane in segment.ParameterLanes)
        {
            MidoraId parameterId = lane.ParameterId, laneId = lane.Id;
            var pointSnapshot = lane.Points.CreateQuerySnapshot();
            components.Add(Make(pointSnapshot, lane.Points,
                value => new(value.Id, TimelineItemKind.LogicalParameterPoint, value.Tick,
                    Value: value.Value, ParameterId: parameterId, LaneId: laneId), laneId, parameterId,
                names.GetValueOrDefault(parameterId, "Broken parameter"), pointSnapshot.EnumerateListCandidates));
        }
        return new(project, segment.Id, TimelineObjectListOwnerKind.LogicalSegment, default, components.ToArray());
    }

    public static TimelineObjectListSource CreateMidi(MidoraProject project, MidiSegment segment)
    {
        var instrumentSnapshot = segment.ChannelEvents.CreateQuerySnapshot();
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(segment);
        var notes = segment.Notes.CreateObjectSource();
        var events = segment.ChannelEvents.CreateObjectSource();
        return new(project, segment.Id, TimelineObjectListOwnerKind.MidiSegment, default,
        [
            Make(notes, segment.Notes,
                static value => new(value.Id, TimelineItemKind.DirectMidiNote, value.StartTick, value.LengthTicks,
                    value.Key, value.NoteOnVelocity, Order: value.NoteOnOrder),
                candidates: (end, _) => notes.QueryTickRange(new(0, end))),
            Wrap(Make(events, segment.ChannelEvents,
                static value => new(value.Id, TimelineItemKind.DirectMidiEvent, value.Tick,
                    Value: value.Kind switch
                    { DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure => value.Data1,
                        DirectMidiChannelEventKind.PitchBend => (value.Data2 << 7) | value.Data1, _ => value.Data2 },
                    DirectKind: value.Kind, Number: value.Data1, SecondaryValue: value.Data2, Order: value.Order),
                candidates: (end, _) => events.QueryTickRange(new(0, end))), segment.InstrumentChanges,
                    group => InstrumentChangeResolver.TryRead(instrumentSnapshot, group, out var value) ? value : null, true),
            MakeOpaque(segment.OpaqueEvents.CreateObjectSource(), segment.OpaqueEvents)
        ]);
    }

    public static TimelineObjectListSource CreateSubVoice(MidoraProject project, SubVoice voice, MidoraId instrumentId = default)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(voice);
        var snapshot = voice.Events.CreateQuerySnapshot();
        return new(project, voice.Id, TimelineObjectListOwnerKind.SubVoice, instrumentId,
        [Wrap(Make(snapshot, voice.Events, static value =>
            new(value.Id, value.Kind == TemplateEventKind.Note ? TimelineItemKind.TemplateNote : TimelineItemKind.TemplateEvent,
                value.Tick, value.LengthTicks, value.Number, value.Value, value.Value,
                TemplateKind: value.Kind, Number: value.Number, SecondaryValue: value.SecondaryValue,
                HasBankMsb: value.HasBankMsb, HasBankLsb: value.HasBankLsb), candidates: snapshot.EnumerateListCandidates),
                voice.InstrumentChanges, group => { InstrumentChangeSelectionQuery.PrepareSource(snapshot);
                    return InstrumentChangeResolver.TryRead(snapshot, group, out var value) ? value : null; })]);
    }

    public bool IsSameContent(TimelineObjectListSource? other)
    {
        if (other is null || _disposed || other._disposed || OwnerId != other.OwnerId || OwnerKind != other.OwnerKind
            || InstrumentId != other.InstrumentId || !ReferenceEquals(_project, other._project)
            || _components.Length != other._components.Length) return false;
        for (int i = 0; i < _components.Length; i++)
            if (!_components[i].Matches(other._components[i])) return false;
        return true;
    }

    public async Task<TimelineObjectListRow[]> ReadRowsAsync(int first, int count, CancellationToken token = default)
    {
        ValidateRange(first, count);
        if (count > 4096) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return [];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (first < FirstPageSize && count <= FirstPageSize - first && !DirectoryReady)
        {
            var rows = await _firstPage.Value.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            // Sorting starts after the first screen's independent pass. It must
            // not race that pass for CPU and disk or delay first-row publication.
            _ = _directory.Value.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return rows.AsSpan(first, Math.Min(count, rows.Length - first)).ToArray();
        }
        var directory = await _directory.Value.WaitAsync(linked.Token).ConfigureAwait(false);
        return await Task.Run(() => ReadStored(directory, first, count, linked.Token), linked.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<MidoraId>> ReadSelectionAsync(int first, int last, CancellationToken token = default)
    {
        if (last < first) throw new ArgumentOutOfRangeException(nameof(last));
        ValidateRange(first, checked(last - first + 1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        var directory = await _directory.Value.WaitAsync(linked.Token).ConfigureAwait(false);
        return await Task.Run<IReadOnlyCollection<MidoraId>>(() => CompressedMidoraIdSet.Create(ReadIds()), linked.Token).ConfigureAwait(false);
        IEnumerable<MidoraId> ReadIds()
        {
            using var reader = AcquireReader();
            foreach (var row in directory.EnumerateRange(first, last - first + 1, linked.Token))
            { linked.Token.ThrowIfCancellationRequested(); foreach (var id in row.SelectionIds) yield return id; }
        }
    }

    /// <summary>Background-only typed subset stream. No chronological directory is needed.</summary>
    public IEnumerable<TimelineObjectListRow> EnumerateSelectedRows(IReadOnlyCollection<MidoraId> ids,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        foreach (var component in _components)
            foreach (var value in component.Selected(_project, ids, linked.Token))
            { linked.Token.ThrowIfCancellationRequested(); yield return value; }
    }

    private TimelineObjectListRow[] ReadFirstPage()
    {
        CancellationToken token = _lifetime.Token;
        PriorityQueue<TimelineObjectListRow, TimelineObjectListRow> smallest = new(ReverseRowComparer.Instance);
        // Ordinary dense music has enough rows near its beginning. Query only
        // bounded spatial blocks in a growing prefix before considering a full
        // pass. A million coincident objects remains a bounded top-K scan.
        if (Count > FirstPageSize && _components.All(component => component.Candidates is not null))
        {
            for (long end = 1; end < long.MaxValue; end = end > long.MaxValue / 16 ? long.MaxValue : end * 16)
            {
                smallest.Clear();
                foreach (var component in _components)
                    foreach (var row in component.Candidates!(end, token)) Include(row);
                if (smallest.Count == FirstPageSize) return Finish();
            }
        }
        smallest.Clear();
        foreach (var row in EnumerateAll(token)) Include(row);
        return Finish();
        void Include(TimelineObjectListRow row)
        {
            token.ThrowIfCancellationRequested();
            if (smallest.Count < FirstPageSize) smallest.Enqueue(row, row);
            else if (RowComparer.Instance.Compare(row, smallest.Peek()) < 0) smallest.EnqueueDequeue(row, row);
        }
        TimelineObjectListRow[] Finish()
        {
            var result = smallest.UnorderedItems.Select(value => value.Element).ToArray();
            Array.Sort(result, RowComparer.Instance); token.ThrowIfCancellationRequested(); return result;
        }
    }

    private async Task<TimelineReadOnlySortedStore<TimelineObjectListRow>> BuildDirectoryAsync()
    {
        CancellationToken token = _lifetime.Token;
        await PreparationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var directory = TimelineReadOnlySortedStore<TimelineObjectListRow>.Create(EnumerateAll(token), RowComparer.Instance, token);
            lock (_storageLock)
            {
                if (_disposed) { _ = Task.Run(directory.Dispose); token.ThrowIfCancellationRequested(); }
                _ownedDirectory = directory;
            }
            return directory;
        }
        finally { PreparationGate.Release(); }
    }

    private IEnumerable<TimelineObjectListRow> EnumerateAll(CancellationToken token)
    {
        foreach (var component in _components)
            foreach (var row in component.All(token)) { token.ThrowIfCancellationRequested(); yield return row; }
    }

    private TimelineObjectListRow[] ReadStored(TimelineReadOnlySortedStore<TimelineObjectListRow> store, int first, int count, CancellationToken token)
    {
        using var reader = AcquireReader();
        token.ThrowIfCancellationRequested();
        return store.ReadRange(first, count, token);
    }

    private void ValidateRange(int first, int count)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (first < 0 || count < 0 || first > Count || count > Count - first)
            throw new ArgumentOutOfRangeException(nameof(count));
    }

    public void Cancel() => Dispose();
    public void Dispose()
    {
        lock (_storageLock)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        ScheduleReleaseIfUnused();
        if (_directory.IsValueCreated)
            _ = _directory.Value.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private IDisposable AcquireReader()
    {
        lock (_storageLock) { _lifetime.Token.ThrowIfCancellationRequested(); _activeReaders++; return new ReaderLease(this); }
    }
    private void ScheduleReleaseIfUnused()
    {
        TimelineReadOnlySortedStore<TimelineObjectListRow>? release = null;
        lock (_storageLock)
            if (_disposed && _activeReaders == 0) { release = _ownedDirectory; _ownedDirectory = null; }
        if (release is not null) _ = Task.Run(release.Dispose);
    }
    private sealed class ReaderLease(TimelineObjectListSource source) : IDisposable
    {
        public void Dispose() { lock (source._storageLock) source._activeReaders--; source.ScheduleReleaseIfUnused(); }
    }

    private static Component Make<T>(ITimelineObjectSource<T> source, object identity,
        Func<T, TimelineObjectListRow> project, MidoraId laneId = default, MidoraId parameterId = default, string? label = null,
        Func<long, CancellationToken, IEnumerable<T>>? candidates = null) where T : unmanaged =>
        new(identity, source.SourceRevision, source.Count, laneId, parameterId,
            token => ReadAll(source, project, token),
            (owner, ids, token) => ProjectTimelineReadPreparation.ReadSelectedValues(owner, source, ids, token,
                requireAll: false, preserveFormalOrder: false).Select(project), label,
            candidates is null ? null : (end, token) => candidates(end, token).Select(project));

    private static Component MakeOpaque(OpaqueMidiEventObjectSource source, object identity) =>
        new(identity, source.SourceRevision, source.Count, default, default,
            token => ReadAll(source, Project, token),
            (owner, ids, token) => ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(owner, source, ids, token,
                requireAll: false).Select(Project), Candidates: (end, _) => source.QueryTickRange(new(0, end)).Select(Project));

    private static Component Wrap(Component source, InstrumentChangeSet groups, Func<InstrumentChange, InstrumentChangeValue?> read, bool direct = false)
    {
        if (groups.Count == 0) return source;
        // Program is the representative identity. The other members never
        // become additional list rows, yet all remain selectable in raw lanes.
        int removed = direct ? 2 : 1;
        return source with { Identity = groups, Count = checked(source.Count - groups.Count * removed),
            All = token => ProjectRows(source.All(token), token),
            Candidates = source.Candidates is null ? null : (end, token) => ProjectRows(source.Candidates(end, token), token),
            Selected = (project, ids, token) => ProjectRows(source.Selected(project, ids, token), token) };
        IEnumerable<TimelineObjectListRow> ProjectRows(IEnumerable<TimelineObjectListRow> rows, CancellationToken token)
        {
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!groups.TryGetByMember(row.Id, out var group)) { yield return row; continue; }
                if (row.Id != group.ProgramEventId) continue;
                var value = read(group) ?? throw new InvalidOperationException("The Instrument Change list source is incomplete.");
                yield return row with { InstrumentChange = value, Order = value.Order };
            }
        }
    }

    private static TimelineObjectListRow Project(OpaqueMidiEventValue value) =>
        new(value.Id, TimelineItemKind.OpaqueMidiEvent, value.Tick, Order: value.Order,
            OpaqueKind: value.Kind, MetaType: value.MetaType, PayloadLength: value.Payload.Length);

    private static IEnumerable<TimelineObjectListRow> ReadAll<T>(ITimelineObjectSource<T> source,
        Func<T, TimelineObjectListRow> project, CancellationToken token)
    {
        for (int first = 0; first < source.Count;)
        {
            token.ThrowIfCancellationRequested();
            if (!source.TryGetPageByOrdinal(first, Math.Min(source.PageCapacity, source.Count - first), out var page)
                || page.Count == 0) throw new InvalidOperationException("The frozen timeline source returned an incomplete page.");
            foreach (T value in page.Values) { token.ThrowIfCancellationRequested(); yield return project(value); }
            first += page.Count;
        }
    }

    private sealed record Component(object Identity, long Revision, int Count, MidoraId LaneId, MidoraId ParameterId,
        Func<CancellationToken, IEnumerable<TimelineObjectListRow>> All,
        Func<MidoraProject, IReadOnlyCollection<MidoraId>, CancellationToken, IEnumerable<TimelineObjectListRow>> Selected,
        string? Label = null, Func<long, CancellationToken, IEnumerable<TimelineObjectListRow>>? Candidates = null)
    {
        public bool Matches(Component other) => ReferenceEquals(Identity, other.Identity) && Revision == other.Revision
            && Count == other.Count && LaneId == other.LaneId && ParameterId == other.ParameterId && Label == other.Label;
    }

    internal sealed class RowComparer : IComparer<TimelineObjectListRow>
    {
        public static readonly RowComparer Instance = new();
        public int Compare(TimelineObjectListRow left, TimelineObjectListRow right)
        {
            int result = left.Tick.CompareTo(right.Tick);
            if (result == 0) result = Rank(left.Kind).CompareTo(Rank(right.Kind));
            if (result == 0) result = left.Order.CompareTo(right.Order);
            if (result == 0) result = left.Id.CompareTo(right.Id);
            return result;
        }
        private static int Rank(TimelineItemKind kind) => kind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote
            or TimelineItemKind.TemplateNote ? 0 : kind == TimelineItemKind.OpaqueMidiEvent ? 2 : 1;
    }
    private sealed class ReverseRowComparer : IComparer<TimelineObjectListRow>
    { public static readonly ReverseRowComparer Instance = new(); public int Compare(TimelineObjectListRow x, TimelineObjectListRow y) => RowComparer.Instance.Compare(y, x); }

    public static string GetTypeLabel(TimelineObjectListRow row) => row.IsInstrumentChange ? "Instrument Change" : row.IsNote ? "Note" : row.Kind switch
    {
        TimelineItemKind.LogicalParameterPoint => "Parameter",
        TimelineItemKind.OpaqueMidiEvent => row.OpaqueKind.ToString(),
        TimelineItemKind.TemplateEvent when row.TemplateKind == TemplateEventKind.ControlChange => MidiControlChangeCatalog.Format(row.Number),
        TimelineItemKind.TemplateEvent when row.TemplateKind is TemplateEventKind.RegisteredParameter
            or TemplateEventKind.NonRegisteredParameter => $"{row.TemplateKind} {row.Number}",
        TimelineItemKind.TemplateEvent => row.TemplateKind.ToString(),
        TimelineItemKind.DirectMidiEvent when row.DirectKind == DirectMidiChannelEventKind.ControlChange => MidiControlChangeCatalog.Format(row.Number),
        TimelineItemKind.DirectMidiEvent when row.DirectKind == DirectMidiChannelEventKind.PolyphonicKeyPressure => $"PolyphonicKeyPressure {row.Number}",
        _ => row.DirectKind.ToString()
    };
    public string GetRowTypeLabel(TimelineObjectListRow row) => row.Kind == TimelineItemKind.LogicalParameterPoint
        ? _parameterNames.GetValueOrDefault(row.LaneId, "Broken parameter") : GetTypeLabel(row);
    public static string GetValueLabel(TimelineObjectListRow row) => row.IsInstrumentChange
        ? $"Bank {row.InstrumentChange.BankMsb}.{row.InstrumentChange.BankLsb} · Program {row.InstrumentChange.Program}"
        : row.IsNote
        ? $"Key {row.Key} · Gate {row.LengthTicks} · Vel {row.Velocity}"
        : row.IsOpaque ? $"{row.PayloadLength} bytes"
        : row.Kind == TimelineItemKind.LogicalParameterPoint ? row.Value.ToString("G", CultureInfo.InvariantCulture)
        : row.Kind == TimelineItemKind.TemplateEvent && row.TemplateKind == TemplateEventKind.Bank
            ? $"MSB {(row.HasBankMsb ? row.Value.ToString(CultureInfo.InvariantCulture) : "—")} · LSB {(row.HasBankLsb ? row.SecondaryValue.ToString(CultureInfo.InvariantCulture) : "—")}"
        : row.Kind == TimelineItemKind.TemplateEvent && row.TemplateKind == TemplateEventKind.PitchBendRange
            ? $"{row.Value} semitones · {row.SecondaryValue} cents"
        : row.Kind == TimelineItemKind.TemplateEvent && row.TemplateKind == TemplateEventKind.Program
            || row.Kind == TimelineItemKind.DirectMidiEvent && row.DirectKind == DirectMidiChannelEventKind.ProgramChange
            ? $"Program {row.Value}"
        : (row.Value + (row.Kind == TimelineItemKind.TemplateEvent ? MidiEditingValueDomain.Offset(row.TemplateKind, row.Number)
            : row.Kind == TimelineItemKind.DirectMidiEvent ? MidiEditingValueDomain.Offset(row.DirectKind, row.Number) : 0)).ToString("G", CultureInfo.InvariantCulture);
}
