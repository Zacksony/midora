using System.Collections.Immutable;
using System.IO;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop;

// Session-only value protocol. No Project, UI, source, delegate or lease belongs in these records.
internal enum EditorStateOwnerKind { Track, Segment, SubVoice }
internal readonly record struct EditorStateOwner(EditorStateOwnerKind Kind, MidoraId Id, MidoraId Instrument = default);

internal readonly record struct EditorSettingsValues(
    TimelineSubdivision Operation, bool Snap, bool Grid, long Length, int Velocity)
{
    internal static EditorSettingsValues Capture(TimelineEditorSettings settings) => new(
        settings.OperationSubdivision,
        settings.SnapEnabled, settings.GridVisible, settings.DefaultLengthTicks, settings.DefaultVelocity);
    internal void Apply(TimelineEditorSettings settings)
    {
        settings.OperationSubdivision = Operation;
        settings.SnapEnabled = Snap;
        settings.GridVisible = Grid;
        settings.DefaultLengthTicks = Length;
        settings.DefaultVelocity = Velocity;
    }
}

[Flags]
internal enum EditorProfileFields
{
    None = 0, Piano = 1, Event = 2, Zoom = 4, Tool = 8, Shape = 16, Lanes = 32, List = 64,
    All = Piano | Event | Zoom | Tool | Shape | Lanes | List
}

internal readonly record struct TrackEditorProfile(
    EditorSettingsValues Piano, EditorSettingsValues Event, long TickSpan, double KeyHeight,
    TimelineToolMode Tool, TimelineValueTraceShape Shape, bool LanesVisible, double LanesHeight,
    bool ListVisible, double ListWidth)
{
    internal static TrackEditorProfile Default(int tpqn, bool subVoice)
    {
        var settings = new TimelineEditorSettings();
        settings.Reset(false, tpqn);
        var values = EditorSettingsValues.Capture(settings);
        return new(values, values, subVoice ? PianoEditorDefaults.TickSpan : PianoEditorDefaults.SegmentTickSpan(tpqn), PianoEditorDefaults.LaneHeight,
            TimelineToolMode.Select, default, true, TimelineLowerEditorLayout.DefaultHeight, false,
            TimelineObjectListState.DefaultWidth);
    }

    internal TrackEditorProfile Merge(TrackEditorProfile other, EditorProfileFields fields) => this with
    {
        Piano = (fields & EditorProfileFields.Piano) != 0 ? other.Piano : Piano,
        Event = (fields & EditorProfileFields.Event) != 0 ? other.Event : Event,
        TickSpan = (fields & EditorProfileFields.Zoom) != 0 ? other.TickSpan : TickSpan,
        KeyHeight = (fields & EditorProfileFields.Zoom) != 0 ? other.KeyHeight : KeyHeight,
        Tool = (fields & EditorProfileFields.Tool) != 0 ? other.Tool : Tool,
        Shape = (fields & EditorProfileFields.Shape) != 0 ? other.Shape : Shape,
        LanesVisible = (fields & EditorProfileFields.Lanes) != 0 ? other.LanesVisible : LanesVisible,
        LanesHeight = (fields & EditorProfileFields.Lanes) != 0 ? other.LanesHeight : LanesHeight,
        ListVisible = (fields & EditorProfileFields.List) != 0 ? other.ListVisible : ListVisible,
        ListWidth = (fields & EditorProfileFields.List) != 0 ? other.ListWidth : ListWidth
    };
}

internal readonly record struct EditorLocalView(long StartTick, int FirstLane, int FirstRow);
internal readonly record struct LaneTargetView(LaneTabKey Key, bool Hidden, double Minimum, double Maximum);
internal sealed record LaneTabMemory(LaneTabKey Active, ImmutableArray<LaneTargetView> Targets)
{
    internal static readonly LaneTabMemory Empty = new(LaneTabKey.Velocity, []);
    internal ImmutableArray<DirectMidiEventLaneTarget> ExplicitMidiTargets { get; init; } = [];
    internal long EntryCount => (long)Targets.Length + ExplicitMidiTargets.Length;
    internal bool SameAs(LaneTabMemory other) => Active == other.Active && Targets.AsSpan().SequenceEqual(other.Targets.AsSpan())
        && ExplicitMidiTargets.AsSpan().SequenceEqual(other.ExplicitMidiTargets.AsSpan());
}
internal readonly record struct VersionedEditorProfile(long Revision, TrackEditorProfile Value);
internal sealed record WorkspaceStateSnapshot(long Revision,
    ImmutableArray<KeyValuePair<EditorStateOwner, VersionedEditorProfile>> Profiles,
    ImmutableArray<KeyValuePair<EditorStateOwner, EditorLocalView>> Views,
    ImmutableArray<KeyValuePair<EditorStateOwner, LaneTabMemory>> Lanes);

/// <summary>One instance per Project session, UI-thread owned. Hot updates touch one entry;
/// only explicit Freeze copies the directories. Never wired to music or Onion revisions.</summary>
internal sealed class WorkspaceStateRegistry : IDisposable
{
    // Conservative accounting includes dictionary capacity and immutable-array overhead.
    internal const long MaximumChargedBytes = 64L * 1024 * 1024;
    internal const int MaximumEntries = 131_072;
    internal const int MaximumLaneTargets = 65_536;
    internal const int ProfileCharge = 768, ViewCharge = 192, LaneCharge = 192, TargetCharge = 128;
    private Dictionary<EditorStateOwner, VersionedEditorProfile> _profiles = [];
    private Dictionary<EditorStateOwner, EditorLocalView> _views = [];
    private Dictionary<EditorStateOwner, LaneTabMemory> _lanes = [];
    private Dictionary<EditorStateOwner, Action<EditorProfileFields>> _listeners = [];
    private int _subscriberCount;
    private readonly long _budget;
    private bool _warned;
    internal WorkspaceStateRegistry(long budget = MaximumChargedBytes) => _budget = budget;
    internal bool IsDisposed { get; private set; }
    internal long Revision { get; private set; }
    internal long SavedRevision { get; private set; }
    internal long ChargedBytes { get; private set; }
    internal int EntryCount => _profiles.Count + _views.Count + _lanes.Count;
    internal event Action<EditorStateOwner, EditorProfileFields>? ProfileChanged;
    internal event Action? CapacityExceeded;
    internal int SubscriberCount => _subscriberCount;

    internal IDisposable Listen(EditorStateOwner owner, Action<EditorProfileFields> listener)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _listeners[owner] = _listeners.TryGetValue(owner, out var current) ? current + listener : listener;
        ++_subscriberCount;
        return new Subscription(this, owner, listener);
    }
    private sealed class Subscription(WorkspaceStateRegistry registry, EditorStateOwner owner,
        Action<EditorProfileFields> listener) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!registry._listeners.TryGetValue(owner, out var current)) return;
            var remaining = current - listener;
            if (remaining is null) registry._listeners.Remove(owner);
            else registry._listeners[owner] = remaining;
            --registry._subscriberCount;
        }
    }
    private void Notify(EditorStateOwner owner, EditorProfileFields fields)
    {
        ProfileChanged?.Invoke(owner, fields);
        // The immutable invocation list is safe if a callback closes another tab.
        if (_listeners.TryGetValue(owner, out var listener)) listener(fields);
    }

    internal bool TryGetProfile(EditorStateOwner owner, out VersionedEditorProfile value) => _profiles.TryGetValue(owner, out value);
    internal bool TryGetView(EditorStateOwner owner, out EditorLocalView value) => _views.TryGetValue(owner, out value);
    internal LaneTabMemory GetLanes(EditorStateOwner owner) => _lanes.GetValueOrDefault(owner, LaneTabMemory.Empty);

    internal bool SetProfile(EditorStateOwner owner, TrackEditorProfile value, EditorProfileFields fields = EditorProfileFields.All)
    {
        if (IsDisposed) return false;
        bool exists = _profiles.TryGetValue(owner, out var previous);
        if (exists)
        {
            value = previous.Value.Merge(value, fields);
            if (previous.Value == value) return true;
        }
        if (!Admit(exists ? 0 : ProfileCharge, !exists)) return false;
        _profiles[owner] = new(++Revision, value);
        Notify(owner, fields);
        return true;
    }

    internal bool SetView(EditorStateOwner owner, EditorLocalView value)
    {
        if (IsDisposed) return false;
        bool exists = _views.TryGetValue(owner, out var previous);
        if (exists && previous == value) return true;
        if (!Admit(exists ? 0 : ViewCharge, !exists)) return false;
        _views[owner] = value; ++Revision;
        return true;
    }

    internal bool SetLanes(EditorStateOwner owner, LaneTabMemory value)
    {
        if (IsDisposed) return false;
        bool exists = _lanes.TryGetValue(owner, out var previous);
        if (exists && previous!.SameAs(value)) return true;
        if (value.EntryCount > MaximumLaneTargets) { Warn(); return false; }
        long delta = (exists ? 0 : LaneCharge) + (long)(value.EntryCount - (previous?.EntryCount ?? 0)) * TargetCharge;
        if (!Admit(delta, !exists)) return false;
        _lanes[owner] = value; ++Revision;
        return true;
    }

    private bool Admit(long delta, bool adding)
    {
        if (ChargedBytes + delta > _budget || (adding && EntryCount >= MaximumEntries)) { Warn(); return false; }
        ChargedBytes += delta;
        return true;
    }
    private void Warn() { if (!_warned) { _warned = true; CapacityExceeded?.Invoke(); } }

    internal WorkspaceStateSnapshot Freeze() => new(Revision, [.. _profiles], [.. _views], [.. _lanes]);
    internal void MarkSaved(long revision) => SavedRevision = Math.Clamp(revision, 0, Revision);

    internal ProjectPresentationWorkspaceStateV4 CapturePresentationState(
        ProjectPresentationMonitoringV4 monitoring)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        WorkspaceStateSnapshot snapshot = Freeze();
        ProjectPresentationEditorProfileV4[] profiles = snapshot.Profiles
            .Select(pair => new ProjectPresentationEditorProfileV4(
                ToPresentationOwner(pair.Key),
                ToPresentationSettings(pair.Value.Value.Piano),
                ToPresentationSettings(pair.Value.Value.Event),
                pair.Value.Value.TickSpan,
                pair.Value.Value.KeyHeight,
                (int)pair.Value.Value.Tool,
                (int)pair.Value.Value.Shape,
                pair.Value.Value.LanesVisible,
                pair.Value.Value.LanesHeight,
                pair.Value.Value.ListVisible,
                pair.Value.Value.ListWidth))
            .ToArray();
        ProjectPresentationEditorLocalViewV4[] views = snapshot.Views
            .Select(pair => new ProjectPresentationEditorLocalViewV4(
                ToPresentationOwner(pair.Key),
                pair.Value.StartTick,
                pair.Value.FirstLane,
                pair.Value.FirstRow))
            .ToArray();
        ProjectPresentationLaneMemoryV4[] lanes = snapshot.Lanes
            .Select(pair => new ProjectPresentationLaneMemoryV4(
                ToPresentationOwner(pair.Key),
                ToPresentationKey(pair.Value.Active),
                pair.Value.Targets.Select(target => new ProjectPresentationLaneTargetV4(
                    ToPresentationKey(target.Key), target.Hidden, target.Minimum, target.Maximum)).ToArray(),
                pair.Value.ExplicitMidiTargets.Select(target => new ProjectPresentationLaneKeyV4(
                    3, null, (int)target.Kind, target.Data1)).ToArray()))
            .ToArray();
        return new(profiles, views, lanes, monitoring);
    }

    internal bool TryRestorePresentationState(ProjectPresentationWorkspaceStateV4 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            EditorStateOwner ToOwner(ProjectPresentationWorkspaceOwnerV4 owner) => owner.Kind switch
            {
                ProjectPresentationWorkspaceOwnerKindV4.Track =>
                    new(EditorStateOwnerKind.Track, owner.Id),
                ProjectPresentationWorkspaceOwnerKindV4.Segment =>
                    new(EditorStateOwnerKind.Segment, owner.Id),
                ProjectPresentationWorkspaceOwnerKindV4.SubVoice when owner.InstrumentId is { } instrument =>
                    new(EditorStateOwnerKind.SubVoice, owner.Id, instrument),
                _ => throw new InvalidDataException("Workspace owner is invalid.")
            };
            EditorSettingsValues FromSettings(ProjectPresentationEditorSettingsV4 value) => new(
                new(value.Operation.Numerator, value.Operation.Denominator, value.Operation.Label, value.Operation.IsBar),
                value.Snap, value.Grid, value.Length, value.Velocity);

            WorkspaceStateSnapshot snapshot = new(
                Revision,
                [.. state.Profiles.Select(value =>
                    new KeyValuePair<EditorStateOwner, VersionedEditorProfile>(
                        ToOwner(value.Owner),
                        new(0, new(
                            FromSettings(value.Piano),
                            FromSettings(value.Event),
                            value.TickSpan,
                            value.KeyHeight,
                            (TimelineToolMode)value.Tool,
                            (TimelineValueTraceShape)value.Shape,
                            value.LanesVisible,
                            value.LanesHeight,
                            value.ListVisible,
                            value.ListWidth))))],
                [.. state.Views.Select(value =>
                    new KeyValuePair<EditorStateOwner, EditorLocalView>(
                        ToOwner(value.Owner),
                        new(value.StartTick, value.FirstLane, value.FirstRow)))],
                [.. state.Lanes.Select(value =>
                {
                    LaneTabKey FromKey(ProjectPresentationLaneKeyV4 key) =>
                        new(key.Type, key.Parameter ?? default, key.Kind, key.Number);
                    LaneTabMemory memory = new(
                        FromKey(value.Active),
                        [.. value.Targets.Select(target => new LaneTargetView(
                            FromKey(target.Key), target.Hidden, target.Minimum, target.Maximum))])
                    {
                        ExplicitMidiTargets = [.. value.ExplicitMidiTargets.Select(target =>
                            new DirectMidiEventLaneTarget(
                                (DirectMidiChannelEventKind)target.Kind,
                                target.Number))]
                    };
                    return new KeyValuePair<EditorStateOwner, LaneTabMemory>(ToOwner(value.Owner), memory);
                })]);
            return TryRestore(snapshot);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static ProjectPresentationWorkspaceOwnerV4 ToPresentationOwner(EditorStateOwner owner) =>
        new(owner.Kind switch
        {
            EditorStateOwnerKind.Track => ProjectPresentationWorkspaceOwnerKindV4.Track,
            EditorStateOwnerKind.Segment => ProjectPresentationWorkspaceOwnerKindV4.Segment,
            EditorStateOwnerKind.SubVoice => ProjectPresentationWorkspaceOwnerKindV4.SubVoice,
            _ => throw new InvalidDataException("Unknown editor state owner kind.")
        }, owner.Id, owner.Kind == EditorStateOwnerKind.SubVoice ? owner.Instrument : null);

    private static ProjectPresentationEditorSettingsV4 ToPresentationSettings(EditorSettingsValues value) =>
        new(new(value.Operation.Numerator, value.Operation.Denominator, value.Operation.Label, value.Operation.IsBar),
            value.Snap, value.Grid, value.Length, value.Velocity);

    private static ProjectPresentationLaneKeyV4 ToPresentationKey(LaneTabKey key) =>
        new(key.Type, key.Parameter == default ? null : key.Parameter, key.Kind, key.Number);

    // B2's codec validates its own wire schema; this boundary validates the value partition atomically.
    internal bool TryRestore(WorkspaceStateSnapshot snapshot)
    {
        if (IsDisposed || snapshot.Profiles.IsDefault || snapshot.Views.IsDefault || snapshot.Lanes.IsDefault
            || (long)snapshot.Profiles.Length + snapshot.Views.Length + snapshot.Lanes.Length > MaximumEntries) return false;
        using var candidate = new WorkspaceStateRegistry(_budget);
        foreach (var pair in snapshot.Profiles)
        {
            var v = pair.Value.Value;
            if (pair.Key.Kind == EditorStateOwnerKind.Segment || !ValidOwner(pair.Key)
                || !ValidSettings(v.Piano) || !ValidSettings(v.Event) || v.TickSpan < 1
                || !double.IsFinite(v.KeyHeight) || v.KeyHeight is < .25 or > TimelineSurface.MaximumPianoLaneHeight
                || !Enum.IsDefined(v.Tool) || !Enum.IsDefined(v.Shape)
                || !double.IsFinite(v.LanesHeight) || v.LanesHeight is < TimelineLowerEditorLayout.MinimumHeight or > TimelineLowerEditorLayout.MaximumHeight
                || !double.IsFinite(v.ListWidth) || v.ListWidth is < 240 or > 700
                || candidate._profiles.ContainsKey(pair.Key) || !candidate.SetProfile(pair.Key, v)) return false;
        }
        foreach (var pair in snapshot.Views)
            if (pair.Key.Kind == EditorStateOwnerKind.Track || !ValidOwner(pair.Key)
                || pair.Value.StartTick < 0 || pair.Value.FirstLane is < 0 or > 127 || pair.Value.FirstRow < 0
                || candidate._views.ContainsKey(pair.Key) || !candidate.SetView(pair.Key, pair.Value)) return false;
        foreach (var pair in snapshot.Lanes)
        {
            if (!ValidOwner(pair.Key) || pair.Key.Kind == EditorStateOwnerKind.Track || pair.Value is null
                || pair.Value.Targets.IsDefault || pair.Value.ExplicitMidiTargets.IsDefault || pair.Value.EntryCount > MaximumLaneTargets
                || pair.Value.ExplicitMidiTargets.Any(x => !ValidLaneKey(new(3, Kind: (int)x.Kind, Number: x.Data1)))
                || pair.Value.ExplicitMidiTargets.Distinct().Count() != pair.Value.ExplicitMidiTargets.Length
                || !ValidLaneKey(pair.Value.Active) || pair.Value.Targets.Any(x => !ValidLaneKey(x.Key))
                || pair.Value.Targets.Select(x => x.Key).Distinct().Count() != pair.Value.Targets.Length
                || pair.Value.Targets.Any(x => !double.IsFinite(x.Minimum) || !double.IsFinite(x.Maximum)
                    || x.Minimum < 0 || x.Maximum > 1 || x.Minimum >= x.Maximum)
                || candidate._lanes.ContainsKey(pair.Key) || !candidate.SetLanes(pair.Key, pair.Value)) return false;
        }
        _profiles = new(candidate._profiles); _views = new(candidate._views); _lanes = new(candidate._lanes);
        ChargedBytes = candidate.ChargedBytes; ++Revision;
        foreach (var key in _profiles.Keys.ToArray()) Notify(key, EditorProfileFields.All);
        return true;
    }
    private static bool ValidOwner(EditorStateOwner owner) => Enum.IsDefined(owner.Kind) && owner.Id.Value > 0
        && (owner.Kind == EditorStateOwnerKind.SubVoice ? owner.Instrument.Value > 0 : owner.Instrument == default);
    private static bool ValidLaneKey(LaneTabKey key) => key.Type switch
    {
        0 or 1 or 4 => key.Parameter == default && key.Kind == 0 && key.Number == 0,
        2 => key.Parameter.Value > 0 && key.Kind == 0 && key.Number == 0,
        3 => key.Parameter == default && Enum.IsDefined((DirectMidiChannelEventKind)key.Kind) && key.Number is >= 0 and <= 127,
        5 => key.Parameter == default && Enum.IsDefined((MidiValueKind)key.Kind) && key.Number is >= 0 and <= 16383,
        _ => false
    };
    private static bool ValidSettings(EditorSettingsValues settings) => settings.Length > 0 && settings.Velocity is >= 1 and <= 127
        && settings.Operation.Numerator > 0 && settings.Operation.Denominator > 0 && settings.Operation.Label is { Length: <= 64 };

    internal void TransferSegmentView(MidoraId sourceId, MidoraId targetId)
    {
        if (IsDisposed || sourceId == targetId) return;
        var source = new EditorStateOwner(EditorStateOwnerKind.Segment, sourceId);
        var target = new EditorStateOwner(EditorStateOwnerKind.Segment, targetId);
        if (_views.Remove(source, out var view))
        {
            ChargedBytes -= ViewCharge;
            SetView(target, view);
        }
        if (_lanes.Remove(source, out var lanes))
        {
            ChargedBytes -= LaneCharge + (long)lanes.EntryCount * TargetCharge;
            // Logical parameters and Direct MIDI targets are not interchangeable.
            SetLanes(target, new(LaneTabKey.Velocity, [.. lanes.Targets.Where(x => x.Key == LaneTabKey.Velocity)]));
        }
    }

    internal void CopyProfiles(IReadOnlyDictionary<MidoraId, MidoraId> map)
    {
        if (IsDisposed) return;
        foreach (var pair in _profiles.ToArray())
        {
            if (!map.TryGetValue(pair.Key.Id, out var id)) continue;
            var key = pair.Key with { Id = id, Instrument = map.GetValueOrDefault(pair.Key.Instrument, pair.Key.Instrument) };
            if (_profiles.ContainsKey(key)) continue;
            SetProfile(key, pair.Value.Value);
            if (key.Kind != EditorStateOwnerKind.SubVoice) continue;
            if (_views.TryGetValue(pair.Key, out var view)) SetView(key, view);
            if (_lanes.TryGetValue(pair.Key, out var lanes)) SetLanes(key, lanes with
            {
                Active = Remap(lanes.Active),
                Targets = [.. lanes.Targets.Select(x => x with { Key = Remap(x.Key) })]
            });
        }
        LaneTabKey Remap(LaneTabKey key) => key with { Parameter = map.GetValueOrDefault(key.Parameter, key.Parameter) };
    }

    internal void Prune(Func<EditorStateOwner, bool> exists)
    {
        if (IsDisposed) return;
        foreach (var key in _profiles.Keys.Where(key => !exists(key)).ToArray())
        { _profiles.Remove(key); ChargedBytes -= ProfileCharge; ++Revision; }
        foreach (var key in _views.Keys.Where(key => !exists(key)).ToArray())
        { _views.Remove(key); ChargedBytes -= ViewCharge; ++Revision; }
        foreach (var key in _lanes.Keys.Where(key => !exists(key)).ToArray())
        { ChargedBytes -= LaneCharge + (long)_lanes[key].EntryCount * TargetCharge; _lanes.Remove(key); ++Revision; }
    }

    public void Dispose()
    {
        IsDisposed = true; _profiles = []; _views = []; _lanes = []; ChargedBytes = 0;
        ProfileChanged = null; CapacityExceeded = null;
        _listeners = []; _subscriberCount = 0;
    }
}
