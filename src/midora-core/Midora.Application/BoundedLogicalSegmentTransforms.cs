using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static bool RequiresBoundedLogicalSegmentContent(IEnumerable<SegmentTransformEntry> segments)
    {
        long count = 0;
        foreach (var entry in segments)
        {
            count += entry.Segment.Notes.Count;
            if (count >= BoundedNoteThreshold) return true;
            foreach (var lane in entry.Segment.ParameterLanes)
            {
                count += lane.Points.Count;
                if (count >= BoundedNoteThreshold) return true;
            }
        }
        return false;
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalSegmentTransform(MidoraProject project,
        IReadOnlyList<SegmentTransformEntry> segments, SegmentContentTransformKind? kind,
        SegmentSelectionTransformScope transformScope, double factor = 1, int semitones = 0,
        BatchEditExpressionProgram? program = null)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        long sourceSegmentCount = segments.Select(v => v.Track).Distinct().Sum(track => (long)track.Segments.Count);
        using var metadataBudget = scope.Resources.ReserveWorking(checked(sourceSegmentCount * 192L + segments.Count * 256L));
        var initialTracks = segments.Select(v => v.Track).Distinct().Select(track =>
        {
            Segment[] original = track.Segments.ToArray();
            return (Track: track, Old: original, Stamps: original.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray());
        }).ToArray();
        long left = segments.Min(v => v.Segment.ProjectStartTick);
        long right = segments.Max(v => v.Segment.ProjectRange.EndTick);
        long expressionOrigin = long.MaxValue;
        List<(SegmentTransformEntry Entry, BoundedImmutableValueSource<MidoraId> Ids)> selections = [];
        List<BoundedImmutableValueSource<TimelineValueEdit<LogicalNoteSnapshotValue>>> notePlans = [];
        List<BoundedCurvePointPlan> pointPlans = [];
        List<SegmentWindowTransform> windows = [];
        Dictionary<Segment, Segment> replacements = [];
        var changes = TrackChange(segments.Select(v => v.Track.Id).Distinct().ToArray());
        bool changed = false, transferred = false;
        try
        {
            foreach (var entry in segments)
            {
                var snapshot = entry.Segment.Notes.CreateQuerySnapshot();
                var store = new BoundedEditRecordStore<MidoraId>(scope.Resources);
                try
                {
                    foreach (var value in snapshot.EnumerateRangeValues(entry.Segment.ContentOffsetTick, entry.Segment.ContentEndTick))
                    {
                        store.Add(value.Id, scope.Token);
                        expressionOrigin = Math.Min(expressionOrigin, value.StartTick);
                    }
                    store.Seal(); store.SpillResidentPages(scope.Token);
                    selections.Add((entry, new(store)));
                }
                catch { store.Dispose(); throw; }
            }
            if (program is not null && expressionOrigin == long.MaxValue)
                throw new InvalidOperationException("The selected Segments expose no Logical Notes to batch edit.");
            Stopwatch clock = Stopwatch.StartNew();
            foreach (var selection in selections)
            {
                var entry = selection.Entry;
                Segment original = entry.Segment;
                Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, original, scope.Token);
                replacements.Add(original, result);
                long contentLeft = original.ContentOffsetTick, contentRight = original.ContentEndTick;
                SegmentWindow oldWindow = new(original.ProjectStartTick, original.LengthTicks, original.ContentOffsetTick);
                SegmentWindow newWindow = oldWindow;
                if (transformScope == SegmentSelectionTransformScope.ExposedContentAndSegments)
                    newWindow = kind switch
                    {
                        SegmentContentTransformKind.FlipHorizontal => oldWindow with { ProjectStartTick = checked(left + (right - checked(oldWindow.ProjectStartTick + oldWindow.LengthTicks))) },
                        SegmentContentTransformKind.Scale => oldWindow with { ProjectStartTick = ScaleTick(left, oldWindow.ProjectStartTick, factor), LengthTicks = ScaleLength(oldWindow.LengthTicks, factor) },
                        _ => oldWindow
                    };
                long minimum = contentLeft;
                if (selection.Ids.Count != 0)
                {
                    var source = original.Notes.CreateQuerySnapshot();
                    using var selected = BoundedLogicalNotePlanning.Select(source, selection.Ids, static _ => true, scope.Resources, scope.Token);
                    var notePlan = BoundedLogicalNotePlanning.Transform(source, selected, Convert,
                        static v => new(v.StartTick, v.Note), static v => v.Id,
                        key => source.EnumerateExactStart(key.Tick, key.Key), true, scope.Resources, scope.Token);
                    notePlans.Add(notePlan);
                    result.Notes.Clear(); result.Notes.AdoptEditedSnapshot(project, source, notePlan, cancellationToken: scope.Token);
                    changed |= notePlan.Count != 0;
                    ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(changes, original, result, selection.Ids);
                }
                if (program is not null)
                {
                    long expansion = checked(contentLeft - minimum);
                    newWindow = oldWindow with { ProjectStartTick = checked(oldWindow.ProjectStartTick - expansion),
                        LengthTicks = checked(oldWindow.LengthTicks + expansion), ContentOffsetTick = minimum };
                }
                if (kind is SegmentContentTransformKind.FlipHorizontal or SegmentContentTransformKind.Scale)
                {
                    for (int laneIndex = 0; laneIndex < original.ParameterLanes.Count; laneIndex++)
                    {
                        var lane = original.ParameterLanes[laneIndex];
                        var snapshot = lane.Points.CreateQuerySnapshot();
                        using var ids = FreezeBoundedIds(snapshot.EnumerateRangeValues(contentLeft, contentRight).Select(v => v.Id), scope);
                        if (ids.Count == 0) continue;
                        var plan = BoundedCurvePointPlan.Transform(snapshot, ids, _ => value => value with
                        {
                            Tick = kind == SegmentContentTransformKind.FlipHorizontal
                                ? checked(contentLeft + (checked(contentRight - 1) - value.Tick)) : ScaleTick(contentLeft, value.Tick, factor)
                        }, true, false, static () => throw new InvalidOperationException("No IDs are created by a point transform."),
                            static value => ArgumentOutOfRangeException.ThrowIfNegative(value.Tick), rejectCollisions: true);
                        pointPlans.Add(plan);
                        var target = result.ParameterLanes[laneIndex];
                        target.Points.Clear(); target.Points.AdoptEditedSnapshot(project, snapshot, plan.Changes, plan.Appended, scope.Token);
                        changed |= plan.Changes.Count != 0 || plan.Appended.Count != 0;
                        ProjectTimelineOwnerChangeSetBuilder.AddLogicalParameterPoints(changes, original.Id, lane, target, plan.AffectedIds);
                    }
                }
                windows.Add(new(entry, oldWindow, newWindow));
                SetWindow(result, newWindow);
                changed |= oldWindow != newWindow;

                LogicalNoteSnapshotValue? Convert(LogicalNoteSnapshotValue value)
                {
                    LogicalNoteSnapshotValue replacement;
                    if (program is not null)
                    {
                        var evaluated = EvaluateLogicalNote(new(value.StartTick, value.LengthTicks, value.Note, value.Velocity), expressionOrigin, program, clock);
                        if (evaluated.Discard || FallsBeforeProjectStart(original, evaluated.Replacement.StartTick)) return null;
                        var v = evaluated.Replacement;
                        replacement = value with { StartTick = v.StartTick, LengthTicks = v.LengthTicks, Note = v.Note, Velocity = v.Velocity };
                        minimum = Math.Min(minimum, replacement.StartTick);
                    }
                    else replacement = kind switch
                    {
                        SegmentContentTransformKind.FlipHorizontal => value with { StartTick = checked(contentLeft + (contentRight - checked(value.StartTick + value.LengthTicks))) },
                        SegmentContentTransformKind.FlipVertical => value with { Note = 127 - value.Note },
                        SegmentContentTransformKind.Scale => value with { StartTick = ScaleTick(contentLeft, value.StartTick, factor), LengthTicks = ScaleLength(value.LengthTicks, factor) },
                        SegmentContentTransformKind.Transpose => value with { Note = checked(value.Note + semitones) },
                        _ => value
                    };
                    if (replacement.Note is < 0 or > 127) return null;
                    ValidateLogicalNote(replacement.StartTick, replacement.LengthTicks, replacement.Note, replacement.Velocity);
                    return replacement;
                }
            }
            ValidateSegmentTransformWindows(project, windows);
            IPreparedProjectEdit edit = changed ? new BoundedLogicalTrackSegmentRoots(project, replacements, changes, initialTracks)
                : Prepared(false, changes, _ => { }, _ => { });
            foreach (var plan in pointPlans) edit = plan.Own(edit);
            transferred = true;
            return edit;
        }
        finally
        {
            foreach (var selection in selections) selection.Ids.Dispose();
            foreach (var plan in pointPlans) plan.Dispose();
            if (!transferred) foreach (var plan in notePlans) plan.Dispose();
        }
    }

    private static BoundedImmutableValueSource<MidoraId> FreezeBoundedIds(IEnumerable<MidoraId> ids, BulkEditPreparationContext scope)
    {
        var store = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        try { store.AddRange(ids, scope.Token); store.Seal(); store.SpillResidentPages(scope.Token); return new(store); }
        catch { store.Dispose(); throw; }
    }

    private sealed class BoundedLogicalTrackSegmentRoots : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private readonly MidoraProject _project;
        private readonly (LogicalTrack Track, Segment[] Old, Segment[] New, ProjectTimelineOwnerSourceStamp[] Stamps)[] _tracks;
        private readonly (LogicalTrack Track, List<Segment> Old, List<Segment> New)[] _lists;
        private bool _published, _applied;
        public BoundedLogicalTrackSegmentRoots(MidoraProject project, Dictionary<Segment, Segment> replacements, ProjectChangeSet changes,
            IReadOnlyList<(LogicalTrack Track, Segment[] Old, ProjectTimelineOwnerSourceStamp[] Stamps)> initialTracks,
            bool preserveOrder = false)
        {
            _project = project; Changes = changes;
            _tracks = initialTracks.Select(entry =>
            {
                Segment[] old = entry.Old;
                IEnumerable<Segment> mapped = old.Select(s => replacements.GetValueOrDefault(s, s));
                Segment[] next = (preserveOrder ? mapped : mapped.OrderBy(s => s.ProjectStartTick).ThenBy(s => s.Id)).ToArray();
                return (entry.Track, old, next, entry.Stamps);
            }).ToArray();
            _lists = _tracks.Select(entry => (entry.Track, entry.Track.Segments, entry.New.ToList())).ToArray();
        }
        public bool HasChanges => true;
        public ProjectChangeSet Changes { get; }
        public void ValidateForPublication(MidoraProject project) => Validate(project, false);
        private void Validate(MidoraProject project, bool after)
        {
            if (!ReferenceEquals(project, _project)) throw new InvalidOperationException("This edit belongs to another project.");
            foreach (var entry in _tracks)
            {
                var expected = after ? entry.New : entry.Old;
                if (!project.Tracks.Contains(entry.Track) || entry.Track.Segments.Count != expected.Length)
                    throw new InvalidOperationException("The Segment owner changed before publication.");
                for (int i = 0; i < expected.Length; i++)
                    if (!ReferenceEquals(entry.Track.Segments[i], expected[i]) || (!after && !_published && !entry.Stamps[i].Matches(expected[i])))
                        throw new InvalidOperationException("The Segment source changed before publication.");
            }
        }
        public void Apply(MidoraProject project)
        {
            if (_applied) throw new InvalidOperationException("The Segment edit is already applied.");
            Validate(project, false);
            foreach (var entry in _lists) entry.Track.Segments = entry.New;
            _published = _applied = true;
        }
        public void Undo(MidoraProject project)
        {
            if (!_applied) return;
            Validate(project, true);
            foreach (var entry in _lists) entry.Track.Segments = entry.Old;
            _applied = false;
        }
    }
}
