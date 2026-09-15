using Midora.Application;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class EventInstrumentPreRollPropertiesTests
{
    [Fact]
    public async Task PropertiesCommitTimingAndLoopAsOneFinalTuple()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Pre-Roll Properties",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentIsolation(
            instrument.Id,
            requiresChannelIsolation: true));
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentTiming(
            instrument.Id,
            templateLengthTicks: 480,
            preRollTicks: 360));
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            loopStartTick: 0,
            loopEndTick: 480));
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentIsolation(
            instrument.Id,
            requiresChannelIsolation: false));
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Clear();
        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);
        PropertyField templateLength = properties.Fields.Single(field =>
            field.Key == "instrument.templateLength");
        PropertyField preRoll = properties.Fields.Single(field =>
            field.Key == "instrument.preRollTicks");
        PropertyField loopStart = properties.Fields.Single(field =>
            field.Key == "instrument.loopStart");
        PropertyField loopEnd = properties.Fields.Single(field =>
            field.Key == "instrument.loopEnd");
        PropertyField isolation = properties.Fields.Single(field =>
            field.Key == "instrument.isolation");
        Assert.Contains("0–480", preRoll.Label, StringComparison.Ordinal);

        templateLength.Value = "240";
        preRoll.Value = "120";
        loopStart.Value = "10";
        loopEnd.Value = "240";
        isolation.Value = bool.TrueString;
        int historyCount = session.Document!.History.Count;

        session.ApplyObjectProperties(
            workspace,
            [templateLength, preRoll, loopStart, loopEnd, isolation]);

        EventInstrument published = Assert.Single(session.Project.EventInstruments);
        Assert.Equal(instrument.Id, published.Id);
        Assert.Equal(240, published.TemplateLengthTicks);
        Assert.Equal(120, published.PreRollTicks);
        Assert.Equal(10, published.LoopStartTick);
        Assert.Equal(240, published.LoopEndTick);
        Assert.True(published.RequiresChannelIsolation);
        Assert.Equal(historyCount + 1, session.Document.History.Count);

        session.Document.Undo();

        Assert.Same(instrument, Assert.Single(session.Project.EventInstruments));
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.Equal(360, instrument.PreRollTicks);
        Assert.Equal(0, instrument.LoopStartTick);
        Assert.Equal(480, instrument.LoopEndTick);
        Assert.False(instrument.RequiresChannelIsolation);
        session.Document.Redo();
        Assert.Same(published, Assert.Single(session.Project.EventInstruments));
    }
}
