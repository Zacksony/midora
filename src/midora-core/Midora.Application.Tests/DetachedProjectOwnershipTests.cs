using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class DetachedProjectOwnershipTests
{
    [Fact]
    public void PublishedMirrorForwardsLazyMappingIdentitiesAndReleasesItsCatalogs()
    {
        using MidoraProject project = new(480);
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        MidoraProject? captured = null;
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var command = new SequentialProjectEditCommand("Rename", [draft =>
        {
            captured = draft;
            return new RenameInstrument(instrument.Id, "Changed");
        }]);
        var prepared = command.Prepare(project);
        Assert.Same(instrument, project.EventInstruments[0]);
        Assert.Empty(captured!.EventInstruments);
        Assert.Empty(captured.Conductor.Tempos);
        prepared.Apply(project);
        var current = project.EventInstruments[0];
        Assert.NotSame(instrument, current);
        long next = project.NextStableId;
        var mapping = current.SubVoices[0].GetOrCreateEventMapping(
            TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)));
        Assert.Equal(MidoraId.FromSequence(next), mapping.Steps.Id);
        Assert.True(project.NextStableId > next);
        Assert.Equal(project.NextStableId, captured.NextStableId);
        prepared.Undo(project);
        Assert.Same(instrument, project.EventInstruments[0]);
        prepared.Apply(project);
        Assert.Same(current, project.EventInstruments[0]);
    }

    [Fact]
    public void ConductorSnapshotSharesImmutableRecordsButNotEditableDirectoriesOrEndMarker()
    {
        using MidoraProject project = new(480);
        project.SetEndMarker(1920);
        using var snapshot = ProjectCompilationSnapshot.Create(project);
        Assert.NotSame(project.Conductor.Tempos, snapshot.Conductor.Tempos);
        Assert.Same(project.Conductor.Tempos[0], snapshot.Conductor.Tempos[0]);
        snapshot.Conductor.Tempos[0] = snapshot.Conductor.Tempos[0] with { BeatsPerMinute = 140 };
        snapshot.Conductor.EndMarker!.Tick = 960;
        Assert.Equal(120m, project.Conductor.Tempos[0].BeatsPerMinute);
        Assert.Equal(1920, project.Conductor.EndMarkerTick);
    }

    private sealed class RenameInstrument(MidoraId id, string name) : IProjectEditCommand
    {
        public string Name => "Rename";
        public IPreparedProjectEdit Prepare(MidoraProject project) => new Edit(project.EventInstruments.Single(i => i.Id == id), name);
    }
    private sealed class Edit(EventInstrument instrument, string name) : IPreparedProjectEdit
    {
        private readonly string _old = instrument.Name;
        public bool HasChanges => true;
        public ProjectChangeSet Changes { get; } = new() { EventInstrumentIds = { instrument.Id } };
        public void Apply(MidoraProject project) => instrument.Name = name;
        public void Undo(MidoraProject project) => instrument.Name = _old;
    }
}
