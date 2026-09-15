using System.Collections;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectClipboardPasteTargetTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompatibleNotePasteFreezesDestinationRatherThanPayloadType(
        bool sourceIsDirect, bool targetIsDirect)
    {
        using Fixture fixture = new();
        ProjectObjectClipboardKind kind = sourceIsDirect
            ? ProjectObjectClipboardKind.DirectMidiNotes : ProjectObjectClipboardKind.LogicalNotes;
        ProjectObjectClipboardData data = sourceIsDirect
            ? new DirectMidiNoteClipboardData([new(0, 24, 60, 90, 12, 0, 1, true)])
            : new LogicalNoteClipboardData([new(0, 24, 60, 90)]);
        using ProjectObjectClipboardPayload payload = fixture.Payload(kind, data);
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteNotesCommand(
            fixture.Document, payload, new(50), 120, targetIsDirect);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(new(kind, new(50), TargetIsDirectMidi: targetIsDirect), paste.PasteTarget);
            Assert.Equal(new(OnlyNotes: true), paste.GetPasteSelectionKinds());
        }
    }

    [Fact]
    public void LogicalParameterContentFreezesExactDestinationLane()
    {
        using Fixture fixture = new();
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.LogicalParameterLaneContent,
            new LogicalParameterLaneContentClipboardData(new(10), [new(0, 64, CurveInterpolation.Step)]));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteLogicalParameterLaneContentCommand(
            fixture.Document, payload, new(50), new(51), 120);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(new(ProjectObjectClipboardKind.LogicalParameterLaneContent, new(50), new(51)), paste.PasteTarget);
            Assert.Equal(new(OnlyEventPoints: true), paste.GetPasteSelectionKinds());
        }
    }

    [Theory]
    [InlineData(DirectMidiChannelEventKind.ControlChange, 11, 11, 11)]
    [InlineData(DirectMidiChannelEventKind.PolyphonicKeyPressure, 60, 60, 60)]
    [InlineData(DirectMidiChannelEventKind.ProgramChange, 12, 80, 0)]
    [InlineData(DirectMidiChannelEventKind.PitchBend, 10, 20, 0)]
    public void DirectEventSummaryUsesFormalLaneSelectorNotEventValue(
        DirectMidiChannelEventKind kind, int firstData1, int secondData1, int selector)
    {
        using Fixture fixture = new();
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.DirectMidiEvents,
            new DirectMidiEventClipboardData([new(0, kind, firstData1, 20, 0), new(24, kind, secondData1, 40, 1)]));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
            fixture.Document, payload, new(50), 120);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(new(ProjectObjectClipboardKind.DirectMidiEvents, new(50), TargetIsDirectMidi: true), paste.PasteTarget);
            Assert.Equal(new(OnlyEventPoints: true, DirectMidiEventKind: kind, DirectMidiData1: selector), paste.GetPasteSelectionKinds());
        }
    }

    [Fact]
    public void MixedDirectLanesRetainOnlyOwnerWideEventClassification()
    {
        using Fixture fixture = new();
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.DirectMidiEvents,
            new DirectMidiEventClipboardData([
                new(0, DirectMidiChannelEventKind.ControlChange, 11, 20, 0),
                new(24, DirectMidiChannelEventKind.ControlChange, 74, 40, 1)]));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
            fixture.Document, payload, new(50), 120);
        using (command as IDisposable)
            Assert.Equal(new(OnlyEventPoints: true),
                Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command).GetPasteSelectionKinds());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SubVoiceSummarySeparatesNotesSingleLaneMultiLaneAndMixedContent(int shape)
    {
        using Fixture fixture = new();
        TemplateEventClipboardSnapshot note = new(TemplateEventKind.Note, 0, 24, 60, 90, 0, false, false, false);
        TemplateEventClipboardSnapshot cc = new(TemplateEventKind.ControlChange, 24, 0, 11, 64, 0, false, false, false);
        TemplateEventClipboardSnapshot[] events = shape switch
        {
            0 => [note, note with { TickOffset = 24 }],
            1 => [cc, cc with { TickOffset = 48 }],
            2 => [cc, cc with { TickOffset = 48, Number = 74 }],
            3 => [note, cc],
            _ => [cc with { Kind = TemplateEventKind.Bank, HasBankMsb = true, HasBankLsb = true }]
        };
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.SubVoiceTimelineEvents,
            new SubVoiceTimelineEventsClipboardData(new(10), events));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            fixture.Document, payload, new(10), new(51), 120);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(new(ProjectObjectClipboardKind.SubVoiceTimelineEvents, new(10), new(51)), paste.PasteTarget);
            ProjectClipboardPasteSelectionKinds expected = shape switch
            {
                0 => new(OnlyNotes: true),
                1 => new(OnlyEventPoints: true, MidiTarget: MidiValueTarget.ControlChange(11)),
                2 or 4 => new(OnlyEventPoints: true),
                _ => default
            };
            Assert.Equal(expected, paste.GetPasteSelectionKinds());
        }
    }

    [Fact]
    public void UnsupportedOpaquePasteIsExplicitlyIdentifiedWithoutReadingPayloadBytes()
    {
        using Fixture fixture = new();
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.OpaqueMidiEvents,
            new OpaqueMidiEventClipboardData([]));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteOpaqueMidiEventsCommand(
            fixture.Document, payload, new(50), 120);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(ProjectObjectClipboardKind.OpaqueMidiEvents, paste.PasteTarget.Kind);
            Assert.Equal(default, paste.GetPasteSelectionKinds());
        }
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, true)]
    [InlineData(true, 3, true)]
    [InlineData(true, 0, false)]
    public void SubVoiceTargetHintMustBeExposedByEveryPhysicalEvent(
        bool pitchBendRange, int requestedKind, bool supported)
    {
        using Fixture fixture = new();
        TemplateEventClipboardSnapshot first = new(
            pitchBendRange ? TemplateEventKind.PitchBendRange : TemplateEventKind.Bank,
            0, 0, 0, 2, 0, !pitchBendRange, !pitchBendRange, false);
        TemplateEventClipboardSnapshot second = first with
        {
            TickOffset = 24,
            HasBankLsb = false
        };
        MidiValueTarget requested = requestedKind switch
        {
            0 => MidiValueTarget.BankMsb,
            1 => MidiValueTarget.BankLsb,
            2 => MidiValueTarget.PitchBendRangeSemitones,
            _ => MidiValueTarget.PitchBendRangeCents
        };
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.SubVoiceTimelineEvents,
            new SubVoiceTimelineEventsClipboardData(new(10), [first, second]));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            fixture.Document, payload, new(10), new(51), 120, requested);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(requested, paste.PasteTarget.MidiTarget);
            Assert.Equal(new(OnlyEventPoints: true, MidiTarget: supported ? requested : null),
                paste.GetPasteSelectionKinds());
        }
    }

    [Fact]
    public void SuccessfulPreparationCachesClassificationBeforeProjectPublication()
    {
        using Fixture fixture = new();
        fixture.Document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
        PureMidiTrack track = Assert.Single(fixture.Project.PureMidiTracks);
        fixture.Document.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 480));
        MidiSegment segment = Assert.Single(fixture.Project.PureMidiTracks[0].Segments);
        StreamingEvents values = new(4, _ => { });
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.DirectMidiEvents, new DirectMidiEventClipboardData(values));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
            fixture.Document, payload, segment.Id, 120);
        using (command as IDisposable)
        using (StagedProjectEdit staged = fixture.Document.PrepareEdit(command))
        {
            Assert.NotNull(staged.PreparedSelection);
            Assert.Empty(segment.ChannelEvents);
            int reads = values.Visited;
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(new(OnlyEventPoints: true,
                DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange,
                DirectMidiData1: 11), paste.GetPasteSelectionKinds());
            Assert.Equal(reads, values.Visited);
            fixture.Document.ExecutePrepared(staged);
            Assert.Equal(4, fixture.Project.PureMidiTracks[0].Segments[0].ChannelEvents.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SegmentPastePreparesOnlyCreatedTopLevelSelection(bool direct)
    {
        using Fixture fixture = new();
        ProjectDocumentSession document = fixture.Document;
        MidoraId trackId;
        MidoraId sourceSegmentId;
        if (direct)
        {
            document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
            trackId = Assert.Single(fixture.Project.PureMidiTracks).Id;
            document.Execute(ProjectDomainEditCommands.CreateMidiSegment(trackId, 0, 480));
            sourceSegmentId = Assert.Single(fixture.Project.PureMidiTracks[0].Segments).Id;
            document.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(sourceSegmentId, 24, 48, 60, 90));
        }
        else
        {
            document.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
            MidoraId instrumentId = Assert.Single(fixture.Project.EventInstruments).Id;
            document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrumentId));
            trackId = Assert.Single(fixture.Project.Tracks).Id;
            document.Execute(ProjectDomainEditCommands.CreateSegment(trackId, 0, 480));
            sourceSegmentId = Assert.Single(fixture.Project.Tracks[0].Segments).Id;
            document.Execute(ProjectDomainEditCommands.CreateLogicalNote(sourceSegmentId, 24, 48, 60, 90));
        }
        using ProjectObjectClipboardPayload payload = direct
            ? ProjectObjectClipboard.CopyMidiSegments(document, [sourceSegmentId], sourceSegmentId)
            : ProjectObjectClipboard.CopySegments(document, [sourceSegmentId], sourceSegmentId);
        IProjectEditCommand command = direct
            ? ProjectObjectClipboard.CreatePasteMidiSegmentsCommand(document, payload, trackId, 600)
            : ProjectObjectClipboard.CreatePasteSegmentsCommand(document, payload, trackId, 600);
        using (command as IDisposable)
        using (StagedProjectEdit staged = document.PrepareEdit(command))
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Equal(new(payload.Kind, trackId, TargetIsDirectMidi: direct), paste.PasteTarget);
            PreparedTimelineSelection selection = Assert.IsType<PreparedTimelineSelection>(staged.PreparedSelection);
            Assert.Empty(selection.OriginalSelectionIds);
            MidoraId resultId = Assert.Single(selection.ResultSelectionIds);
            Assert.NotEqual(sourceSegmentId, resultId);
            Assert.Equal(1, direct ? fixture.Project.PureMidiTracks[0].Segments.Count : fixture.Project.Tracks[0].Segments.Count);
            document.ExecutePrepared(staged);
            if (direct)
            {
                MidiSegment result = Assert.Single(fixture.Project.PureMidiTracks[0].Segments,
                    value => value.Id == resultId);
                Assert.NotEqual(resultId, Assert.Single(result.Notes).Id);
            }
            else
            {
                Segment result = Assert.Single(fixture.Project.Tracks[0].Segments,
                    value => value.Id == resultId);
                Assert.NotEqual(resultId, Assert.Single(result.Notes).Id);
            }
            document.Undo();
            Assert.Equal(1, direct ? fixture.Project.PureMidiTracks[0].Segments.Count : fixture.Project.Tracks[0].Segments.Count);
            document.Redo();
            Assert.Equal(2, direct ? fixture.Project.PureMidiTracks[0].Segments.Count : fixture.Project.Tracks[0].Segments.Count);
        }
    }

    [Fact]
    public void ClassificationStreamsOnceAndChecksCancellationWithoutPublishingPartialSummary()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        StreamingEvents values = new(1024, index =>
        {
            if (index == 2) cancellation.Cancel();
        });
        using ProjectObjectClipboardPayload payload = fixture.Payload(
            ProjectObjectClipboardKind.DirectMidiEvents, new DirectMidiEventClipboardData(values));
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
            fixture.Document, payload, new(50), 120);
        using (command as IDisposable)
        {
            IProjectClipboardPasteCommand paste = Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command);
            Assert.Throws<OperationCanceledException>(() => paste.GetPasteSelectionKinds(cancellation.Token));
            Assert.InRange(values.Visited, 3, 257);
            ProjectClipboardPasteSelectionKinds summary = paste.GetPasteSelectionKinds();
            Assert.Equal(new(OnlyEventPoints: true,
                DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange,
                DirectMidiData1: 11), summary);
            int readCount = values.Visited;
            payload.Dispose();
            Assert.Equal(summary, paste.GetPasteSelectionKinds());
            Assert.Equal(readCount, values.Visited);
            Assert.Throws<OperationCanceledException>(() => paste.GetPasteSelectionKinds(cancellation.Token));
        }
    }

    private sealed class StreamingEvents(int count, Action<int> onRead)
        : IReadOnlyList<DirectMidiEventClipboardSnapshot>
    {
        public int Count => count;
        public int Visited { get; private set; }
        public DirectMidiEventClipboardSnapshot this[int index] => throw new InvalidOperationException("Do not index the entire payload.");
        public IEnumerator<DirectMidiEventClipboardSnapshot> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                Visited++;
                onRead(index);
                yield return new(index, DirectMidiChannelEventKind.ControlChange, 11, 64, index);
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MidoraProject _project = new(480);
        private readonly ProjectCompilationSession _compilation;
        public Fixture()
        {
            _compilation = new(_project);
            Document = new(_compilation, ProjectDocumentOrigin.Persisted);
        }
        public ProjectDocumentSession Document { get; }
        public MidoraProject Project => _project;
        public ProjectObjectClipboardPayload Payload(ProjectObjectClipboardKind kind, ProjectObjectClipboardData data) =>
            new(Document.ClipboardSessionIdentity, kind, 1, "Test clipboard content", data);
        public void Dispose()
        {
            Document.Dispose();
            _compilation.Dispose();
            _project.Dispose();
        }
    }
}
