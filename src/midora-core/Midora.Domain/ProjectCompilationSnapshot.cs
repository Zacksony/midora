namespace Midora.Domain;

/// <summary>
/// Creates and incrementally synchronizes the source-model mirror owned by a
/// background compiler. Stable IDs are preserved because compiler checkpoints
/// and diagnostics use them as identity.
/// </summary>
internal static partial class ProjectCompilationSnapshot
{
    public static MidoraProject Create(
        MidoraProject source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ProjectMetadataSnapshot metadata = source.Metadata.Snapshot();
        MidoraProject result = new(
            source.TicksPerQuarterNote,
            source.NextStableId,
            metadata.CreatedAtUtc);
        result.Metadata.Restore(metadata);
        CopyConductor(source.Conductor, result.Conductor, cancellationToken);
        CopyState(source.GlobalInitialState, result.GlobalInitialState, cancellationToken);
        CopyState(source.GlobalResetDefaults, result.GlobalResetDefaults, cancellationToken);

        foreach (DamagedProjectObject damaged in source.DamagedEventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedEventInstruments.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedLogicalTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedLogicalTracks.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedEventInstrumentUsages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedEventInstrumentUsages.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedMidiChannelRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedMidiChannelRoots.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedPureMidiTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedPureMidiTracks.Add(damaged);
        }
        result.ArrangementTracks.AddRange(source.ArrangementTracks);
        foreach (EventInstrument instrument in source.EventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.EventInstruments.Add(CloneInstrument(result, instrument, cancellationToken));
        }
        foreach (EventInstrumentUsage usage in source.EventInstrumentUsages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.EventInstrumentUsages.Add(CloneEventInstrumentUsage(result, usage));
        }
        foreach (LogicalTrack track in source.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Tracks.Add(CloneTrack(result, track, cancellationToken));
        }
        foreach (MidiChannelRoot root in source.MidiChannelRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.MidiChannelRoots.Add(CloneMidiChannelRoot(result, root));
        }
        foreach (PureMidiTrack track in source.PureMidiTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.PureMidiTracks.Add(ClonePureMidiTrack(result, track, cancellationToken));
        }

        result.RestoreNextStableId(source.NextStableId);
        return result;
    }

    public static MidoraProject Synchronize(
        MidoraProject snapshot,
        MidoraProject source,
        ProjectChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.AffectsEverything
            || snapshot.TicksPerQuarterNote != source.TicksPerQuarterNote)
        {
            return Create(source, cancellationToken);
        }

        if (changes.AffectsConductor)
        {
            CopyConductor(source.Conductor, snapshot.Conductor, cancellationToken);
        }
        if (changes.EventInstrumentIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.EventInstruments,
                source.EventInstruments,
                changes.EventInstrumentIds,
                value => CloneInstrument(snapshot, value, cancellationToken),
                cancellationToken);
        }
        if (changes.EventInstrumentUsageIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.EventInstrumentUsages,
                source.EventInstrumentUsages,
                changes.EventInstrumentUsageIds,
                value => CloneEventInstrumentUsage(snapshot, value),
                cancellationToken);
        }
        if (changes.TrackIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.Tracks,
                source.Tracks,
                changes.TrackIds,
                value => CloneTrack(snapshot, value, cancellationToken),
                cancellationToken);
        }
        if (changes.MidiChannelRootIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.MidiChannelRoots,
                source.MidiChannelRoots,
                changes.MidiChannelRootIds,
                value => CloneMidiChannelRoot(snapshot, value),
                cancellationToken);
        }
        HashSet<MidoraId> changedPureMidiTrackIds = [.. changes.PureMidiTrackIds];
        if (changes.MidiChannelRootIds.Count != 0)
        {
            changedPureMidiTrackIds.UnionWith(source.PureMidiTracks
                .Where(value => changes.MidiChannelRootIds.Contains(value.MidiChannelRootId))
                .Select(value => value.Id));
            changedPureMidiTrackIds.UnionWith(snapshot.PureMidiTracks
                .Where(value => changes.MidiChannelRootIds.Contains(value.MidiChannelRootId))
                .Select(value => value.Id));
        }
        if (changedPureMidiTrackIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.PureMidiTracks,
                source.PureMidiTracks,
                changedPureMidiTrackIds,
                value => ClonePureMidiTrack(snapshot, value, cancellationToken),
                cancellationToken);
        }

        if (changes.TrackIds.Count != 0
            || changes.EventInstrumentUsageIds.Count != 0
            || changes.MidiChannelRootIds.Count != 0
            || changes.PureMidiTrackIds.Count != 0)
        {
            snapshot.ArrangementTracks.Clear();
            snapshot.ArrangementTracks.AddRange(source.ArrangementTracks);
        }

        snapshot.RestoreNextStableId(source.NextStableId);
        return snapshot;
    }

    private static void SynchronizeByStableId<T>(
        List<T> snapshot,
        IReadOnlyList<T> source,
        IReadOnlySet<MidoraId> changedIds,
        Func<T, T> clone,
        CancellationToken cancellationToken)
        where T : class
    {
        static MidoraId IdOf(T value) => value switch
        {
            EventInstrument instrument => instrument.Id,
            EventInstrumentUsage usage => usage.Id,
            LogicalTrack track => track.Id,
            MidiChannelRoot root => root.Id,
            PureMidiTrack track => track.Id,
            _ => throw new InvalidOperationException("Unsupported compilation snapshot object.")
        };

        Dictionary<MidoraId, T> reusable = new(snapshot.Count);
        foreach (T value in snapshot)
        {
            reusable.TryAdd(IdOf(value), value);
        }
        List<T> synchronized = new(source.Count);
        foreach (T sourceValue in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MidoraId sourceId = IdOf(sourceValue);
            if (changedIds.Contains(sourceId)
                || !reusable.TryGetValue(sourceId, out T? reusableValue))
            {
                synchronized.Add(clone(sourceValue));
                continue;
            }

            synchronized.Add(reusableValue);
            reusable.Remove(sourceId);
        }
        snapshot.Clear();
        snapshot.AddRange(synchronized);
    }

    private static void CopyConductor(
        ConductorTrack source,
        ConductorTrack target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConductorTrack captured = source.CloneFrozen();
        cancellationToken.ThrowIfCancellationRequested();
        target.Tempos = captured.Tempos;
        target.TimeSignatures = captured.TimeSignatures;
        target.KeySignatures = captured.KeySignatures;
        target.Markers = captured.Markers;
        target.EndMarker = captured.EndMarker;
    }

    private static LogicalTrack CloneTrack(
        MidoraProject project,
        LogicalTrack source,
        CancellationToken cancellationToken)
    {
        LogicalTrack result = new(project, source.Id)
        {
            Name = source.Name,
            EventInstrumentUsageId = source.EventInstrumentUsageId,
            LastBoundEventInstrumentName = source.LastBoundEventInstrumentName,
            ColorOverride = source.ColorOverride
        };
        foreach (Segment segment in source.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Segment segmentCopy = new(project, segment.Id)
            {
                ProjectStartTick = segment.ProjectStartTick,
                LengthTicks = segment.LengthTicks,
                ContentOffsetTick = segment.ContentOffsetTick
            };
            segmentCopy.Notes.AdoptSnapshot(project, segment.Notes.CreateQuerySnapshot());
            foreach (LogicalParameterLane lane in segment.ParameterLanes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogicalParameterLane laneCopy = new(project, lane.Id)
                {
                    ParameterId = lane.ParameterId
                };
                laneCopy.Points.AdoptSnapshot(project, lane.Points.CreateQuerySnapshot());
                segmentCopy.ParameterLanes.Add(laneCopy);
            }
            result.Segments.Add(segmentCopy);

        }
        return result;
    }

    private static EventInstrumentUsage CloneEventInstrumentUsage(
        MidoraProject project,
        EventInstrumentUsage source) =>
        new(project, source.Id)
        {
            EventInstrumentId = source.EventInstrumentId
        };

    private static MidiChannelRoot CloneMidiChannelRoot(
        MidoraProject project,
        MidiChannelRoot source)
    {
        return new(project, source.Id)
        {
            Name = source.Name,
            RoutingMode = source.RoutingMode,
            FixedZeroBasedPort = source.FixedZeroBasedPort,
            FixedZeroBasedChannel = source.FixedZeroBasedChannel,
            ChannelMode = source.ChannelMode
        };
    }

    private static PureMidiTrack ClonePureMidiTrack(
        MidoraProject project,
        PureMidiTrack source,
        CancellationToken cancellationToken)
    {
        PureMidiTrack result = new(project, source.Id)
        {
            Name = source.Name,
            MidiChannelRootId = source.MidiChannelRootId,
            Color = source.Color
        };
        foreach (MidiSegment segment in source.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MidiSegment segmentCopy = new(project, segment.Id)
            {
                ProjectStartTick = segment.ProjectStartTick,
                LengthTicks = segment.LengthTicks,
                ContentOffsetTick = segment.ContentOffsetTick
            };
            segment.CloneContentTo(segmentCopy, cancellationToken);
            result.Segments.Add(segmentCopy);
        }
        return result;
    }

    internal static EventInstrument CloneInstrument(
        MidoraProject project,
        EventInstrument source,
        CancellationToken cancellationToken)
    {
        EventInstrument result = new(project, source.Id)
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
        CopyState(source.InitialState, result.InitialState, cancellationToken);

        foreach (LogicalParameterDefinition definition in source.LogicalParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterDefinition definitionCopy = new(project, definition.Id)
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
            for (int index = 0; index < definition.EnumItems.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                LogicalParameterEnumItem item = definition.EnumItems[index];
                definitionCopy.EnumItems.Add(new LogicalParameterEnumItem(project, item.Id)
                {
                    Name = item.Name,
                    Value = item.Value
                });
            }
            result.LogicalParameters.Add(definitionCopy);
        }
        foreach (CSharpMappingFunction function in source.MappingFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CSharpMappingFunction functionCopy = new(project, function.Id)
            {
                Name = function.Name,
                Body = function.Body,
                AbiVersion = function.AbiVersion
            };
            functionCopy.DeclaredContextFields.UnionWith(function.DeclaredContextFields);
            result.MappingFunctions.Add(functionCopy);
        }
        foreach (InstrumentEnvelope envelope in source.Envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Envelopes.Add(new InstrumentEnvelope(project, envelope.Id)
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
        foreach (SubVoice voice in source.SubVoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.SubVoices.Add(CloneSubVoice(project, voice, cancellationToken));
        }
        foreach (LogicalParameterMapping mapping in source.ParameterMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterMapping mappingCopy = new(
                project,
                mapping.Id,
                mapping.Steps.Id)
            {
                ParameterId = mapping.ParameterId,
                SubVoiceId = mapping.SubVoiceId,
                Target = mapping.Target
            };
            CopyTargetSettings(mapping.TargetSettings, mappingCopy.TargetSettings);
            CopyChain(project, mapping.Steps, mappingCopy.Steps, cancellationToken);
            result.ParameterMappings.Add(mappingCopy);
        }
        return result;
    }

    private static SubVoice CloneSubVoice(
        MidoraProject project,
        SubVoice source,
        CancellationToken cancellationToken)
    {
        SubVoice result = new(project, source.Id)
        {
            Name = source.Name,
            RootNoteOverride = source.RootNoteOverride
        };
        CopyState(source.InitialState, result.InitialState, cancellationToken);
        foreach (SubVoiceEventMapping mapping in source.EventMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubVoiceEventMapping mappingCopy = new(
                project,
                mapping.Target,
                mapping.Steps.Id);
            CopyTargetSettings(mapping.TargetSettings, mappingCopy.TargetSettings);
            CopyChain(project, mapping.Steps, mappingCopy.Steps, cancellationToken);
            result.EventMappings.Add(mappingCopy);
        }
        result.Events.AdoptSnapshot(project, source.Events.CreateQuerySnapshot());
        result.InstrumentChanges = source.InstrumentChanges;
        foreach (ValueCurve curve in source.Curves)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        // Sequential page enumeration avoids IList[index]'s page-directory scan.
        int index = 0;
        foreach (CurvePoint point in source)
        {
            if ((index++ & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
            yield return ClonePoint(project, point);
        }
    }

    private static CurvePoint ClonePoint(MidoraProject project, CurvePoint source) =>
        new(project, source.Id, source.Tick, source.Value, source.Interpolation);

    private static void CopyChain(
        MidoraProject project,
        MappingChain source,
        MappingChain target,
        CancellationToken cancellationToken)
    {
        target.IsEnabled = source.IsEnabled;
        target.Clear();
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
        target.Controllers.Clear();
        target.RegisteredParameters.Clear();
        target.NonRegisteredParameters.Clear();
        foreach ((int key, int value) in source.Controllers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Controllers.Add(key, value);
        }
        foreach ((int key, int value) in source.RegisteredParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.RegisteredParameters.Add(key, value);
        }
        foreach ((int key, int value) in source.NonRegisteredParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.NonRegisteredParameters.Add(key, value);
        }
    }
}
