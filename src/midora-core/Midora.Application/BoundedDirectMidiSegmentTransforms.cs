using System.Diagnostics;
using Midora.Domain;
using Midora.Compiler;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static bool RequiresBoundedMidiContent(IEnumerable<MidiSegmentSelection> segments)
    {
        long count = 0;
        foreach (var entry in segments)
        {
            // A few opaque records can still carry many MiB. Record count is
            // not a safe bound for the old per-record payload snapshot path.
            if (entry.Segment.OpaqueEvents.Count != 0) return true;
            count += (long)entry.Segment.Notes.Count + entry.Segment.ChannelEvents.Count + entry.Segment.OpaqueEvents.Count;
            if (count > 4096) return true;
        }
        return false;
    }

    private static IPreparedProjectEdit PrepareBoundedMidiSegmentTransform(MidoraProject project, string name,
        MidiSegmentSelection[] selected, SegmentSelectionTransformScope transformScope,
        MidiSegmentContentTransformKind kind, double factor, int semitones,
        BatchEditExpressionProgram? program = null)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        long left = selected.Min(static entry => entry.Segment.ProjectStartTick);
        long right = selected.Max(static entry => entry.Segment.ProjectRange.EndTick);
        long batchOrigin = long.MaxValue;
        if (program is not null)
        {
            foreach (var entry in selected)
                foreach (var value in entry.Segment.Notes.CreateQuerySnapshot().QueryValues(
                    entry.Segment.ContentOffsetTick, entry.Segment.ContentEndTick))
                {
                    scope.Token.ThrowIfCancellationRequested();
                    batchOrigin = Math.Min(batchOrigin, value.StartTick);
                }
            if (batchOrigin == long.MaxValue)
                throw new InvalidOperationException("The selected MIDI Segments expose no Direct MIDI Notes to batch edit.");
        }
        var clock = Stopwatch.StartNew();
        var windows = selected.Select(entry =>
        {
            SegmentWindow old = new(entry.Segment.ProjectStartTick, entry.Segment.LengthTicks, entry.Segment.ContentOffsetTick);
            SegmentWindow next = transformScope != SegmentSelectionTransformScope.ExposedContentAndSegments ? old : kind switch
            {
                MidiSegmentContentTransformKind.FlipHorizontal => old with
                { ProjectStartTick = checked(left + right - checked(old.ProjectStartTick + old.LengthTicks)) },
                MidiSegmentContentTransformKind.Scale => old with
                { ProjectStartTick = ScaleTick(left, old.ProjectStartTick, factor), LengthTicks = ScaleLength(old.LengthTicks, factor) },
                _ => old
            };
            return new MidiSegmentWindowTransform(entry, old, next);
        }).ToArray();
        ValidateMidiSegmentTransformWindows(windows);
        List<Func<MidoraProject, IProjectEditCommand>> commands = [];
        foreach (var window in windows)
        {
            MidoraId segmentId = window.Entry.Segment.Id;
            long contentLeft = window.Entry.Segment.ContentOffsetTick;
            long contentRight = window.Entry.Segment.ContentEndTick;
            commands.Add(_ => Command(name, draft => PrepareBoundedExposedMidiNotes(draft, segmentId,
                contentLeft, contentRight, TransformNote)));
            if (program is null && kind is MidiSegmentContentTransformKind.FlipHorizontal or MidiSegmentContentTransformKind.Scale)
            {
                commands.Add(_ => Command(name, draft => PrepareBoundedExposedMidiEvents(draft, segmentId,
                    contentLeft, contentRight, TransformTick)));
                commands.Add(_ => Command(name, draft => PrepareBoundedExposedOpaque(draft, segmentId,
                    contentLeft, contentRight, TransformTick)));
            }
            DirectMidiNoteValue? TransformNote(DirectMidiNoteValue value)
            {
                DirectMidiNoteValue next;
                if (program is not null)
                {
                    var calculated = program.Evaluate(new(value.NoteOnVelocity, 0, value.Key, value.LengthTicks,
                        value.StartTick, checked(value.StartTick - batchOrigin)), clock, BatchExpressionTimeout);
                    long? tick = RoundTickOrDiscard(calculated.Tick);
                    double key = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
                    if (!tick.HasValue || key is < 0 or > 127) return null;
                    next = value with { StartTick = tick.Value, LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
                        Key = checked((int)key), NoteOnVelocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127)) };
                }
                else next = kind switch
                {
                    MidiSegmentContentTransformKind.FlipHorizontal => value with
                    { StartTick = checked(contentLeft + contentRight - checked(value.StartTick + value.LengthTicks)) },
                    MidiSegmentContentTransformKind.FlipVertical => value with { Key = 127 - value.Key },
                    MidiSegmentContentTransformKind.Scale => value with
                    { StartTick = ScaleTick(contentLeft, value.StartTick, factor), LengthTicks = ScaleLength(value.LengthTicks, factor) },
                    MidiSegmentContentTransformKind.Transpose => value with { Key = checked(value.Key + semitones) },
                    _ => value
                };
                return next.Key is < 0 or > 127 ? null : next;
            }
            long TransformTick(long tick) => kind == MidiSegmentContentTransformKind.FlipHorizontal
                ? checked(contentLeft + checked(contentRight - 1) - tick) : ScaleTick(contentLeft, tick, factor);
        }
        if (windows.Any(static window => window.Old != window.Replacement))
            commands.Add(_ => Command(name, draft =>
            {
                // Resolve after the preceding content-root swaps. Retaining the
                // original Segment reference here would silently edit an old root.
                var current = windows.Select(window => window with
                { Entry = SelectMidiSegments(draft, new[] { window.Entry.Segment.Id })[0] }).ToArray();
                return Prepared(true, PureMidiTrackChange(current.Select(static value => value.Entry.Track.Id).Distinct().ToArray()),
                    _ => { foreach (var value in current) SetMidiSegmentWindow(value.Entry.Segment, value.Replacement); SortTransformedMidiSegments(current); },
                    _ => { foreach (var value in current) SetMidiSegmentWindow(value.Entry.Segment, value.Old); SortTransformedMidiSegments(current); });
            }));
        return new SequentialProjectEditCommand(name, commands).Prepare(project);
    }

    private static IPreparedProjectEdit PrepareBoundedExposedMidiNotes(MidoraProject project, MidoraId segmentId,
        long start, long end, Func<DirectMidiNoteValue, DirectMidiNoteValue?> transform, bool resolveCollisions = true)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
        using var sorted = BoundedEditSort.Sort(Plan(), BoundedDirectMidiNoteSource.OrdinalComparer, scope.Resources, scope.Token);
        using var plan = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(sorted);
        return PrepareBoundedDirectMidiNotes(project, location, source, plan, scope,
            resolveCollisions: resolveCollisions, expectedSourceStamp: stamp);
        IEnumerable<BoundedDirectNoteDelta> Plan()
        {
            foreach (var resolved in source.ResolveValues(source.QueryNotes(start, end), scope.Token))
            {
                scope.Token.ThrowIfCancellationRequested();
                var value = resolved.Value;
                var next = transform(value);
                if (next is { } valid) ValidateDirectMidiNote(valid.StartTick, valid.LengthTicks, valid.Key, valid.NoteOnVelocity, valid.NoteOffVelocity);
                yield return new(resolved.Ordinal, next is null, value, next ?? value);
            }
        }
    }

    private static IPreparedProjectEdit PrepareBoundedExposedMidiEvents(MidoraProject project, MidoraId segmentId,
        long start, long end, Func<long, long> transform, bool allContent = false)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiEventSource.Capture(location.Segment.ChannelEvents);
        using var sorted = BoundedEditSort.Sort(Plan(), BoundedDirectMidiEventSource.OrdinalComparer, scope.Resources, scope.Token);
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(sorted);
        return PublishBoundedDirectMidiEvents(project, location, source, plan, scope,
            allContent ? BoundedEventCollisionMode.None : BoundedEventCollisionMode.Overwrite,
            expectedSourceStamp: stamp);
        IEnumerable<BoundedDirectEventDelta> Plan()
        {
            var values = allContent ? Enumerable.Range(0, source.ChannelEventCount).Select(source.GetChannelEvent)
                : source.QueryChannelEvents(start, end);
            foreach (var resolved in source.ResolveValues(values, scope.Token))
            {
                scope.Token.ThrowIfCancellationRequested();
                var value = resolved.Value;
                var next = value with { Tick = transform(value.Tick) };
                ValidateDirectMidiEvent(next.Tick, next.Kind, next.Data1, next.Data2);
                yield return new(resolved.Ordinal, false, value, next);
            }
        }
    }

    private static IPreparedProjectEdit PrepareBoundedExposedOpaque(MidoraProject project, MidoraId segmentId,
        long start, long end, Func<long, long> transform, bool allContent = false)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedOpaqueMidiSource.Capture(project, location.Segment.OpaqueEvents);
        using var sorted = BoundedEditSort.Sort(Plan(), BoundedDirectMidiEventSource.OrdinalComparer, scope.Resources, scope.Token);
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(sorted);
        return PublishBoundedOpaqueRoot(project, location, source, plan, scope, null, source.Metadata.FormalExtent, null, stamp);
        IEnumerable<BoundedDirectEventDelta> Plan()
        {
            var values = allContent ? Enumerable.Range(0, source.Metadata.ChannelEventCount).Select(source.Metadata.GetChannelEvent)
                : source.Metadata.QueryChannelEvents(start, end);
            foreach (var resolved in source.Metadata.ResolveValues(values, scope.Token))
            {
                scope.Token.ThrowIfCancellationRequested();
                var value = resolved.Value;
                long tick = transform(value.Tick);
                if (tick < 0) throw new InvalidOperationException("An imported MIDI Event cannot precede tick 0.");
                if (tick != value.Tick) yield return new(resolved.Ordinal, false, value, value with { Tick = tick });
            }
        }
    }

    private static IPreparedProjectEdit PrepareBoundedMidiSegmentEdgeShifts(MidoraProject project, MidiSegmentEdgeEdit[] edits)
    {
        const string name = "Adjust MIDI Segment edges";
        List<Func<MidoraProject, IProjectEditCommand>> commands = [];
        foreach (var edit in edits)
        {
            MidoraId id = edit.Selection.Segment.Id;
            long shift = edit.ContentShift;
            if (shift != 0)
            {
                commands.Add(_ => Command(name, draft => PrepareBoundedExposedMidiNotes(draft, id,
                    0, long.MaxValue, value => value with { StartTick = checked(value.StartTick + shift) }, resolveCollisions: false)));
                commands.Add(_ => Command(name, draft => PrepareBoundedExposedMidiEvents(draft, id,
                    0, long.MaxValue, tick => checked(tick + shift), allContent: true)));
                commands.Add(_ => Command(name, draft => PrepareBoundedExposedOpaque(draft, id,
                    0, long.MaxValue, tick => checked(tick + shift), allContent: true)));
            }
            commands.Add(_ => Command(name, draft =>
            {
                var current = FindMidiSegment(draft, id);
                return Prepared(edit.Old != edit.Replacement, PureMidiTrackChange(current.Track.Id),
                    _ => SetMidiSegmentWindow(current.Segment, edit.Replacement),
                    _ => SetMidiSegmentWindow(current.Segment, edit.Old));
            }));
        }
        return new SequentialProjectEditCommand(name, commands).Prepare(project);
    }
}
