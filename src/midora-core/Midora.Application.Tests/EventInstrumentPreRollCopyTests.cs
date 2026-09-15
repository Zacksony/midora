using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class EventInstrumentPreRollCopyTests
{
    [Fact]
    public void DuplicateEventInstrumentPreservesPreRollThroughUndoAndRedo()
    {
        MidoraProject project = new(480);
        EventInstrument source = EventInstrumentLibrary.Create(project, "Source");
        source.TemplateLengthTicks = 960;
        source.PreRollTicks = 240;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.DuplicateEventInstrument(
            source.Id,
            "Duplicate"));

        EventInstrument duplicate = Assert.Single(
            project.EventInstruments,
            value => value.Id != source.Id);
        Assert.Equal(240, duplicate.PreRollTicks);
        Assert.Equal(960, duplicate.TemplateLengthTicks);

        document.Undo();

        Assert.Same(source, Assert.Single(project.EventInstruments));

        document.Redo();

        Assert.Same(duplicate, Assert.Single(
            project.EventInstruments,
            value => value.Id == duplicate.Id));
        Assert.Equal(240, duplicate.PreRollTicks);
        Assert.Equal(960, duplicate.TemplateLengthTicks);
    }

    [Fact]
    public void EventInstrumentClipboardSnapshotsAndPastesPreRollThroughUndoAndRedo()
    {
        MidoraProject project = new(480);
        EventInstrument source = EventInstrumentLibrary.Create(project, "Source");
        source.TemplateLengthTicks = 960;
        source.PreRollTicks = 240;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyEventInstrument(
            document,
            source.Id);
        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(
            source.Id,
            480));

        document.Execute(ProjectObjectClipboard.CreatePasteEventInstrumentCommand(
            document,
            payload));

        EventInstrument pasted = Assert.Single(
            project.EventInstruments,
            value => value.Id != source.Id);
        Assert.Equal(480, source.PreRollTicks);
        Assert.Equal(240, pasted.PreRollTicks);
        Assert.Equal(960, pasted.TemplateLengthTicks);

        document.Undo();

        Assert.Same(source, Assert.Single(project.EventInstruments));

        document.Redo();

        Assert.Same(pasted, Assert.Single(
            project.EventInstruments,
            value => value.Id == pasted.Id));
        Assert.Equal(240, pasted.PreRollTicks);
        Assert.Equal(960, pasted.TemplateLengthTicks);
    }
}
