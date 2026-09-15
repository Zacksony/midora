using Midora.Domain;

namespace Midora.Application.Tests;

public sealed partial class InstrumentChangeTests
{
    [Fact]
    public void DefinitionCopyRemapsAssociationsAndDoesNotRebuildDeletedMappings()
    {
        using var project = new MidoraProject(480);
        var instrument = new EventInstrument(project) { Name = "I", TemplateLengthTicks = 960 };
        var voice = new SubVoice(project) { Name = "V" }; instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        var create = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 100, new(1, 2, 3)).Prepare(project);
        try
        {
            create.Apply(project); var source = project.EventInstruments.Single();
            var sourceVoice = source.SubVoices.Single(); sourceVoice.EventMappings.Clear();
            var second = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(source.Id, sourceVoice.Id, 200, new(4, 5, 6)).Prepare(project);
            try
            {
                second.Apply(project); source = project.EventInstruments.Single(); sourceVoice = source.SubVoices.Single();
                Assert.Empty(sourceVoice.EventMappings);
                var copy = EventInstrumentLibrary.CopyInto(project, source);
                var copiedVoice = copy.SubVoices.Single();
                Assert.Equal(2, copiedVoice.InstrumentChanges.Count);
                foreach (var group in copiedVoice.InstrumentChanges.Values)
                {
                    Assert.False(sourceVoice.InstrumentChanges.TryGet(group.Id, out _));
                    Assert.True(InstrumentChangeResolver.TryRead(copiedVoice, group, out _));
                }
            }
            finally { (second as IDisposable)?.Dispose(); }
        }
        finally { (create as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void NewMidiGroupPrecedesItsNoteOnAndMetadataDoesNotChangeCanonicalMusic()
    {
        using var project = new MidoraProject(480); var id = AddMidi(project);
        Midi(project).Notes.Add(new DirectMidiNote(project) { StartTick = 100, LengthTicks = 100, Key = 60,
            NoteOnVelocity = 100, NoteOnOrder = 0, NoteOffOrder = 0 });
        var edit = ProjectDomainEditCommands.SetMidiInstrumentChange(id, 100, new(4, 5, 6)).Prepare(project);
        try
        {
            edit.Apply(project);
            using var compiler = new Midora.Compiler.MidoraCompiler();
            var first = compiler.CompileFull(project); Assert.True(first.IsConsumable);
            var messages = first.QueryEventPages(100, 101).SelectMany(page => page.Items).Select(e => e.Message).ToArray();
            int note = Array.FindIndex(messages, m => m.MessageType == Midora.Midi.MidiMessageType.NoteOn);
            int program = Array.FindIndex(messages, m => m.MessageType == Midora.Midi.MidiMessageType.ProgramChange && m.Byte1 == 6);
            Assert.True(program >= 0 && note > program);
            Midi(project).InstrumentChanges = InstrumentChangeSet.Empty;
            var second = compiler.CompileFull(project);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal(first.QueryEventPages(0, first.EndTick).SelectMany(p => p.Items),
                second.QueryEventPages(0, second.EndTick).SelectMany(p => p.Items));
        }
        finally { (edit as IDisposable)?.Dispose(); }
    }
    [Fact]
    public void SameTickExtremeOrderRebasePreservesOldOrderAndUndo()
    {
        using var project = new MidoraProject(480);
        var id = AddMidi(project); var owner = Midi(project);
        owner.Notes.Add(new DirectMidiNote(project) { StartTick = 100, LengthTicks = 10, Key = 60,
            NoteOnVelocity = 100, NoteOnOrder = long.MaxValue, NoteOffOrder = long.MaxValue });
        owner.ChannelEvents.Add(new DirectMidiChannelEvent(project) { Tick = 100,
            Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 90, Order = long.MaxValue - 1 });
        var edit = ProjectDomainEditCommands.SetMidiInstrumentChange(id, 100, new(0, 0, 0)).Prepare(project);
        try
        {
            edit.Apply(project);
            var expression = Midi(project).ChannelEvents.CreateQuerySnapshot().QueryValues(100, 101).Single(e => e.Data1 == 11);
            var note = Midi(project).Notes.CreateQuerySnapshot().QueryValues(100, 101).Single();
            Assert.True(expression.Order > 2 && expression.Order < note.NoteOnOrder);
            Assert.Equal(long.MaxValue, note.NoteOffOrder);
            edit.Undo(project);
            Assert.Equal(long.MaxValue, Midi(project).Notes.CreateQuerySnapshot().QueryValues(100, 101).Single().NoteOnOrder);
        }
        finally { (edit as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void MidiSplitKeepsEachGroupWithItsMembersAndUndoRestoresIdentity()
    {
        using var project = new MidoraProject(480); var id = AddMidi(project);
        var create = ProjectDomainEditCommands.SetMidiInstrumentChange(id, 200, new(1, 2, 3)).Prepare(project);
        try
        {
            create.Apply(project); var original = Assert.Single(Midi(project).InstrumentChanges.Values);
            var split = InstrumentChangeMaintenance.Wrap(project, ProjectDomainEditCommands.SplitMidiSegment(id, 150).Prepare(project));
            try
            {
                split.Apply(project);
                var owners = project.PureMidiTracks[0].Segments;
                Assert.Equal(2, owners.Count); Assert.Empty(owners[0].InstrumentChanges.Values);
                var group = Assert.Single(owners[1].InstrumentChanges.Values);
                Assert.Equal(original, group);
                Assert.True(InstrumentChangeResolver.TryRead(owners[1], group, out var value));
                Assert.Equal(200, value.Tick); Assert.Equal(50, value.Tick - owners[1].ContentOffsetTick);
                split.Undo(project); Assert.Equal(original, Assert.Single(Midi(project).InstrumentChanges.Values));
            }
            finally { (split as IDisposable)?.Dispose(); }
        }
        finally { (create as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void DisplayProjectionIsPixelBoundedAndHandlesExtremeTicks()
    {
        var groups = InstrumentChangeSet.Empty;
        for (int i = 1; i <= 20_000; i++) groups = groups.Add(new(new(i * 4L), new(i * 4L + 1),
            new MidoraId(i * 4L + 2), new(i * 4L + 3)), true);
        using var projection = InstrumentChangeProjection.Create(groups,
            group => new(group.Id, group.Id.Value, 0, 0, 0), default);
        Assert.InRange(projection.ReadVisible(0, 100_000, 640, default).Length, 1, 641);
        Assert.Empty(projection.ReadVisible(long.MaxValue - 50, long.MaxValue, 640, default));
        Assert.InRange(projection.ReadVisible(0, long.MaxValue, 640, default).Length, 1, 641);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InstrumentChangeProjection.Create(groups, _ => null, canceled.Token));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubVoiceExistingRawCollisionAndWholeGroupMoveAreAtomic(bool move)
    {
        using var project = new MidoraProject(480);
        var instrument = new EventInstrument(project) { Name = "Instrument", TemplateLengthTicks = 960 };
        var voice = new SubVoice(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        Apply(ProjectDomainEditCommands.CreateTemplateBank(instrument.Id, voice.Id, 100, 7, null));
        Apply(ProjectDomainEditCommands.CreateTemplateProgram(instrument.Id, voice.Id, 100, 8));
        Apply(ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 100, new(1, 2, 3)));
        var group = Assert.Single(Voice(project).InstrumentChanges.Values);
        Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out var before));
        Assert.Equal(2, Voice(project).Events.Count);
        var edit = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id,
            move ? 200 : 100, new(4, 5, 6), group.Id).Prepare(project);
        try
        {
            edit.Apply(project);
            Assert.Equal(group, Assert.Single(Voice(project).InstrumentChanges.Values));
            Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out var after));
            Assert.Equal(move ? 200 : 100, after.Tick); Assert.Equal(6, after.Program);
            edit.Undo(project);
            Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out after)); Assert.Equal(before, after);
        }
        finally { (edit as IDisposable)?.Dispose(); }
        void Apply(IProjectEditCommand command)
        { var prepared = InstrumentChangeMaintenance.Wrap(project, ExactTimelineCollisionPolicy.Wrap(project, command.Prepare(project)));
          try { prepared.Apply(project); } finally { (prepared as IDisposable)?.Dispose(); } }
    }

    [Fact]
    public void PresetAuditionIsConsumableUsesCleanStateAndExactHalfSecondGate()
    {
        var result = Midora.Compiler.InstrumentPresetPreviewCompiler.Compile(new(4, 5, 6));
        Assert.True(result.IsConsumable, string.Join("; ", result.Diagnostics.Select(value => value.Message)));
        Assert.Equal(1000, result.TicksPerQuarterNote);
        Assert.Equal(1, result.TotalNoteOnEventCount);
        var events = result.QueryEventPages(0, result.EndTick).SelectMany(page => page.Items).ToArray();
        Assert.Contains(events, value => value.Tick == 1000 && value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOff);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(127, 127, 127)]
    public void MidiCreateIsDetachedOrderedAndUndoable(byte msb, byte lsb, byte program)
    {
        using var project = new MidoraProject(480);
        var segmentId = AddMidi(project);
        var owner = Midi(project);
        var note = new DirectMidiNote(project) { StartTick = 100, LengthTicks = 10, Key = 60,
            NoteOnVelocity = 100, NoteOnOrder = 0, NoteOffOrder = 1 };
        owner.Notes.Add(note);
        long before = project.NextStableId;
        var edit = ProjectDomainEditCommands.SetMidiInstrumentChange(segmentId, 100, new(msb, lsb, program)).Prepare(project);
        try
        {
            Assert.Equal(before, project.NextStableId);
            Assert.Equal(0, Midi(project).InstrumentChanges.Count);
            edit.Apply(project);
            var group = Assert.Single(Midi(project).InstrumentChanges.Values);
            Assert.True(InstrumentChangeResolver.TryRead(Midi(project), group, out var value));
            Assert.Equal(new InstrumentChangeValue(group.Id, 100, msb, lsb, program, 2,
                group.BankEventId, group.BankLsbEventId, group.ProgramEventId), value);
            var events = Midi(project).ChannelEvents.CreateQuerySnapshot().QueryValues(100, 101).OrderBy(e => e.Order).ToArray();
            Assert.Equal(new[] { 0L, 1L, 2L }, events.Select(e => e.Order));
            Assert.True(Midi(project).Notes.CreateQuerySnapshot().QueryValues(100, 101).Single().NoteOnOrder > events[^1].Order);
            edit.Undo(project);
            Assert.Empty(Midi(project).InstrumentChanges.Values);
            Assert.Equal(0, Midi(project).Notes.CreateQuerySnapshot().QueryValues(100, 101).Single().NoteOnOrder);
            edit.Apply(project);
            Assert.Equal(group, Assert.Single(Midi(project).InstrumentChanges.Values));
        }
        finally { (edit as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void SubVoiceCreateAndValueEditKeepMembersAndUndo()
    {
        using var project = new MidoraProject(480);
        var instrument = new EventInstrument(project) { Name = "Instrument", TemplateLengthTicks = 960 };
        var voice = new SubVoice(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        var create = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 100, new(1, 2, 3)).Prepare(project);
        try
        {
            create.Apply(project);
            var group = Assert.Single(Voice(project).InstrumentChanges.Values);
            Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out var original));
            var edit = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 100, new(4, 5, 6), group.Id).Prepare(project);
            try
            {
                edit.Apply(project);
                Assert.Equal(group, Assert.Single(Voice(project).InstrumentChanges.Values));
                Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out var value));
                Assert.Equal(6, value.Program);
                edit.Undo(project);
                Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out value));
                Assert.Equal(original, value);
            }
            finally { (edit as IDisposable)?.Dispose(); }
            create.Undo(project);
            Assert.Empty(Voice(project).InstrumentChanges.Values);
        }
        finally { (create as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void PartialMemberMoveDissolvesButRetainsRawAndUndoRestores()
    {
        using var project = new MidoraProject(480);
        var segmentId = AddMidi(project);
        var create = ProjectDomainEditCommands.SetMidiInstrumentChange(segmentId, 100, new(1, 2, 3)).Prepare(project);
        try
        {
            create.Apply(project);
            var group = Assert.Single(Midi(project).InstrumentChanges.Values);
            var command = ProjectDomainEditCommands.AdjustDirectMidiEventPoints(segmentId, [group.ProgramEventId], 10, 0, 0, false);
            var edit = InstrumentChangeMaintenance.Wrap(project, command.Prepare(project));
            try
            {
                edit.Apply(project);
                Assert.Empty(Midi(project).InstrumentChanges.Values);
                Assert.Equal(3, Midi(project).ChannelEvents.Count);
                edit.Undo(project);
                Assert.Equal(group, Assert.Single(Midi(project).InstrumentChanges.Values));
            }
            finally { (edit as IDisposable)?.Dispose(); }
        }
        finally { (create as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void ConsecutiveRawMemberMovesValidateOnlyTheFinalTransaction()
    {
        using var project = new MidoraProject(480);
        var segmentId = AddMidi(project);
        var create = ProjectDomainEditCommands.SetMidiInstrumentChange(segmentId, 100, new(1, 2, 3)).Prepare(project);
        try
        {
            create.Apply(project);
            var group = Assert.Single(Midi(project).InstrumentChanges.Values);
            var move = new SequentialProjectEditCommand("Move all members",
                group.MemberIds.Select(id => new Func<MidoraProject, IProjectEditCommand>(_ =>
                    ProjectDomainEditCommands.AdjustDirectMidiEventPoints(segmentId, [id], 20, 0, 0, false))));
            var prepared = move.Prepare(project);
            try
            {
                prepared.Apply(project);
                Assert.Equal(group, Assert.Single(Midi(project).InstrumentChanges.Values));
                Assert.True(InstrumentChangeResolver.TryRead(Midi(project), group, out var value));
                Assert.Equal(120, value.Tick);
                Assert.Empty(Midi(project).InstrumentChanges.PendingReconciliation);
                prepared.Undo(project);
                Assert.True(InstrumentChangeResolver.TryRead(Midi(project), group, out value));
                Assert.Equal(100, value.Tick);
            }
            finally { (prepared as IDisposable)?.Dispose(); }
        }
        finally { (create as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void SubVoiceWholeMoveKeepsGroupButPartialBankDissolvesWithoutDeletingRaw()
    {
        using var project = new MidoraProject(480);
        var instrument = new EventInstrument(project) { Name = "I", TemplateLengthTicks = 960 };
        var voice = new SubVoice(project) { Name = "V" };
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        var create = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 100, new(1, 2, 3)).Prepare(project);
        try
        {
            create.Apply(project);
            var group = Assert.Single(Voice(project).InstrumentChanges.Values);
            var move = ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 200, new(3, 4, 5), group.Id).Prepare(project);
            try
            {
                move.Apply(project);
                Assert.Equal(group, Assert.Single(Voice(project).InstrumentChanges.Values));
                var partial = InstrumentChangeMaintenance.Wrap(project,
                    ProjectDomainEditCommands.UpdateTemplateBank(instrument.Id, voice.Id, group.BankEventId, 200, 3, null).Prepare(project));
                try
                {
                    partial.Apply(project);
                    Assert.Empty(Voice(project).InstrumentChanges.Values);
                    Assert.Equal(2, Voice(project).Events.Count);
                    partial.Undo(project);
                    Assert.Equal(group, Assert.Single(Voice(project).InstrumentChanges.Values));
                }
                finally { (partial as IDisposable)?.Dispose(); }
                move.Undo(project);
                Assert.True(InstrumentChangeResolver.TryRead(Voice(project), group, out var value));
                Assert.Equal(100, value.Tick);
            }
            finally { (move as IDisposable)?.Dispose(); }
        }
        finally { (create as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void ParentClipboardCopiesRemapGroupsAndUndoRedoKeepsThem()
    {
        using var project = new MidoraProject(480);
        var segmentId = AddMidi(project);
        var instrument = EventInstrumentLibrary.Create(project, "I");
        var voice = instrument.SubVoices[0]; instrument.TemplateLengthTicks = 960;
        using var compilation = new Midora.Playback.ProjectCompilationSession(project);
        var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(ProjectDomainEditCommands.SetMidiInstrumentChange(segmentId, 100, new(1, 2, 3)));
        document.Execute(ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 200, new(4, 5, 6)));
        var midiGroup = Assert.Single(Midi(project).InstrumentChanges.Values);
        using var midi = ProjectObjectClipboard.CopyMidiSegments(document, [segmentId], segmentId);
        document.Execute(ProjectObjectClipboard.CreatePasteMidiSegmentsCommand(document, midi, project.PureMidiTracks[0].Id, 2000));
        var copied = project.PureMidiTracks[0].Segments.Single(s => s.Id != segmentId);
        var copiedGroup = Assert.Single(copied.InstrumentChanges.Values);
        Assert.NotEqual(midiGroup.Id, copiedGroup.Id);
        Assert.False(midiGroup.MemberIds.Intersect(copiedGroup.MemberIds).Any());
        Assert.True(InstrumentChangeResolver.TryRead(copied, copiedGroup, out var value));
        Assert.Equal(3, value.Program);
        document.Undo(); Assert.Single(project.PureMidiTracks[0].Segments);
        document.Redo(); Assert.Equal(copiedGroup, Assert.Single(project.PureMidiTracks[0].Segments.Single(s => s.Id == copied.Id).InstrumentChanges.Values));
        var voiceGroup = Assert.Single(Voice(project).InstrumentChanges.Values);
        using var subVoice = ProjectObjectClipboard.CopySubVoice(document, instrument.Id, voice.Id);
        document.Execute(ProjectObjectClipboard.CreatePasteSubVoiceCommand(document, subVoice, instrument.Id));
        var copiedVoice = project.EventInstruments[0].SubVoices.Single(v => v.Id != voice.Id);
        var copiedVoiceGroup = Assert.Single(copiedVoice.InstrumentChanges.Values);
        Assert.NotEqual(voiceGroup.Id, copiedVoiceGroup.Id);
        Assert.False(voiceGroup.MemberIds.Intersect(copiedVoiceGroup.MemberIds).Any());
        Assert.True(InstrumentChangeResolver.TryRead(copiedVoice, copiedVoiceGroup, out value));
        Assert.Equal(6, value.Program);
        document.Undo(); Assert.Single(project.EventInstruments[0].SubVoices);
        document.Redo(); Assert.Equal(copiedVoiceGroup, Assert.Single(project.EventInstruments[0].SubVoices.Single(v => v.Id == copiedVoice.Id).InstrumentChanges.Values));
    }

    [Fact]
    public void InitialStateDraftPreservesInheritanceAndOtherControllers()
    {
        var state = new MidiInitialState { BankMsb = 8, Program = null };
        state.Controllers[7] = 76;
        var draft = InstrumentSelectionValues.From(state);
        Assert.Equal(new InstrumentAddress(8, 3, 4), draft.Resolve(new(2, 3, 4)));
        (draft with { Program = 9 }).ApplyTo(state);
        Assert.Null(state.BankLsb);
        Assert.Equal(76, state.Controllers[7]);
        Assert.Equal(9, state.Program);
    }

    private static MidoraId AddMidi(MidoraProject project)
    {
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "Track", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { LengthTicks = 1000 };
        track.Segments.Add(segment); project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return segment.Id;
    }
    private static MidiSegment Midi(MidoraProject project) => project.PureMidiTracks[0].Segments[0];
    private static SubVoice Voice(MidoraProject project) => project.EventInstruments[0].SubVoices[0];
}
