using System.Collections;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>
/// Resolves an Arrangement gesture only after the foreground task has opened.
/// The Dispatcher freezes IDs and scalar gesture inputs, never Segment arrays.
/// </summary>
internal sealed class ArrangementGestureEditCommand(
    IReadOnlyCollection<MidoraId> selected,
    MidoraId primaryId,
    long primaryStartTick,
    TimelineItemEditKind kind,
    bool copy,
    long startDelta,
    long endDelta,
    long minimumLength,
    ArrangementLaneDescriptor? target) : IProgressReportingProjectEditCommand
{
    public string Name => kind == TimelineItemEditKind.Move
        ? copy ? "Copy Arrangement Segments" : "Move Arrangement Segments"
        : "Resize Arrangement Segments";
    public bool PreparedCopy { get; private set; }

    public IPreparedProjectEdit Prepare(MidoraProject project) => Prepare(project, default, null);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) => Prepare(project, token, null);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token,
        IProgress<TimelineEditPreparationProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        PreparedCopy = false;
        // Keep the immutable directory references stable throughout preparation.
        var logicalTracks = project.Tracks;
        var midiTracks = project.PureMidiTracks;
        long total = 0;
        foreach (var track in logicalTracks) { token.ThrowIfCancellationRequested(); total += track.Segments.Count; }
        foreach (var track in midiTracks) { token.ThrowIfCancellationRequested(); total += track.Segments.Count; }
        int logicalCount = 0, midiCount = 0;
        long minimumStart = long.MaxValue, visited = 0;
        foreach (var track in logicalTracks)
            foreach (var segment in track.Segments)
            {
                Checkpoint();
                if (!IsSelected(segment.Id)) continue;
                logicalCount++;
                minimumStart = Math.Min(minimumStart, segment.ProjectStartTick);
            }
        foreach (var track in midiTracks)
            foreach (var segment in track.Segments)
            {
                Checkpoint();
                if (!IsSelected(segment.Id)) continue;
                midiCount++;
                minimumStart = Math.Min(minimumStart, segment.ProjectStartTick);
            }
        token.ThrowIfCancellationRequested();
        progress?.Report(new(TimelineEditPreparationPhase.ResolvingSelection, total, total));
        IReadOnlyCollection<MidoraId> resolved = new ResolvedIds(checked(logicalCount + midiCount), EnumerateIds);
        if (resolved.Count == 0)
        {
            if (TimelineWorkspaceViewModel.FindSegment(project, primaryId) is { } logical)
            {
                logicalCount = 1; minimumStart = logical.Segment.ProjectStartTick;
            }
            else if (TimelineWorkspaceViewModel.FindMidiSegment(project, primaryId) is { } midi)
            {
                midiCount = 1; minimumStart = midi.Segment.ProjectStartTick;
            }
            else return new NoChange();
            resolved = new[] { primaryId };
        }

        IProjectEditCommand command;
        if (logicalCount != 0 && midiCount != 0)
        {
            if (kind == TimelineItemEditKind.Move && copy)
                throw new InvalidOperationException(
                    "Copy-drag is unavailable for a mixed Logical/MIDI Segment selection. Use Duplicate, then drag the copy.");
            command = kind == TimelineItemEditKind.Move
                ? ProjectDomainEditCommands.MoveArrangementSegmentsHorizontal(resolved, Math.Max(startDelta, -minimumStart))
                : ProjectDomainEditCommands.AdjustArrangementSegmentEdges(resolved,
                    kind == TimelineItemEditKind.ResizeStart ? startDelta : 0,
                    kind == TimelineItemEditKind.ResizeEnd ? endDelta : 0, minimumLength);
        }
        else if (kind == TimelineItemEditKind.Move)
        {
            ArrangementLaneKind required = logicalCount != 0
                ? ArrangementLaneKind.LogicalTrack : ArrangementLaneKind.PureMidiTrack;
            if (target is not { ObjectId: MidoraId trackId } lane || lane.Kind != required)
                return new NoChange();
            long tick = checked(primaryStartTick + Math.Max(startDelta, -minimumStart));
            command = logicalCount != 0
                ? copy ? ProjectDomainEditCommands.DuplicateSegments(resolved, primaryId, trackId, tick)
                    : ProjectDomainEditCommands.MoveSegments(resolved, primaryId, trackId, tick)
                : copy ? ProjectDomainEditCommands.DuplicateMidiSegments(resolved, primaryId, trackId, tick)
                    : ProjectDomainEditCommands.MoveMidiSegments(resolved, primaryId, trackId, tick);
            PreparedCopy = copy;
        }
        else
        {
            long left = kind == TimelineItemEditKind.ResizeStart ? startDelta : 0;
            long right = kind == TimelineItemEditKind.ResizeEnd ? endDelta : 0;
            command = logicalCount != 0
                ? ProjectDomainEditCommands.AdjustSegmentEdges(resolved, left, right, minimumLength)
                : ProjectDomainEditCommands.AdjustMidiSegmentEdges(resolved, left, right, minimumLength);
        }
        return command switch
        {
            IProgressReportingProjectEditCommand reporting => reporting.Prepare(project, token, progress),
            ICancellableProjectEditCommand cancellable => cancellable.Prepare(project, token),
            _ => command.Prepare(project)
        };

        void Checkpoint()
        {
            token.ThrowIfCancellationRequested();
            if ((++visited & 1023) == 0)
                progress?.Report(new(TimelineEditPreparationPhase.ResolvingSelection, visited, total));
        }
        bool IsSelected(MidoraId id) => selected is IReadOnlySet<MidoraId> set
            ? set.Contains(id) : selected.Contains(id);
        IEnumerable<MidoraId> EnumerateIds()
        {
            foreach (var track in logicalTracks)
                foreach (var segment in track.Segments)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsSelected(segment.Id)) yield return segment.Id;
                }
            foreach (var track in midiTracks)
                foreach (var segment in track.Segments)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsSelected(segment.Id)) yield return segment.Id;
                }
        }
    }

    private sealed class ResolvedIds(int count, Func<IEnumerable<MidoraId>> values) : IReadOnlyCollection<MidoraId>
    {
        public int Count => count;
        public IEnumerator<MidoraId> GetEnumerator() => values().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NoChange : IPreparedProjectEdit
    {
        public bool HasChanges => false;
        public ProjectChangeSet Changes { get; } = new();
        public void Apply(MidoraProject project) { }
        public void Undo(MidoraProject project) { }
    }
}
