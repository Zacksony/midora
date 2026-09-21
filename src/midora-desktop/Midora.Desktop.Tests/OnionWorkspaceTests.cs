using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Application;
using Midora.Audio;
using Midora.Compiler;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class OnionWorkspaceTests
{
    [Fact]
    public async Task OnionRefreshAllowsWorkspaceRemovalDuringPresentationNotification()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Reentrant roster", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        var track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 192));
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 192, 192));
        var a = session.OpenSegment(track.Segments[0].Id);
        var b = session.OpenSegment(track.Segments[1].Id);
        session.ActiveWorkspace = a;
        bool removed = false;
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(a.IsOnionEnabled) || removed) return;
            removed = true; session.CloseWorkspace(b);
        };
        session.SetOnionEnabled(a, true);
        Assert.True(removed);
        Assert.DoesNotContain(b, session.Workspaces);
        Assert.True(a.IsOnionEnabled);
    }

    [Fact]
    public async Task NeighborMenuPreservesManualSourcesAndUsesFormalTrackOrderWithoutWrapping()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Neighbors", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var project = session.Project!;
        var instrument = Assert.Single(project.EventInstruments);
        foreach (var name in new[] { "A", "B", "C", "D" })
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(name, instrument.Id));
        var tracks = project.Tracks.ToArray();
        foreach (var track in tracks) session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 192));
        var a = session.OpenSegment(tracks[0].Segments[0].Id);
        var b = session.OpenSegment(tracks[1].Segments[0].Id);
        var d = session.OpenSegment(tracks[3].Segments[0].Id);
        session.ActiveWorkspace = b;
        session.Document!.MarkSaveSucceeded();
        long revision = session.Document.PublicationRevision;
        Assert.Null(session.GetOnionControls(a)!.Previous);
        Assert.Null(session.GetOnionControls(d)!.Next);
        Assert.True(b.CanConfigureOnion); Assert.False(b.IsOnionEnabled);
        session.SetTrackOnion(new(tracks[1].Id, false, .45, [tracks[3].Id]));
        session.ShowAdjacentOnionSource(b, previous: true);
        var state = session.GetOnionControls(b)!;
        Assert.True(state.Enabled); Assert.True(b.IsOnionEnabled);
        Assert.Equal(.45, state.Opacity);
        Assert.Equal(new[] { tracks[3].Id }, state.Sources);
        Assert.Equal(OnionSourceMode.Previous, state.SourceMode);
        Assert.Equal(tracks[0].Id, Assert.Single(b.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
        session.ShowAdjacentOnionSource(b, previous: false);
        Assert.Equal(tracks[2].Id, Assert.Single(b.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
        Assert.Equal(new[] { tracks[3].Id }, session.GetOnionControls(b)!.Sources);
        session.SetOnionOpacity(b, .6);
        Assert.Equal(OnionSourceMode.Next, session.GetOnionControls(b)!.SourceMode);
        session.SetOnionEnabled(b, false);
        Assert.False(b.IsOnionEnabled); Assert.Null(b.OnionSnapshot);
        Assert.Single(session.GetOnionControls(b)!.Sources);
        session.SetOnionEnabled(b, true);
        Assert.NotNull(b.OnionSnapshot);
        session.SetCustomOnionSources(b, session.GetOnionControls(b)!.Sources);
        Assert.Equal(OnionSourceMode.Custom, session.GetOnionControls(b)!.SourceMode);
        Assert.Equal(tracks[3].Id, Assert.Single(b.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
        Assert.Equal(revision, session.Document.PublicationRevision);
        // Adjacent means the current formal order, not the remembered neighbor at setup time.
        project.ArrangementTracks.Reverse();
        Assert.Equal(tracks[2].Id, session.GetOnionControls(b)!.Previous);
        Assert.Equal(tracks[0].Id, session.GetOnionControls(b)!.Next);
        session.ShowAdjacentOnionSource(b, false);
        Assert.Equal(tracks[0].Id, Assert.Single(b.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        var midi = new PureMidiTrack(project) { Name = "MIDI", MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(midi);
        project.ArrangementTracks.Insert(2, new(ArrangementTrackKind.PureMidiTrack, midi.Id));
        Assert.Equal(midi.Id, session.GetOnionControls(b)!.Previous);
    }

    [Fact]
    public async Task SubVoiceNeighborMenuIsPerVoiceAndNeverCrossesInstruments()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Voices", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("One"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "B"));
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "C"));
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Two"));
        var view = session.OpenInstrument(instrument.Id);
        var voices = instrument.SubVoices;
        session.ActivateSubVoiceEditor(view, voices[0].Id);
        Assert.Null(session.GetOnionControls(view)!.Previous);
        Assert.Equal(voices[1].Id, session.GetOnionControls(view)!.Next);
        session.SetCustomOnionSources(view, [voices[2].Id]);
        session.ShowAdjacentOnionSource(view, false);
        Assert.True(view.IsOnionEnabled);
        Assert.Equal(voices[1].Id, Assert.Single(view.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
        session.ActivateSubVoiceEditor(view, voices[2].Id);
        Assert.False(view.IsOnionEnabled); Assert.Null(session.GetOnionControls(view)!.Next);
        session.ShowAdjacentOnionSource(view, true);
        Assert.Empty(session.GetOnionControls(view)!.Sources);
        Assert.Equal(voices[1].Id, Assert.Single(view.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
        session.ActivateSubVoiceEditor(view, voices[0].Id);
        Assert.True(view.IsOnionEnabled);
        Assert.Equal(voices[2].Id, Assert.Single(session.GetOnionControls(view)!.Sources));
        Assert.Equal(OnionSourceMode.Next, session.GetOnionControls(view)!.SourceMode);
        session.SetCustomOnionSources(view, session.GetOnionControls(view)!.Sources);
        Assert.Equal(voices[2].Id, Assert.Single(view.OnionSnapshot!.Blocks.SelectMany(x => x)).Id);
    }

    [Fact]
    public async Task AllTracksReceivesSessionCursorUpdatesWithoutRebuildingOnionSnapshots()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Cursor", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        var view = session.OpenAllTracks();
        Assert.Equal(session.CurrentTick, view.PlaybackCursorTick);
        var snapshot = view.OnionSnapshot;
        typeof(DesktopSessionController).GetMethod("RefreshTimelinePlaybackCursors", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(long)])!
            .Invoke(session, [1500L]);
        Assert.Equal(1500, view.PlaybackCursorTick);
        Assert.Same(snapshot, view.OnionSnapshot);
        IPlaybackTimelineWorkspace timeline = view;
        timeline.StartTick = 0; timeline.TickSpan = 400;
        long? following = TimelinePlaybackFollowPolicy.ResolveStartTick(true, true, false, timeline.StartTick, timeline.TickSpan, timeline.PlaybackCursorTick, false);
        Assert.NotNull(following);
        Assert.Null(TimelinePlaybackFollowPolicy.ResolveStartTick(true, true, true, timeline.StartTick, timeline.TickSpan, timeline.PlaybackCursorTick, false));
        Assert.Null(TimelinePlaybackFollowPolicy.ResolveStartTick(false, true, false, timeline.StartTick, timeline.TickSpan, timeline.PlaybackCursorTick, false));
    }

    [Fact]
    public void AllTracksClickSeeksWithoutEditingSelectingSnappingOrRebuildingNotes()
    {
        Sta(() =>
        {
            var session = new DesktopSessionController();
            try
            {
                session.CreateProjectAsync(new NewProjectCreationRequest
                { ProjectName = "Navigation", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved }).GetAwaiter().GetResult();
                // Exercise the real desktop cursor command without native audio dependencies.
                object context = typeof(DesktopSessionController).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                var playbackProperty = context.GetType().GetProperty("Playback")!;
                (playbackProperty.GetValue(context) as PlaybackController)?.Dispose();
                playbackProperty.SetValue(context, new PlaybackController(
                    (ProjectCompilationSession)context.GetType().GetProperty("Compilation")!.GetValue(context)!, new NavigationBackend()));
                var vm = session.OpenAllTracks();
                session.Document!.MarkSaveSucceeded();
                long revision = session.Document.PublicationRevision;
                AllTracksView view = new() { DataContext = vm };
                view.PlaybackCursorRequested += (_, e) => session.SetPlaybackCursor(e.Tick);
                view.Measure(new(1200, 700)); view.Arrange(new Rect(0, 0, 1200, 700)); view.UpdateLayout();
                view.Timeline.StartTick = 1000; view.Timeline.TickSpan = 600;
                // Not a hidden edit-Snap multiple. The keyboard ruler is 52 DIPs wide.
                double x = 52 + (view.Timeline.ActualWidth - 52) / 2;
                foreach (var mode in new[] { ProjectPresentationAllTracksModeV3.Raw, ProjectPresentationAllTracksModeV3.Compiled })
                {
                    vm.Mode = mode;
                    var original = vm.OnionSnapshot;
                    Assert.True(view.TryNavigate(new(x, 8), MouseButton.Left));
                    Assert.Equal(1300, session.CurrentTick);
                    Assert.Equal(1300, vm.PlaybackCursorTick);
                    Assert.True(view.TryNavigate(new(52, 90), MouseButton.Left));
                    Assert.Equal(1000, session.CurrentTick);
                    Assert.Same(original, vm.OnionSnapshot);
                    Assert.False(view.TryNavigate(new(x, 90), MouseButton.Middle));
                    Assert.False(view.TryNavigate(new(x, 90), MouseButton.Right));
                    Assert.False(view.TryNavigate(new(25, 90), MouseButton.Left));
                    Assert.False(view.TryNavigate(new(x, -1), MouseButton.Left));
                    Assert.False(view.TryNavigate(new(double.NaN, 90), MouseButton.Left));
                    Assert.Equal(1000, session.CurrentTick);
                    Assert.Empty(vm.Selection.Ids);
                    Assert.False(view.Timeline.CanEdit);
                    Assert.Equal(revision, session.Document.PublicationRevision);
                    Assert.False(session.Document.IsModified);
                }
                vm.Dispose();
            }
            finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Fact]
    public void CompiledHybridReusesCurrentMidiSourcesAndOnlyExpandsLogicalNotes()
    {
        Sta(() =>
        {
            using var project = new MidoraProject(192);
            var root = new MidiChannelRoot(project) { Name = "Shared", RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0, FixedZeroBasedChannel = 0 }; project.MidiChannelRoots.Add(root);
            foreach (var (name, start, gate) in new[] { ("A", 0, 90), ("B", 10, 10) })
            {
                var track = new PureMidiTrack(project) { Name = name, MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(track);
                project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
                var segment = new MidiSegment(project) { LengthTicks = 100 }; track.Segments.Add(segment);
                segment.Notes.Add(new(project) { StartTick = start, LengthTicks = gate, Key = 60, NoteOnVelocity = 100 });
            }
            var instrument = EventInstrumentLibrary.Create(project, "Mapped"); instrument.RootNote = 60;
            instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 48, 65, 100));
            var logical = new LogicalTrack(project) { Name = "Logical" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, logical, instrument.Id);
            var logicalSegment = new Segment(project) { LengthTicks = 192 }; logical.Segments.Add(logicalSegment);
            logicalSegment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 48, Note = 62, Velocity = 100 });
            using var compiler = new MidoraCompiler(); var canonical = compiler.CompileFull(project);
            Assert.True(canonical.IsConsumable);
            var vm = new AllTracksWorkspaceViewModel(_ => { });
            try
            {
                vm.Rebuild(project, 0);
                var raw = vm.OnionSnapshot!;
                var sources = raw.Blocks.SelectMany(b => b).ToArray();
                vm.Mode = ProjectPresentationAllTracksModeV3.Compiled;
                Assert.Equal(2, vm.OnionSnapshot!.Blocks.SelectMany(b => b).Count()); // MIDI visible before index.
                vm.UpdateCompilation(canonical, true);
                PumpUntil(() => !vm.IsBuilding);
                Assert.False(vm.IsStale);
                var hybrid = vm.OnionSnapshot!.Blocks.SelectMany(b => b).ToArray();
                Assert.Same(sources[0], hybrid[0]); Assert.Same(sources[1], hybrid[1]);
                Assert.Equal(sources.Select(t => t.Id), hybrid.Select(t => t.Id));
                Assert.Equal((0L, 90L), Bounds(hybrid[0]));
                Assert.Equal((10L, 10L), Bounds(hybrid[1])); // Not canonical FIFO's 10..90.
                List<TimelineRenderItem> notes = [];
                hybrid[2].Clips[0].Source.QueryInto(0, 192, 0, 128, notes);
                Assert.Equal(127 - 67, Assert.Single(notes).Lane);
                vm.Mode = ProjectPresentationAllTracksModeV3.Raw;
                Assert.Same(sources[0], vm.OnionSnapshot!.Blocks.SelectMany(b => b).First());
                vm.Mode = ProjectPresentationAllTracksModeV3.Compiled;
                project.PureMidiTracks[0].Segments[0].Notes[0].LengthTicks = 70;
                vm.Rebuild(project, 1); vm.UpdateCompilation(canonical, false);
                Assert.True(vm.IsStale);
                Assert.Equal((0L, 70L), Bounds(vm.OnionSnapshot!.Blocks.SelectMany(b => b).First()));
                // Deleted MIDI disappears immediately; stale canonical logical expansion is retained and labeled.
                var removed = project.PureMidiTracks[1]; project.PureMidiTracks.Remove(removed);
                project.ArrangementTracks.RemoveAll(t => t.TrackId == removed.Id);
                project.Tracks.Remove(logical); project.ArrangementTracks.RemoveAll(t => t.TrackId == logical.Id);
                vm.Rebuild(project, 2);
                var retained = vm.OnionSnapshot!.Blocks.SelectMany(b => b).Select(t => t.Id).ToArray();
                Assert.DoesNotContain(removed.Id, retained); Assert.Contains(logical.Id, retained);
            }
            finally { vm.Dispose(); }
            static (long Tick, long Length) Bounds(TimelineOnionTrack track)
            {
                List<TimelineRenderItem> notes = []; track.Clips[0].Source.QueryInto(0, 192, 0, 128, notes);
                var note = Assert.Single(notes); return (note.StartTick, note.Length);
            }
        });
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Background logical index did not settle.");
            DispatcherFrame frame = new();
            DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
    [Fact]
    public async Task SubVoiceSourcesRemainPerTargetAndDormantDeletionRestoresOnUndo()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Voices", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        var a = instrument.SubVoices[0];
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "B"));
        var b = instrument.SubVoices[1];
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(instrument.Id, b.Id, 10, 20, 60, 90));
        var workspace = session.OpenInstrument(instrument.Id);
        session.SetSubVoiceOnion(new(instrument.Id, a.Id, true, .4, [b.Id]));
        session.ActivateSubVoiceEditor(workspace, a.Id);
        Assert.Equal(b.Id, Assert.Single(Assert.Single(workspace.OnionSnapshot!.Blocks)).Id);
        session.ActivateSubVoiceEditor(workspace, b.Id); Assert.Null(workspace.OnionSnapshot);
        session.ActivateSubVoiceEditor(workspace, a.Id);
        session.Execute(ProjectDomainEditCommands.DuplicateSubVoice(instrument.Id, a.Id));
        var copy = instrument.SubVoices.Single(v => v.Id != a.Id && v.Id != b.Id);
        Assert.Contains(session.Persistence!.Presentation.Current.SubVoiceOnionPresets, p => p.TargetSubVoiceId == copy.Id);
        session.ActivateSubVoiceEditor(workspace, copy.Id);
        Assert.Equal(b.Id, Assert.Single(Assert.Single(workspace.OnionSnapshot!.Blocks)).Id);
        session.Undo(); session.Redo();
        // B1: deleting the duplicate (including Undo of creation) releases its
        // own presentation. Redo restores music, not the deleted view profile.
        Assert.Single(session.Persistence.Presentation.Current.SubVoiceOnionPresets);
        Assert.DoesNotContain(session.Persistence.Presentation.Current.SubVoiceOnionPresets, p => p.TargetSubVoiceId == copy.Id);
    }
    [Fact]
    public void RawProjectionUsesSourceExposureAndTargetContentOffsetAcrossTrackTypes()
    {
        using var project = new MidoraProject(192);
        var targetTrack = new LogicalTrack(project) { Name = "Target" }; project.Tracks.Add(targetTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, targetTrack.Id));
        var target = new Segment(project) { ProjectStartTick = 1000, ContentOffsetTick = 200, LengthTicks = 500 };
        targetTrack.Segments.Add(target);
        var midi = new PureMidiTrack(project) { Name = "Source", Color = new(80, 100, 120) }; project.PureMidiTracks.Add(midi);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midi.Id));
        var source = new MidiSegment(project) { ProjectStartTick = 1100, ContentOffsetTick = 100, LengthTicks = 100 }; midi.Segments.Add(source);
        source.Notes.Add(new(project) { StartTick = 50, LengthTicks = 500, Key = 60, NoteOnVelocity = 100 });
        var location = OnionPresentation.Target(project, target.Id)!.Value;
        Assert.Equal(800, location.Offset);
        var tracks = OnionPresentation.CaptureTracks(project, new HashSet<MidoraId> { midi.Id }, location.Offset);
        var clip = Assert.Single(Assert.Single(tracks).Clips);
        Assert.Equal((100L, 200L, 200L), (clip.StartTick, clip.EndTick, clip.Shift));
        var tile = TimelineOnionRasterizer.Render(new("raw", tracks, .5), 0, 0, 0, 1, 1);
        Assert.Equal(0, tile.Pixels[(67 * 512 + 299) * 4 + 3]);
        Assert.NotEqual(0, tile.Pixels[(67 * 512 + 300) * 4 + 3]);
        Assert.NotEqual(0, tile.Pixels[(67 * 512 + 399) * 4 + 3]);
        Assert.Equal(0, tile.Pixels[(67 * 512 + 400) * 4 + 3]);
        source.Notes[0].Key = 100;
        List<TimelineRenderItem> frozen = []; clip.Source.QueryInto(0, 1000, 0, 128, frozen);
        Assert.Equal(67, Assert.Single(frozen).Lane);
        source.ContentOffsetTick = long.MaxValue - 1000;
        source.ProjectStartTick = 0;
        var outside = OnionPresentation.CaptureTracks(project, new HashSet<MidoraId> { midi.Id }, long.MaxValue - 1000);
        Assert.Empty(Assert.Single(outside).Clips);
    }
    [Fact]
    public void AllTracksViewAndDialogLoadWithoutApplicationAndRemainReadOnly()
    {
        Sta(() =>
        {
            using var project = new MidoraProject(192);
            var vm = new AllTracksWorkspaceViewModel(_ => { }); vm.Rebuild(project, 0);
            AllTracksView view = new() { DataContext = vm };
            view.Measure(new(1200, 700)); view.Arrange(new Rect(0, 0, 1200, 700)); view.UpdateLayout();
            Assert.False(view.Timeline.CanEdit); Assert.False(view.Timeline.IsTimeRangeSelectionEnabled);
            Assert.False(view.Timeline.Snapshot!.HasHitTestableItems); Assert.Empty(vm.Selection.Ids);
            OnionSettingsDialog dialog = new(.3);
            ((FrameworkElement)dialog.Content).Measure(new(480, 244));
            ((FrameworkElement)dialog.Content).Arrange(new Rect(0, 0, 480, 244));
            Assert.Equal(30, dialog.OpacityPercent);
            dialog.OpacityPercent = 41; Assert.Equal(41, dialog.OpacityPercent);
            var original = new OnionSourceChoice(new(10), "Long source track name", 0xff607080, true);
            OnionSourcesDialog sources = new([original], "Select Onion Tracks");
            ((FrameworkElement)sources.Content).Measure(new(510, 510));
            ((FrameworkElement)sources.Content).Arrange(new Rect(0, 0, 510, 510));
            Assert.True(sources.Sources[0].Included);
            sources.Sources[0].Included = false;
            Assert.True(original.Included); // Cancel owns no externally shared draft.
            sources.Close(); dialog.Close(); vm.Dispose();
        });
    }
    [Fact]
    public async Task TrackPresetIsSharedAcrossSegmentsDoesNotChangeMusicAndRebuildsOnSourceEdit()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest { ProjectName = "Onion", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Target", instrument.Id));
        var target = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Source", instrument.Id));
        var source = session.Project.Tracks.Single(t => t.Id != target.Id);
        session.Execute(ProjectDomainEditCommands.CreateSegment(target.Id, 0, 400));
        session.Execute(ProjectDomainEditCommands.CreateSegment(target.Id, 500, 400));
        session.Execute(ProjectDomainEditCommands.CreateSegment(source.Id, 0, 900));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(source.Segments[0].Id, 10, 30, 60, 90));
        var a = session.OpenSegment(target.Segments[0].Id);
        var b = session.OpenSegment(target.Segments[1].Id);
        session.Document!.MarkSaveSucceeded();
        long revision = session.Document.PublicationRevision;
        session.SetTrackOnion(new(target.Id, true, .3, [source.Id]));
        Assert.Equal(revision, session.Document.PublicationRevision);
        Assert.Null(a.OnionSnapshot); Assert.NotNull(b.OnionSnapshot);
        session.ActiveWorkspace = a;
        Assert.NotNull(a.OnionSnapshot); Assert.Null(b.OnionSnapshot);
        Assert.Empty(a.Selection.Ids); Assert.False(a.Snapshot!.HasHitTestableItems);
        var old = a.OnionSnapshot;
        session.RefreshWorkspaceSelection(a); Assert.Same(old, a.OnionSnapshot);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(source.Segments[0].Id, 100, 30, 70, 90));
        Assert.NotSame(old, a.OnionSnapshot);
        session.Execute(ProjectDomainEditCommands.DuplicateLogicalTrack(target.Id));
        var copy = session.Project.Tracks.Single(t => t.Id != target.Id && t.Id != source.Id);
        Assert.Contains(session.Persistence!.Presentation.Current.TrackOnionPresets, p => p.TargetTrackId == copy.Id);
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() => { try { action(); } catch (Exception e) { error = e; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class NavigationBackend : IRealtimePlaybackBackend
    {
        public int ActualSampleRate => 48000;
        public long PositionFrames => 0;
        public long RenderPositionFrames => 0;
        public bool IsBuffering => false;
        public bool IsCompleted => false;
        public bool IsFaulted => false;
        public string? FaultDescription => null;
        public bool OutputDeviceSelectionRequired => false;
        public string? OutputDeviceSelectionReason => null;
        public int Prepare() => throw new InvalidOperationException("A stopped seek must not prepare audio.");
        public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master) => throw new InvalidOperationException("A stopped seek must not start audio.");
        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands) { }
        public void Stop(bool flush) { }
        public void Reset() { }
        public void SelectOutputDevice(string? deviceId) { }
        public void Dispose() { }
    }
}
