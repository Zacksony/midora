using System.Collections.ObjectModel;

namespace Midora.Domain;

public enum ShortNoteLifecycle
{
    CutAtNoteOff,
    OneShot,
    Tail
}

public enum LongNoteLifecycle
{
    HoldLastState,
    EndAtTemplate
}

public enum OverlapPolicy
{
    Reject,
    Warn,
    LetOverlap,
    CutPrevious,
    CutNewRejectNew
}

public enum OverlapScope
{
    SamePitch,
    AnyPitch
}

public enum TemplateEventKind
{
    Note,
    ControlChange,
    Bank,
    Program,
    PitchBend,
    RegisteredParameter,
    NonRegisteredParameter,
    PitchBendRange
}

public enum TemplateEventMappingParameter
{
    Number,
    Value,
    SecondaryValue
}

public readonly record struct TemplateEventMappingTarget(
    TemplateEventKind EventKind,
    int EventNumber,
    TemplateEventMappingParameter Parameter)
{
    public static TemplateEventMappingTarget Create(
        TemplateEventKind eventKind,
        int eventNumber,
        TemplateEventMappingParameter parameter) =>
        new(
            eventKind,
            UsesEventNumber(eventKind) ? eventNumber : 0,
            parameter);

    public static IEnumerable<TemplateEventMappingTarget> Enumerate(TemplateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Enumerate(new TemplateEventSnapshotValue(value.Id, value.Kind, value.Tick,
            value.LengthTicks, value.Number, value.Value, value.SecondaryValue,
            value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta));
    }

    public static IEnumerable<TemplateEventMappingTarget> Enumerate(TemplateEventSnapshotValue value)
    {
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Number);
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                break;
            case TemplateEventKind.ControlChange:
            case TemplateEventKind.Program:
            case TemplateEventKind.PitchBend:
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb)
                {
                    yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                }
                if (value.HasBankLsb)
                {
                    yield return Create(
                        value.Kind,
                        value.Number,
                        TemplateEventMappingParameter.SecondaryValue);
                }
                break;
            case TemplateEventKind.PitchBendRange:
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                yield return Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.SecondaryValue);
                break;
        }
    }

    public static bool IsSupported(TemplateEventMappingTarget target) =>
        Enum.IsDefined(target.EventKind)
        && Enum.IsDefined(target.Parameter)
        && target.EventNumber == (UsesEventNumber(target.EventKind) ? target.EventNumber : 0)
        && target.Parameter switch
        {
            TemplateEventMappingParameter.Number => target.EventKind == TemplateEventKind.Note,
            TemplateEventMappingParameter.Value => true,
            TemplateEventMappingParameter.SecondaryValue =>
                target.EventKind is TemplateEventKind.Bank or TemplateEventKind.PitchBendRange,
            _ => false
        };

    private static bool UsesEventNumber(TemplateEventKind eventKind) =>
        eventKind is TemplateEventKind.ControlChange
            or TemplateEventKind.RegisteredParameter
            or TemplateEventKind.NonRegisteredParameter;
}

public sealed class SubVoiceEventMapping
{
    public SubVoiceEventMapping(MidoraProject project, TemplateEventMappingTarget target)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        Target = target;
        Steps = new MappingChain(project);
    }

    internal SubVoiceEventMapping(
        MidoraProject project,
        TemplateEventMappingTarget target,
        MidoraId mappingChainId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        Target = target;
        Steps = new MappingChain(project, mappingChainId);
    }

    public TemplateEventMappingTarget Target { get; }
    public MappingChain Steps { get; internal set; }
    public MidiIntegerTargetSettings TargetSettings { get; } = new();
}

public sealed class TemplateEvent
{
    public TemplateEvent(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Id = project.AllocateStableId();
    }

    internal TemplateEvent(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        _project = project;
        Id = preservedId;
    }

    private readonly MidoraProject _project;
    private readonly Dictionary<TemplateEventMappingParameter, SubVoiceEventMapping> _detachedMappings = [];
    private SubVoice? _owner;
    private Action<TemplateEvent>? _changeSink;
    private TemplateEventKind _kind;
    private long _tick;
    private long _lengthTicks;
    private int _number;
    private int _value;
    private int _secondaryValue;
    private bool _hasBankMsb = true;
    private bool _hasBankLsb = true;
    private bool _followPitchDelta = true;

    public MidoraId Id { get; init; }
    public TemplateEventKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }
    public long Tick
    {
        get => _tick;
        set => Set(ref _tick, value);
    }
    public long LengthTicks
    {
        get => _lengthTicks;
        set => Set(ref _lengthTicks, value);
    }
    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }
    public int Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
    public int SecondaryValue
    {
        get => _secondaryValue;
        set => Set(ref _secondaryValue, value);
    }
    public bool HasBankMsb
    {
        get => _hasBankMsb;
        internal set => Set(ref _hasBankMsb, value);
    }
    public bool HasBankLsb
    {
        get => _hasBankLsb;
        internal set => Set(ref _hasBankLsb, value);
    }
    public bool FollowPitchDelta
    {
        get => _followPitchDelta;
        set => Set(ref _followPitchDelta, value);
    }
    public MappingChain NumberMappings
    {
        get => GetMapping(TemplateEventMappingParameter.Number).Steps;
        internal set => GetMapping(TemplateEventMappingParameter.Number).Steps = value;
    }

    public MappingChain ValueMappings
    {
        get => GetMapping(TemplateEventMappingParameter.Value).Steps;
        internal set => GetMapping(TemplateEventMappingParameter.Value).Steps = value;
    }

    public MappingChain SecondaryValueMappings
    {
        get => GetMapping(TemplateEventMappingParameter.SecondaryValue).Steps;
        internal set => GetMapping(TemplateEventMappingParameter.SecondaryValue).Steps = value;
    }

    public MidiIntegerTargetSettings NumberTargetSettings =>
        GetMapping(TemplateEventMappingParameter.Number).TargetSettings;

    public MidiIntegerTargetSettings ValueTargetSettings =>
        GetMapping(TemplateEventMappingParameter.Value).TargetSettings;

    public MidiIntegerTargetSettings SecondaryValueTargetSettings =>
        GetMapping(TemplateEventMappingParameter.SecondaryValue).TargetSettings;

    internal bool AttachTo(SubVoice owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null && !ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "A Template Event cannot be attached to more than one SubVoice.");
        }
        bool newlyAttached = _owner is null;
        _owner = owner;
        foreach (SubVoiceEventMapping detached in _detachedMappings.Values)
        {
            SubVoiceEventMapping? existing = owner.FindEventMapping(detached.Target);
            if (existing is null)
            {
                owner.EventMappings.Add(detached);
                continue;
            }
            MergeDetachedMapping(existing, detached);
        }
        _detachedMappings.Clear();
        return newlyAttached;
    }

    internal bool CanUseBulkAttachPath(SubVoice owner) =>
        (_owner is null || ReferenceEquals(_owner, owner))
        && _detachedMappings.Count == 0;

    internal void EnsureMappings() => _owner?.EnsureEventMappings(this, createOptional: false);

    internal void SetChangeSink(Action<TemplateEvent>? sink) => _changeSink = sink;

    internal void SetValues(
        TemplateEventKind kind,
        long tick,
        long lengthTicks,
        int number,
        int value,
        int secondaryValue,
        bool hasBankMsb,
        bool hasBankLsb,
        bool followPitchDelta)
    {
        if (_kind == kind
            && _tick == tick
            && _lengthTicks == lengthTicks
            && _number == number
            && _value == value
            && _secondaryValue == secondaryValue
            && _hasBankMsb == hasBankMsb
            && _hasBankLsb == hasBankLsb
            && _followPitchDelta == followPitchDelta)
        {
            return;
        }

        _kind = kind;
        _tick = tick;
        _lengthTicks = lengthTicks;
        _number = number;
        _value = value;
        _secondaryValue = secondaryValue;
        _hasBankMsb = hasBankMsb;
        _hasBankLsb = hasBankLsb;
        _followPitchDelta = followPitchDelta;
        _changeSink?.Invoke(this);
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _changeSink?.Invoke(this);
    }

    private SubVoiceEventMapping GetMapping(TemplateEventMappingParameter parameter)
    {
        if (_owner is null)
        {
            if (_detachedMappings.TryGetValue(parameter, out SubVoiceEventMapping? detached))
            {
                return detached;
            }
            TemplateEventMappingTarget target = TemplateEventMappingTarget.Create(
                Kind,
                Number,
                parameter);
            if (!TemplateEventMappingTarget.IsSupported(target))
            {
                throw new InvalidOperationException(
                    "The Template Event does not expose the requested Mapping target.");
            }
            detached = new SubVoiceEventMapping(_project, target);
            _detachedMappings.Add(parameter, detached);
            return detached;
        }
        return _owner.GetOrCreateEventMapping(
            TemplateEventMappingTarget.Create(Kind, Number, parameter));
    }

    private static void MergeDetachedMapping(
        SubVoiceEventMapping existing,
        SubVoiceEventMapping detached)
    {
        bool detachedHasContent = detached.Steps.Count != 0
            || !detached.Steps.IsEnabled
            || detached.TargetSettings.Rounding != MappingRounding.Round
            || detached.TargetSettings.Overflow != MappingOverflow.Fail;
        if (!detachedHasContent)
        {
            return;
        }
        bool existingHasContent = existing.Steps.Count != 0
            || !existing.Steps.IsEnabled
            || existing.TargetSettings.Rounding != MappingRounding.Round
            || existing.TargetSettings.Overflow != MappingOverflow.Fail;
        if (existingHasContent)
        {
            throw new InvalidOperationException(
                "A shared SubVoice event Mapping already exists for this target.");
        }
        existing.Steps = detached.Steps;
        existing.TargetSettings.Rounding = detached.TargetSettings.Rounding;
        existing.TargetSettings.Overflow = detached.TargetSettings.Overflow;
    }

    public static TemplateEvent Note(
        MidoraProject project,
        long tick,
        long lengthTicks,
        int note,
        int velocity) => new(project)
        {
            Kind = TemplateEventKind.Note,
            Tick = tick,
            LengthTicks = lengthTicks,
            Number = note,
            Value = velocity
        };

    public static TemplateEvent ControlChange(
        MidoraProject project,
        long tick,
        int controller,
        int value) => new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = tick,
            Number = controller,
            Value = value
        };

    public static TemplateEvent Program(MidoraProject project, long tick, int program) => new(project)
    {
        Kind = TemplateEventKind.Program,
        Tick = tick,
        Value = program
    };

    public static TemplateEvent Bank(MidoraProject project, long tick, int? msb, int? lsb) => new(project)
    {
        Kind = TemplateEventKind.Bank,
        Tick = tick,
        Value = msb ?? 0,
        SecondaryValue = lsb ?? 0,
        HasBankMsb = msb.HasValue,
        HasBankLsb = lsb.HasValue
    };
}

public sealed class MidiInitialState
{
    public int? BankMsb { get; set; }
    public int? BankLsb { get; set; }
    public int? Program { get; set; }
    public int? PitchBend { get; set; }
    public int? PitchBendRangeSemitones { get; set; }
    public int? PitchBendRangeCents { get; set; }
    public Dictionary<int, int> Controllers { get; } = [];
    public Dictionary<int, int> RegisteredParameters { get; } = [];
    public Dictionary<int, int> NonRegisteredParameters { get; } = [];

    public MidiInitialState Clone()
    {
        MidiInitialState result = new()
        {
            BankMsb = BankMsb,
            BankLsb = BankLsb,
            Program = Program,
            PitchBend = PitchBend,
            PitchBendRangeSemitones = PitchBendRangeSemitones,
            PitchBendRangeCents = PitchBendRangeCents
        };
        foreach ((int controller, int value) in Controllers)
        {
            result.Controllers.Add(controller, value);
        }
        foreach ((int parameter, int value) in RegisteredParameters)
        {
            result.RegisteredParameters.Add(parameter, value);
        }
        foreach ((int parameter, int value) in NonRegisteredParameters)
        {
            result.NonRegisteredParameters.Add(parameter, value);
        }

        return result;
    }
}

public sealed class SubVoice
{
    public SubVoice(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Id = project.AllocateStableId();
        Events = new TemplateEventCollection(this);
    }

    internal SubVoice(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        _project = project;
        Id = preservedId;
        Events = new TemplateEventCollection(this);
    }

    private readonly MidoraProject _project;

    internal TemplateEvent MaterializeEvent(TemplateEventSnapshotValue value)
    {
        TemplateEvent result = new(_project, value.Id)
        {
            Kind = value.Kind, Tick = value.Tick, LengthTicks = value.LengthTicks,
            Number = value.Number, Value = value.Value, SecondaryValue = value.SecondaryValue,
            HasBankMsb = value.HasBankMsb, HasBankLsb = value.HasBankLsb,
            FollowPitchDelta = value.FollowPitchDelta
        };
        result.AttachTo(this);
        return result;
    }

    public MidoraId Id { get; init; }
    public string? Name { get; set; }
    public int? RootNoteOverride { get; set; }
    public MidiInitialState InitialState { get; } = new();
    public TemplateEventCollection Events { get; }
    public InstrumentChangeSet InstrumentChanges { get; set; } = InstrumentChangeSet.Empty;
    public List<SubVoiceEventMapping> EventMappings { get; } = [];
    public List<ValueCurve> Curves { get; } = [];

    public SubVoiceEventMapping? FindEventMapping(TemplateEventMappingTarget target) =>
        EventMappings.SingleOrDefault(value => value.Target == target);

    public SubVoiceEventMapping GetOrCreateEventMapping(TemplateEventMappingTarget target)
    {
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        SubVoiceEventMapping? existing = FindEventMapping(target);
        if (existing is not null)
        {
            return existing;
        }
        SubVoiceEventMapping created = new(_project, target);
        EventMappings.Add(created);
        return created;
    }

    internal void EnsureEventMappings(TemplateEvent value, bool createOptional)
    {
        foreach (TemplateEventMappingTarget target in TemplateEventMappingTarget.Enumerate(value))
        {
            if (FindEventMapping(target) is not null) continue;
            bool mandatory = target.EventKind == TemplateEventKind.Note;
            // Optional non-Note owners are created only for a genuinely new
            // target. Existing raw events with no owner represent an explicit
            // Mapping deletion and must remain raw during edits/reinsertion.
            if (!mandatory && (!createOptional || Events.CreateQuerySnapshot().EnumerateAll().Any(existing =>
                    TemplateEventMappingTarget.Enumerate(existing).Contains(target))))
            {
                continue;
            }
            _ = GetOrCreateEventMapping(target);
        }
    }
}

public sealed class TemplateEventCollection : Collection<TemplateEvent>
{
    private readonly SubVoice _owner;
    private readonly PagedTimelineObjectList<TemplateEvent, TemplateEventSnapshotValue> _store;
    private bool _suppressOptionalMappingCreation;

    internal TemplateEventCollection(SubVoice owner)
        : this(owner, CreateStore(owner))
    {
    }

    private TemplateEventCollection(
        SubVoice owner,
        PagedTimelineObjectList<TemplateEvent, TemplateEventSnapshotValue> store)
        : base(store)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _store = store;
    }

    public long Generation => _store.Generation;
    public int PageCount => _store.PageCount;
    internal (int RetainedObjects, int FacadeSlots) StorageCounts => _store.StorageCounts;

    public void AddRange(IEnumerable<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<TemplateEvent> materialized = values as IReadOnlyList<TemplateEvent>
            ?? values.ToArray();
        InsertRange(Count, materialized);
    }

    public void InsertRange(int index, IReadOnlyList<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (values.Count == 0) return;

        // Detached events with authored mapping state retain the legacy
        // item-by-item merge path. Fresh and already-owned events have no
        // fallible detached merge, so they can publish one structural range
        // without changing CLR object identity or mapping ownership semantics.
        if (values.Any(value => value is null || !value.CanUseBulkAttachPath(_owner)))
        {
            using IDisposable batch = _store.BeginBatchChange();
            for (int valueIndex = 0; valueIndex < values.Count; valueIndex++)
                Insert(checked(index + valueIndex), values[valueIndex]);
            return;
        }

        _store.ValidateInsertRange(values);
        foreach (TemplateEvent item in values)
        {
            bool newlyAttached = item.AttachTo(_owner);
            _owner.EnsureEventMappings(
                item,
                createOptional: newlyAttached && !_suppressOptionalMappingCreation);
        }
        _store.InsertRange(index, values);
    }

    public int RemoveRange(IReadOnlyCollection<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return _store.RemoveRange(values);
    }

    internal Action RemoveRangeForExactCollision(IReadOnlyCollection<TemplateEvent> values) =>
        _store.RemoveRangeForExactCollision(values);

    internal Action RemoveRangeWithUndo(IReadOnlyCollection<TemplateEvent> values) =>
        _store.RemoveRangeForExactCollision(values);

    public IDisposable BeginBatchChange() => _store.BeginBatchChange();

    public TemplateEventQuerySnapshot CreateQuerySnapshot() =>
        new(_store.CreateSnapshot());

    public void AdoptSnapshot(MidoraProject project, TemplateEventQuerySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptSnapshot(snapshot.Values, value =>
        {
            TemplateEvent result = new(project, value.Id)
            {
                Kind = value.Kind, Tick = value.Tick, LengthTicks = value.LengthTicks,
                Number = value.Number, Value = value.Value, SecondaryValue = value.SecondaryValue,
                HasBankMsb = value.HasBankMsb, HasBankLsb = value.HasBankLsb,
                FollowPitchDelta = value.FollowPitchDelta
            };
            result.AttachTo(_owner);
            return result;
        });
    }

    public void AdoptSource(MidoraProject project, IImmutableTimelineValueSource<TemplateEventSnapshotValue> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        _store.AdoptSource(source, value =>
        {
            TemplateEvent result = new(project, value.Id)
            {
                Kind = value.Kind, Tick = value.Tick, LengthTicks = value.LengthTicks,
                Number = value.Number, Value = value.Value, SecondaryValue = value.SecondaryValue,
                HasBankMsb = value.HasBankMsb, HasBankLsb = value.HasBankLsb,
                FollowPitchDelta = value.FollowPitchDelta
            };
            result.AttachTo(_owner);
            return result;
        }, cancellationToken);
    }

    public void AdoptEditedSnapshot(MidoraProject project, TemplateEventQuerySnapshot snapshot,
        IImmutableTimelineValueSource<TimelineValueEdit<TemplateEventSnapshotValue>> changes,
        IImmutableTimelineValueSource<TemplateEventSnapshotValue>? appended = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptEditedSnapshot(snapshot.Values, changes, appended, value =>
        {
            TemplateEvent result = new(project, value.Id)
            {
                Kind = value.Kind, Tick = value.Tick, LengthTicks = value.LengthTicks,
                Number = value.Number, Value = value.Value, SecondaryValue = value.SecondaryValue,
                HasBankMsb = value.HasBankMsb, HasBankLsb = value.HasBankLsb,
                FollowPitchDelta = value.FollowPitchDelta
            };
            result.AttachTo(_owner);
            return result;
        }, cancellationToken);
    }

    public bool TryGetById(MidoraId id, out TemplateEvent? value) =>
        _store.TryGetById(id, out value);

    public void AdoptSplicedSnapshot(MidoraProject project, TemplateEventQuerySnapshot snapshot,
        IImmutableTimelineValueSource<TimelineValueSplice> splices,
        IIndexedImmutableTimelineValueSource<TemplateEventSnapshotValue> values, CancellationToken cancellationToken = default)
    {
        _store.AdoptSplicedSnapshot(snapshot.Values, splices, values, value =>
        {
            TemplateEvent result = new(project, value.Id)
            {
                Kind = value.Kind, Tick = value.Tick, LengthTicks = value.LengthTicks,
                Number = value.Number, Value = value.Value, SecondaryValue = value.SecondaryValue,
                HasBankMsb = value.HasBankMsb, HasBankLsb = value.HasBankLsb, FollowPitchDelta = value.FollowPitchDelta
            };
            result.AttachTo(_owner);
            return result;
        }, cancellationToken);
    }

    public IReadOnlyList<TemplateEvent> ResolveByIdsInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids) =>
        _store.ResolveByIdsInCollectionOrder(ids);

    public IReadOnlyList<(int Index, TemplateEvent Value)> ResolveByIdsWithIndicesInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids) =>
        _store.ResolveByIdsWithIndicesInCollectionOrder(ids);

    internal void AddRangeWithoutOptionalMappingCreation(IEnumerable<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        bool oldValue = _suppressOptionalMappingCreation;
        _suppressOptionalMappingCreation = true;
        try
        {
            AddRange(values);
        }
        finally
        {
            _suppressOptionalMappingCreation = oldValue;
        }
    }

    internal void InsertRangeWithoutOptionalMappingCreation(
        int index,
        IReadOnlyList<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        bool oldValue = _suppressOptionalMappingCreation;
        _suppressOptionalMappingCreation = true;
        try
        {
            InsertRange(index, values);
        }
        finally
        {
            _suppressOptionalMappingCreation = oldValue;
        }
    }

    internal void AddWithoutOptionalMappingCreation(TemplateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool oldValue = _suppressOptionalMappingCreation;
        _suppressOptionalMappingCreation = true;
        try
        {
            Add(value);
        }
        finally
        {
            _suppressOptionalMappingCreation = oldValue;
        }
    }

    protected override void InsertItem(int index, TemplateEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool newlyAttached = item.AttachTo(_owner);
        _owner.EnsureEventMappings(
            item,
            createOptional: newlyAttached && !_suppressOptionalMappingCreation);
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, TemplateEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool newlyAttached = item.AttachTo(_owner);
        _owner.EnsureEventMappings(
            item,
            createOptional: newlyAttached && !_suppressOptionalMappingCreation);
        base.SetItem(index, item);
    }

    private static PagedTimelineObjectList<TemplateEvent, TemplateEventSnapshotValue> CreateStore(SubVoice owner) =>
        new(
            static value => new(
                value.Id,
                value.Kind,
                value.Tick,
                value.LengthTicks,
                value.Number,
                value.Value,
                value.SecondaryValue,
                value.HasBankMsb,
                value.HasBankLsb,
                value.FollowPitchDelta),
            static value => value.Id,
            static value => value.Tick,
            static value => value.Kind == TemplateEventKind.Note
                ? SaturatingAdd(value.Tick, Math.Max(1, value.LengthTicks))
                : SaturatingAdd(value.Tick, 1),
            static value => value.Kind == TemplateEventKind.Note ? value.Number : 0,
            static value => PagedTimelineFingerprint.ForTemplateEvent(value),
            static (value, sink) => value.SetChangeSink(sink),
            static value => value.Kind == TemplateEventKind.Note ? 1UL : 2UL,
            static value => value.Kind == TemplateEventKind.Note
                ? value.Value / 127d
                : value.Value,
            static value => TemplateEventMidiTargets.EnumerateDiscoveryKeys(value),
            owner.MaterializeEvent);

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed class InstrumentEnvelope
{
    public InstrumentEnvelope(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal InstrumentEnvelope(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public string? Name { get; set; }
    public long DelayTicks { get; set; }
    public long AttackTicks { get; set; }
    public long HoldTicks { get; set; }
    public long DecayTicks { get; set; }
    public double StartValue { get; set; }
    public double PeakValue { get; set; } = 1;
    public double SustainValue { get; set; } = 1;
    public long ReleaseTicks { get; set; }
    public double EndValue { get; set; }
}

public sealed class EventInstrument
{
    public EventInstrument(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal EventInstrument(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public MidoraColor Color { get; set; } = MidoraColor.DefaultInstrument;
    public int RootNote { get; set; } = 60;
    public long TemplateLengthTicks { get; set; }
    public long PreRollTicks { get; set; }
    public bool RequiresChannelIsolation { get; set; }
    public OverlapPolicy OverlapPolicy { get; set; } = OverlapPolicy.Reject;
    public OverlapScope OverlapScope { get; set; } = OverlapScope.SamePitch;
    public ShortNoteLifecycle ShortLifecycle { get; set; } = ShortNoteLifecycle.CutAtNoteOff;
    public LongNoteLifecycle LongLifecycle { get; set; } = LongNoteLifecycle.HoldLastState;
    public long? LoopStartTick { get; set; }
    public long? LoopEndTick { get; set; }
    public MidiInitialState InitialState { get; } = new();
    public List<LogicalParameterDefinition> LogicalParameters { get; } = [];
    public List<SubVoice> SubVoices { get; } = [];
    public List<InstrumentEnvelope> Envelopes { get; } = [];
    public List<CSharpMappingFunction> MappingFunctions { get; } = [];
    public List<LogicalParameterMapping> ParameterMappings { get; } = [];
}

/// <summary>
/// A materialized Event Instrument execution identity. The object has no user
/// visible name: it exists solely to let one or more Logical Tracks share one
/// stateful compiled Channel Unit while other usages of the same Definition
/// remain isolated.
/// </summary>
public sealed class EventInstrumentUsage
{
    public EventInstrumentUsage(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal EventInstrumentUsage(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public MidoraId EventInstrumentId { get; set; }
}
