using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class MixedTimelineSelectionCommandsTests
{
    [Fact]
    public void EmptySelectionDoesNotResolveAnOwnerAndStillHonorsCancellation()
    {
        using var fixture = Create(ProjectTimelineOwnerKind.LogicalSegment, 2);
        var absentOwner = new TimelineObjectOwner(ProjectTimelineOwnerKind.LogicalSegment, new MidoraId(99999999));
        Assert.Empty(ProjectTimelineObjectSelection.ReadMembers(fixture.Project, absentOwner, []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProjectTimelineObjectSelection.ReadMembers(fixture.Project, absentOwner, [], cancellation.Token).ToArray());
    }

    [Theory]
    [InlineData(ProjectTimelineOwnerKind.LogicalSegment, 2)]
    [InlineData(ProjectTimelineOwnerKind.DirectMidiSegment, 2)]
    [InlineData(ProjectTimelineOwnerKind.SubVoice, 2)]
    [InlineData(ProjectTimelineOwnerKind.LogicalSegment, 5000)]
    [InlineData(ProjectTimelineOwnerKind.DirectMidiSegment, 5000)]
    [InlineData(ProjectTimelineOwnerKind.SubVoice, 5000)]
    public void MixedDeleteIsOneAtomicHistoryEntryAndRestoresAllKinds(ProjectTimelineOwnerKind kind, int count)
    {
        using var fixture = Create(kind, count);
        using var compilation = new ProjectCompilationSession(fixture.Project);
        var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Persisted);
        document.MarkSaveSucceeded();
        long next = fixture.Project.NextStableId;
        using var resources = BulkEditPreparationContext.Enter(project: fixture.Project);
        var command = ProjectDomainEditCommands.DeleteTimelineObjects(fixture.Owner, fixture.Selected);
        using var staged = document.PrepareEdit(command);
        Assert.Equal(fixture.Selected.Count + 1, fixture.CurrentCount());
        Assert.Empty(document.History);
        Assert.NotNull(staged.PreparedSelection);
        Assert.Equal(fixture.Selected.OrderBy(static id => id), staged.PreparedSelection.OriginalSelectionIds);
        Assert.Empty(staged.PreparedSelection.ResultSelectionIds);
        document.ExecutePrepared(staged);
        Assert.Equal(1, fixture.CurrentCount());
        Assert.Single(document.History);
        Assert.Empty(command.ResultSelectionIds);
        Assert.Equal(next, fixture.Project.NextStableId);
        document.Undo();
        Assert.Equal(fixture.Selected.Count + 1, fixture.CurrentCount());
        Assert.Equal(fixture.Selected.OrderBy(static id => id), command.ResultSelectionIds);
        document.Redo();
        Assert.Equal(1, fixture.CurrentCount());
        Assert.Empty(command.ResultSelectionIds);
        Assert.True(resources.Resources.PeakResidentBytes <= resources.Resources.Budget.MaximumResidentBytes);
        Assert.True(resources.Resources.PeakWorkingBytes <= resources.Resources.Budget.MaximumWorkingBytes);
    }

    [Theory]
    [InlineData(ProjectTimelineOwnerKind.LogicalSegment)]
    [InlineData(ProjectTimelineOwnerKind.DirectMidiSegment)]
    [InlineData(ProjectTimelineOwnerKind.SubVoice)]
    public void InvalidForeignOrDuplicateIdFailsBeforePublication(ProjectTimelineOwnerKind kind)
    {
        using var fixture = Create(kind, 2);
        long next = fixture.Project.NextStableId;
        var foreign = new MidoraId(987654321);
        Assert.Throws<ArgumentException>(() => ProjectDomainEditCommands.DeleteTimelineObjects(fixture.Owner,
            fixture.Selected.Append(foreign).ToArray()).Prepare(fixture.Project));
        Assert.Throws<ArgumentException>(() => ProjectDomainEditCommands.DeleteTimelineObjects(fixture.Owner,
            fixture.Selected.Append(fixture.Selected[0]).ToArray()).Prepare(fixture.Project));
        Assert.Equal(next, fixture.Project.NextStableId);
        Assert.Equal(fixture.Selected.Count + 1, fixture.CurrentCount());
    }

    [Theory]
    [InlineData(ProjectTimelineOwnerKind.LogicalSegment)]
    [InlineData(ProjectTimelineOwnerKind.DirectMidiSegment)]
    [InlineData(ProjectTimelineOwnerKind.SubVoice)]
    public void CancellationAtEveryReportedPhaseLeavesProjectUnchanged(ProjectTimelineOwnerKind kind)
    {
        using var fixture = Create(kind, 12);
        using var compilation = new ProjectCompilationSession(fixture.Project);
        var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Persisted);
        long next = fixture.Project.NextStableId;
        HashSet<TimelineEditPreparationPhase> phases = [];
        using (var observed = document.PrepareEdit(ProjectDomainEditCommands.DeleteTimelineObjects(fixture.Owner, fixture.Selected),
            progress: new ImmediateProgress(value => phases.Add(value.Phase)))) { }
        Assert.NotEmpty(phases);
        foreach (var phase in phases)
        {
            using var cancellation = new CancellationTokenSource();
            Assert.ThrowsAny<OperationCanceledException>(() => document.PrepareEdit(
                ProjectDomainEditCommands.DeleteTimelineObjects(fixture.Owner, fixture.Selected), cancellation.Token,
                new ImmediateProgress(value =>
                {
                    // Cached address lookup can omit a phase seen by the cold
                    // probe. Still cancel at Ready instead of asserting that
                    // timing-dependent progress callbacks must be identical.
                    if (value.Phase == phase || value.Phase == TimelineEditPreparationPhase.Ready) cancellation.Cancel();
                })));
            Assert.Equal(fixture.Selected.Count + 1, fixture.CurrentCount());
            Assert.Equal(next, fixture.Project.NextStableId);
            Assert.Empty(document.History);
        }
    }

    [Fact]
    public void OpaquePayloadAndHiddenObjectsAreNotReinterpretedByClassificationOrUndo()
    {
        using var fixture = Create(ProjectTimelineOwnerKind.DirectMidiSegment, 2);
        var segment = fixture.Project.PureMidiTracks[0].Segments[0];
        var opaque = segment.OpaqueEvents[0];
        var payload = opaque.Payload.ToArray();
        var members = ProjectTimelineObjectSelection.ReadMembers(fixture.Project, fixture.Owner, fixture.Selected).ToArray();
        Assert.Equal(2, members.Count(static m => m.Kind == TimelineObjectSelectionKind.Note));
        Assert.Equal(2, members.Count(static m => m.Kind == TimelineObjectSelectionKind.Event));
        Assert.Single(members, static m => m.Kind == TimelineObjectSelectionKind.Opaque);
        var prepared = ProjectDomainEditCommands.DeleteTimelineObjects(fixture.Owner, fixture.Selected).Prepare(fixture.Project);
        using var lifetime = prepared as IDisposable;
        prepared.Apply(fixture.Project);
        Assert.Equal(90000, fixture.Project.PureMidiTracks[0].Segments[0].Notes[0].StartTick);
        prepared.Undo(fixture.Project);
        Assert.Equal(payload, fixture.Project.PureMidiTracks[0].Segments[0].OpaqueEvents[0].Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SmallLegacyTransformsPublishOnlyActuallySurvivingOriginalIdentities(int operation)
    {
        using var fixture = Create(ProjectTimelineOwnerKind.LogicalSegment, 2);
        var segment = fixture.Project.Tracks[0].Segments[0];
        MidoraId[] ids = operation == 3 ? [segment.Notes[0].Id] : [segment.Notes[0].Id, segment.Notes[1].Id];
        IProjectEditCommand command = operation switch
        {
            0 => ProjectDomainEditCommands.FlipLogicalNotesHorizontal(segment.Id, ids),
            1 => ProjectDomainEditCommands.ScaleLogicalNotes(segment.Id, ids, 2),
            2 => ProjectDomainEditCommands.TransposeLogicalNotes(segment.Id, ids, 1),
            _ => ProjectDomainEditCommands.MoveLogicalNotes(segment.Id, ids, 4, 0)
        };
        var wrapper = ProjectDomainEditCommands.WithTimelineObjectSelection(command, fixture.Owner, ids);
        var edit = wrapper.Prepare(fixture.Project);
        using var editLifetime = edit as IDisposable;
        Assert.Same(segment, fixture.Project.Tracks[0].Segments[0]);
        Assert.Equal(0, segment.Notes[0].StartTick);
        edit.Apply(fixture.Project);
        Assert.Equal(operation == 3 ? 2 : 3, fixture.Project.Tracks[0].Segments[0].Notes.Count);
        Assert.True(wrapper.ResultSelectionIds.All(id => ids.Contains(id)));
        Assert.Equal(operation == 3 ? 0 : 2, wrapper.ResultSelectionIds.Count);
        edit.Undo(fixture.Project);
        Assert.Same(segment, fixture.Project.Tracks[0].Segments[0]);
        Assert.Equal(ids.OrderBy(static id => id), wrapper.ResultSelectionIds.OrderBy(static id => id));
        edit.Apply(fixture.Project);
        Assert.Equal(operation == 3 ? 0 : 2, wrapper.ResultSelectionIds.Count);
    }

    [Theory]
    [InlineData(ProjectTimelineOwnerKind.LogicalSegment)]
    [InlineData(ProjectTimelineOwnerKind.DirectMidiSegment)]
    [InlineData(ProjectTimelineOwnerKind.SubVoice)]
    public void ExistingBoundedSelectionPlanIsPreparedOnlyOnce(ProjectTimelineOwnerKind kind)
    {
        using var fixture = Create(kind, 5000);
        var ids = ProjectTimelineObjectSelection.ReadMembers(fixture.Project, fixture.Owner, fixture.Selected)
            .Where(static value => value.Kind == TimelineObjectSelectionKind.Note).Select(static value => value.Id).ToArray();
        IProjectEditCommand source = kind switch
        {
            ProjectTimelineOwnerKind.LogicalSegment => ProjectDomainEditCommands.ScaleLogicalNotes(fixture.Owner.OwnerId, ids, 2),
            ProjectTimelineOwnerKind.DirectMidiSegment => ProjectDomainEditCommands.ScaleDirectMidiNotes(fixture.Owner.OwnerId, ids, 2),
            _ => ProjectDomainEditCommands.ScaleTemplateNotes(fixture.Owner.EventInstrumentId!.Value, fixture.Owner.OwnerId, ids, 2)
        };
        var counted = new CountingCommand(source);
        var wrapper = ProjectDomainEditCommands.WithTimelineObjectSelection(counted, fixture.Owner, ids);
        var edit = wrapper.Prepare(fixture.Project);
        using var lifetime = edit as IDisposable;
        Assert.Equal(1, counted.PrepareCount);
        Assert.Equal(5000, Assert.IsAssignableFrom<IPreparedTimelineSelectionEdit>(edit).PreparedSelection.ResultSelectionIds.Count);
        // The opt-in scope must not change normal command allocations or
        // selection behavior after this preparation leaves its async context.
        var ordinary = source.Prepare(fixture.Project);
        using var ordinaryLifetime = ordinary as IDisposable;
        Assert.False(ordinary is IPreparedTimelineSelectionEdit { HasPreparedSelection: true });
    }

    [Fact]
    public void FailedSelectionForwardingDisposesThePreparedResources()
    {
        using var project = new MidoraProject(480);
        var command = new FailedSelectionCommand();
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.WithTimelineObjectSelection(
            command, new(ProjectTimelineOwnerKind.LogicalSegment, new(1)), [new MidoraId(2)]).Prepare(project));
        Assert.True(command.Disposed);
    }

    private static Fixture Create(ProjectTimelineOwnerKind kind, int count)
    {
        var project = new MidoraProject(480);
        List<MidoraId> selected = [];
        if (kind == ProjectTimelineOwnerKind.DirectMidiSegment)
        {
            var root = new MidiChannelRoot(project) { Name = "Root" };
            var track = new PureMidiTrack(project) { Name = "MIDI", MidiChannelRootId = root.Id };
            var segment = new MidiSegment(project) { LengthTicks = 64 };
            project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id)); track.Segments.Add(segment);
            for (int i = 0; i < count; i++)
            {
                var note = new DirectMidiNote(project) { StartTick = i * 4, LengthTicks = 2, Key = 60, NoteOnVelocity = 90, NoteOffVelocity = 17 };
                var point = new DirectMidiChannelEvent(project) { Tick = i * 4, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11 + i % 2, Data2 = 31 };
                segment.Notes.Add(note); segment.ChannelEvents.Add(point); selected.Add(note.Id); selected.Add(point.Id);
            }
            var opaque = new OpaqueMidiEvent(project) { Tick = 1, Kind = OpaqueMidiEventKind.SystemExclusive, Payload = [0x41, 0x10, 0xf7] };
            segment.OpaqueEvents.Add(opaque); selected.Add(opaque.Id);
            segment.Notes.Add(new(project) { StartTick = 90000, LengthTicks = 5, Key = 1, NoteOnVelocity = 100 });
            return new(project, new(kind, segment.Id), selected,
                () => { var current = project.PureMidiTracks[0].Segments[0]; return current.Notes.Count + current.ChannelEvents.Count + current.OpaqueEvents.Count; });
        }
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        if (kind == ProjectTimelineOwnerKind.SubVoice)
        {
            var voice = new SubVoice(project) { Name = "Voice" }; instrument.SubVoices.Add(voice);
            for (int i = 0; i < count; i++)
            {
                var note = new TemplateEvent(project) { Kind = TemplateEventKind.Note, Tick = i * 4, LengthTicks = 2, Number = 60, Value = 90 };
                var point = new TemplateEvent(project) { Kind = TemplateEventKind.ControlChange, Tick = i * 4, Number = 11 + i % 2, Value = 31 };
                voice.Events.Add(note); voice.Events.Add(point); selected.Add(note.Id); selected.Add(point.Id);
            }
            voice.Events.Add(new(project) { Kind = TemplateEventKind.Note, Tick = 90000, LengthTicks = 2, Number = 1, Value = 90 });
            return new(project, new(kind, voice.Id, instrument.Id), selected,
                () => project.EventInstruments.Single(value => value.Id == instrument.Id).SubVoices.Single(value => value.Id == voice.Id).Events.Count);
        }
        var logicalTrack = new LogicalTrack(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        var logical = new Segment(project) { LengthTicks = 64 }; logicalTrack.Segments.Add(logical);
        // Orphan parameter references are deliberate: delete must still work.
        var lanes = new[] { new LogicalParameterLane(project) { ParameterId = new(80000001) }, new LogicalParameterLane(project) { ParameterId = new(80000002) } };
        logical.ParameterLanes.AddRange(lanes);
        for (int i = 0; i < count; i++)
        {
            var note = new LogicalNote(project) { StartTick = i * 4, LengthTicks = 2, Note = 60, Velocity = 90 };
            var point = new CurvePoint(project, i * 4, .25);
            logical.Notes.Add(note); lanes[i % 2].Points.Add(point); selected.Add(note.Id); selected.Add(point.Id);
        }
        logical.Notes.Add(new(project) { StartTick = 90000, LengthTicks = 2, Note = 1, Velocity = 90 });
        return new(project, new(kind, logical.Id), selected,
            () => { var current = project.Tracks[0].Segments[0]; return current.Notes.Count + current.ParameterLanes.Sum(static lane => lane.Points.Count); });
    }

    private sealed record Fixture(MidoraProject Project, TimelineObjectOwner Owner, List<MidoraId> Selected,
        Func<int> CurrentCount) : IDisposable { public void Dispose() => Project.Dispose(); }
    private sealed class ImmediateProgress(Action<TimelineEditPreparationProgress> action)
        : IProgress<TimelineEditPreparationProgress> { public void Report(TimelineEditPreparationProgress value) => action(value); }
    private sealed class CountingCommand(IProjectEditCommand source) : IProjectEditCommand
    {
        public string Name => source.Name;
        public int PrepareCount { get; private set; }
        public IPreparedProjectEdit Prepare(MidoraProject project) { PrepareCount++; return source.Prepare(project); }
    }
    private sealed class FailedSelectionCommand : IProjectEditCommand, IPreparedTimelineSelectionEdit, IDisposable
    {
        public string Name => "Failed selection result";
        public bool Disposed { get; private set; }
        public bool HasChanges => false;
        public ProjectChangeSet Changes { get; } = new();
        public bool HasPreparedSelection => true;
        public PreparedTimelineSelection PreparedSelection => throw new InvalidOperationException("Unavailable selection result.");
        public IPreparedProjectEdit Prepare(MidoraProject project) => this;
        public void Apply(MidoraProject project) { }
        public void Undo(MidoraProject project) { }
        public void Dispose() => Disposed = true;
    }
}
