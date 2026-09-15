using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private readonly record struct GeneratedNote(int Iteration, long Tick, long Gate, int Key, int Velocity);
    private readonly record struct GeneratedPoint(int Iteration, long Tick, double Value);

    public static IProjectEditCommand GenerateDirectMidiNotes(MidoraId segmentId, NoteGenerationOptions options) =>
        new GeneratorProjectEditCommand("Generate Direct MIDI Notes", (project, token, generation) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateValues();
            using var program = options.Compile();
            var segment = FindMidiSegment(project, segmentId).Segment;
            long order = GetGenerationOrderStart(segment);
            return PrepareBoundedDirectMidiNoteAppend(project, segmentId, firstId => Values(firstId));
            IEnumerable<DirectMidiNoteValue> Values(long nextId)
            {
                foreach (var value in GenerateNotes(options, program, generation))
                {
                    long on = checked(order + checked(value.Iteration * 2L));
                    yield return new(new(checked(nextId++)), value.Tick, value.Gate, value.Key, value.Velocity,
                        0, on, checked(on + 1));
                }
            }
        });

    public static IProjectEditCommand GenerateLogicalNotes(MidoraId segmentId, NoteGenerationOptions options) =>
        new GeneratorProjectEditCommand("Generate Logical Notes", (project, token, generation) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateValues();
            using var program = options.Compile();
            var scope = BulkEditPreparationContext.Current!;
            var location = FindSegment(project, segmentId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            var source = location.Segment.Notes.CreateQuerySnapshot();
            long expectedId = project.NextStableId, nextId = expectedId;
            var appended = BoundedLogicalNotePlanning.Append(Values(), static value => new(value.StartTick, value.Note),
                key => source.EnumerateExactStart(key.Tick, key.Key), scope.Resources, token, static value => value.Id);
            try
            {
                if (appended.Count == 0)
                {
                    appended.Dispose();
                    return PublishBoundedNoteSelection(ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(
                        project, location.Track, location.Segment, TrackChange(location.Track.Id), stamp),
                        [], [], static ids => ids, scope);
                }
                var replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, token);
                replacement.Notes.Clear();
                replacement.Notes.AdoptEditedSnapshot(project, source,
                    EmptyBoundedChanges<LogicalNoteSnapshotValue>(scope), appended, token);
                var changes = TrackChange(location.Track.Id);
                ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(changes, location.Segment, replacement,
                    appended.Select(static value => value.Id));
                var root = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track,
                    location.Segment, replacement, changes, expectedId, nextId, stamp);
                return PublishBoundedNoteSelection(root, [], appended.Select(static value => value.Id), static ids => ids, scope);
            }
            catch { appended.Dispose(); throw; }
            IEnumerable<LogicalNoteSnapshotValue> Values()
            {
                foreach (var value in GenerateNotes(options, program, generation))
                    yield return new(new(checked(nextId++)), value.Tick, value.Gate, value.Key, value.Velocity);
            }
        });

    public static IProjectEditCommand GenerateTemplateNotes(MidoraId instrumentId, MidoraId subVoiceId,
        NoteGenerationOptions options) => new GeneratorProjectEditCommand("Generate SubVoice Notes", (project, token, generation) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateValues();
            using var program = options.Compile();
            var instrument = FindEventInstrument(project, instrumentId);
            var voice = FindSubVoice(instrument, subVoiceId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            long expectedId = project.NextStableId, nextId = expectedId;
            using var plan = BoundedTemplatePointPlan.Append(voice.Events.CreateQuerySnapshot(), Values(),
                () => new(checked(nextId++)), ValidateTemplateEventValue);
            return PublishBoundedTemplatePoints(project, instrument, voice, stamp, plan, expectedId, nextId, static ids => ids);
            IEnumerable<TemplateEventSnapshotValue> Values()
            {
                foreach (var value in GenerateNotes(options, program, generation))
                    yield return new(default, TemplateEventKind.Note, value.Tick, value.Gate,
                        value.Key, value.Velocity, 0, false, false, true);
            }
        });

    public static IProjectEditCommand GenerateLogicalParameterPoints(MidoraId segmentId, MidoraId laneId,
        EventGenerationOptions options) => new GeneratorProjectEditCommand("Generate Logical Parameter Points", (project, token, generation) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateValues();
            using var program = options.Compile();
            var location = FindSegment(project, segmentId);
            var lane = FindLogicalParameterLane(location.Segment, laneId);
            var definition = FindBoundLogicalParameter(project, location.Track, lane.ParameterId);
            var normalize = CreateGeneratedParameterNormalizer(definition);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            long expectedId = project.NextStableId, nextId = expectedId;
            using var plan = BoundedCurvePointPlan.Append(lane.Points.CreateQuerySnapshot(), Values(),
                () => new(checked(nextId++)), point => ValidatePointValue(definition, point.Value, CurveInterpolation.Step));
            return PublishBoundedLogicalPoints(project, location, lane, stamp, plan, expectedId, nextId, static ids => ids, false);
            IEnumerable<CurvePointSnapshotValue> Values()
            {
                foreach (var value in GeneratePoints(options, program, normalize, generation))
                    yield return new(default, value.Tick, value.Value, CurveInterpolation.Step);
            }
        });

    public static IProjectEditCommand GenerateTemplateEventPoints(MidoraId instrumentId, MidoraId subVoiceId,
        MidiValueTarget target, EventGenerationOptions options) =>
        new GeneratorProjectEditCommand("Generate SubVoice Event Points", (project, token, generation) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateValues();
            using var program = options.Compile();
            (double minimum, double maximum) = MidiEventTargetRange(target);
            var instrument = FindEventInstrument(project, instrumentId);
            var voice = FindSubVoice(instrument, subVoiceId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            var source = voice.Events.CreateQuerySnapshot();
            long expectedId = project.NextStableId, nextId = expectedId;
            using var plan = BoundedTemplatePointPlan.Append(source, Values(),
                () => new(checked(nextId++)), ValidateTemplateEventValue);
            return PublishBoundedTemplatePoints(project, instrument, voice, stamp, plan, expectedId, nextId, static ids => ids);
            IEnumerable<TemplateEventSnapshotValue> Values()
            {
                foreach (var value in GeneratePoints(options, program, value => NormalizeGeneratedInteger(value, minimum, maximum), generation))
                {
                    var initial = new TemplateEventSnapshotValue(default, default, value.Tick, 0, 0, 0, 0, false, false, true);
                    // Bank and Pitch Bend Range have two scalar lane components
                    // in one formal event. Generating one lane must preserve the
                    // other component at that exact tick.
                    if (target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb
                        or MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents)
                    {
                        var kind = target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb
                            ? TemplateEventKind.Bank : TemplateEventKind.PitchBendRange;
                        foreach (var old in source.EnumerateEventExactTick(value.Tick))
                            if (old.Kind == kind) initial = old with { Id = default };
                    }
                    yield return AssignBoundedTemplateTarget(initial, target, checked((int)value.Value));
                }
            }
        });

    public static IProjectEditCommand GenerateDirectMidiEventPoints(MidoraId segmentId,
        DirectMidiChannelEventKind kind, int laneData1, EventGenerationOptions options) =>
        new GeneratorProjectEditCommand("Generate Direct MIDI Event Points", (project, token, generation) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateValues();
            if (!Enum.IsDefined(kind) || kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff
                || laneData1 is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(kind));
            using var program = options.Compile();
            long order = GetGenerationOrderStart(FindMidiSegment(project, segmentId).Segment);
            return PrepareBoundedDirectMidiEventAppend(project, segmentId, firstId => Values(firstId));
            IEnumerable<DirectMidiChannelEventValue> Values(long nextId)
            {
                foreach (var point in GeneratePoints(options, program,
                    value => NormalizeGeneratedInteger(value, 0, kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127), generation))
                {
                    int value = checked((int)point.Value);
                    int first = kind switch
                    {
                        DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure => value,
                        DirectMidiChannelEventKind.PitchBend => value & 127,
                        _ => laneData1
                    };
                    int second = kind switch
                    {
                        DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure => 0,
                        DirectMidiChannelEventKind.PitchBend => value >> 7,
                        _ => value
                    };
                    yield return new(new(checked(nextId++)), point.Tick, kind, first, second, checked(order + point.Iteration));
                }
            }
        });

    private static IEnumerable<GeneratedNote> GenerateNotes(NoteGenerationOptions options, GeneratorExpressionProgram program,
        GenerationProgress generation)
    {
        var scope = BulkEditPreparationContext.Current!;
        var previous = Normalize(new(options.InitialVelocity, 0, options.InitialKey, options.InitialGate,
            options.InitialTick, options.InitialTick)).Values;
        int attempted = 0;
        for (int i = 0; i < options.MaximumCandidates; i++)
        {
            if ((i & 255) == 0) scope.Checkpoint(i, options.MaximumCandidates, TimelineEditPreparationPhase.Planning, 0.05, 0.45);
            BatchEditValues raw = i == 0 && options.CreateFirstFromInitialValues ? previous : program.Evaluate(previous, i);
            long relative = NormalizeGeneratedTick(raw.Tick);
            var normalized = Normalize(raw);
            attempted++;
            generation.Candidates = attempted;
            if (options.MaximumRelativeStartTick is long maximum && relative > maximum) break;
            previous = normalized.Values;
            yield return normalized.Note with { Iteration = i };
        }
        scope.Checkpoint(attempted, attempted, TimelineEditPreparationPhase.Planning, 0.05, 0.45);

        (BatchEditValues Values, GeneratedNote Note) Normalize(BatchEditValues raw)
        {
            long relative = NormalizeGeneratedTick(raw.Tick);
            long start = checked(options.BaseTick + relative);
            if (start == long.MaxValue) throw new OverflowException("A generated note must have space for a positive gate.");
            double roundedGate = Math.Round(raw.Gate, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(roundedGate)) throw new InvalidOperationException("The Gate expression returned a non-finite value.");
            long maximumGate = long.MaxValue - start;
            long gate = roundedGate >= maximumGate ? maximumGate : roundedGate <= 1 ? 1 : checked((long)roundedGate);
            int velocity = checked((int)NormalizeGeneratedInteger(raw.Velocity, 1, 127));
            int key = checked((int)NormalizeGeneratedInteger(raw.KeyNumber, 0, 127));
            return (new(velocity, 0, key, gate, relative, relative), new(0, start, gate, key, velocity));
        }
    }

    private static IEnumerable<GeneratedPoint> GeneratePoints(EventGenerationOptions options,
        GeneratorExpressionProgram program, Func<double, double> normalize, GenerationProgress generation)
    {
        var scope = BulkEditPreparationContext.Current!;
        long initialTick = NormalizeGeneratedTick(options.InitialTick);
        var previous = new BatchEditValues(0, normalize(options.InitialValue), 0, 0, initialTick, initialTick);
        int attempted = 0;
        for (int i = 0; i < options.MaximumCandidates; i++)
        {
            if ((i & 255) == 0) scope.Checkpoint(i, options.MaximumCandidates, TimelineEditPreparationPhase.Planning, 0.05, 0.45);
            var raw = i == 0 && options.CreateFirstFromInitialValues ? previous : program.Evaluate(previous, i);
            long relative = NormalizeGeneratedTick(raw.Tick);
            long tick = checked(options.BaseTick + relative);
            if (tick == long.MaxValue) throw new OverflowException("A generated point tick exceeds the supported range.");
            double value = normalize(raw.PointValue);
            attempted++;
            generation.Candidates = attempted;
            if (options.MaximumRelativeStartTick is long maximum && relative > maximum) break;
            previous = new(0, value, 0, 0, relative, relative);
            yield return new(i, tick, value);
        }
        scope.Checkpoint(attempted, attempted, TimelineEditPreparationPhase.Planning, 0.05, 0.45);
    }

    private static long NormalizeGeneratedTick(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("The Tick expression returned a non-finite value.");
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded <= 0 ? 0 : checked((long)rounded);
    }

    private static double NormalizeGeneratedInteger(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("The expression returned a non-finite value.");
        return Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), minimum, maximum);
    }

    private static Func<double, double> CreateGeneratedParameterNormalizer(LogicalParameterDefinition definition)
    {
        double minimum = definition.Minimum, maximum = definition.Maximum;
        LogicalParameterType type = definition.Type;
        double[] choices = type == LogicalParameterType.Enum && definition.UsesExplicitEnumValues
            ? definition.EnumItems.Select(static item => (double)item.Value).Distinct().Order().ToArray() : [];
        return value =>
        {
            if (!double.IsFinite(value)) throw new InvalidOperationException("The Point Value expression returned a non-finite value.");
            double clamped = Math.Clamp(value, minimum, maximum);
            if (type == LogicalParameterType.Double) return clamped;
            if (choices.Length == 0) return Math.Round(clamped, MidpointRounding.AwayFromZero);
            int found = Array.BinarySearch(choices, clamped);
            if (found >= 0) return choices[found];
            int right = ~found;
            if (right == 0) return choices[0];
            if (right == choices.Length) return choices[^1];
            return clamped - choices[right - 1] <= choices[right] - clamped ? choices[right - 1] : choices[right];
        };
    }

    private static long GetGenerationOrderStart(MidiSegment segment)
    {
        var scope = BulkEditPreparationContext.Current!;
        long maximum = -1, read = 0;
        long total = (long)segment.Notes.Count + segment.ChannelEvents.Count + segment.OpaqueEvents.Count;
        var notes = segment.Notes.CreateObjectSource();
        var events = segment.ChannelEvents.CreateObjectSource();
        var opaque = segment.OpaqueEvents.CreateObjectSource();
        for (int i = 0; i < notes.Count; i++)
        {
            if ((read++ & 255) == 0) scope.Checkpoint(read, total, TimelineEditPreparationPhase.Planning, 0, 0.05);
            var note = notes.GetByOrdinal(i);
            maximum = Math.Max(maximum, Math.Max(note.NoteOnOrder, note.NoteOffOrder));
        }
        for (int i = 0; i < events.Count; i++)
        {
            if ((read++ & 255) == 0) scope.Checkpoint(read, total, TimelineEditPreparationPhase.Planning, 0, 0.05);
            maximum = Math.Max(maximum, events.GetByOrdinal(i).Order);
        }
        for (int i = 0; i < opaque.Count; i++)
        {
            if ((read++ & 255) == 0) scope.Checkpoint(read, total, TimelineEditPreparationPhase.Planning, 0, 0.05);
            maximum = Math.Max(maximum, opaque.GetByOrdinal(i).Order);
        }
        scope.Checkpoint(total, total, TimelineEditPreparationPhase.Planning, 0, 0.05);
        return checked(maximum + 1);
    }

    private sealed class GenerationProgress(IProgress<TimelineEditPreparationProgress>? destination)
        : IProgress<TimelineEditPreparationProgress>
    {
        public int Candidates { get; set; }
        public int? Retained { get; set; }
        public void Report(TimelineEditPreparationProgress value)
        {
            if (destination is null) return;
            // The preparation context has already throttled notifications.
            // Candidate evaluation itself only updates an integer counter.
            string retained = Retained is int count ? count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "pending";
            destination.Report(value with
            { Detail = $"Candidates: {Candidates.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}; retained: {retained}" });
        }
    }

    private sealed class GeneratorProjectEditCommand(string name,
        Func<MidoraProject, CancellationToken, GenerationProgress, IPreparedProjectEdit> prepare)
        : IProgressReportingProjectEditCommand
    {
        public string Name => name;
        public IPreparedProjectEdit Prepare(MidoraProject project) => Prepare(project, default, null);
        public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) => Prepare(project, token, null);
        public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token,
            IProgress<TimelineEditPreparationProgress>? progress)
        {
            var generation = new GenerationProgress(progress);
            using var scope = BulkEditPreparationContext.Enter(token, generation, project: project);
            IPreparedProjectEdit result = prepare(project, scope.Token, generation);
            try
            {
                generation.Retained = result is IPreparedTimelineSelectionEdit selection
                    ? selection.PreparedSelection.ResultSelectionIds.Count
                    : throw new InvalidOperationException("A generator must publish its exact retained selection.");
                scope.Checkpoint(1, 1, TimelineEditPreparationPhase.Ready);
                return result;
            }
            catch { (result as IDisposable)?.Dispose(); throw; }
        }
    }
}
