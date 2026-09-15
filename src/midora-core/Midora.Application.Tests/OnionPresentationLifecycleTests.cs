using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class OnionPresentationLifecycleTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MidiTrackDuplicateCopiesOnionTargetAndSurvivesHistory(bool staged)
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        var a = new PureMidiTrack(project) { Name = "A", MidiChannelRootId = root.Id };
        var b = new PureMidiTrack(project) { Name = "B", MidiChannelRootId = root.Id };
        foreach (var track in new[] { a, b })
        { project.PureMidiTracks.Add(track); project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id)); }
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Unsaved);
        var persistence = new ProjectPersistenceCoordinator(document, new MidoraProjectPackageV1("1.0.0-dev"));
        persistence.Presentation.Replace(new(ProjectPresentationAllTracksModeV3.Raw, [new(a.Id, true, .6, [b.Id], OnionSourceMode.Next)], []));
        var command = ProjectDomainEditCommands.DuplicatePureMidiTrack(a.Id);
        if (staged) { using var prepared = document.PrepareEdit(command); document.ExecutePrepared(prepared); }
        else document.Execute(command);
        var copy = Assert.Single(project.PureMidiTracks, t => t.Id != a.Id && t.Id != b.Id);
        var preset = Assert.Single(persistence.Presentation.Current.TrackOnionPresets, p => p.TargetTrackId == copy.Id);
        Assert.Equal(.6, preset.Opacity); Assert.Equal(b.Id, Assert.Single(preset.SourceTrackIds));
        Assert.Equal(OnionSourceMode.Next, preset.SourceMode);
        Assert.Single(document.History);
        document.Undo(); Assert.Single(persistence.Presentation.CreateSaveSnapshot(project).State.TrackOnionPresets);
        document.Redo(); Assert.Equal(2, persistence.Presentation.CreateSaveSnapshot(project).State.TrackOnionPresets.Count);
    }
    [Fact]
    public void SavingUsesFormalTrackAndVoiceOrderAndDoesNotForgetDormantSources()
    {
        using var project = new MidoraProject(192);
        var first = new LogicalTrack(project) { Name = "First" };
        var second = new LogicalTrack(project) { Name = "Second" };
        var target = new LogicalTrack(project) { Name = "Target" };
        foreach (var t in new[] { first, second, target }) ProjectGraphConstruction.AddUnboundLogicalTrack(project, t);
        var instrument = EventInstrumentLibrary.Create(project);
        var a = instrument.SubVoices[0]; var b = new SubVoice(project); var c = new SubVoice(project);
        instrument.SubVoices.Add(b); instrument.SubVoices.Add(c);
        var state = new ProjectPresentationSessionV3(new(ProjectPresentationAllTracksModeV3.Raw,
            [new(target.Id, true, .4, [second.Id, first.Id], OnionSourceMode.Previous)], [new(instrument.Id, c.Id, true, .5, [b.Id, a.Id], OnionSourceMode.Previous)]));
        var snapshot = state.CreateSaveSnapshot(project);
        Assert.Equal(new[] { first.Id, second.Id }, snapshot.State.TrackOnionPresets[0].SourceTrackIds);
        Assert.Equal(new[] { a.Id, b.Id }, snapshot.State.SubVoiceOnionPresets[0].SourceSubVoiceIds);
        project.ArrangementTracks.RemoveAt(0); project.Tracks.Remove(first); instrument.SubVoices.Remove(a);
        var missing = state.CreateSaveSnapshot(project);
        Assert.Equal(new[] { second.Id }, missing.State.TrackOnionPresets[0].SourceTrackIds);
        Assert.Equal(new[] { b.Id }, missing.State.SubVoiceOnionPresets[0].SourceSubVoiceIds);
        state.MarkSaveSucceeded(missing);
        ProjectGraphConstruction.AddUnboundLogicalTrack(project, first, 0); instrument.SubVoices.Insert(0, a);
        var restored = state.CreateSaveSnapshot(project);
        Assert.Equal(OnionSourceMode.Previous, restored.State.TrackOnionPresets[0].SourceMode);
        Assert.Equal(OnionSourceMode.Previous, restored.State.SubVoiceOnionPresets[0].SourceMode);
        Assert.Equal(snapshot.State.TrackOnionPresets[0].SourceTrackIds, restored.State.TrackOnionPresets[0].SourceTrackIds);
        Assert.Equal(snapshot.State.SubVoiceOnionPresets[0].SourceSubVoiceIds, restored.State.SubVoiceOnionPresets[0].SourceSubVoiceIds);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InstrumentDuplicateRemapsVoiceReferencesWithoutAddingHistory(bool staged)
    {
        using var project = new MidoraProject(192);
        var instrument = EventInstrumentLibrary.Create(project, "Source");
        instrument.SubVoices.Clear();
        var first = new SubVoice(project) { Name = "A" }; var second = new SubVoice(project) { Name = "B" };
        instrument.SubVoices.Add(first); instrument.SubVoices.Add(second);
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Unsaved);
        var persistence = new ProjectPersistenceCoordinator(document, new MidoraProjectPackageV1("1.0.0-dev"));
        persistence.Presentation.Replace(new(ProjectPresentationAllTracksModeV3.Compiled, [],
            [new(instrument.Id, first.Id, true, .35, [second.Id], OnionSourceMode.Next)]));
        Assert.Empty(document.History);
        var command = ProjectDomainEditCommands.DuplicateEventInstrument(instrument.Id);
        if (staged) { using var plan = document.PrepareEdit(command); document.ExecutePrepared(plan); }
        else document.Execute(command);
        var copy = Assert.Single(project.EventInstruments, i => i.Id != instrument.Id);
        var preset = Assert.Single(persistence.Presentation.Current.SubVoiceOnionPresets, p => p.EventInstrumentId == copy.Id);
        Assert.Equal(copy.SubVoices[0].Id, preset.TargetSubVoiceId);
        Assert.Equal(copy.SubVoices[1].Id, Assert.Single(preset.SourceSubVoiceIds));
        Assert.Equal(OnionSourceMode.Next, preset.SourceMode);
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal(2, persistence.Presentation.Current.SubVoiceOnionPresets.Count);
        Assert.Single(persistence.Presentation.CreateSaveSnapshot(project).State.SubVoiceOnionPresets);
        document.Redo();
        Assert.Equal(2, persistence.Presentation.CreateSaveSnapshot(project).State.SubVoiceOnionPresets.Count);
    }
    [Fact]
    public void ViewOnlySaveBaselineDoesNotConsumeNewerViewEdits()
    {
        using var project = new MidoraProject(192);
        var session = new ProjectPresentationSessionV3();
        session.Replace(session.Current with { AllTracksMode = ProjectPresentationAllTracksModeV3.Compiled });
        var frozen = session.CreateSaveSnapshot(project);
        session.Replace(session.Current with { AllTracksMode = ProjectPresentationAllTracksModeV3.Raw });
        session.MarkSaveSucceeded(frozen);
        Assert.True(session.IsModified);
        session.MarkSaveSucceeded(session.CreateSaveSnapshot(project)); Assert.False(session.IsModified);
    }
}
