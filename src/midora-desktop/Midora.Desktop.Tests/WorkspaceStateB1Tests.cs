using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class WorkspaceStateB1Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TrackProfileSharesPreferencesNotSettingsOrLocalPosition(bool midi)
    {
        await using var session = await CreateSession(midi);
        var ids = SegmentIds(session, midi);
        var a = session.OpenSegment(ids[0]); var b = session.OpenSegment(ids[1]); var other = session.OpenSegment(ids[2]);
        session.ActiveWorkspace = a;
        session.Document!.MarkSaveSucceeded();
        long music = session.Document.PublicationRevision; int history = session.Document.History.Count;
        var snapshot = a.Snapshot; var otherSnapshot = other.Snapshot;
        a.StartTick = 177; a.FirstLane = 30; b.StartTick = 33; b.FirstLane = 40;
        a.EditorSettings.OperationSubdivisionText = "1/32"; a.EditorSettings.SnapEnabled = false;
        a.LaneEditorSettings.OperationSubdivisionText = "1/8";
        a.LaneEditorSettings.SnapEnabled = true;
        a.EditorSettings.GridVisible = false; a.EditorSettings.DefaultLengthTicks = 49; a.EditorSettings.DefaultVelocity = 71;
        a.TickSpan = 1536; a.LaneHeight = 21; a.ToolMode = TimelineToolMode.Draw;
        a.ValueTraceShape = TimelineValueTraceShape.Line;
        a.BottomEditorRowHeight = new(257); a.IsLowerEditorVisible = false;
        a.BottomEditorRowHeight = new(0); // Collapsed layout is not a new user preference.
        a.ObjectList.LayoutWidth = 540; a.ObjectList.IsVisible = true;
        a.ObjectList.FirstRow = 71;
        Assert.Same(snapshot, a.Snapshot); Assert.Same(otherSnapshot, other.Snapshot);
        Assert.Equal(music, session.Document.PublicationRevision); Assert.Equal(history, session.Document.History.Count);
        Assert.False(session.Document.IsModified);
        Assert.NotSame(a.EditorSettings, b.EditorSettings);
        Assert.NotSame(a.LaneEditorSettings, b.LaneEditorSettings);
        session.ActiveWorkspace = b;
        Assert.Equal("1/32", b.EditorSettings.OperationSubdivisionText); Assert.False(b.EditorSettings.SnapEnabled);
        Assert.Equal("1/8", b.LaneEditorSettings.OperationSubdivisionText); Assert.True(b.LaneEditorSettings.SnapEnabled);
        Assert.False(b.EditorSettings.GridVisible); Assert.Equal(49, b.EditorSettings.DefaultLengthTicks); Assert.Equal(71, b.EditorSettings.DefaultVelocity);
        Assert.Equal(1536, b.TickSpan); Assert.Equal(21, b.LaneHeight); Assert.Equal(TimelineToolMode.Draw, b.ToolMode);
        Assert.Equal(a.ValueTraceShape, b.ValueTraceShape);
        Assert.False(b.IsLowerEditorVisible); b.IsLowerEditorVisible = true; Assert.Equal(257, b.BottomEditorRowHeight.Value);
        Assert.True(b.ObjectList.IsVisible); Assert.Equal(540, b.ObjectList.LayoutWidth);
        Assert.Equal(33, b.StartTick); Assert.Equal(40, b.FirstLane); Assert.Equal(0, b.ObjectList.FirstRow);
        session.ActiveWorkspace = other;
        Assert.Equal("1/16", other.EditorSettings.OperationSubdivisionText); Assert.True(other.EditorSettings.SnapEnabled);
        Assert.Equal(15, other.LaneHeight); Assert.Equal(TimelineToolMode.Select, other.ToolMode);
        Assert.False(other.ObjectList.IsVisible);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CloseReopenRestoresValuesButNotSelectionOrViewModel(bool midi)
    {
        await using var session = await CreateSession(midi);
        var id = SegmentIds(session, midi)[0];
        var a = session.OpenSegment(id);
        a.StartTick = 801; a.TickSpan = 1701; a.FirstLane = 29; a.LaneHeight = 12;
        a.ObjectList.FirstRow = 38; a.Selection.Replace(new MidoraId(987));
        a.ToolMode = TimelineToolMode.Erase;
        session.CloseWorkspace(a);
        var b = session.OpenSegment(id);
        Assert.NotSame(a, b); Assert.True(a.IsDisposed); Assert.Empty(b.Selection.IdSet);
        Assert.Equal((801L, 1701L, 29, 12d, 38), (b.StartTick, b.TickSpan, b.FirstLane, b.LaneHeight, b.ObjectList.FirstRow));
        Assert.Equal(TimelineToolMode.Erase, b.ToolMode);
        var arrangement = session.OpenArrangement(); arrangement.EditCursorTick = 150;
        session.OpenSegment(id);
        Assert.Equal(150, b.EditCursorTick); Assert.Equal(0, b.StartTick);
        b.StartTick = 123; session.ActiveWorkspace = arrangement; session.ActiveWorkspace = b;
        Assert.Equal(123, b.StartTick);
    }

    [Fact]
    public async Task TransferUsesTargetProfileAndUndoRebindsButDeletionPurgesLocalMemory()
    {
        await using var session = await CreateSession(false);
        var project = session.Project!; var ids = SegmentIds(session, false);
        var a = session.OpenSegment(ids[0]); a.TickSpan = 1700; a.StartTick = 91;
        var target = session.OpenSegment(ids[2]); target.TickSpan = 3400;
        session.Execute(ProjectDomainEditCommands.MoveSegment(ids[0], project.Tracks[1].Id, 6000));
        session.ActiveWorkspace = a;
        Assert.Equal(3400, a.TickSpan); Assert.Equal(91, a.StartTick);
        session.Undo(); Assert.Equal(1700, a.TickSpan); Assert.Equal(91, a.StartTick);
        session.Redo(); Assert.Equal(3400, a.TickSpan);
        session.Execute(ProjectDomainEditCommands.DeleteSegment(ids[0], true));
        Assert.True(a.IsDisposed);
        Assert.DoesNotContain(session.EditorStates.Freeze().Views, x => x.Key.Id == ids[0]);
        session.Undo(); Assert.DoesNotContain(session.Workspaces, x => x.ObjectId == ids[0]);
        var reopened = session.OpenSegment(ids[0]); Assert.Equal(0, reopened.StartTick); Assert.Equal(3400, reopened.TickSpan);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DuplicateTrackCopiesProfileOnceThenBothTracksAreIndependent(bool shared)
    {
        await using var session = await CreateSession(false);
        var track = session.Project!.Tracks[0];
        var a = session.OpenSegment(track.Segments[0].Id); a.TickSpan = 1830; a.EditorSettings.SnapEnabled = false;
        session.Execute(shared ? ProjectDomainEditCommands.DuplicateLogicalTrackAndShareState(track.Id, "Copy")
            : ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id, "Copy"));
        var copy = session.Project.Tracks.Single(t => t.Name == "Copy");
        var b = session.OpenSegment(copy.Segments[0].Id);
        Assert.Equal(1830, b.TickSpan); Assert.False(b.EditorSettings.SnapEnabled);
        b.TickSpan = 3830;
        session.EditorStates.CopyProfiles(new Dictionary<MidoraId, MidoraId> { [track.Id] = copy.Id });
        Assert.Equal(3830, b.TickSpan);
        session.ActiveWorkspace = a; Assert.Equal(1830, a.TickSpan);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CrossTypeMoveUsesOfficialIdentityMapAndOnlyCarriesCompatibleLocalState(bool copy)
    {
        await using var session = await CreateSession(false);
        var sourceId = session.Project!.Tracks[0].Segments[0].Id;
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI target"));
        var targetId = session.Project.PureMidiTracks[0].Id;
        var source = session.OpenSegment(sourceId); source.StartTick = 330; source.FirstLane = 21;
        source.TickSpan = 1200;
        var sourceKey = new EditorStateOwner(EditorStateOwnerKind.Segment, sourceId);
        session.EditorStates.SetLanes(sourceKey, new(LaneTabKey.Velocity, [new(LaneTabKey.Velocity, false, .2, .7)]));
        using var plan = SegmentConversionService.AnalyzeTransfer(session.Document!, [sourceId], sourceId, targetId, 0, copy);
        int notices = 0;
        session.Document!.SegmentIdentitiesTransferred += (map, undo) =>
        { Assert.Equal(sourceId, Assert.Single(map).SourceId); notices++; };
        session.Execute(plan.CreateCommand(true));
        var newId = session.Project.PureMidiTracks[0].Segments[0].Id;
        Assert.NotEqual(sourceId, newId);
        Assert.Equal(copy ? 0 : 1, notices);
        var target = session.OpenSegment(newId);
        Assert.Equal(copy ? 0 : 330, target.StartTick);
        Assert.Equal(copy ? 48 : 21, target.FirstLane);
        Assert.Equal(PianoEditorDefaults.SegmentTickSpan(session.Project.TicksPerQuarterNote), target.TickSpan);
        Assert.Equal(copy ? 0 : 1, notices);
        if (copy) return;
        Assert.True(source.IsDisposed);
        Assert.Equal(.2, session.EditorStates.GetLanes(new(EditorStateOwnerKind.Segment, newId)).Targets[0].Minimum);
        target.StartTick = 451;
        session.Undo();
        Assert.Equal(2, notices); Assert.True(target.IsDisposed);
        Assert.DoesNotContain(session.Workspaces, x => x.ObjectId == sourceId);
        var restored = session.OpenSegment(sourceId);
        Assert.Equal(451, restored.StartTick); Assert.Equal(1200, restored.TickSpan);
        session.Redo(); Assert.Equal(3, notices);
        Assert.Equal(451, session.OpenSegment(newId).StartTick);
    }

    [Fact]
    public async Task EmptyDirectMidiLaneSurvivesCloseWithoutCreatingEventsOrLeakingToOtherSegments()
    {
        await using var session = await CreateSession(true);
        var ids = SegmentIds(session, true); var workspace = session.OpenSegment(ids[0]);
        var target = new DirectMidiEventLaneTarget(DirectMidiChannelEventKind.ControlChange, 7);
        workspace.AddDirectMidiLaneTarget(target); session.RefreshWorkspace(workspace);
        Assert.Contains(workspace.ParameterLaneOptions, option => option.DirectMidiTarget == target);
        session.CloseWorkspace(workspace); workspace = session.OpenSegment(ids[0]);
        Assert.Contains(workspace.ParameterLaneOptions, option => option.DirectMidiTarget == target);
        var other = session.OpenSegment(ids[1]); Assert.DoesNotContain(other.ParameterLaneOptions, option => option.DirectMidiTarget == target);
        Assert.All(session.Project!.PureMidiTracks.SelectMany(t => t.Segments), segment => Assert.Empty(segment.ChannelEvents));
    }

    [Fact]
    public async Task SubVoicesKeepAllSettingsIndependentAcrossSwitchAndInstrumentClose()
    {
        await using var session = await CreateSession(false);
        var project = session.Project!; var instrument = project.EventInstruments[0];
        instrument.SubVoices.Add(new(project) { Name = "Second" });
        var aId = instrument.SubVoices[0].Id; var bId = instrument.SubVoices[1].Id;
        var v = session.OpenInstrument(instrument.Id);
        v.Selection.Replace(aId); session.RefreshWorkspace(v);
        v.TimelineStartTick = 301; v.TimelineFirstLane = 29; v.TimelineTickSpan = 2345;
        v.EditorSettings.DefaultVelocity = 57; v.EventLaneEditorSettings.SnapEnabled = false;
        v.ToolMode = TimelineToolMode.Erase; v.ValueTraceShape = TimelineValueTraceShape.Line;
        v.ObjectList.LayoutWidth = 544; v.ObjectList.IsVisible = true; v.ObjectList.FirstRow = 41;
        v.BottomEditorRowHeight = new(291); v.IsLowerEditorVisible = false;
        v.Selection.Replace(bId); session.RefreshWorkspace(v);
        Assert.Equal((0L, 59, 3072L), (v.TimelineStartTick, v.TimelineFirstLane, v.TimelineTickSpan));
        Assert.Equal(100, v.EditorSettings.DefaultVelocity); Assert.True(v.EventLaneEditorSettings.SnapEnabled);
        Assert.Equal(TimelineToolMode.Select, v.ToolMode); Assert.False(v.ObjectList.IsVisible); Assert.True(v.IsLowerEditorVisible);
        v.TimelineStartTick = 701; v.ToolMode = TimelineToolMode.Draw;
        v.Selection.Replace(aId); session.RefreshWorkspace(v);
        Assert.Equal((301L, 29, 2345L), (v.TimelineStartTick, v.TimelineFirstLane, v.TimelineTickSpan));
        Assert.Equal(57, v.EditorSettings.DefaultVelocity); Assert.False(v.EventLaneEditorSettings.SnapEnabled);
        Assert.Equal(TimelineToolMode.Erase, v.ToolMode); Assert.False(v.IsLowerEditorVisible);
        Assert.Equal((544d, 41), (v.ObjectList.LayoutWidth, v.ObjectList.FirstRow));
        session.CloseWorkspace(v); v = session.OpenInstrument(instrument.Id);
        v.Selection.Replace(bId); session.RefreshWorkspace(v); Assert.Equal(701, v.TimelineStartTick);
        Assert.Equal(TimelineToolMode.Draw, v.ToolMode);
        var logical = session.OpenSegment(project.Tracks[0].Segments[0].Id);
        Assert.Equal(TimelineToolMode.Select, logical.ToolMode); Assert.Equal(100, logical.EditorSettings.DefaultVelocity);
    }

    [Fact]
    public void RegistryFreezeRestoreIsIsolatedValidatedAndBounded()
    {
        using var state = new WorkspaceStateRegistry();
        var key = new EditorStateOwner(EditorStateOwnerKind.Track, new(1));
        var local = new EditorStateOwner(EditorStateOwnerKind.Segment, new(2));
        var defaults = TrackEditorProfile.Default(480, false);
        int notifications = 0; state.ProfileChanged += (_, _) => notifications++;
        state.SetProfile(key, defaults); var before = state.Freeze(); long revision = state.Revision;
        state.SetProfile(key, defaults); Assert.Equal(revision, state.Revision); Assert.Equal(1, notifications);
        state.SetProfile(key, defaults with { TickSpan = 1700 }); Assert.Equal(7680, before.Profiles[0].Value.Value.TickSpan);
        state.MarkSaved(before.Revision); Assert.NotEqual(state.Revision, state.SavedRevision);
        var invalid = before with { Views = [new(local, new(-1, 0, 0))] };
        Assert.False(state.TryRestore(invalid)); Assert.Equal(1700, state.Freeze().Profiles[0].Value.Value.TickSpan);
        using var fresh = new WorkspaceStateRegistry(); Assert.True(fresh.TryRestore(before));
        Assert.Equal(7680, fresh.Freeze().Profiles[0].Value.Value.TickSpan);
        using var limited = new WorkspaceStateRegistry(WorkspaceStateRegistry.ProfileCharge);
        int warnings = 0; limited.CapacityExceeded += () => warnings++;
        Assert.True(limited.SetProfile(key, defaults));
        Assert.False(limited.SetView(local, new())); Assert.False(limited.SetView(local, new())); Assert.Equal(1, warnings);
        Assert.Single(limited.Freeze().Profiles); Assert.Empty(limited.Freeze().Views);
        limited.Prune(_ => false); Assert.Equal(0, limited.ChargedBytes); Assert.True(limited.SetView(local, new()));
        fresh.Dispose(); Assert.False(fresh.SetProfile(key, defaults)); Assert.Empty(fresh.Freeze().Profiles);
    }

    [Fact]
    public void PendingLaneMemorySurvivesColdDirectoryAndOnlyAuthoritativeDeletionFiltersIt()
    {
        var cc7 = new LaneTabKey(3, Kind: (int)DirectMidiChannelEventKind.ControlChange, Number: 7);
        var cc11 = cc7 with { Number = 11 };
        LaneTabMemory memory = new(cc11, [new(cc7, true, .1, .8), new(cc11, false, .2, .6)]);
        var state = new LaneTabSession(memory, next => memory = next);
        state.Sync([new(LaneTabKey.Velocity, "Vel.", null)], authoritative: false);
        Assert.Equal(cc11, state.Active); Assert.Equal(LaneTabKey.Velocity, state.EffectiveActive);
        Assert.Equal(2, memory.Targets.Length);
        state.Sync([new(LaneTabKey.Velocity, "Vel.", 0), new(cc7, "CC7", 0), new(cc11, "CC11", 1)]);
        Assert.Equal(cc11, state.EffectiveActive); Assert.Equal((.2, .6), state.Axis(cc11)); Assert.False(state.IsVisible(cc7));
        state.Sync([new(LaneTabKey.Velocity, "Vel.", 0), new(cc7, "CC7", 0)]);
        Assert.Equal(LaneTabKey.Velocity, memory.Active); Assert.DoesNotContain(memory.Targets, x => x.Key == cc11);
    }

    [Theory]
    [InlineData(1)] [InlineData(480)] [InlineData(32767)]
    public void SharedPreferencesKeepPerSegmentAbsoluteTimeContextAndExtremePositions(int tpqn)
    {
        using var project = new MidoraProject(tpqn);
        var instrument = EventInstrumentLibrary.Create(project, "Timing");
        ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id).Prepare(project).Apply(project);
        var a = new Segment(project) { LengthTicks = 1920, ContentOffsetTick = 48 };
        var b = new Segment(project) { ProjectStartTick = 4000, LengthTicks = 1920, ContentOffsetTick = 96 };
        project.Tracks[0].Segments.AddRange([a, b]);
        project.Conductor.TimeSignatures.Add(new(project, 3000, 3, 4));
        using var state = new WorkspaceStateRegistry();
        using var first = Make(a); using var second = Make(b);
        first.EditorSettings.OperationSubdivisionText = "Bar";
        Assert.Equal("Bar", second.EditorSettings.OperationSubdivisionText);
        Assert.Equal(4L * tpqn, first.EditorSettings.OperationStepTicks);
        Assert.Equal(3L * tpqn, second.EditorSettings.OperationStepTicks);
        Assert.NotSame(first.EditorSettings, second.EditorSettings);
        foreach (long tick in new[] { 100L, 2999, 4000, long.MaxValue - 1 })
        {
            var expected = new TimelineEditorSettings(); expected.ConfigureProject(project, b.ProjectStartTick);
            expected.OperationSubdivisionText = "Bar";
            Equivalent(() => expected.SnapAbsolute(tick), () => second.EditorSettings.SnapAbsolute(tick));
            Equivalent(() => expected.SnapDelta(192, tick), () => second.EditorSettings.SnapDelta(192, tick));
        }
        b.ProjectStartTick = long.MaxValue - 4096; second.StartTick = 9000;
        second.Rebuild(project, 2);
        Assert.Equal(9000, second.StartTick); Assert.Equal(3L * tpqn, second.EditorSettings.OperationStepTicks);
        TimelineWorkspaceViewModel Make(Segment segment)
        {
            var view = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id), "Segment", TimelineWorkspaceMode.Segment);
            view.EditorState = new(view, state); view.Rebuild(project, 1); return view;
        }
        static void Equivalent(Func<long> expected, Func<long> actual)
        {
            long value = 0;
            var error = Record.Exception(() => value = expected());
            if (error is null) Assert.Equal(value, actual());
            else Assert.Equal(error.GetType(), Record.Exception(() => actual())?.GetType());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TracksInTheSameMidiRootDoNotShareProfiles(bool fixedRoute)
    {
        await using var session = await CreateSession(true);
        var root = session.Project!.MidiChannelRoots[0];
        root.RoutingMode = fixedRoute ? MidiChannelRootRoutingMode.Fixed : MidiChannelRootRoutingMode.Auto;
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrack(root.Id, "Same Root"));
        var track = session.Project.PureMidiTracks.Single(t => t.Name == "Same Root");
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 1920));
        var a = session.OpenSegment(session.Project.PureMidiTracks[0].Segments[0].Id); a.TickSpan = 1999;
        a.LaneEditorSettings.SnapEnabled = false;
        var b = session.OpenSegment(session.Project.PureMidiTracks.Single(t => t.Id == track.Id).Segments[0].Id);
        Assert.NotEqual(a.TickSpan, b.TickSpan); Assert.True(b.LaneEditorSettings.SnapEnabled);
    }

    [Fact]
    public async Task DeletingAClosedSubVoicePurgesItsStateAndUndoDoesNotResurrectIt()
    {
        await using var session = await CreateSession(false);
        var instrument = session.Project!.EventInstruments[0];
        var voiceId = instrument.SubVoices[0].Id;
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Surviving voice"));
        var view = session.OpenInstrument(instrument.Id); view.TimelineStartTick = 777;
        session.CloseWorkspace(view);
        session.Execute(ProjectDomainEditCommands.DeleteSubVoice(instrument.Id, voiceId, true));
        Assert.DoesNotContain(session.EditorStates.Freeze().Profiles, x => x.Key.Id == voiceId);
        session.Undo();
        view = session.OpenInstrument(instrument.Id); Assert.Equal(0, view.TimelineStartTick);
        view.TimelineStartTick = 123; view.ToolMode = TimelineToolMode.Erase; view.EditorSettings.DefaultVelocity = 71;
        session.Execute(ProjectDomainEditCommands.DuplicateEventInstrument(instrument.Id, "Definition copy"));
        var copy = session.Project.EventInstruments.Single(x => x.Name == "Definition copy");
        var other = session.OpenInstrument(copy.Id);
        Assert.Equal(123, other.TimelineStartTick); Assert.Equal(TimelineToolMode.Erase, other.ToolMode); Assert.Equal(71, other.EditorSettings.DefaultVelocity);
        other.TimelineStartTick = 444; other.TimelineLaneHeight = 23;
        session.ActiveWorkspace = view; Assert.Equal(123, view.TimelineStartTick); Assert.Equal(15, view.TimelineLaneHeight);
    }

    [Fact]
    public async Task StagedConversionPublishesViewTransferOnlyAfterCommitNotOnCancellation()
    {
        await using var session = await CreateSession(false);
        var sourceId = session.Project!.Tracks[0].Segments[0].Id;
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("Target"));
        var targetId = session.Project.PureMidiTracks[0].Id;
        var view = session.OpenSegment(sourceId); view.StartTick = 789;
        var before = session.EditorStates.Freeze(); int notices = 0;
        session.Document!.SegmentIdentitiesTransferred += (_, _) => notices++;
        using (var abandoned = SegmentConversionService.AnalyzeTransfer(session.Document, [sourceId], sourceId, targetId, 0, false))
        using (var command = (IDisposable)abandoned.CreateCommand(true))
        using (session.Document.PrepareEdit((IProjectEditCommand)command))
        {
            Assert.Equal(0, notices); Assert.Equal(before.Revision, session.EditorStates.Revision);
        }
        Assert.Equal(0, notices); Assert.False(view.IsDisposed);
        using var plan = SegmentConversionService.AnalyzeTransfer(session.Document, [sourceId], sourceId, targetId, 0, false);
        using var accepted = (IDisposable)plan.CreateCommand(true);
        using var edit = session.Document.PrepareEdit((IProjectEditCommand)accepted);
        session.Document.ExecutePrepared(edit);
        Assert.Equal(1, notices); Assert.True(view.IsDisposed);
        var target = session.OpenSegment(session.Project.PureMidiTracks[0].Segments[0].Id);
        Assert.Equal(789, target.StartTick);
    }

    [Fact]
    public async Task FailedProjectReplacementKeepsRegistryButSuccessIsolatesIdenticalIdsAndLateCallbacks()
    {
        await using var session = await CreateSession(false);
        var view = session.OpenSegment(SegmentIds(session, false)[0]); view.StartTick = 333;
        var registry = session.EditorStates;
        await Assert.ThrowsAnyAsync<Exception>(() => session.ImportMidiBytesAsNewProjectAsync([1, 2, 3], "Invalid"));
        Assert.Same(registry, session.EditorStates); Assert.False(view.IsDisposed);
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Replacement", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        Assert.True(registry.IsDisposed); Assert.True(view.IsDisposed); Assert.Equal(0, session.EditorStates.EntryCount);
        view.StartTick = 999; view.EditorSettings.SnapEnabled = false;
        Assert.Equal(0, session.EditorStates.EntryCount); Assert.Equal(0, registry.SubscriberCount);
    }

    [Fact]
    public void TenThousandVisitedOwnersHaveBoundedValueOnlyMemoryAndCheapHotUpdates()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        using var registry = new WorkspaceStateRegistry();
        var defaults = TrackEditorProfile.Default(480, false);
        for (int i = 1; i <= 1000; i++) registry.SetProfile(new(EditorStateOwnerKind.Track, new(i)), defaults);
        var targets = Enumerable.Range(0, 16).Select(n => new LaneTargetView(new(3, Number: n), n % 3 == 0, .1, .9)).ToImmutableArray();
        for (int i = 1001; i <= 11000; i++)
        {
            var owner = new EditorStateOwner(EditorStateOwnerKind.Segment, new(i));
            registry.SetView(owner, new(i, 48, 0));
            registry.SetLanes(owner, new(targets[0].Key, [.. targets]));
        }
        long retained = GC.GetTotalMemory(true) - before;
        output.WriteLine($"1000 profiles + 10000 local views + 160000 lane states: retained={retained:N0} bytes; charged={registry.ChargedBytes:N0}");
        Assert.InRange(retained, 1, registry.ChargedBytes); Assert.True(registry.ChargedBytes < WorkspaceStateRegistry.MaximumChargedBytes);
        var key = new EditorStateOwner(EditorStateOwnerKind.Track, new(1));
        for (int i = 0; i < 1000; i++) registry.SetProfile(key, defaults with { TickSpan = 100 + i });
        int notifications = 0; registry.ProfileChanged += (_, _) => notifications++;
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) registry.SetProfile(key, defaults with { TickSpan = 2000 + i }, EditorProfileFields.Zoom);
        watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"10000 hot updates: {watch.Elapsed.TotalMilliseconds:F3} ms, {allocated:N0} allocated bytes, {notifications} notices");
        Assert.Equal(10000, notifications); Assert.InRange(allocated, 0, 4096);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        registry.Dispose(); Assert.Equal(0, registry.EntryCount); Assert.Equal(0, registry.SubscriberCount);
    }

    private static async Task<DesktopSessionController> CreateSession(bool midi)
    {
        var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "B1", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        for (int i = 0; i < 2; i++)
        {
            if (midi)
            {
                session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI " + i));
                var track = session.Project!.PureMidiTracks[i];
                session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 1920));
                if (i == 0) session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 2000, 1920));
            }
            else
            {
                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical " + i, session.Project!.EventInstruments[0].Id));
                var track = session.Project.Tracks[i];
                session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 1920));
                if (i == 0) session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 2000, 1920));
            }
        }
        return session;
    }
    private static MidoraId[] SegmentIds(DesktopSessionController session, bool midi) => midi
        ? session.Project!.PureMidiTracks.SelectMany(t => t.Segments).Select(s => s.Id).ToArray()
        : session.Project!.Tracks.SelectMany(t => t.Segments).Select(s => s.Id).ToArray();
}
