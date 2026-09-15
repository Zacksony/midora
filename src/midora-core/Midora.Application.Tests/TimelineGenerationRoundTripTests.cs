using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class TimelineGenerationRoundTripTests
{
    [Theory]
    [InlineData("DirectNote")]
    [InlineData("LogicalNote")]
    [InlineData("TemplateNote")]
    [InlineData("DirectEvent")]
    [InlineData("LogicalPoint")]
    [InlineData("TemplateEvent")]
    public async Task GeneratedRootsPublishCompileUndoRedoAndRoundTripThroughFormat3(string kind)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "generation-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using MidoraProject project = new(480);
        try
        {
            Fixture fixture = CreateFixture(project, kind);
            using MidoraCompiler beforeCompiler = new();
            CanonicalCompiledResult before = beforeCompiler.CompileFull(project);
            Assert.True(before.IsConsumable, string.Join("\n", before.Diagnostics));
            SourceValue[] original = ReadSource(project, kind);
            using ProjectCompilationSession compilation = new(project);
            using ProjectDocumentSession document = new(compilation);
            using StagedProjectEdit prepared = document.PrepareEdit(fixture.Command);
            Assert.NotNull(prepared.PreparedSelection);
            Assert.Equal(kind.EndsWith("Note", StringComparison.Ordinal) ? 3 : 4,
                prepared.PreparedSelection.ResultSelectionIds.Count);
            Assert.Equal(original, ReadSource(project, kind));

            Assert.True(document.ExecutePrepared(prepared).Changed);
            Assert.Single(document.History);
            using MidoraCompiler fullCompiler = new();
            CanonicalCompiledResult full = fullCompiler.CompileFull(project);
            Assert.True(full.IsConsumable, string.Join("\n", full.Diagnostics));
            Assert.True(full.TotalNoteOnEventCount > 0);
            if (kind.EndsWith("Note", StringComparison.Ordinal))
                Assert.Contains(Events(full), value => value.Tick == 60
                    && value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 60);
            else
                Assert.Contains(Events(full), value => value.Tick == 24
                    && value.Message.MessageType == MidiMessageType.ControlChange
                    && value.Message.Byte1 == 11 && value.Message.Byte2 == 29);
            AssertCanonicalEqual(full, compilation.LastAttempt, sameRepresentation: true);
            SourceValue[] expected = ReadSource(project, kind);
            Assert.NotEqual(original, expected);

            document.Undo();
            Assert.Equal(original, ReadSource(project, kind));
            AssertCanonicalEqual(before, compilation.LastAttempt, sameRepresentation: true);
            document.Redo();
            Assert.Equal(expected, ReadSource(project, kind));
            AssertCanonicalEqual(full, compilation.LastAttempt, sameRepresentation: true);

            var packages = new MidoraProjectPackageV1("1.0.0-test");
            string path = Path.Combine(directory, "generated.midora");
            await packages.SaveCopyAsync(project, path);
            await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(path);
            Assert.Equal(4, reopened.SourceFileFormatVersion);
            Assert.Empty(reopened.Diagnostics);
            Assert.Equal(expected, ReadSource(reopened.Project, kind));
            using MidoraCompiler restoredCompiler = new();
            CanonicalCompiledResult restored = restoredCompiler.CompileFull(reopened.Project);
            // Direct source+delta storage is flattened into a content pack by
            // Save. Its source-aware cache fingerprint may change, but every
            // formal event, allocation, source identity and count must not.
            AssertCanonicalEqual(full, restored,
                sameRepresentation: !kind.StartsWith("Direct", StringComparison.Ordinal));
        }
        finally
        {
            project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Fixture CreateFixture(MidoraProject project, string kind)
    {
        NoteGenerationOptions notes = new()
        {
            MaximumCandidates = 8,
            TickExpression = "=i<4?60-i*12:24",
            KeyExpression = "=60",
            VelocityExpression = "=60+k1%12",
            GateExpression = "=v1/20"
        };
        EventGenerationOptions points = new()
        {
            MaximumCandidates = 8,
            TickExpression = "=i<4?60-i*12:24",
            ValueExpression = "=Round(20+t1/12+i)"
        };
        if (kind.StartsWith("Direct", StringComparison.Ordinal))
        {
            MidiChannelRoot root = new(project) { Name = "Root" };
            PureMidiTrack track = new(project) { Name = "Direct", MidiChannelRootId = root.Id };
            MidiSegment segment = new(project) { LengthTicks = 240 };
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            track.Segments.Add(segment);
            segment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 6, Key = 48,
                NoteOnVelocity = 80, NoteOffVelocity = 31, NoteOnOrder = 10, NoteOffOrder = 11 });
            if (kind == "DirectNote")
            {
                segment.Notes.Add(new(project) { StartTick = 24, LengthTicks = 6, Key = 60,
                    NoteOnVelocity = 99, NoteOffVelocity = 45, NoteOnOrder = 20, NoteOffOrder = 21 });
                return new(ProjectDomainEditCommands.GenerateDirectMidiNotes(segment.Id, notes));
            }
            ProjectDomainEditCommands.CreateDirectMidiChannelEvent(segment.Id, 24,
                DirectMidiChannelEventKind.ControlChange, 11, 99).Prepare(project).Apply(project);
            ProjectDomainEditCommands.CreateDirectMidiChannelEvent(segment.Id, 8,
                DirectMidiChannelEventKind.ControlChange, 7, 80).Prepare(project).Apply(project);
            return new(ProjectDomainEditCommands.GenerateDirectMidiEventPoints(
                segment.Id, DirectMidiChannelEventKind.ControlChange, 11, points));
        }

        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.TemplateLengthTicks = 240;
        instrument.OverlapPolicy = OverlapPolicy.Warn;
        SubVoice voice = instrument.SubVoices[0];
        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logical = new(project) { LengthTicks = 480 };
        logicalTrack.Segments.Add(logical);
        if (kind == "LogicalNote")
        {
            voice.Events.Add(TemplateEvent.Note(project, 0, 4, 60, 80));
            logical.Notes.Add(new(project) { StartTick = 0, LengthTicks = 6, Note = 48, Velocity = 80 });
            logical.Notes.Add(new(project) { StartTick = 24, LengthTicks = 6, Note = 60, Velocity = 99 });
            return new(ProjectDomainEditCommands.GenerateLogicalNotes(logical.Id, notes));
        }

        logical.Notes.Add(new(project) { StartTick = 0, LengthTicks = 240, Note = 60, Velocity = 80 });
        if (kind == "TemplateNote")
        {
            voice.Events.Add(TemplateEvent.Note(project, 0, 4, 48, 80));
            voice.Events.Add(TemplateEvent.Note(project, 24, 4, 60, 99));
            return new(ProjectDomainEditCommands.GenerateTemplateNotes(instrument.Id, voice.Id, notes));
        }
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 80));
        if (kind == "TemplateEvent")
        {
            voice.Events.Add(TemplateEvent.ControlChange(project, 24, 11, 99));
            voice.Events.Add(TemplateEvent.ControlChange(project, 8, 7, 80));
            return new(ProjectDomainEditCommands.GenerateTemplateEventPoints(
                instrument.Id, voice.Id, MidiValueTarget.ControlChange(11), points));
        }

        ProjectDomainEditCommands.CreateLogicalParameterEventBinding(instrument.Id, new()
        {
            Name = "Expression",
            Target = MidiValueTarget.ControlChange(11),
            Operation = LogicalParameterEventBindingOperation.Override,
            SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
            SourceMinimum = 0,
            SourceMaximum = 127
        }).Prepare(project).Apply(project);
        MidoraId parameterId = instrument.LogicalParameters.Single().Id;
        ProjectDomainEditCommands.CreateLogicalParameterLane(logical.Id, parameterId).Prepare(project).Apply(project);
        MidoraId laneId = logical.ParameterLanes.Single().Id;
        ProjectDomainEditCommands.CreateLogicalParameterPoint(logical.Id, laneId, 24, 99,
            CurveInterpolation.Step).Prepare(project).Apply(project);
        return new(ProjectDomainEditCommands.GenerateLogicalParameterPoints(logical.Id, laneId, points));
    }

    private static void AssertCanonicalEqual(CanonicalCompiledResult expected, CanonicalCompiledResult actual,
        bool sameRepresentation)
    {
        Assert.True(actual.IsConsumable, string.Join("\n", actual.Diagnostics));
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        if (sameRepresentation) Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.TotalEventCount, actual.TotalEventCount);
        Assert.Equal(expected.TotalNoteOnEventCount, actual.TotalNoteOnEventCount);
        Assert.Equal(Events(expected), Events(actual));
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.SmfTracks.ToArray(), actual.SmfTracks.ToArray());
        Assert.Equal(expected.Tempos.ToArray(), actual.Tempos.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private static CanonicalMidiEvent[] Events(CanonicalCompiledResult result) =>
        result.QueryEventPages(result.StartTick, result.EndTick).SelectMany(static page => page.Items).ToArray();

    private static SourceValue[] ReadSource(MidoraProject project, string kind)
    {
        IEnumerable<SourceValue> values = kind switch
        {
            "DirectNote" => project.PureMidiTracks.Single().Segments.Single().Notes.Select(value =>
                new SourceValue(value.Id, value.StartTick, value.LengthTicks, -1, value.Key, value.NoteOnVelocity,
                    value.NoteOffVelocity, false, false, false, value.NoteOnOrder, value.NoteOffOrder)),
            "DirectEvent" => project.PureMidiTracks.Single().Segments.Single().ChannelEvents.Select(value =>
                new SourceValue(value.Id, value.Tick, 0, (int)value.Kind, value.Data1, value.Data2,
                    0, false, false, false, value.Order, 0)),
            "LogicalNote" => project.Tracks.Single().Segments.Single().Notes.Select(value =>
                new SourceValue(value.Id, value.StartTick, value.LengthTicks, -1, value.Note, value.Velocity,
                    0, false, false, false, 0, 0)),
            "LogicalPoint" => project.Tracks.Single().Segments.Single().ParameterLanes.Single().Points.Select(value =>
                new SourceValue(value.Id, value.Tick, 0, (int)value.Interpolation, 0, value.Value,
                    0, false, false, false, 0, 0)),
            _ => project.EventInstruments.Single().SubVoices.Single().Events.Select(value =>
                new SourceValue(value.Id, value.Tick, value.LengthTicks, (int)value.Kind, value.Number, value.Value,
                    value.SecondaryValue, value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta, 0, 0))
        };
        return values.OrderBy(static value => value.Id.Value).ToArray();
    }

    private sealed record Fixture(IProjectEditCommand Command);
    private readonly record struct SourceValue(MidoraId Id, long Tick, long Gate, int Kind, int Number,
        double Value, int Secondary, bool HasBankMsb, bool HasBankLsb, bool FollowPitchDelta, long Order, long EndOrder);
}
