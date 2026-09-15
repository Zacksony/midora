namespace Midora.Domain;

/// <summary>
/// Two-phase source capture for background compilation. The capture phase runs
/// while source mutation is excluded, but it only freezes scalar structure and
/// immutable page snapshots. Million-item materialization runs after the source
/// gate has been released.
/// </summary>
internal static partial class ProjectCompilationSnapshot
{
    internal sealed class RevisionCapture
    {
        public required MidoraProject TargetProject { get; init; }
        public required bool ReplacesProject { get; init; }
        public required ProjectMetadataSnapshot Metadata { get; init; }
        public required ConductorCapture? Conductor { get; init; }
        public required MidiInitialState? GlobalInitialState { get; init; }
        public required MidiInitialState? GlobalResetDefaults { get; init; }
        public required DamagedProjectObject[]? DamagedEventInstruments { get; init; }
        public required DamagedProjectObject[]? DamagedLogicalTracks { get; init; }
        public required DamagedProjectObject[]? DamagedEventInstrumentUsages { get; init; }
        public required DamagedProjectObject[]? DamagedMidiChannelRoots { get; init; }
        public required DamagedProjectObject[]? DamagedPureMidiTracks { get; init; }
        public required ArrangementTrackReference[]? ArrangementTracks { get; init; }
        public required CapturedSequence<CapturedInstrument> EventInstruments { get; init; }
        public required CapturedSequence<EventInstrumentUsage> EventInstrumentUsages { get; init; }
        public required CapturedSequence<CapturedLogicalTrack> LogicalTracks { get; init; }
        public required CapturedSequence<MidiChannelRoot> MidiChannelRoots { get; init; }
        public required CapturedSequence<CapturedPureMidiTrack> PureMidiTracks { get; init; }
        public required long NextStableId { get; init; }
    }

    internal sealed record CapturedSequence<T>(
        MidoraId[] Order,
        IReadOnlyDictionary<MidoraId, T> Replacements);

    internal sealed record CapturedLogicalTrack(
        LogicalTrack Shell,
        CapturedLogicalSegment[] Segments);

    internal sealed record CapturedLogicalSegment(
        Segment Shell,
        LogicalNoteQuerySnapshot Notes,
        CapturedLogicalParameterLane[] ParameterLanes);

    internal sealed record CapturedLogicalParameterLane(
        LogicalParameterLane Shell,
        CurvePointQuerySnapshot Points);

    internal sealed record CapturedPureMidiTrack(
        PureMidiTrack Shell,
        CapturedMidiSegment[] Segments);

    internal sealed record CapturedMidiSegment(
        MidiSegment Shell,
        DirectMidiNoteFormalSequenceSnapshot Notes,
        DirectMidiChannelEventFormalSequenceSnapshot ChannelEvents,
        OpaqueMidiEventFormalSequenceSnapshot OpaqueEvents);

    internal sealed record CapturedInstrument(
        EventInstrument Shell,
        CapturedSubVoice[] SubVoices);

    internal sealed record CapturedSubVoice(
        SubVoice Shell,
        TemplateEventQuerySnapshot Events,
        CapturedValueCurve[] Curves);

    internal sealed record CapturedValueCurve(
        ValueCurve Shell,
        CurvePointQuerySnapshot Points);

    internal sealed record ConductorCapture(
        ConductorQuerySnapshot<TempoChange> Tempos,
        ConductorQuerySnapshot<TimeSignatureChange> TimeSignatures,
        ConductorQuerySnapshot<KeySignatureChange> KeySignatures,
        ConductorQuerySnapshot<ProjectMarker> Markers,
        ProjectEndMarker? EndMarker);

    internal static RevisionCapture CaptureRevision(
        MidoraProject snapshot,
        MidoraProject source,
        ProjectChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changes);

        bool replace = changes.AffectsEverything
            || snapshot.TicksPerQuarterNote != source.TicksPerQuarterNote;
        ProjectMetadataSnapshot metadata = source.Metadata.Snapshot();
        MidoraProject target = replace
            ? new MidoraProject(
                source.TicksPerQuarterNote,
                source.NextStableId,
                metadata.CreatedAtUtc)
            : snapshot;

        IReadOnlySet<MidoraId> instrumentIds = replace
            ? source.EventInstruments.Select(static value => value.Id).ToHashSet()
            : changes.EventInstrumentIds;
        IReadOnlySet<MidoraId> usageIds = replace
            ? source.EventInstrumentUsages.Select(static value => value.Id).ToHashSet()
            : changes.EventInstrumentUsageIds;
        IReadOnlySet<MidoraId> logicalTrackIds = replace
            ? source.Tracks.Select(static value => value.Id).ToHashSet()
            : changes.TrackIds;
        IReadOnlySet<MidoraId> rootIds = replace
            ? source.MidiChannelRoots.Select(static value => value.Id).ToHashSet()
            : changes.MidiChannelRootIds;
        HashSet<MidoraId> pureTrackIds = replace
            ? source.PureMidiTracks.Select(static value => value.Id).ToHashSet()
            : [.. changes.PureMidiTrackIds];
        if (!replace && changes.MidiChannelRootIds.Count != 0)
        {
            pureTrackIds.UnionWith(source.PureMidiTracks
                .Where(value => changes.MidiChannelRootIds.Contains(value.MidiChannelRootId))
                .Select(static value => value.Id));
            pureTrackIds.UnionWith(snapshot.PureMidiTracks
                .Where(value => changes.MidiChannelRootIds.Contains(value.MidiChannelRootId))
                .Select(static value => value.Id));
        }

        bool captureArrangement = replace
            || changes.TrackIds.Count != 0
            || changes.EventInstrumentUsageIds.Count != 0
            || changes.MidiChannelRootIds.Count != 0
            || changes.PureMidiTrackIds.Count != 0;
        return new RevisionCapture
        {
            TargetProject = target,
            ReplacesProject = replace,
            Metadata = metadata,
            Conductor = replace || changes.AffectsConductor
                ? CaptureConductor(source.Conductor, cancellationToken)
                : null,
            GlobalInitialState = replace ? source.GlobalInitialState.Clone() : null,
            GlobalResetDefaults = replace ? source.GlobalResetDefaults.Clone() : null,
            DamagedEventInstruments = replace ? [.. source.DamagedEventInstruments] : null,
            DamagedLogicalTracks = replace ? [.. source.DamagedLogicalTracks] : null,
            DamagedEventInstrumentUsages = replace ? [.. source.DamagedEventInstrumentUsages] : null,
            DamagedMidiChannelRoots = replace ? [.. source.DamagedMidiChannelRoots] : null,
            DamagedPureMidiTracks = replace ? [.. source.DamagedPureMidiTracks] : null,
            ArrangementTracks = captureArrangement ? [.. source.ArrangementTracks] : null,
            EventInstruments = CaptureSequence(
                source.EventInstruments,
                replace ? [] : snapshot.EventInstruments,
                instrumentIds,
                static value => value.Id,
                value => CaptureInstrumentShell(target, value, cancellationToken),
                cancellationToken),
            EventInstrumentUsages = CaptureSequence(
                source.EventInstrumentUsages,
                replace ? [] : snapshot.EventInstrumentUsages,
                usageIds,
                static value => value.Id,
                value => CloneEventInstrumentUsage(target, value),
                cancellationToken),
            LogicalTracks = CaptureSequence(
                source.Tracks,
                replace ? [] : snapshot.Tracks,
                logicalTrackIds,
                static value => value.Id,
                value => CaptureLogicalTrackShell(target, value, cancellationToken),
                cancellationToken),
            MidiChannelRoots = CaptureSequence(
                source.MidiChannelRoots,
                replace ? [] : snapshot.MidiChannelRoots,
                rootIds,
                static value => value.Id,
                value => CloneMidiChannelRoot(target, value),
                cancellationToken),
            // Content Pack pages and formal overlay sequence roots are immutable.
            // Capturing a changed Pure MIDI Track therefore scales with Segment
            // structure, not with imported or edited record count. Object
            // materialization happens after the source gate has been released.
            PureMidiTracks = CaptureSequence(
                source.PureMidiTracks,
                replace ? [] : snapshot.PureMidiTracks,
                pureTrackIds,
                static value => value.Id,
                value => CapturePureMidiTrackShell(target, value, cancellationToken),
                cancellationToken),
            NextStableId = source.NextStableId
        };
    }

    internal static MidoraProject MaterializeRevision(
        RevisionCapture capture,
        CancellationToken cancellationToken = default,
        Action? beforeCommitForTests = null,
        Action? afterFirstCommitMutationForTests = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        MidoraProject target = capture.TargetProject;
        cancellationToken.ThrowIfCancellationRequested();
        // Build every changed branch without mutating the current compiler mirror.
        // A superseding edit may cancel at any point in million-item page
        // materialization; only a fully constructed revision is committed below.
        List<EventInstrument> eventInstruments = BuildMaterializedSequence(
            target.EventInstruments,
            capture.EventInstruments,
            value => MaterializeInstrument(target, value, cancellationToken),
            static value => value.Id,
            cancellationToken);
        List<EventInstrumentUsage> eventInstrumentUsages = BuildMaterializedSequence(
            target.EventInstrumentUsages,
            capture.EventInstrumentUsages,
            static value => value,
            static value => value.Id,
            cancellationToken);
        List<LogicalTrack> logicalTracks = BuildMaterializedSequence(
            target.Tracks,
            capture.LogicalTracks,
            value => MaterializeLogicalTrack(target, value, cancellationToken),
            static value => value.Id,
            cancellationToken);
        List<MidiChannelRoot> midiChannelRoots = BuildMaterializedSequence(
            target.MidiChannelRoots,
            capture.MidiChannelRoots,
            static value => value,
            static value => value.Id,
            cancellationToken);
        List<PureMidiTrack> pureMidiTracks = BuildMaterializedSequence(
            target.PureMidiTracks,
            capture.PureMidiTracks,
            value => MaterializePureMidiTrack(target, value, cancellationToken),
            static value => value.Id,
            cancellationToken);

        beforeCommitForTests?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        // From this point onward the commit is intentionally non-cancelable and
        // bounded by Project structure rather than note/event object count.
        if (capture.ReplacesProject)
        {
            target.Metadata.Restore(capture.Metadata);
            ApplyState(capture.GlobalInitialState!, target.GlobalInitialState, CancellationToken.None);
            ApplyState(capture.GlobalResetDefaults!, target.GlobalResetDefaults, CancellationToken.None);
            ReplaceList(target.DamagedEventInstruments, capture.DamagedEventInstruments!);
            ReplaceList(target.DamagedLogicalTracks, capture.DamagedLogicalTracks!);
            ReplaceList(
                target.DamagedEventInstrumentUsages,
                capture.DamagedEventInstrumentUsages!);
            ReplaceList(target.DamagedMidiChannelRoots, capture.DamagedMidiChannelRoots!);
            ReplaceList(target.DamagedPureMidiTracks, capture.DamagedPureMidiTracks!);
        }
        if (capture.Conductor is not null)
        {
            MaterializeConductor(capture.Conductor, target.Conductor, CancellationToken.None);
        }

        ReplaceList(target.EventInstruments, eventInstruments);
        // Fault injection deliberately sits after the first mutation so session
        // recovery tests exercise a genuinely partial multi-list commit. A
        // production exception from any commit step is handled identically: the
        // mirror is poisoned and the next attempt must build Everything into a
        // fresh Project root rather than reusing this instance.
        afterFirstCommitMutationForTests?.Invoke();
        ReplaceList(target.EventInstrumentUsages, eventInstrumentUsages);
        ReplaceList(target.Tracks, logicalTracks);
        ReplaceList(target.MidiChannelRoots, midiChannelRoots);
        ReplaceList(target.PureMidiTracks, pureMidiTracks);
        if (capture.ArrangementTracks is not null)
        {
            ReplaceList(target.ArrangementTracks, capture.ArrangementTracks);
        }
        target.RestoreNextStableId(capture.NextStableId);
        return target;
    }

    private static CapturedSequence<TCapture> CaptureSequence<TSource, TCapture>(
        IReadOnlyList<TSource> source,
        IReadOnlyList<TSource> snapshot,
        IReadOnlySet<MidoraId> changedIds,
        Func<TSource, MidoraId> getId,
        Func<TSource, TCapture> capture,
        CancellationToken cancellationToken)
        where TSource : class
    {
        HashSet<MidoraId> reusableIds = new(snapshot.Count);
        foreach (TSource value in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reusableIds.Add(getId(value));
        }
        MidoraId[] order = new MidoraId[source.Count];
        Dictionary<MidoraId, TCapture> replacements = [];
        for (int index = 0; index < source.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TSource value = source[index];
            MidoraId id = getId(value);
            order[index] = id;
            if (changedIds.Contains(id) || !reusableIds.Contains(id))
            {
                replacements[id] = capture(value);
            }
        }
        return new(order, replacements);
    }

    private static List<TTarget> BuildMaterializedSequence<TTarget, TCapture>(
        IReadOnlyList<TTarget> target,
        CapturedSequence<TCapture> capture,
        Func<TCapture, TTarget> materialize,
        Func<TTarget, MidoraId> getId,
        CancellationToken cancellationToken)
        where TTarget : class
    {
        Dictionary<MidoraId, TTarget> reusable = new(target.Count);
        foreach (TTarget value in target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reusable.TryAdd(getId(value), value);
        }
        List<TTarget> synchronized = new(capture.Order.Length);
        foreach (MidoraId id in capture.Order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.Replacements.TryGetValue(id, out TCapture? replacement))
            {
                synchronized.Add(materialize(replacement));
            }
            else if (reusable.TryGetValue(id, out TTarget? value))
            {
                synchronized.Add(value);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Compilation revision capture is missing Stable ID {id.Value}.");
            }
        }
        return synchronized;
    }

    private static void ReplaceList<T>(List<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        target.AddRange(values);
    }

    private static CapturedPureMidiTrack CapturePureMidiTrackShell(
        MidoraProject target,
        PureMidiTrack source,
        CancellationToken cancellationToken)
    {
        PureMidiTrack shell = new(target, source.Id)
        {
            Name = source.Name,
            MidiChannelRootId = source.MidiChannelRootId,
            Color = source.Color
        };
        CapturedMidiSegment[] segments = new CapturedMidiSegment[source.Segments.Count];
        for (int segmentIndex = 0; segmentIndex < source.Segments.Count; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MidiSegment sourceSegment = source.Segments[segmentIndex];
            MidiSegment segmentShell = new(target, sourceSegment.Id)
            {
                ProjectStartTick = sourceSegment.ProjectStartTick,
                LengthTicks = sourceSegment.LengthTicks,
                ContentOffsetTick = sourceSegment.ContentOffsetTick
            };
            shell.Segments.Add(segmentShell);
            segments[segmentIndex] = new(
                segmentShell,
                sourceSegment.Notes.CreateFormalSequenceSnapshot(),
                sourceSegment.ChannelEvents.CreateFormalSequenceSnapshot(),
                sourceSegment.OpaqueEvents.CreateFormalSequenceSnapshot());
        }
        return new(shell, segments);
    }

    private static PureMidiTrack MaterializePureMidiTrack(
        MidoraProject target,
        CapturedPureMidiTrack capture,
        CancellationToken cancellationToken)
    {
        foreach (CapturedMidiSegment segment in capture.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A sparse edit can replace one collection without rewriting its
            // siblings. Each formal collection carries its own immutable source.
            segment.Shell.Notes.RestoreFormalSequenceForCompilation(segment.Notes);
            segment.Shell.ChannelEvents.RestoreFormalSequenceForCompilation(
                segment.ChannelEvents);
            segment.Shell.OpaqueEvents.RestoreFormalSequenceForCompilation(
                segment.OpaqueEvents);
        }
        return capture.Shell;
    }

    private static CapturedLogicalTrack CaptureLogicalTrackShell(
        MidoraProject target,
        LogicalTrack source,
        CancellationToken cancellationToken)
    {
        LogicalTrack shell = new(target, source.Id)
        {
            Name = source.Name,
            EventInstrumentUsageId = source.EventInstrumentUsageId,
            LastBoundEventInstrumentName = source.LastBoundEventInstrumentName,
            ColorOverride = source.ColorOverride
        };
        CapturedLogicalSegment[] segments = new CapturedLogicalSegment[source.Segments.Count];
        for (int segmentIndex = 0; segmentIndex < source.Segments.Count; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Segment sourceSegment = source.Segments[segmentIndex];
            Segment segmentShell = new(target, sourceSegment.Id)
            {
                ProjectStartTick = sourceSegment.ProjectStartTick,
                LengthTicks = sourceSegment.LengthTicks,
                ContentOffsetTick = sourceSegment.ContentOffsetTick
            };
            CapturedLogicalParameterLane[] lanes =
                new CapturedLogicalParameterLane[sourceSegment.ParameterLanes.Count];
            for (int laneIndex = 0; laneIndex < sourceSegment.ParameterLanes.Count; laneIndex++)
            {
                LogicalParameterLane sourceLane = sourceSegment.ParameterLanes[laneIndex];
                LogicalParameterLane laneShell = new(target, sourceLane.Id)
                {
                    ParameterId = sourceLane.ParameterId
                };
                segmentShell.ParameterLanes.Add(laneShell);
                lanes[laneIndex] = new(
                    laneShell,
                    sourceLane.Points.CreateQuerySnapshot());
            }
            shell.Segments.Add(segmentShell);
            segments[segmentIndex] = new(
                segmentShell,
                sourceSegment.Notes.CreateQuerySnapshot(),
                lanes);
        }
        return new(shell, segments);
    }

    private static LogicalTrack MaterializeLogicalTrack(
        MidoraProject target,
        CapturedLogicalTrack capture,
        CancellationToken cancellationToken)
    {
        foreach (CapturedLogicalSegment segment in capture.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segment.Shell.Notes.AdoptSnapshot(target, segment.Notes);
            foreach (CapturedLogicalParameterLane lane in segment.ParameterLanes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lane.Shell.Points.AdoptSnapshot(target, lane.Points);
            }
        }
        return capture.Shell;
    }

    private static CapturedInstrument CaptureInstrumentShell(
        MidoraProject target,
        EventInstrument source,
        CancellationToken cancellationToken)
    {
        EventInstrument shell = new(target, source.Id)
        {
            Name = source.Name,
            Description = source.Description,
            Color = source.Color,
            RootNote = source.RootNote,
            TemplateLengthTicks = source.TemplateLengthTicks,
            PreRollTicks = source.PreRollTicks,
            RequiresChannelIsolation = source.RequiresChannelIsolation,
            OverlapPolicy = source.OverlapPolicy,
            OverlapScope = source.OverlapScope,
            ShortLifecycle = source.ShortLifecycle,
            LongLifecycle = source.LongLifecycle,
            LoopStartTick = source.LoopStartTick,
            LoopEndTick = source.LoopEndTick
        };
        CopyState(source.InitialState, shell.InitialState, cancellationToken);

        foreach (LogicalParameterDefinition definition in source.LogicalParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterDefinition definitionShell = new(target, definition.Id)
            {
                Name = definition.Name,
                Type = definition.Type,
                Minimum = definition.Minimum,
                Maximum = definition.Maximum,
                DisplayMinimum = definition.DisplayMinimum,
                DisplayMaximum = definition.DisplayMaximum,
                DefaultValue = definition.DefaultValue,
                UsesExplicitEnumValues = definition.UsesExplicitEnumValues
            };
            foreach (LogicalParameterEnumItem item in definition.EnumItems)
            {
                definitionShell.EnumItems.Add(new LogicalParameterEnumItem(target, item.Id)
                {
                    Name = item.Name,
                    Value = item.Value
                });
            }
            shell.LogicalParameters.Add(definitionShell);
        }
        foreach (CSharpMappingFunction function in source.MappingFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CSharpMappingFunction functionShell = new(target, function.Id)
            {
                Name = function.Name,
                Body = function.Body,
                AbiVersion = function.AbiVersion
            };
            functionShell.DeclaredContextFields.UnionWith(function.DeclaredContextFields);
            shell.MappingFunctions.Add(functionShell);
        }
        foreach (InstrumentEnvelope envelope in source.Envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shell.Envelopes.Add(new InstrumentEnvelope(target, envelope.Id)
            {
                Name = envelope.Name,
                DelayTicks = envelope.DelayTicks,
                AttackTicks = envelope.AttackTicks,
                HoldTicks = envelope.HoldTicks,
                DecayTicks = envelope.DecayTicks,
                StartValue = envelope.StartValue,
                PeakValue = envelope.PeakValue,
                SustainValue = envelope.SustainValue,
                ReleaseTicks = envelope.ReleaseTicks,
                EndValue = envelope.EndValue
            });
        }

        CapturedSubVoice[] voices = new CapturedSubVoice[source.SubVoices.Count];
        for (int voiceIndex = 0; voiceIndex < source.SubVoices.Count; voiceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedSubVoice voice = CaptureSubVoiceShell(
                target,
                source.SubVoices[voiceIndex],
                cancellationToken);
            shell.SubVoices.Add(voice.Shell);
            voices[voiceIndex] = voice;
        }
        foreach (LogicalParameterMapping mapping in source.ParameterMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterMapping mappingShell = new(
                target,
                mapping.Id,
                mapping.Steps.Id)
            {
                ParameterId = mapping.ParameterId,
                SubVoiceId = mapping.SubVoiceId,
                Target = mapping.Target
            };
            CopyTargetSettings(mapping.TargetSettings, mappingShell.TargetSettings);
            CopyChain(target, mapping.Steps, mappingShell.Steps, cancellationToken);
            shell.ParameterMappings.Add(mappingShell);
        }
        return new(shell, voices);
    }

    private static CapturedSubVoice CaptureSubVoiceShell(
        MidoraProject target,
        SubVoice source,
        CancellationToken cancellationToken)
    {
        SubVoice shell = new(target, source.Id)
        {
            Name = source.Name,
            RootNoteOverride = source.RootNoteOverride
        };
        CopyState(source.InitialState, shell.InitialState, cancellationToken);
        foreach (SubVoiceEventMapping mapping in source.EventMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubVoiceEventMapping mappingShell = new(
                target,
                mapping.Target,
                mapping.Steps.Id);
            CopyTargetSettings(mapping.TargetSettings, mappingShell.TargetSettings);
            CopyChain(target, mapping.Steps, mappingShell.Steps, cancellationToken);
            shell.EventMappings.Add(mappingShell);
        }
        CapturedValueCurve[] curves = new CapturedValueCurve[source.Curves.Count];
        for (int curveIndex = 0; curveIndex < source.Curves.Count; curveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueCurve sourceCurve = source.Curves[curveIndex];
            ValueCurve curveShell = new(target, sourceCurve.Id)
            {
                Target = sourceCurve.Target
            };
            CopyTargetSettings(sourceCurve.TargetSettings, curveShell.TargetSettings);
            shell.Curves.Add(curveShell);
            curves[curveIndex] = new(
                curveShell,
                sourceCurve.Points.CreateQuerySnapshot());
        }
        return new(shell, source.Events.CreateQuerySnapshot(), curves);
    }

    private static EventInstrument MaterializeInstrument(
        MidoraProject target,
        CapturedInstrument capture,
        CancellationToken cancellationToken)
    {
        foreach (CapturedSubVoice voice in capture.SubVoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            voice.Shell.Events.AdoptSnapshot(target, voice.Events);
            foreach (CapturedValueCurve curve in voice.Curves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                curve.Shell.Points.AdoptSnapshot(target, curve.Points);
            }
        }
        return capture.Shell;
    }

    private static ConductorCapture CaptureConductor(
        ConductorTrack source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConductorQuerySnapshot<TempoChange> tempos = source.Tempos.CaptureQuerySnapshot();
        ConductorQuerySnapshot<TimeSignatureChange> timeSignatures = source.TimeSignatures.CaptureQuerySnapshot();
        ConductorQuerySnapshot<KeySignatureChange> keySignatures = source.KeySignatures.CaptureQuerySnapshot();
        ConductorQuerySnapshot<ProjectMarker> markers = source.Markers.CaptureQuerySnapshot();
        ProjectEndMarker? endMarker = source.EndMarker is null
            ? null
            : new ProjectEndMarker(source.EndMarker.Id, source.EndMarker.Tick);
        return new(tempos, timeSignatures, keySignatures, markers, endMarker);
    }

    private static void MaterializeConductor(
        ConductorCapture capture,
        ConductorTrack target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        target.Tempos.AdoptSnapshot(capture.Tempos);
        target.TimeSignatures.AdoptSnapshot(capture.TimeSignatures);
        target.KeySignatures.AdoptSnapshot(capture.KeySignatures);
        target.Markers.AdoptSnapshot(capture.Markers);
        target.EndMarker = capture.EndMarker is { } marker ? new(marker.Id, marker.Tick) : null;
    }

    private static void ApplyState(
        MidiInitialState source,
        MidiInitialState target,
        CancellationToken cancellationToken) =>
        CopyState(source, target, cancellationToken);
}
