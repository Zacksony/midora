using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Builds detached replacement roots for one timeline owner.  The returned
/// graph is not attached to the formal Project until an owner-root replacement
/// edit publishes it.
/// </summary>
internal static class ProjectTimelineOwnerRootClone
{
    public static Segment CloneLogicalSegment(
        MidoraProject project,
        Segment source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);

        Segment result = new(project, source.Id)
        {
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = source.LengthTicks,
            ContentOffsetTick = source.ContentOffsetTick
        };

        cancellationToken.ThrowIfCancellationRequested();
        result.Notes.AdoptSnapshot(project, source.Notes.CreateQuerySnapshot());
        for (int index = 0; index < source.ParameterLanes.Count; index++)
        {
            CheckCancellation(index, cancellationToken);
            LogicalParameterLane lane = source.ParameterLanes[index];
            LogicalParameterLane laneCopy = new(project, lane.Id)
            {
                ParameterId = lane.ParameterId
            };
            laneCopy.Points.AdoptSnapshot(project, lane.Points.CreateQuerySnapshot());
            result.ParameterLanes.Add(laneCopy);
        }
        return result;
    }

    public static MidiSegment CloneDirectMidiSegment(
        MidoraProject project,
        MidiSegment source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);

        MidiSegment result = new(project, source.Id)
        {
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = source.LengthTicks,
            ContentOffsetTick = source.ContentOffsetTick
        };
        // CloneContentTo captures the three formal COW sequences.  For a
        // source-backed MIDI Segment this retains the immutable page source;
        // it does not enumerate or materialize the base records.
        source.CloneContentTo(result, cancellationToken);
        return result;
    }

    public static SubVoice CloneSubVoice(
        MidoraProject project,
        SubVoice source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);

        SubVoice result = new(project, source.Id)
        {
            Name = source.Name,
            RootNoteOverride = source.RootNoteOverride
        };
        CopyState(source.InitialState, result.InitialState, cancellationToken);

        for (int index = 0; index < source.EventMappings.Count; index++)
        {
            CheckCancellation(index, cancellationToken);
            SubVoiceEventMapping mapping = source.EventMappings[index];
            SubVoiceEventMapping mappingCopy = new(
                project,
                mapping.Target,
                mapping.Steps.Id);
            CopyTargetSettings(mapping.TargetSettings, mappingCopy.TargetSettings);
            CopyChain(project, mapping.Steps, mappingCopy.Steps, cancellationToken);
            result.EventMappings.Add(mappingCopy);
        }

        cancellationToken.ThrowIfCancellationRequested();
        result.Events.AdoptSnapshot(project, source.Events.CreateQuerySnapshot());
        result.InstrumentChanges = source.InstrumentChanges;
        for (int index = 0; index < source.Curves.Count; index++)
        {
            CheckCancellation(index, cancellationToken);
            ValueCurve curve = source.Curves[index];
            ValueCurve curveCopy = new(project, curve.Id) { Target = curve.Target };
            CopyTargetSettings(curve.TargetSettings, curveCopy.TargetSettings);
            curveCopy.Points.AdoptSnapshot(project, curve.Points.CreateQuerySnapshot());
            result.Curves.Add(curveCopy);
        }
        return result;
    }

    private static IEnumerable<CurvePoint> CloneCurvePoints(
        IEnumerable<CurvePoint> source,
        MidoraProject project,
        CancellationToken cancellationToken)
    {
        int index = 0;
        foreach (CurvePoint point in source)
        {
            CheckCancellation(index++, cancellationToken);
            yield return new CurvePoint(
                project,
                point.Id,
                point.Tick,
                point.Value,
                point.Interpolation);
        }
    }

    private static void CopyState(
        MidiInitialState source,
        MidiInitialState target,
        CancellationToken cancellationToken)
    {
        target.BankMsb = source.BankMsb;
        target.BankLsb = source.BankLsb;
        target.Program = source.Program;
        target.PitchBend = source.PitchBend;
        target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        foreach ((int number, int value) in source.Controllers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Controllers.Add(number, value);
        }
        foreach ((int number, int value) in source.RegisteredParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.RegisteredParameters.Add(number, value);
        }
        foreach ((int number, int value) in source.NonRegisteredParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.NonRegisteredParameters.Add(number, value);
        }
    }

    private static void CopyChain(
        MidoraProject project,
        MappingChain source,
        MappingChain target,
        CancellationToken cancellationToken)
    {
        target.IsEnabled = source.IsEnabled;
        foreach (ValueMappingStep step in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Add(new ValueMappingStep(project, step.Id)
            {
                IsEnabled = step.IsEnabled,
                Source = step.Source,
                Operation = step.Operation,
                LogicalParameterId = step.LogicalParameterId,
                EnvelopeId = step.EnvelopeId,
                MappingFunctionId = step.MappingFunctionId,
                Constant = step.Constant,
                SourceMinimum = step.SourceMinimum,
                SourceMaximum = step.SourceMaximum,
                TargetMinimum = step.TargetMinimum,
                TargetMaximum = step.TargetMaximum,
                InputOverflow = step.InputOverflow,
                DivideByZero = step.DivideByZero
            });
        }
    }

    private static void CopyTargetSettings(
        MidiIntegerTargetSettings source,
        MidiIntegerTargetSettings target)
    {
        target.Rounding = source.Rounding;
        target.Overflow = source.Overflow;
    }

    private static void CheckCancellation(int index, CancellationToken cancellationToken)
    {
        if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Describes one detached timeline-owner root replacement.  The typed factory
/// methods are the only construction path, so the batch publisher never has
/// to accept an arbitrary owner/root pairing.
/// </summary>
internal sealed class ProjectTimelineOwnerRootReplacementSpec
{
    private ProjectTimelineOwnerRootReplacementSpec(
        ProjectTimelineOwnerRootKind kind,
        object owner,
        object expectedRoot,
        object replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        Kind = kind;
        Owner = owner;
        ExpectedRoot = expectedRoot;
        ReplacementRoot = replacementRoot;
        ExpectedSourceStamp = expectedSourceStamp;
    }

    internal ProjectTimelineOwnerRootKind Kind { get; }
    internal object Owner { get; }
    internal object ExpectedRoot { get; }
    internal object ReplacementRoot { get; }
    internal ProjectTimelineOwnerSourceStamp? ExpectedSourceStamp { get; }

    public static ProjectTimelineOwnerRootReplacementSpec LogicalSegment(
        LogicalTrack owner,
        Segment expectedRoot,
        Segment replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) => new(
            ProjectTimelineOwnerRootKind.LogicalSegment,
            owner ?? throw new ArgumentNullException(nameof(owner)),
            expectedRoot ?? throw new ArgumentNullException(nameof(expectedRoot)),
            replacementRoot ?? throw new ArgumentNullException(nameof(replacementRoot)),
            expectedSourceStamp);

    public static ProjectTimelineOwnerRootReplacementSpec DirectMidiSegment(
        PureMidiTrack owner,
        MidiSegment expectedRoot,
        MidiSegment replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) => new(
            ProjectTimelineOwnerRootKind.DirectMidiSegment,
            owner ?? throw new ArgumentNullException(nameof(owner)),
            expectedRoot ?? throw new ArgumentNullException(nameof(expectedRoot)),
            replacementRoot ?? throw new ArgumentNullException(nameof(replacementRoot)),
            expectedSourceStamp);

    public static ProjectTimelineOwnerRootReplacementSpec SubVoice(
        EventInstrument owner,
        SubVoice expectedRoot,
        SubVoice replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) => new(
            ProjectTimelineOwnerRootKind.SubVoice,
            owner ?? throw new ArgumentNullException(nameof(owner)),
            expectedRoot ?? throw new ArgumentNullException(nameof(expectedRoot)),
            replacementRoot ?? throw new ArgumentNullException(nameof(replacementRoot)),
            expectedSourceStamp);
}

internal enum ProjectTimelineOwnerRootKind
{
    LogicalSegment,
    DirectMidiSegment,
    SubVoice
}

/// <summary>
/// Creates compact single- or multi-owner edits whose publication and history
/// operation are expected-reference root exchanges.  This is deliberately not
/// a Project-catalog transaction: only the captured timeline root slots are
/// exchanged.
/// </summary>
internal static class ProjectTimelineOwnerRootReplacement
{
    public static IPreparedProjectEdit PrepareLogicalSegmentRevisionGate(
        MidoraProject project,
        LogicalTrack owner,
        Segment expectedRoot,
        ProjectChangeSet changes,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) =>
        PrepareRevisionGate(
            project,
            FreezeLogicalSegmentGuard(project, owner, expectedRoot, expectedSourceStamp),
            changes);

    public static IPreparedProjectEdit PrepareDirectMidiSegmentRevisionGate(
        MidoraProject project,
        PureMidiTrack owner,
        MidiSegment expectedRoot,
        ProjectChangeSet changes,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) =>
        PrepareRevisionGate(
            project,
            FreezeDirectMidiSegmentGuard(project, owner, expectedRoot, expectedSourceStamp),
            changes);

    public static IPreparedProjectEdit PrepareSubVoiceRevisionGate(
        MidoraProject project,
        EventInstrument owner,
        SubVoice expectedRoot,
        ProjectChangeSet changes,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) =>
        PrepareRevisionGate(
            project,
            FreezeSubVoiceGuard(project, owner, expectedRoot, expectedSourceStamp),
            changes);

    public static IPreparedProjectEdit PrepareLogicalSegment(
        MidoraProject project,
        LogicalTrack owner,
        Segment expectedRoot,
        Segment replacementRoot,
        ProjectChangeSet changes,
        long? expectedNextStableId = null,
        long? replacementNextStableId = null,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) =>
        PrepareMultiple(
            project,
            [ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                owner,
                expectedRoot,
                replacementRoot,
                expectedSourceStamp)],
            changes,
            expectedNextStableId,
            replacementNextStableId);

    public static IPreparedProjectEdit PrepareDirectMidiSegment(
        MidoraProject project,
        PureMidiTrack owner,
        MidiSegment expectedRoot,
        MidiSegment replacementRoot,
        ProjectChangeSet changes,
        long? expectedNextStableId = null,
        long? replacementNextStableId = null,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) =>
        PrepareMultiple(
            project,
            [ProjectTimelineOwnerRootReplacementSpec.DirectMidiSegment(
                owner,
                expectedRoot,
                replacementRoot,
                expectedSourceStamp)],
            changes,
            expectedNextStableId,
            replacementNextStableId);

    public static IPreparedProjectEdit PrepareSubVoice(
        MidoraProject project,
        EventInstrument owner,
        SubVoice expectedRoot,
        SubVoice replacementRoot,
        ProjectChangeSet changes,
        long? expectedNextStableId = null,
        long? replacementNextStableId = null,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null) =>
        PrepareMultiple(
            project,
            [ProjectTimelineOwnerRootReplacementSpec.SubVoice(
                owner,
                expectedRoot,
                replacementRoot,
                expectedSourceStamp)],
            changes,
            expectedNextStableId,
            replacementNextStableId);

    public static IPreparedProjectEdit PrepareMultiple(
        MidoraProject project,
        IReadOnlyCollection<ProjectTimelineOwnerRootReplacementSpec> replacements,
        ProjectChangeSet changes,
        long? expectedNextStableId = null,
        long? replacementNextStableId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(changes);
        ProjectTimelineOwnerRootReplacementSpec[] frozenSpecifications = replacements.ToArray();
        if (frozenSpecifications.Length == 0)
            throw new ArgumentException("At least one timeline owner root is required.", nameof(replacements));
        if (frozenSpecifications.Any(static value => value is null))
            throw new ArgumentException("A timeline owner root specification cannot be null.", nameof(replacements));
        ValidateAllocatorBounds(project, expectedNextStableId, replacementNextStableId);

        FrozenRootSlot[] slots = frozenSpecifications.Select(value => Freeze(project, value)).ToArray();
        HashSet<object> expectedRoots = new(ReferenceEqualityComparer.Instance);
        HashSet<object> replacementRoots = new(ReferenceEqualityComparer.Instance);
        foreach (FrozenRootSlot slot in slots)
        {
            if (!expectedRoots.Add(slot.OldRoot))
                throw new ArgumentException("A timeline owner root is listed more than once.", nameof(replacements));
            if (!replacementRoots.Add(slot.NewRoot))
                throw new ArgumentException("A detached replacement root is listed more than once.", nameof(replacements));
        }
        if (expectedRoots.Overlaps(replacementRoots))
            throw new ArgumentException(
                "A detached replacement root cannot also be an expected active root in the same batch.",
                nameof(replacements));

        return new PreparedMultipleOwnerRootReplacement(
            project,
            slots,
            CloneChanges(changes),
            expectedNextStableId,
            replacementNextStableId);
    }

    private static void ValidateAllocatorBounds(
        MidoraProject project,
        long? expectedNextStableId,
        long? replacementNextStableId)
    {
        if (expectedNextStableId.HasValue != replacementNextStableId.HasValue)
        {
            throw new ArgumentException(
                "A detached Stable ID reservation requires both allocator bounds.",
                nameof(replacementNextStableId));
        }
        if (expectedNextStableId is long expectedId)
        {
            if (expectedId <= 0 || replacementNextStableId!.Value < expectedId)
                throw new ArgumentOutOfRangeException(nameof(replacementNextStableId));
            if (project.NextStableId != expectedId)
            {
                throw new InvalidOperationException(
                    "The detached Stable ID reservation belongs to a stale Project allocator revision.");
            }
        }
    }

    private static FrozenRootSlot Freeze(
        MidoraProject project,
        ProjectTimelineOwnerRootReplacementSpec specification) => specification.Kind switch
    {
        ProjectTimelineOwnerRootKind.LogicalSegment => FreezeLogicalSegment(
            project,
            (LogicalTrack)specification.Owner,
            (Segment)specification.ExpectedRoot,
            (Segment)specification.ReplacementRoot,
            specification.ExpectedSourceStamp),
        ProjectTimelineOwnerRootKind.DirectMidiSegment => FreezeDirectMidiSegment(
            project,
            (PureMidiTrack)specification.Owner,
            (MidiSegment)specification.ExpectedRoot,
            (MidiSegment)specification.ReplacementRoot,
            specification.ExpectedSourceStamp),
        ProjectTimelineOwnerRootKind.SubVoice => FreezeSubVoice(
            project,
            (EventInstrument)specification.Owner,
            (SubVoice)specification.ExpectedRoot,
            (SubVoice)specification.ReplacementRoot,
            specification.ExpectedSourceStamp),
        _ => throw new ArgumentOutOfRangeException(nameof(specification))
    };

    private static IPreparedProjectEdit PrepareRevisionGate(
        MidoraProject project,
        FrozenActiveRootSlot slot,
        ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(changes);
        return new PreparedOwnerRevisionGate(project, slot, CloneChanges(changes));
    }

    private static FrozenActiveRootSlot FreezeLogicalSegmentGuard(
        MidoraProject project,
        LogicalTrack owner,
        Segment expectedRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        int ownerIndex = FindReferenceIndex(project.Tracks, owner);
        if (ownerIndex < 0)
            throw new InvalidOperationException("The Logical Segment owner is not active in the Project.");
        int rootIndex = FindReferenceIndex(owner.Segments, expectedRoot);
        if (rootIndex < 0)
            throw new InvalidOperationException("The expected Logical Segment root is not active in its owner.");
        ProjectTimelineOwnerSourceStamp sourceStamp =
            expectedSourceStamp ?? ProjectTimelineOwnerSourceStamp.Capture(expectedRoot);
        if (!sourceStamp.Matches(expectedRoot))
            throw new InvalidOperationException("The Logical Segment source changed while the edit was being prepared.");
        return new(
            ProjectTimelineOwnerRootKind.LogicalSegment,
            owner,
            ownerIndex,
            rootIndex,
            expectedRoot,
            sourceStamp);
    }

    private static FrozenActiveRootSlot FreezeDirectMidiSegmentGuard(
        MidoraProject project,
        PureMidiTrack owner,
        MidiSegment expectedRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        int ownerIndex = FindReferenceIndex(project.PureMidiTracks, owner);
        if (ownerIndex < 0)
            throw new InvalidOperationException("The Direct MIDI Segment owner is not active in the Project.");
        int rootIndex = FindReferenceIndex(owner.Segments, expectedRoot);
        if (rootIndex < 0)
            throw new InvalidOperationException("The expected Direct MIDI Segment root is not active in its owner.");
        ProjectTimelineOwnerSourceStamp sourceStamp =
            expectedSourceStamp ?? ProjectTimelineOwnerSourceStamp.Capture(expectedRoot);
        if (!sourceStamp.Matches(expectedRoot))
            throw new InvalidOperationException("The Direct MIDI Segment source changed while the edit was being prepared.");
        return new(
            ProjectTimelineOwnerRootKind.DirectMidiSegment,
            owner,
            ownerIndex,
            rootIndex,
            expectedRoot,
            sourceStamp);
    }

    private static FrozenActiveRootSlot FreezeSubVoiceGuard(
        MidoraProject project,
        EventInstrument owner,
        SubVoice expectedRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        int ownerIndex = FindReferenceIndex(project.EventInstruments, owner);
        if (ownerIndex < 0)
            throw new InvalidOperationException("The SubVoice owner is not active in the Project.");
        int rootIndex = FindReferenceIndex(owner.SubVoices, expectedRoot);
        if (rootIndex < 0)
            throw new InvalidOperationException("The expected SubVoice root is not active in its owner.");
        ProjectTimelineOwnerSourceStamp sourceStamp =
            expectedSourceStamp ?? ProjectTimelineOwnerSourceStamp.Capture(owner, expectedRoot);
        if (!sourceStamp.Matches(expectedRoot))
            throw new InvalidOperationException("The SubVoice source changed while the edit was being prepared.");
        return new(
            ProjectTimelineOwnerRootKind.SubVoice,
            owner,
            ownerIndex,
            rootIndex,
            expectedRoot,
            sourceStamp);
    }

    private static FrozenRootSlot FreezeLogicalSegment(
        MidoraProject project,
        LogicalTrack owner,
        Segment expectedRoot,
        Segment replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        ValidateDetachedRoot(project, expectedRoot, replacementRoot, "Logical Segment");
        int ownerIndex = FindReferenceIndex(project.Tracks, owner);
        if (ownerIndex < 0)
            throw new InvalidOperationException("The Logical Segment owner is not active in the Project.");
        int rootIndex = FindReferenceIndex(owner.Segments, expectedRoot);
        if (rootIndex < 0)
            throw new InvalidOperationException("The expected Logical Segment root is not active in its owner.");
        if (project.Tracks.Any(track => FindReferenceIndex(track.Segments, replacementRoot) >= 0))
            throw new ArgumentException("The Logical Segment replacement root is already attached.");
        ProjectTimelineOwnerSourceStamp frozenExpectedSource =
            expectedSourceStamp ?? ProjectTimelineOwnerSourceStamp.Capture(expectedRoot);
        if (!frozenExpectedSource.Matches(expectedRoot))
            throw new InvalidOperationException("The Logical Segment source changed while the edit was being prepared.");
        return new(
            ProjectTimelineOwnerRootKind.LogicalSegment,
            owner,
            ownerIndex,
            rootIndex,
            expectedRoot,
            replacementRoot,
            frozenExpectedSource,
            ProjectTimelineOwnerSourceStamp.Capture(replacementRoot));
    }

    private static FrozenRootSlot FreezeDirectMidiSegment(
        MidoraProject project,
        PureMidiTrack owner,
        MidiSegment expectedRoot,
        MidiSegment replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        ValidateDetachedRoot(project, expectedRoot, replacementRoot, "Direct MIDI Segment");
        int ownerIndex = FindReferenceIndex(project.PureMidiTracks, owner);
        if (ownerIndex < 0)
            throw new InvalidOperationException("The Direct MIDI Segment owner is not active in the Project.");
        int rootIndex = FindReferenceIndex(owner.Segments, expectedRoot);
        if (rootIndex < 0)
            throw new InvalidOperationException("The expected Direct MIDI Segment root is not active in its owner.");
        if (project.PureMidiTracks.Any(track => FindReferenceIndex(track.Segments, replacementRoot) >= 0))
            throw new ArgumentException("The Direct MIDI Segment replacement root is already attached.");
        ProjectTimelineOwnerSourceStamp frozenExpectedSource =
            expectedSourceStamp ?? ProjectTimelineOwnerSourceStamp.Capture(expectedRoot);
        if (!frozenExpectedSource.Matches(expectedRoot))
            throw new InvalidOperationException("The Direct MIDI Segment source changed while the edit was being prepared.");
        return new(
            ProjectTimelineOwnerRootKind.DirectMidiSegment,
            owner,
            ownerIndex,
            rootIndex,
            expectedRoot,
            replacementRoot,
            frozenExpectedSource,
            ProjectTimelineOwnerSourceStamp.Capture(replacementRoot));
    }

    private static FrozenRootSlot FreezeSubVoice(
        MidoraProject project,
        EventInstrument owner,
        SubVoice expectedRoot,
        SubVoice replacementRoot,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp)
    {
        ValidateDetachedRoot(project, expectedRoot, replacementRoot, "SubVoice");
        int ownerIndex = FindReferenceIndex(project.EventInstruments, owner);
        if (ownerIndex < 0)
            throw new InvalidOperationException("The SubVoice owner is not active in the Project.");
        int rootIndex = FindReferenceIndex(owner.SubVoices, expectedRoot);
        if (rootIndex < 0)
            throw new InvalidOperationException("The expected SubVoice root is not active in its owner.");
        if (project.EventInstruments.Any(instrument =>
                FindReferenceIndex(instrument.SubVoices, replacementRoot) >= 0))
        {
            throw new ArgumentException("The SubVoice replacement root is already attached.");
        }
        ProjectTimelineOwnerSourceStamp frozenExpectedSource =
            expectedSourceStamp ?? ProjectTimelineOwnerSourceStamp.Capture(owner, expectedRoot);
        if (!frozenExpectedSource.Matches(expectedRoot))
            throw new InvalidOperationException("The SubVoice source changed while the edit was being prepared.");
        return new(
            ProjectTimelineOwnerRootKind.SubVoice,
            owner,
            ownerIndex,
            rootIndex,
            expectedRoot,
            replacementRoot,
            frozenExpectedSource,
            ProjectTimelineOwnerSourceStamp.Capture(owner, replacementRoot));
    }

    private static void ValidateDetachedRoot<TRoot>(
        MidoraProject project,
        TRoot expectedRoot,
        TRoot replacementRoot,
        string rootKind)
        where TRoot : class
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(replacementRoot);
        if (ReferenceEquals(expectedRoot, replacementRoot))
            throw new ArgumentException("A replacement root must be detached.", nameof(replacementRoot));
        if (GetStableId(expectedRoot) != GetStableId(replacementRoot))
        {
            throw new ArgumentException(
                $"The detached {rootKind} replacement must preserve its Stable ID.",
                nameof(replacementRoot));
        }
    }

    private static MidoraId GetStableId<TRoot>(TRoot root)
        where TRoot : class => root switch
        {
            Segment value => value.Id,
            MidiSegment value => value.Id,
            SubVoice value => value.Id,
            _ => throw new ArgumentException("Unsupported timeline owner root.", nameof(root))
        };

    private static int FindReferenceIndex<T>(List<T> values, T expected)
        where T : class
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (ReferenceEquals(values[index], expected)) return index;
        }
        return -1;
    }

    private static ProjectChangeSet CloneChanges(ProjectChangeSet source)
    {
        ProjectChangeSet result = new()
        {
            AffectsEverything = source.AffectsEverything,
            AffectsConductor = source.AffectsConductor,
            AffectsAudioPcmCacheGeneration = source.AffectsAudioPcmCacheGeneration
        };
        result.TrackIds.UnionWith(source.TrackIds);
        result.EventInstrumentIds.UnionWith(source.EventInstrumentIds);
        result.EventInstrumentUsageIds.UnionWith(source.EventInstrumentUsageIds);
        result.MidiChannelRootIds.UnionWith(source.MidiChannelRootIds);
        result.PureMidiTrackIds.UnionWith(source.PureMidiTrackIds);
        result.PresentationTrackIds.UnionWith(source.PresentationTrackIds);
        result.PresentationEventInstrumentIds.UnionWith(
            source.PresentationEventInstrumentIds);
        result.TimelineOwnerChanges.AddRange(source.TimelineOwnerChanges);
        return result;
    }

    private sealed record FrozenRootSlot(
        ProjectTimelineOwnerRootKind Kind,
        object Owner,
        int OwnerIndex,
        int RootIndex,
        object OldRoot,
        object NewRoot,
        ProjectTimelineOwnerSourceStamp OldSourceStamp,
        ProjectTimelineOwnerSourceStamp NewSourceStamp)
    {
        public void Validate(
            MidoraProject project,
            bool expectNewRoot,
            bool validateFrozenSource)
        {
            object expectedRoot = expectNewRoot ? NewRoot : OldRoot;
            switch (Kind)
            {
                case ProjectTimelineOwnerRootKind.LogicalSegment:
                    Validate(
                        project.Tracks,
                        (LogicalTrack)Owner,
                        ((LogicalTrack)Owner).Segments,
                        (Segment)expectedRoot,
                        "Logical Segment");
                    break;
                case ProjectTimelineOwnerRootKind.DirectMidiSegment:
                    Validate(
                        project.PureMidiTracks,
                        (PureMidiTrack)Owner,
                        ((PureMidiTrack)Owner).Segments,
                        (MidiSegment)expectedRoot,
                        "Direct MIDI Segment");
                    break;
                case ProjectTimelineOwnerRootKind.SubVoice:
                    Validate(
                        project.EventInstruments,
                        (EventInstrument)Owner,
                        ((EventInstrument)Owner).SubVoices,
                        (SubVoice)expectedRoot,
                        "SubVoice");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported timeline owner root kind.");
            }

            if (validateFrozenSource
                && !(expectNewRoot ? NewSourceStamp : OldSourceStamp).Matches(expectedRoot))
            {
                throw new InvalidOperationException(
                    "The expected timeline owner source revision changed before atomic publication.");
            }
        }

        public void Swap(bool toNewRoot)
        {
            object replacement = toNewRoot ? NewRoot : OldRoot;
            switch (Kind)
            {
                case ProjectTimelineOwnerRootKind.LogicalSegment:
                    ((LogicalTrack)Owner).Segments[RootIndex] = (Segment)replacement;
                    return;
                case ProjectTimelineOwnerRootKind.DirectMidiSegment:
                    ((PureMidiTrack)Owner).Segments[RootIndex] = (MidiSegment)replacement;
                    return;
                case ProjectTimelineOwnerRootKind.SubVoice:
                    ((EventInstrument)Owner).SubVoices[RootIndex] = (SubVoice)replacement;
                    return;
                default:
                    throw new InvalidOperationException("Unsupported timeline owner root kind.");
            }
        }

        public void ValidateFrozenReplacement()
        {
            if (!NewSourceStamp.Matches(NewRoot))
            {
                throw new InvalidOperationException(
                    "The detached timeline owner replacement changed before atomic publication.");
            }
        }

        private void Validate<TOwner, TRoot>(
            List<TOwner> projectOwners,
            TOwner owner,
            List<TRoot> roots,
            TRoot expectedRoot,
            string rootKind)
            where TOwner : class
            where TRoot : class
        {
            if ((uint)OwnerIndex >= (uint)projectOwners.Count
                || !ReferenceEquals(projectOwners[OwnerIndex], owner))
            {
                throw new InvalidOperationException(
                    $"The {rootKind} owner changed before atomic publication.");
            }
            if ((uint)RootIndex >= (uint)roots.Count
                || !ReferenceEquals(roots[RootIndex], expectedRoot))
            {
                throw new InvalidOperationException(
                    $"The expected {rootKind} root changed before atomic publication.");
            }
        }
    }

    private sealed record FrozenActiveRootSlot(
        ProjectTimelineOwnerRootKind Kind,
        object Owner,
        int OwnerIndex,
        int RootIndex,
        object Root,
        ProjectTimelineOwnerSourceStamp SourceStamp)
    {
        public void Validate(MidoraProject project, bool validateFrozenSource)
        {
            switch (Kind)
            {
                case ProjectTimelineOwnerRootKind.LogicalSegment:
                    Validate(
                        project.Tracks,
                        (LogicalTrack)Owner,
                        ((LogicalTrack)Owner).Segments,
                        (Segment)Root,
                        "Logical Segment");
                    break;
                case ProjectTimelineOwnerRootKind.DirectMidiSegment:
                    Validate(
                        project.PureMidiTracks,
                        (PureMidiTrack)Owner,
                        ((PureMidiTrack)Owner).Segments,
                        (MidiSegment)Root,
                        "Direct MIDI Segment");
                    break;
                case ProjectTimelineOwnerRootKind.SubVoice:
                    Validate(
                        project.EventInstruments,
                        (EventInstrument)Owner,
                        ((EventInstrument)Owner).SubVoices,
                        (SubVoice)Root,
                        "SubVoice");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported timeline owner root kind.");
            }

            if (validateFrozenSource && !SourceStamp.Matches(Root))
            {
                throw new InvalidOperationException(
                    "The expected timeline owner source revision changed before atomic publication.");
            }
        }

        private void Validate<TOwner, TRoot>(
            List<TOwner> projectOwners,
            TOwner owner,
            List<TRoot> roots,
            TRoot expectedRoot,
            string rootKind)
            where TOwner : class
            where TRoot : class
        {
            if ((uint)OwnerIndex >= (uint)projectOwners.Count
                || !ReferenceEquals(projectOwners[OwnerIndex], owner))
            {
                throw new InvalidOperationException(
                    $"The {rootKind} owner changed before atomic publication.");
            }
            if ((uint)RootIndex >= (uint)roots.Count
                || !ReferenceEquals(roots[RootIndex], expectedRoot))
            {
                throw new InvalidOperationException(
                    $"The expected {rootKind} root changed before atomic publication.");
            }
        }
    }

    private sealed class PreparedOwnerRevisionGate(
        MidoraProject project,
        FrozenActiveRootSlot slot,
        ProjectChangeSet changes)
        : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private readonly MidoraProject _project = project;
        private readonly FrozenActiveRootSlot _slot = slot;
        private bool _isApplied;
        private bool _hasPublishedOnce;
        private bool _rollbackAfterRejectedApply;

        public bool HasChanges => false;
        public ProjectChangeSet Changes { get; } = changes;

        public void ValidateForPublication(MidoraProject project)
        {
            ValidateProject(project);
            _slot.Validate(project, validateFrozenSource: !_hasPublishedOnce);
        }

        public void Apply(MidoraProject project)
        {
            if (_isApplied)
                throw new InvalidOperationException("The timeline owner revision gate is already applied.");
            _rollbackAfterRejectedApply = false;
            try
            {
                ValidateForPublication(project);
            }
            catch
            {
                _rollbackAfterRejectedApply = true;
                throw;
            }
            _isApplied = true;
            _hasPublishedOnce = true;
        }

        public void Undo(MidoraProject project)
        {
            if (!_isApplied)
            {
                if (_rollbackAfterRejectedApply)
                {
                    _rollbackAfterRejectedApply = false;
                    return;
                }
                throw new InvalidOperationException("The timeline owner revision gate is not applied.");
            }
            ValidateProject(project);
            _slot.Validate(project, validateFrozenSource: false);
            _isApplied = false;
        }

        private void ValidateProject(MidoraProject project)
        {
            if (!ReferenceEquals(project, _project))
            {
                throw new InvalidOperationException(
                    "The timeline owner revision gate belongs to another Project.");
            }
        }
    }

    private sealed class PreparedMultipleOwnerRootReplacement
        : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private readonly MidoraProject _project;
        private readonly FrozenRootSlot[] _slots;
        private readonly long? _expectedNextStableId;
        private readonly long? _replacementNextStableId;
        private bool _isApplied;
        private bool _hasPublishedOnce;
        private bool _hasPublishedStableIds;
        private bool _rollbackAfterRejectedApply;

        public PreparedMultipleOwnerRootReplacement(
            MidoraProject project,
            FrozenRootSlot[] slots,
            ProjectChangeSet changes,
            long? expectedNextStableId,
            long? replacementNextStableId)
        {
            _project = project;
            _slots = slots;
            Changes = changes;
            _expectedNextStableId = expectedNextStableId;
            _replacementNextStableId = replacementNextStableId;
        }

        public bool HasChanges => true;
        public ProjectChangeSet Changes { get; }

        public void ValidateForPublication(MidoraProject project)
        {
            ValidateProject(project);
            foreach (FrozenRootSlot slot in _slots)
            {
                slot.Validate(
                    project,
                    expectNewRoot: false,
                    validateFrozenSource: !_hasPublishedOnce);
            }
            if (!_hasPublishedOnce)
            {
                foreach (FrozenRootSlot slot in _slots) slot.ValidateFrozenReplacement();
            }
            if (!_hasPublishedStableIds
                && _expectedNextStableId is long expectedNextStableId
                && project.NextStableId != expectedNextStableId)
            {
                throw new InvalidOperationException(
                    "The Project Stable ID allocator changed before atomic publication.");
            }
        }

        public void Apply(MidoraProject project)
        {
            if (_isApplied)
                throw new InvalidOperationException("The timeline owner root replacement is already applied.");
            _rollbackAfterRejectedApply = false;
            try
            {
                ValidateForPublication(project);
            }
            catch
            {
                // ProjectCompilationSession invokes the rollback delegate even
                // when the edit delegate fails before changing Project state.
                // Record that exact case so the rollback is a verified no-op
                // rather than obscuring the source-revision error.
                _rollbackAfterRejectedApply = true;
                throw;
            }

            foreach (FrozenRootSlot slot in _slots) slot.Swap(toNewRoot: true);
            if (!_hasPublishedStableIds
                && _replacementNextStableId is long replacementNextStableId)
            {
                project.AdvanceNextStableId(replacementNextStableId);
                _hasPublishedStableIds = true;
            }
            _isApplied = true;
            _hasPublishedOnce = true;
        }

        public void Undo(MidoraProject project)
        {
            if (!_isApplied)
            {
                if (_rollbackAfterRejectedApply)
                {
                    _rollbackAfterRejectedApply = false;
                    return;
                }
                throw new InvalidOperationException("The timeline owner root replacement is not applied.");
            }
            ValidateProject(project);
            foreach (FrozenRootSlot slot in _slots)
            {
                slot.Validate(
                    project,
                    expectNewRoot: true,
                    validateFrozenSource: false);
            }
            foreach (FrozenRootSlot slot in _slots) slot.Swap(toNewRoot: false);
            _isApplied = false;
        }

        private void ValidateProject(MidoraProject project)
        {
            if (!ReferenceEquals(project, _project))
                throw new InvalidOperationException("The owner-root replacement belongs to another Project.");
        }
    }
}
