using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand FlipArrangementSegmentsHorizontal(
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope) =>
        ComposeMixedSegmentContentEdit(
            "Flip Arrangement Segments horizontally",
            segmentIds,
            (logical, _) => FlipSegmentsHorizontal(
                logical,
                SegmentSelectionTransformScope.ExposedContentOnly),
            (_, midi) => FlipMidiSegmentsHorizontal(
                midi,
                SegmentSelectionTransformScope.ExposedContentOnly),
            scope == SegmentSelectionTransformScope.ExposedContentAndSegments
                ? project => TransformMixedSegmentWindows(
                    segmentIds,
                    MixedSegmentWindowTransformKind.FlipHorizontal,
                    factor: 1)
                : null);

    public static IProjectEditCommand FlipArrangementSegmentsVertical(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        ComposeMixedSegmentContentEdit(
            "Flip Arrangement Segment Notes vertically",
            segmentIds,
            (logical, _) => FlipSegmentsVertical(logical),
            (_, midi) => FlipMidiSegmentsVertical(midi));

    public static IProjectEditCommand ScaleArrangementSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        double factor,
        SegmentSelectionTransformScope scope) =>
        ComposeMixedSegmentContentEdit(
            "Scale Arrangement Segments",
            segmentIds,
            (logical, _) => ScaleSegments(
                logical,
                factor,
                SegmentSelectionTransformScope.ExposedContentOnly),
            (_, midi) => ScaleMidiSegments(
                midi,
                factor,
                SegmentSelectionTransformScope.ExposedContentOnly),
            scope == SegmentSelectionTransformScope.ExposedContentAndSegments
                ? project => TransformMixedSegmentWindows(
                    segmentIds,
                    MixedSegmentWindowTransformKind.Scale,
                    factor)
                : null);

    public static IProjectEditCommand TransposeArrangementSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        int semitones) =>
        ComposeMixedSegmentContentEdit(
            "Transpose Arrangement Segment Notes",
            segmentIds,
            (logical, _) => TransposeSegments(logical, semitones),
            (_, midi) => TransposeMidiSegments(midi, semitones));

    public static IProjectEditCommand BatchEditArrangementSegmentExposedNotes(
        IReadOnlyCollection<MidoraId> segmentIds,
        BatchEditExpressionProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return ComposeMixedSegmentContentEdit(
            "Batch edit Arrangement Segment Notes",
            segmentIds,
            (logical, _) => BatchEditSegmentExposedNotes(logical, program),
            (_, midi) => BatchEditMidiSegmentExposedNotes(midi, program));
    }

    public static IProjectEditCommand MoveArrangementSegmentsHorizontal(
        IReadOnlyCollection<MidoraId> segmentIds,
        long tickDelta) =>
        TransformMixedSegmentWindows(
            segmentIds,
            MixedSegmentWindowTransformKind.Translate,
            factor: 1,
            tickDelta);

    public static IProjectEditCommand AdjustArrangementSegmentEdges(
        IReadOnlyCollection<MidoraId> segmentIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks = 1) =>
        Command("Adjust Arrangement Segment edges", project =>
        {
            if (minimumLengthTicks < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumLengthTicks));
            MixedArrangementSegmentSelection selection =
                SelectMixedArrangementSegments(project, segmentIds);
            if (startDelta != 0 && endDelta != 0)
            {
                throw new ArgumentException(
                    "Exactly one Segment edge may change.",
                    nameof(startDelta));
            }
            long boundedStartDelta = startDelta < 0
                ? Math.Max(startDelta, -selection.MinimumStartTick)
                : startDelta;
            List<Func<MidoraProject, IProjectEditCommand>> factories = [];
            if (selection.LogicalIds.Length != 0)
            {
                factories.Add(_ => AdjustSegmentEdges(
                    selection.LogicalIds,
                    boundedStartDelta,
                    endDelta,
                    minimumLengthTicks));
            }
            if (selection.MidiIds.Length != 0)
            {
                factories.Add(_ => AdjustMidiSegmentEdges(
                    selection.MidiIds,
                    boundedStartDelta,
                    endDelta,
                    minimumLengthTicks));
            }
            if (factories.Count == 1)
                return factories[0](project).Prepare(project);
            return new SequentialProjectEditCommand(
                "Adjust Arrangement Segment edges",
                factories).Prepare(project);
        });

    private static IProjectEditCommand ComposeMixedSegmentContentEdit(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        Func<MidoraId[], MidoraId[], IProjectEditCommand> logicalFactory,
        Func<MidoraId[], MidoraId[], IProjectEditCommand> midiFactory,
        Func<MidoraProject, IProjectEditCommand>? finalFactory = null) =>
        Command(name, project =>
        {
            MixedArrangementSegmentSelection selection =
                SelectMixedArrangementSegments(project, segmentIds);
            List<Func<MidoraProject, IProjectEditCommand>> factories = [];
            if (selection.LogicalIds.Length != 0)
            {
                factories.Add(_ => logicalFactory(
                    selection.LogicalIds,
                    selection.MidiIds));
            }
            if (selection.MidiIds.Length != 0)
            {
                factories.Add(_ => midiFactory(
                    selection.LogicalIds,
                    selection.MidiIds));
            }
            if (finalFactory is not null) factories.Add(finalFactory);
            if (factories.Count == 1)
                return factories[0](project).Prepare(project);
            return new SequentialProjectEditCommand(name, factories).Prepare(project);
        });

    private static IProjectEditCommand TransformMixedSegmentWindows(
        IReadOnlyCollection<MidoraId> segmentIds,
        MixedSegmentWindowTransformKind kind,
        double factor,
        long tickDelta = 0) =>
        Command(kind switch
        {
            MixedSegmentWindowTransformKind.FlipHorizontal =>
                "Flip Arrangement Segment windows horizontally",
            MixedSegmentWindowTransformKind.Scale =>
                "Scale Arrangement Segment windows",
            MixedSegmentWindowTransformKind.Translate =>
                "Move Arrangement Segments horizontally",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }, project =>
        {
            if (kind == MixedSegmentWindowTransformKind.Scale) ValidateScaleFactor(factor);
            MixedArrangementSegmentSelection selection =
                SelectMixedArrangementSegments(project, segmentIds);
            long left = selection.MinimumStartTick;
            long right = selection.MaximumEndTick;
            List<SegmentWindowTransform> logicalWindows = [];
            List<MidiSegmentWindowTransform> midiWindows = [];

            foreach (SegmentTransformEntry entry in selection.Logical)
            {
                SegmentWindow old = new(
                    entry.Segment.ProjectStartTick,
                    entry.Segment.LengthTicks,
                    entry.Segment.ContentOffsetTick);
                logicalWindows.Add(new(entry, old, TransformWindow(old, left, right, kind, factor, tickDelta)));
            }
            foreach (MidiSegmentSelection entry in selection.Midi)
            {
                SegmentWindow old = new(
                    entry.Segment.ProjectStartTick,
                    entry.Segment.LengthTicks,
                    entry.Segment.ContentOffsetTick);
                midiWindows.Add(new(entry, old, TransformWindow(old, left, right, kind, factor, tickDelta)));
            }

            ValidateSegmentTransformWindows(project, logicalWindows);
            ValidateMidiSegmentTransformWindows(midiWindows);
            ProjectChangeSet changes = new();
            changes.TrackIds.UnionWith(selection.Logical.Select(value => value.Track.Id));
            changes.PureMidiTrackIds.UnionWith(selection.Midi.Select(value => value.Track.Id));
            return Prepared(
                logicalWindows.Any(value => value.Old != value.Replacement)
                    || midiWindows.Any(value => value.Old != value.Replacement),
                changes,
                _ =>
                {
                    foreach (SegmentWindowTransform value in logicalWindows)
                        SetWindow(value.Entry.Segment, value.Replacement);
                    foreach (MidiSegmentWindowTransform value in midiWindows)
                        SetMidiSegmentWindow(value.Entry.Segment, value.Replacement);
                    SortTransformedSegments(logicalWindows);
                    SortTransformedMidiSegments(midiWindows);
                },
                _ =>
                {
                    foreach (SegmentWindowTransform value in logicalWindows)
                        SetWindow(value.Entry.Segment, value.Old);
                    foreach (MidiSegmentWindowTransform value in midiWindows)
                        SetMidiSegmentWindow(value.Entry.Segment, value.Old);
                    SortTransformedSegments(logicalWindows);
                    SortTransformedMidiSegments(midiWindows);
                });
        });

    private static SegmentWindow TransformWindow(
        SegmentWindow value,
        long selectionLeft,
        long selectionRight,
        MixedSegmentWindowTransformKind kind,
        double factor,
        long tickDelta) => kind switch
        {
            MixedSegmentWindowTransformKind.FlipHorizontal => value with
            {
                ProjectStartTick = checked(selectionLeft + selectionRight
                    - checked(value.ProjectStartTick + value.LengthTicks))
            },
            MixedSegmentWindowTransformKind.Scale => value with
            {
                ProjectStartTick = ScaleTick(selectionLeft, value.ProjectStartTick, factor),
                LengthTicks = ScaleLength(value.LengthTicks, factor)
            },
            MixedSegmentWindowTransformKind.Translate => value with
            {
                ProjectStartTick = checked(value.ProjectStartTick + tickDelta)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static MixedArrangementSegmentSelection SelectMixedArrangementSegments(
        MidoraProject project,
        IReadOnlyCollection<MidoraId> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        HashSet<MidoraId> requested = segmentIds.ToHashSet();
        if (requested.Count == 0
            || requested.Count != segmentIds.Count
            || requested.Contains(default))
        {
            throw new ArgumentException(
                "Arrangement Segment IDs must be distinct and valid.",
                nameof(segmentIds));
        }
        List<SegmentTransformEntry> logicalValues = [];
        List<MidiSegmentSelection> midiValues = [];
        foreach (MidoraId id in requested)
        {
            if (ProjectSegmentIndex.FindLogical(project, id) is LogicalSegmentIndexEntry logicalEntry)
            {
                logicalValues.Add(new(logicalEntry.Track, logicalEntry.Segment, logicalEntry.Index));
                continue;
            }
            if (ProjectSegmentIndex.FindMidi(project, id) is MidiSegmentIndexEntry midiEntry)
            {
                midiValues.Add(new(
                    midiEntry.Track,
                    midiEntry.Segment,
                    midiEntry.Index,
                    midiEntry.Segment.ProjectStartTick));
                continue;
            }
            throw new ArgumentOutOfRangeException(nameof(segmentIds));
        }

        Dictionary<LogicalTrack, int> logicalTrackOrder = project.Tracks
            .Select((track, index) => (track, index))
            .ToDictionary(static value => value.track, static value => value.index);
        Dictionary<PureMidiTrack, int> midiTrackOrder = project.PureMidiTracks
            .Select((track, index) => (track, index))
            .ToDictionary(static value => value.track, static value => value.index);
        SegmentTransformEntry[] logical = logicalValues
            .OrderBy(value => logicalTrackOrder[value.Track])
            .ThenBy(value => value.Index)
            .ToArray();
        MidiSegmentSelection[] midi = midiValues
            .OrderBy(value => midiTrackOrder[value.Track])
            .ThenBy(value => value.Index)
            .ToArray();
        long minimum = logical.Select(value => value.Segment.ProjectStartTick)
            .Concat(midi.Select(value => value.Segment.ProjectStartTick))
            .Min();
        long maximum = logical.Select(value => value.Segment.ProjectRange.EndTick)
            .Concat(midi.Select(value => value.Segment.ProjectRange.EndTick))
            .Max();
        return new(
            logical,
            midi,
            logical.Select(value => value.Segment.Id).ToArray(),
            midi.Select(value => value.Segment.Id).ToArray(),
            minimum,
            maximum);
    }

    private enum MixedSegmentWindowTransformKind
    {
        FlipHorizontal,
        Scale,
        Translate
    }

    private sealed record MixedArrangementSegmentSelection(
        SegmentTransformEntry[] Logical,
        MidiSegmentSelection[] Midi,
        MidoraId[] LogicalIds,
        MidoraId[] MidiIds,
        long MinimumStartTick,
        long MaximumEndTick);
}
