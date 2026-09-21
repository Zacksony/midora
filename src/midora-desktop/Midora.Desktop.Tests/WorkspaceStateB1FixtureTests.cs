using System.IO.Compression;
using Midora.Application;
using Midora.Domain;
using Midora.Persistence;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class WorkspaceStateB1FixtureTests
{
    [Fact]
    public async Task OptInGenerateB1AcceptanceProject()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_B1_UAT_FILE");
        if (string.IsNullOrEmpty(path)) return;
        using var project = CreateProject();
        var packages = new MidoraProjectPackageV1("1.0.0-dev-test");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await packages.SaveCopyAsync(project, path, overwriteAuthorized: false);
        var reopened = await packages.OpenAsync(path);
        using var restored = reopened.Project;
        Assert.Equal(2, restored.Tracks.Count); Assert.Equal(4, restored.PureMidiTracks.Count);
        Assert.Equal(2, restored.EventInstruments[0].SubVoices.Count);
        Assert.Equal(2, restored.Tracks[0].Segments.Count);
        Assert.Equal(2, restored.PureMidiTracks[0].Segments.Count);
    }

    [Fact]
    public async Task B1ViewChangesDoNotChangeSavedSourceOrPresentationBytes()
    {
        // SaveCopy has no session-time accumulator; both packages should therefore
        // contain identical music and the unchanged presentation schema 2 payload.
        using var project = CreateProject();
        var packages = new MidoraProjectPackageV1("1.0.0-dev-test", new FixedClock(DateTimeOffset.UtcNow));
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts", "b1-bytes-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            string before = Path.Combine(directory, "before.midora"), after = Path.Combine(directory, "after.midora");
            await packages.SaveCopyAsync(project, before);
            using var registry = new WorkspaceStateRegistry();
            using var view = new TimelineWorkspaceViewModel(
                Midora.Desktop.Presentation.Interaction.WorkspaceKey.ForObject(Midora.Desktop.Presentation.Interaction.WorkspaceKind.SegmentEditor, project.Tracks[0].Segments[0].Id),
                "B1", TimelineWorkspaceMode.Segment);
            view.EditorState = new(view, registry); view.Rebuild(project, 1);
            view.StartTick = 331; view.TickSpan = 1600; view.EditorSettings.SnapEnabled = false;
            view.LaneEditorSettings.OperationSubdivisionText = "1/7"; view.ObjectList.IsVisible = true;
            view.ObjectList.LayoutWidth = 520; view.ObjectList.FirstRow = 5; view.IsLowerEditorVisible = false;
            await packages.SaveCopyAsync(project, after);
            using var left = ZipFile.OpenRead(before); using var right = ZipFile.OpenRead(after);
            Assert.Equal(left.Entries.Select(x => x.FullName), right.Entries.Select(x => x.FullName));
            foreach (var entry in left.Entries)
            {
                using var a = new MemoryStream(); using var b = new MemoryStream();
                using (var input = entry.Open()) input.CopyTo(a);
                using (var input = right.GetEntry(entry.FullName)!.Open()) input.CopyTo(b);
                Assert.True(a.ToArray().AsSpan().SequenceEqual(b.ToArray()), entry.FullName);
            }
        }
        finally
        {
            string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(allowed, directory, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }

    private static MidoraProject CreateProject()
    {
        var project = new MidoraProject(192); project.Metadata.ProjectName = "B1 Workspace State";
        var instrument = EventInstrumentLibrary.Create(project, "B1 Instrument - two SubVoices");
        instrument.TemplateLengthTicks = 768;
        instrument.SubVoices[0].Name = "Voice A";
        instrument.SubVoices.Add(new(project) { Name = "Voice B" });
        foreach (var voice in instrument.SubVoices)
        {
            for (int i = 0; i < 8; i++) voice.Events.Add(new(project)
            { Kind = TemplateEventKind.Note, Tick = i * 96, LengthTicks = 48, Number = 60 + i, Value = 90 });
            voice.Events.Add(new(project) { Kind = TemplateEventKind.ControlChange, Number = 11, Tick = 0, Value = 90 });
            voice.Events.Add(new(project) { Kind = TemplateEventKind.PitchBend, Tick = 192, Value = -1000 });
        }
        var parameter = new LogicalParameterDefinition(project)
        { Name = "B1 Logical parameter", Minimum = 0, Maximum = 127, DisplayMinimum = 0, DisplayMaximum = 127 };
        instrument.LogicalParameters.Add(parameter);
        ProjectDomainEditCommands.CreateLogicalTrack("Logical A - same track, two Segments", instrument.Id).Prepare(project).Apply(project);
        var logical = project.Tracks[0];
        for (int segmentIndex = 0; segmentIndex < 2; segmentIndex++)
        {
            var segment = new Segment(project) { ProjectStartTick = segmentIndex * 3072, LengthTicks = 1536, ContentOffsetTick = segmentIndex * 384 };
            for (int i = 0; i < 24; i++) segment.Notes.Add(new(project)
            { StartTick = segment.ContentOffsetTick + i * 48, LengthTicks = 32, Note = 52 + i % 12, Velocity = 72 + i });
            var lane = new LogicalParameterLane(project) { ParameterId = parameter.Id };
            lane.Points.Add(new CurvePoint(project, segment.ContentOffsetTick, 40) { Interpolation = CurveInterpolation.Step });
            lane.Points.Add(new CurvePoint(project, segment.ContentOffsetTick + 384, 90) { Interpolation = CurveInterpolation.Step });
            segment.ParameterLanes.Add(lane); logical.Segments.Add(segment);
        }
        ProjectDomainEditCommands.DuplicateLogicalTrackAndShareState(logical.Id, "Logical B - shared sound, independent view").Prepare(project).Apply(project);
        foreach (bool fixedRoute in new[] { true, false })
        {
            var root = new MidiChannelRoot(project) { Name = fixedRoute ? "Fixed" : "Auto", RoutingMode = fixedRoute ? MidiChannelRootRoutingMode.Fixed : MidiChannelRootRoutingMode.Auto };
            project.MidiChannelRoots.Add(root);
            for (int member = 0; member < 2; member++)
            {
                var track = new PureMidiTrack(project) { Name = $"MIDI {root.Name} {(member == 0 ? "A - two Segments" : "B - independent view")}", MidiChannelRootId = root.Id };
                project.PureMidiTracks.Add(track); project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
                for (int si = 0; si < (member == 0 ? 2 : 1); si++)
                {
                    var segment = new MidiSegment(project) { ProjectStartTick = si * 3072, LengthTicks = 1536, ContentOffsetTick = si * 192 };
                    for (int i = 0; i < 24; i++) segment.Notes.Add(new(project)
                    { StartTick = segment.ContentOffsetTick + i * 48, LengthTicks = 32, Key = 48 + i % 12, NoteOnVelocity = 72 + i });
                    foreach (int cc in new[] { 1, 7, 11 }) segment.ChannelEvents.Add(new(project)
                    { Tick = segment.ContentOffsetTick + cc * 16, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = cc, Data2 = 80 });
                    segment.ChannelEvents.Add(new(project) { Tick = segment.ContentOffsetTick, Kind = DirectMidiChannelEventKind.PitchBend, Data1 = 0, Data2 = 64 });
                    track.Segments.Add(segment);
                }
            }
        }
        return project;
    }
}
