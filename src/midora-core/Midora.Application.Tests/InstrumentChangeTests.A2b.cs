using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed partial class InstrumentChangeTests
{
    [Fact]
    public void DenseSelectionProjectionRetainsNonRepresentativePointsAndFormalOrder()
    {
        var groups = Enumerable.Range(1, 1000).Select(i => new InstrumentChange(new(i * 4), new(i * 4 + 1),
            new MidoraId(i * 4 + 2), new(i * 4 + 3))).ToArray();
        using var projection = InstrumentChangeProjection.Create(groups, group =>
            new(group.Id, group.Id.Value / 4, 0, 0, 1, 0, group.BankEventId, group.BankLsbEventId, group.ProgramEventId), default);
        var ids = groups.Where((_, index) => index % 2 == 1).SelectMany(group => group.MemberIds).ToHashSet();
        using var selected = projection.SelectMembers(ids, default);
        Assert.Equal(2, projection.MinimumSelectedTick(ids, default));
        Assert.Equal(500, selected.ReadRange(0, 2000, default).Count());
        Assert.All(selected.ReadVisible(0, 2000, 20, default), value => Assert.Equal(0, value.Tick % 2));
        Assert.Equal(998, selected.Hit(998, 0, default)!.Value.Tick);
        ids.Remove(groups[1].ProgramEventId);
        Assert.Equal(4, projection.MinimumSelectedTick(ids, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => projection.SelectMembers(ids, cancel.Token));
        Assert.Equal(1000, projection.ReadRange(0, 2000, default).Count());
    }

    [Fact]
    public async Task WriteAcceptanceSampleWhenExplicitlyRequested()
    {
        string? directory = Environment.GetEnvironmentVariable("MIDORA_A2B_UAT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        using var project = new MidoraProject(192);
        project.Metadata.ProjectName = "A2b — Instrument Changes and Lane Tabs";
        var midiId = AddMidi(project); var midi = Midi(project); midi.LengthTicks = 4096;
        project.PureMidiTracks[0].Name = "MIDI — Inst. + raw lanes";
        var instrument = EventInstrumentLibrary.Create(project, "A2b instrument");
        instrument.TemplateLengthTicks = 4096;
        var voice = instrument.SubVoices[0]; voice.Name = "SubVoice — Inst. + raw lanes"; voice.Events.Clear();
        var parameter = new LogicalParameterDefinition(project) { Name = "Expression — long target label for layout", Maximum = 127, DisplayMaximum = 127 };
        instrument.LogicalParameters.Add(parameter);
        ProjectDomainEditCommands.CreateLogicalTrack("Logical — parameter lanes", instrument.Id).Prepare(project).Apply(project);
        var logical = new Segment(project) { LengthTicks = 4096 }; project.Tracks[0].Segments.Add(logical);
        var lane = new LogicalParameterLane(project) { ParameterId = parameter.Id }; logical.ParameterLanes.Add(lane);
        for (int i = 0; i < 8; i++)
        {
            long tick = i * 384;
            midi.Notes.Add(new(project) { StartTick = tick, LengthTicks = 192, Key = 60 + i, NoteOnVelocity = 100 });
            logical.Notes.Add(new(project) { StartTick = tick, LengthTicks = 192, Note = 60 + i, Velocity = 100 });
            voice.Events.Add(TemplateEvent.Note(project, tick, 192, 60 + i, 100));
            lane.Points.Add(new CurvePoint(project, tick, 30 + i * 10));
        }
        foreach (int controller in new[] { 1, 7, 10, 11, 64 })
        {
            midi.ChannelEvents.Add(new(project) { Tick = 96, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = controller, Data2 = 80 });
            voice.Events.Add(TemplateEvent.ControlChange(project, 96, controller, 80));
        }
        foreach (var owner in new[] { new InstrumentChangeOwner(midiId), new InstrumentChangeOwner(voice.Id, instrument.Id) })
        {
            using var scope = BulkEditPreparationContext.Enter(project: project);
            var edit = ProjectDomainEditCommands.AppendInstrumentChanges(owner, Enumerable.Range(0, 4)
                .Select(i => new InstrumentChangeValue(new(i + 1), i * 768, 0, 0, i))).Prepare(project);
            edit.Apply(project); (edit as IDisposable)?.Dispose();
        }
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "A2b-Lanes-and-Instruments.midora");
        var package = new Midora.Persistence.MidoraProjectPackageV1("1.0.0-dev", instrumentChangeStorage: BoundedInstrumentChangeStorageLoader.Instance);
        await package.SaveCopyAsync(project, path);
        var reopened = await package.OpenAsync(path);
        using (reopened.Project)
        {
            Assert.Equal(4, Midi(reopened.Project).InstrumentChanges.Count);
            Assert.Equal(4, Voice(reopened.Project).InstrumentChanges.Count);
            Assert.Single(reopened.Project.Tracks);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CopyDragClampsAtZeroAndCanceledPlanningDoesNotPublish(bool subVoice)
    {
        using var project = new MidoraProject(480); var segment = AddMidi(project);
        var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0]; voice.Events.Clear();
        var owner = subVoice ? new InstrumentChangeOwner(voice.Id, instrument.Id) : new(segment);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var append = ProjectDomainEditCommands.AppendInstrumentChanges(owner, Enumerable.Range(0, 600)
            .Select(i => new InstrumentChangeValue(new(i + 1), 100 + i * 10, 0, 0, 1))).Prepare(project);
        append.Apply(project); (append as IDisposable)?.Dispose();
        var context = InstrumentChangeSelectionQuery.Capture(project, owner);
        var ids = context.Groups.Values.SelectMany(g => g.MemberIds).ToHashSet();
        long expectedNext = project.NextStableId;
        using var cancel = new CancellationTokenSource();
        var command = ProjectDomainEditCommands.EditInstrumentChanges(owner, ids, new(InstrumentChangeOperation.Move, TickDelta: 1));
        Assert.ThrowsAny<OperationCanceledException>(() => ((IProgressReportingProjectEditCommand)command).Prepare(project, cancel.Token, new CancelProgress(cancel)));
        Assert.Equal(expectedNext, project.NextStableId); Assert.Same(context.Groups, InstrumentChangeSelectionQuery.Capture(project, owner).Groups);
        var single = context.Groups.Values.Take(1).SelectMany(g => g.MemberIds).ToHashSet();
        var copy = ProjectDomainEditCommands.DuplicateInstrumentChanges(owner, single, -200);
        var prepared = copy.Prepare(project);
        try
        {
            prepared.Apply(project); var after = InstrumentChangeSelectionQuery.Capture(project, owner);
            var copied = Assert.Single(InstrumentChangeSelectionQuery.EnumerateGroups(after.Groups, copy.ResultSelectionIds.ToHashSet()));
            Assert.Equal(0, after.Read(copied)!.Value.Tick); Assert.Empty(copy.ResultSelectionIds.Intersect(single));
            prepared.Undo(project); Assert.Equal(600, InstrumentChangeSelectionQuery.Capture(project, owner).Groups.Count);
        }
        finally { (prepared as IDisposable)?.Dispose(); }
    }
    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) { if (value.Phase == TimelineEditPreparationPhase.ResolvingSelection) cancel.Cancel(); } }

    [Fact]
    public void TemplateCountsTrackTargetReplacementAndNoteCategoryWithoutChangingOlderSnapshots()
    {
        using var project = new MidoraProject(480);
        var voice = EventInstrumentLibrary.Create(project, "I").SubVoices[0]; voice.Events.Clear();
        voice.Events.Add(TemplateEvent.Note(project, 0, 10, 60, 100));
        voice.Events.Add(TemplateEvent.ControlChange(project, 1, 7, 90));
        var original = voice.Events.CreateQuerySnapshot();
        voice.Events[1].Number = 11;
        var replacement = voice.Events.CreateQuerySnapshot();
        Assert.Equal(1, replacement.NoteCount);
        long oldKey = TemplateEventMidiTargets.EncodeDiscoveryKey(MidiValueTarget.ControlChange(7));
        long newKey = TemplateEventMidiTargets.EncodeDiscoveryKey(MidiValueTarget.ControlChange(11));
        Assert.Equal(1, original.DiscoveryCounts[oldKey]); Assert.False(original.DiscoveryCounts.ContainsKey(newKey));
        Assert.False(replacement.DiscoveryCounts.ContainsKey(oldKey)); Assert.Equal(1, replacement.DiscoveryCounts[newKey]);
        voice.Events.Clear(); Assert.Equal(0, voice.Events.CreateQuerySnapshot().NoteCount); Assert.Empty(voice.Events.CreateQuerySnapshot().DiscoveryCounts);
        Assert.Equal(1, original.NoteCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClipboardConvertsOwnersAndBoundedPackageRoundTrips(bool fromSubVoice)
    {
        using var project = new MidoraProject(480); var segment = AddMidi(project);
        var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0];
        voice.Events.Clear();
        var sourceOwner = fromSubVoice ? new InstrumentChangeOwner(voice.Id, instrument.Id) : new(segment);
        var destination = fromSubVoice ? new InstrumentChangeOwner(segment) : new(voice.Id, instrument.Id);
        var create = (fromSubVoice ? ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 100, new(127, 64, 32))
            : ProjectDomainEditCommands.SetMidiInstrumentChange(segment, 100, new(127, 64, 32))).Prepare(project);
        create.Apply(project); (create as IDisposable)?.Dispose();
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var groups = InstrumentChangeSelectionQuery.Capture(project, sourceOwner).Groups;
        using var clipboard = ProjectObjectClipboard.CopyInstrumentChanges(document, sourceOwner, groups.Values.SelectMany(g => g.MemberIds).ToHashSet());
        var command = ProjectObjectClipboard.CreatePasteInstrumentChangesCommand(document, clipboard, destination, 300);
        var prepared = command.Prepare(project);
        try
        {
            prepared.Apply(project);
            var target = InstrumentChangeSelectionQuery.Capture(project, destination);
            var group = Assert.Single(target.Groups.Values); var value = target.Read(group)!.Value;
            Assert.Equal((300L, 127, 64, 32), (value.Tick, value.BankMsb, value.BankLsb, value.Program));
            Assert.Equal(fromSubVoice ? 3 : 2, group.MemberIds.Count());
            prepared.Undo(project); Assert.Empty(InstrumentChangeSelectionQuery.Capture(project, destination).Groups.Values);
            prepared.Apply(project);
            string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "a2b-roundtrip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var service = new Midora.Persistence.MidoraProjectPackageV1("1.0.0-dev", instrumentChangeStorage: BoundedInstrumentChangeStorageLoader.Instance);
                string file = Path.Combine(directory, "roundtrip.midora");
                await service.SaveCopyAsync(project, file);
                var reopened = await service.OpenAsync(file);
                using (reopened.Project)
                {
                    var restored = InstrumentChangeSelectionQuery.Capture(reopened.Project, destination);
                    Assert.IsType<BoundedInstrumentChangeStorage>(restored.Groups.Storage);
                    Assert.Equal(value, restored.Read(Assert.Single(restored.Groups.Values))!.Value);
                    Assert.Equal(4, reopened.SourceFileFormatVersion);
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
        finally { (prepared as IDisposable)?.Dispose(); (command as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void ExactOrderedEventQueryPreservesEveryDensePointAndExcludesOutsideKeys()
    {
        using var project = new MidoraProject(480); var segment = AddMidi(project);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var create = ProjectDomainEditCommands.AppendInstrumentChanges(new(segment), Enumerable.Range(0, 3000)
            .Select(i => new InstrumentChangeValue(new(i + 1), i, 0, 0, i % 128))).Prepare(project);
        try
        {
            create.Apply(project);
            var source = BoundedDirectMidiEventSource.Capture(Midi(project).ChannelEvents);
            var keys = Enumerable.Range(990, 1035).Select(i => new DirectMidiEventStartKey(i, DirectMidiChannelEventKind.ControlChange, 32)).ToHashSet();
            var actual = source.QueryStartKeys(keys).OrderBy(v => v.Tick).ToArray();
            Assert.Equal(1035, actual.Length);
            Assert.All(actual, value => { Assert.Equal(32, value.Data1); Assert.InRange(value.Tick, 990, 2024); });
            Assert.Equal(990L, source.QueryChannelEvents(990, 991).First().Tick);
            Assert.Equal(3, source.QueryChannelEvents(990, 991).Count());
            Assert.Empty(source.QueryChannelEvents(3000, 4000));
        }
        finally { (create as IDisposable)?.Dispose(); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CopyDragResolvesCollisionsAndUndoRestoresBothAssociations(bool subVoice)
    {
        using var project = new MidoraProject(480); var segment = AddMidi(project);
        var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0];
        var owner = subVoice ? new InstrumentChangeOwner(voice.Id, instrument.Id) : new(segment);
        foreach (long tick in new long[] { 100, 200 })
        {
            var create = (subVoice ? ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, tick, new(1, 2, (byte)(tick / 100)))
                : ProjectDomainEditCommands.SetMidiInstrumentChange(segment, tick, new(1, 2, (byte)(tick / 100)))).Prepare(project);
            create.Apply(project); (create as IDisposable)?.Dispose();
        }
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var context = InstrumentChangeSelectionQuery.Capture(project, owner);
        var ids = context.Groups.Values.SelectMany(g => g.MemberIds).ToHashSet();
        var originals = context.Groups.Values.ToArray();
        var command = ProjectDomainEditCommands.DuplicateInstrumentChanges(owner, ids, 100);
        var prepared = InstrumentChangeMaintenance.Wrap(project, command.Prepare(project));
        try
        {
            prepared.Apply(project);
            var after = InstrumentChangeSelectionQuery.Capture(project, owner);
            Assert.Equal(3, after.Groups.Count);
            Assert.Equal(new long[] { 100, 200, 300 }, after.Groups.Values.Select(g => after.Read(g)!.Value.Tick).Order());
            Assert.Equal(1, after.Groups.Values.Select(g => after.Read(g)!.Value).Single(v => v.Tick == 200).Program);
            Assert.Equal(subVoice ? 4 : 6, command.ResultSelectionIds.Count);
            Assert.Empty(command.ResultSelectionIds.Intersect(ids));
            prepared.Undo(project);
            Assert.Equal(originals, InstrumentChangeSelectionQuery.Capture(project, owner).Groups.Values);
            prepared.Apply(project); Assert.Equal(3, InstrumentChangeSelectionQuery.Capture(project, owner).Groups.Count);
        }
        finally { (prepared as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FlipCollisionRejectsAtomicallyAndPartialSelectionIsNotAWrapper(bool subVoice)
    {
        using var project = new MidoraProject(480); var segment = AddMidi(project);
        var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0];
        var owner = subVoice ? new InstrumentChangeOwner(voice.Id, instrument.Id) : new(segment);
        foreach (long tick in new long[] { 100, 200, 300 })
        {
            var create = (subVoice ? ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, tick, new(0, 0, 1))
                : ProjectDomainEditCommands.SetMidiInstrumentChange(segment, tick, new(0, 0, 1))).Prepare(project);
            create.Apply(project); (create as IDisposable)?.Dispose();
        }
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var context = InstrumentChangeSelectionQuery.Capture(project, owner);
        var groups = context.Groups.Values.ToArray();
        Assert.Empty(InstrumentChangeSelectionQuery.EnumerateGroups(context.Groups, new HashSet<MidoraId> { groups[0].ProgramEventId }));
        // Scaling 100/200 onto 100/300 collides with the unselected last group.
        var ids = groups.Take(2).SelectMany(g => g.MemberIds).ToHashSet();
        long next = project.NextStableId;
        Assert.ThrowsAny<Exception>(() => ProjectDomainEditCommands.EditInstrumentChanges(owner, ids,
            new(InstrumentChangeOperation.Scale, Scale: 2)).Prepare(project));
        Assert.Equal(next, project.NextStableId);
        Assert.Equal(groups, InstrumentChangeSelectionQuery.Capture(project, owner).Groups.Values);
    }

    [Fact]
    public void PagedAssociationStorageRejectsDuplicateIdsAndMembersAndHonorsCancellation()
    {
        using var project = new MidoraProject(480);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        InstrumentChange first = new(new(100), new(101), new(102), new(103));
        Assert.ThrowsAny<ArgumentException>(() => BoundedInstrumentChangeStorage.Create([first, first]));
        Assert.ThrowsAny<ArgumentException>(() => BoundedInstrumentChangeStorage.Create([first, first with { Id = new(104) }]));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using var canceledScope = BulkEditPreparationContext.Enter(cancel.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => BoundedInstrumentChangeStorage.Create([first]));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WrapperBatchEditsPreserveAssociationsAndUndo(bool subVoice)
    {
        using var project = new MidoraProject(480);
        var segment = AddMidi(project);
        var instrument = EventInstrumentLibrary.Create(project, "I");
        var voice = instrument.SubVoices[0];
        InstrumentChangeOwner owner = subVoice ? new(voice.Id, instrument.Id) : new(segment);
        foreach (long tick in new long[] { 100, 200, 500 })
        {
            var command = subVoice ? ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, tick, new(1, 2, 3))
                : ProjectDomainEditCommands.SetMidiInstrumentChange(segment, tick, new(1, 2, 3));
            var create = command.Prepare(project); create.Apply(project); (create as IDisposable)?.Dispose();
        }
        foreach (var edit in new[] { new InstrumentChangeEdit(InstrumentChangeOperation.Move, TickDelta: 20),
            new(InstrumentChangeOperation.Flip), new(InstrumentChangeOperation.Scale, Scale: 1.5),
            new(InstrumentChangeOperation.Properties, BankMsb: 7, Program: 11), new(InstrumentChangeOperation.Delete) })
        {
            using var scope = BulkEditPreparationContext.Enter(project: project);
            var context = InstrumentChangeSelectionQuery.Capture(project, owner);
            var before = context.Groups.Values.Select(group => context.Read(group)!.Value).OrderBy(value => value.Id).ToArray();
            var ids = context.Groups.Values.SelectMany(group => group.MemberIds).ToHashSet();
            var prepared = InstrumentChangeMaintenance.Wrap(project, ProjectDomainEditCommands.EditInstrumentChanges(owner, ids, edit).Prepare(project));
            try
            {
                prepared.Apply(project);
                var after = InstrumentChangeSelectionQuery.Capture(project, owner);
                Assert.Equal(edit.Operation == InstrumentChangeOperation.Delete ? 0 : 3, after.Groups.Count);
                Assert.All(after.Groups.Values, group => Assert.NotNull(after.Read(group)));
                prepared.Undo(project);
                var restored = InstrumentChangeSelectionQuery.Capture(project, owner);
                Assert.Equal(before, restored.Groups.Values.Select(group => restored.Read(group)!.Value).OrderBy(value => value.Id));
            }
            finally { (prepared as IDisposable)?.Dispose(); }
        }
    }

    [Fact]
    public void DirectTargetCountsFollowValueEditCollisionAndUndo()
    {
        using var project = new MidoraProject(480); var segment = AddMidi(project);
        var create = ProjectDomainEditCommands.SetMidiInstrumentChange(segment, 100, new(1, 2, 3)).Prepare(project);
        create.Apply(project); (create as IDisposable)?.Dispose();
        var original = Midi(project).ChannelEvents.CreateQuerySnapshot();
        Assert.Equal(3, original.GetTargetCounts().Count);
        var selection = Midi(project).InstrumentChanges.Values.SelectMany(value => value.MemberIds).ToHashSet();
        var edit = ProjectDomainEditCommands.EditInstrumentChanges(new(segment), selection,
            new(InstrumentChangeOperation.Properties, BankMsb: 20)).Prepare(project);
        try
        {
            edit.Apply(project);
            Assert.Equal(original.GetTargetCounts().OrderBy(value => value.Key.Kind).ThenBy(value => value.Key.Number),
                Midi(project).ChannelEvents.CreateQuerySnapshot().GetTargetCounts().OrderBy(value => value.Key.Kind).ThenBy(value => value.Key.Number));
            edit.Undo(project);
            Assert.Equal(3, Midi(project).ChannelEvents.CreateQuerySnapshot().GetTargetCounts().Values.Sum());
        }
        finally { (edit as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void TemplateTargetCountersCountEventsNotPages()
    {
        using var project = new MidoraProject(480);
        var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0];
        voice.Events.Clear();
        using (voice.Events.BeginBatchChange())
            for (int i = 0; i < 9000; i++) voice.Events.Add(new TemplateEvent(project)
                { Kind = TemplateEventKind.ControlChange, Tick = i, Number = 11, Value = 100 });
        var first = voice.Events.CreateQuerySnapshot();
        long key = TemplateEventMidiTargets.EncodeDiscoveryKey(new(MidiValueKind.ControlChange, 11));
        Assert.Equal(9000, first.DiscoveryCounts[key]);
        voice.Events[2].Value = 30;
        Assert.Equal(9000, voice.Events.CreateQuerySnapshot().DiscoveryCounts[key]);
        voice.Events.RemoveAt(2);
        Assert.Equal(8999, voice.Events.CreateQuerySnapshot().DiscoveryCounts[key]);
        Assert.Equal(9000, first.DiscoveryCounts[key]);
    }
}
