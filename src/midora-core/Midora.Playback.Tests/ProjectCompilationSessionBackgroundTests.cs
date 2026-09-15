using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Playback.Tests;

public sealed class ProjectCompilationSessionBackgroundTests
{
    [Theory]
    [InlineData(ProjectCompilationExecutionMode.Synchronous)]
    [InlineData(ProjectCompilationExecutionMode.Background)]
    public void PresentationOnlyEditPreservesCanonicalAndSampleDomainCaches(
        ProjectCompilationExecutionMode executionMode)
    {
        (MidoraProject project, LogicalTrack track, LogicalNote _, LogicalNote _) =
            CreateNoteProject(2);
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        using ProjectCompilationSession session = new(
            project,
            executionMode: executionMode,
            backgroundDebounce: TimeSpan.Zero);
        CanonicalCompiledResult compiled = session.CompileForPlayback(0, 960);
        var plan = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId> { track.Id });
        long sourceRevision = session.SourceRevision;
        ProjectChangeSet changes = new();
        changes.PresentationEventInstrumentIds.Add(instrument.Id);

        _ = session.ApplyEdit(
            _ => instrument.Color = new MidoraColor(0x33, 0x66, 0x99),
            changes);

        CanonicalCompiledResult replay = session.CompileForPlayback(0, 960);
        var replayPlan = session.GetOrCreateRealtimeRenderPlan(
            replay,
            48_000,
            new HashSet<MidoraId> { track.Id });
        Assert.Same(compiled, replay);
        Assert.Same(plan, replayPlan);
        Assert.Equal(sourceRevision, session.SourceRevision);
        Assert.True(session.IsCompilationCurrent);
    }

    [Fact]
    public async Task BackgroundSnapshotIncludesAndSynchronizesPureMidiBranch()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project)
        {
            LengthTicks = 480
        };
        DirectMidiNote note = new(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 31,
            NoteOnOrder = 10,
            NoteOffOrder = 20
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);

        Assert.Contains(
            session.LastAttempt.Events.ToArray(),
            value => value.Role == CanonicalEventRole.DirectMidi
                && value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte1 == 60);

        ProjectChangeSet changes = new();
        changes.PureMidiTrackIds.Add(track.Id);
        _ = session.ApplyEdit(_ => note.Key = 65, changes);

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.SmfTracks.ToArray(), current.SmfTracks.ToArray());
        Assert.Equal(full.OpaqueMidiEvents.ToArray(), current.OpaqueMidiEvents.ToArray());
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Contains(
            current.Events.ToArray(),
            value => value.Role == CanonicalEventRole.DirectMidi
                && value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte1 == 65);

        ProjectChangeSet rootChanges = new();
        rootChanges.MidiChannelRootIds.Add(root.Id);
        _ = session.ApplyEdit(_ =>
        {
            root.RoutingMode = MidiChannelRootRoutingMode.Fixed;
            root.FixedZeroBasedPort = 2;
            root.FixedZeroBasedChannel = 3;
        }, rootChanges);

        current = await session.EnsureCurrentCompilationAsync();
        full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.All(current.Events.ToArray(), value =>
        {
            Assert.Equal((byte)2, value.ZeroBasedPort);
            Assert.Equal((byte)3, value.ZeroBasedChannel);
        });

        ProjectChangeSet hierarchyChanges = new();
        hierarchyChanges.MidiChannelRootIds.Add(root.Id);
        _ = session.ApplyEdit(value =>
        {
            PureMidiTrack addedTrack = new(value)
            {
                Name = "Added MIDI Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment addedSegment = new(value)
            {
                ProjectStartTick = 480,
                LengthTicks = 240
            };
            addedSegment.ChannelEvents.Add(new DirectMidiChannelEvent(value)
            {
                Tick = 0,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 11,
                Data2 = 80,
                Order = 100
            });
            addedTrack.Segments.Add(addedSegment);
            value.PureMidiTracks.Add(addedTrack);
            value.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, addedTrack.Id));
        }, hierarchyChanges);

        current = await session.EnsureCurrentCompilationAsync();
        full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.SmfTracks.ToArray(), current.SmfTracks.ToArray());
        Assert.Equal(full.Fingerprint, current.Fingerprint);
    }

    [Fact]
    public async Task BackgroundEditPublishesOnlyTheCurrentSourceRevision()
    {
        MidoraProject project = new(480);
        project.Conductor.EndMarker = new ProjectEndMarker(project, 960);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(10));
        CanonicalCompiledResult previous = session.LastAttempt;

        CanonicalCompiledResult editReturn = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 240, "A")),
            new ProjectChangeSet { AffectsConductor = true });

        Assert.Same(previous, editReturn);
        Assert.Equal(1, session.SourceRevision);
        Assert.False(session.IsCompilationCurrent);
        Assert.Contains(
            session.CompilationState,
            new[] { ProjectCompilationState.Outdated, ProjectCompilationState.Compiling });

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.True(current.IsConsumable);
        Assert.True(session.IsCompilationCurrent);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Conductor.Markers.ToArray(), current.Conductor.Markers.ToArray());
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task ConsecutiveEditsConvergeToTheLatestRevision()
    {
        MidoraProject project = new(480);
        project.Conductor.EndMarker = new ProjectEndMarker(project, 960);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(25));

        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 120, "First")),
            new ProjectChangeSet { AffectsConductor = true });
        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 360, "Second")),
            new ProjectChangeSet { AffectsConductor = true });

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();

        Assert.Equal(2, session.SourceRevision);
        Assert.Equal(2, session.CompiledRevision);
        Assert.True(session.IsCompilationCurrent);
        CanonicalMarker[] markers = current.Conductor.Markers.ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Contains(markers, value => value.Name == "First");
        Assert.Contains(markers, value => value.Name == "Second");
    }

    [Fact]
    public async Task EventInstrumentUsageChangeSynchronizesAndRecompiles()
    {
        (MidoraProject project, LogicalTrack _, LogicalNote _, LogicalNote _) =
            CreateNoteProject(2);
        EventInstrument replacement = new(project)
        {
            Name = "Replacement",
            TemplateLengthTicks = 120,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.LetOverlap
        };
        SubVoice replacementVoice = new(project);
        replacementVoice.Events.Add(TemplateEvent.Note(project, 0, 120, 72, 100));
        replacement.SubVoices.Add(replacementVoice);
        project.EventInstruments.Add(replacement);
        EventInstrumentUsage usage = Assert.Single(project.EventInstrumentUsages);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        ProjectChangeSet changes = new();
        changes.EventInstrumentUsageIds.Add(usage.Id);

        _ = session.ApplyEdit(_ => usage.EventInstrumentId = replacement.Id, changes);
        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(1, session.SourceRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task WaitingConsumerCanCancelWithoutCancelingSharedCompilation()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(30));
        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 120, "Marker")),
            new ProjectChangeSet { AffectsConductor = true });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.EnsureCurrentCompilationAsync(cancellation.Token));

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        Assert.True(current.IsConsumable);
        Assert.True(session.IsCompilationCurrent);
    }

    [Fact]
    public async Task EditArrivingAfterWorkerStartsSupersedesTheOlderRevision()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote second) =
            CreateNoteProject(2_000);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        TaskCompletionSource<bool> compiling = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.CompilationChanged += (_, _) =>
        {
            if (session.CompilationState == ProjectCompilationState.Compiling)
            {
                compiling.TrySetResult(true);
            }
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        _ = session.ApplyEdit(_ => first.Note = 61, changes);
        await compiling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = session.ApplyEdit(_ => second.Note = 62, changes);

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(2, session.SourceRevision);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task EditCancellationBoundsWaitForSnapshotSynchronization()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote second) =
            CreateNoteProject(2_000);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        using ManualResetEventSlim synchronizationEntered = new();
        int hookInvocation = 0;
        session.CompilationSnapshotSynchronizationStartingForTests = cancellationToken =>
        {
            if (Interlocked.Increment(ref hookInvocation) == 1)
            {
                synchronizationEntered.Set();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
            }
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        try
        {
            _ = session.ApplyEdit(_ => first.Note = 61, changes);
            Assert.True(synchronizationEntered.Wait(TimeSpan.FromSeconds(5)));

            Task secondEdit = Task.Run(() => session.ApplyEdit(_ => second.Note = 62, changes));
            await secondEdit.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(2, session.SourceRevision);
            Assert.False(session.IsCompilationCurrent);
        }
        finally
        {
            session.CompilationSnapshotSynchronizationStartingForTests = null;
        }

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task EditDoesNotWaitForCapturedRevisionMaterialization()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote second) =
            CreateNoteProject(2_000);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        using ManualResetEventSlim materializationEntered = new();
        int hookInvocation = 0;
        session.CompilationSnapshotMaterializationStartingForTests = cancellationToken =>
        {
            if (Interlocked.Increment(ref hookInvocation) == 1)
            {
                materializationEntered.Set();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
            }
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        try
        {
            _ = session.ApplyEdit(_ => first.Note = 61, changes);
            Assert.True(materializationEntered.Wait(TimeSpan.FromSeconds(5)));

            Stopwatch stopwatch = Stopwatch.StartNew();
            _ = session.ApplyEdit(_ => second.Note = 62, changes);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"The edit waited {stopwatch.Elapsed.TotalMilliseconds:F1} ms for gate-external materialization.");
            Assert.Equal(2, session.SourceRevision);
        }
        finally
        {
            session.CompilationSnapshotMaterializationStartingForTests = null;
        }

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task SupersededMixedGraphCaptureConvergesToTheLatestFullCompilation()
    {
        (MidoraProject project, LogicalTrack logicalTrack, LogicalNote first, LogicalNote second) =
            CreateNoteProject(256);
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 0, 1, 40);
        voice.Events.Add(controller);
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(11) };
        CurvePoint curvePoint = new(project, 0, 32, CurveInterpolation.Step);
        curve.Points.Add(curvePoint);
        voice.Curves.Add(curve);

        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack pureTrack = new(project)
        {
            Name = "Pure",
            MidiChannelRootId = root.Id
        };
        MidiSegment midiSegment = new(project) { LengthTicks = 480 };
        DirectMidiNote directNote = new(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 64,
            NoteOnVelocity = 75,
            NoteOffVelocity = 20,
            NoteOnOrder = 10,
            NoteOffOrder = 20
        };
        midiSegment.Notes.Add(directNote);
        midiSegment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 0,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 70,
            Order = 5
        });
        pureTrack.Segments.Add(midiSegment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(pureTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, pureTrack.Id));

        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        using ManualResetEventSlim materializationEntered = new();
        int hookInvocation = 0;
        session.CompilationSnapshotMaterializationStartingForTests = cancellationToken =>
        {
            if (Interlocked.Increment(ref hookInvocation) == 1)
            {
                materializationEntered.Set();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
            }
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(logicalTrack.Id);
        changes.EventInstrumentIds.Add(instrument.Id);
        changes.PureMidiTrackIds.Add(pureTrack.Id);

        try
        {
            _ = session.ApplyEdit(_ =>
            {
                first.Velocity = 81;
                controller.Value = 41;
                curve.Points[0] = curvePoint with { Value = 33 };
                directNote.NoteOnVelocity = 76;
            }, changes);
            Assert.True(materializationEntered.Wait(TimeSpan.FromSeconds(5)));

            _ = session.ApplyEdit(_ =>
            {
                second.Note = 67;
                controller.Value = 99;
                curve.Points[0] = curvePoint with { Value = 88 };
                directNote.Key = 72;
            }, changes);
        }
        finally
        {
            session.CompilationSnapshotMaterializationStartingForTests = null;
        }

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.SmfTracks.ToArray(), current.SmfTracks.ToArray());
        Assert.Equal(full.OpaqueMidiEvents.ToArray(), current.OpaqueMidiEvents.ToArray());
    }

    [Fact]
    public async Task PartialMirrorCommitFaultForcesFreshEverythingRecovery()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote _) =
            CreateNoteProject(4);
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        int injected = 0;
        session.CompilationSnapshotCommitFaultForTests = () =>
        {
            if (Interlocked.Increment(ref injected) == 1)
                throw new InjectedCompilationFaultException();
        };
        ProjectChangeSet mixedChanges = new();
        mixedChanges.EventInstrumentIds.Add(instrument.Id);
        mixedChanges.TrackIds.Add(track.Id);

        _ = session.ApplyEdit(_ =>
        {
            instrument.Name = "Changed before partial commit";
            first.Note = 72;
        }, mixedChanges);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.EnsureCurrentCompilationAsync());
        Assert.IsType<InjectedCompilationFaultException>(failure.InnerException);
        Assert.Equal(ProjectCompilationState.Failed, session.CompilationState);

        session.CompilationSnapshotCommitFaultForTests = null;
        // A retry without another source edit must not reuse the partially
        // mutated mirror. RecompileAsync consumes the preserved Everything
        // recovery request and constructs a fresh compiler Project root.
        CanonicalCompiledResult recovered = await session.RecompileAsync(new ProjectChangeSet())
            .WaitAsync(TimeSpan.FromSeconds(10));
        using MidoraCompiler verifier = new();
        CanonicalCompiledResult full = verifier.CompileFull(project);

        Assert.True(recovered.IsConsumable);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, recovered.Fingerprint);
        Assert.Equal(full.Events.ToArray(), recovered.Events.ToArray());
        Assert.Equal(full.SmfTracks.ToArray(), recovered.SmfTracks.ToArray());
        Assert.Equal(full.OpaqueMidiEvents.ToArray(), recovered.OpaqueMidiEvents.ToArray());
        Assert.Equal(0, session.LastCompilationTelemetry.ReusedTrackCount);
    }

    [Fact]
    public async Task CancellationAfterMaterializationForcesFreshEverythingRecovery()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote second) =
            CreateNoteProject(64);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        using ManualResetEventSlim compileEntered = new();
        int invocation = 0;
        session.CompilationStartingForTests = cancellationToken =>
        {
            if (Interlocked.Increment(ref invocation) != 1) return;
            compileEntered.Set();
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancellationToken.ThrowIfCancellationRequested();
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        _ = session.ApplyEdit(_ => first.Note = 71, changes);
        Assert.True(compileEntered.Wait(TimeSpan.FromSeconds(5)));
        _ = session.ApplyEdit(_ => second.Note = 73, changes);
        session.CompilationStartingForTests = null;

        CanonicalCompiledResult recovered = await session.EnsureCurrentCompilationAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        using MidoraCompiler verifier = new();
        CanonicalCompiledResult full = verifier.CompileFull(project);

        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, recovered.Fingerprint);
        Assert.Equal(full.Events.ToArray(), recovered.Events.ToArray());
        Assert.Equal(0, session.LastCompilationTelemetry.ReusedTrackCount);
    }

    [Fact]
    public async Task CompilerStageFaultCanBeRetriedWithoutAnotherSourceEdit()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote _) =
            CreateNoteProject(8);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        int injected = 0;
        session.CompilationStartingForTests = _ =>
        {
            if (Interlocked.Increment(ref injected) == 1)
                throw new InjectedCompilationFaultException();
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        _ = session.ApplyEdit(_ => first.Velocity = 77, changes);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.EnsureCurrentCompilationAsync());
        Assert.IsType<InjectedCompilationFaultException>(failure.InnerException);

        session.CompilationStartingForTests = null;
        CanonicalCompiledResult recovered = await session.RecompileAsync(new ProjectChangeSet())
            .WaitAsync(TimeSpan.FromSeconds(10));
        using MidoraCompiler verifier = new();
        CanonicalCompiledResult full = verifier.CompileFull(project);

        Assert.Equal(full.Fingerprint, recovered.Fingerprint);
        Assert.Equal(full.Events.ToArray(), recovered.Events.ToArray());
        Assert.Equal(0, session.LastCompilationTelemetry.ReusedTrackCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagnosticCapacityFailureKeepsLastCompleteResultAndReportsOnlyItsOwnCause(bool capacity)
    {
        var (project, track, first, _) = CreateNoteProject(8);
        using (project)
        using (ProjectCompilationSession session = new(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.Zero))
        {
            CanonicalCompiledResult previous = session.LastAttempt;
            Exception injected = capacity ? new DiagnosticCapacityExceededException()
                : new OverflowException("An unrelated numeric operation overflowed.");
            session.CompilationStartingForTests = _ => throw injected;
            ProjectChangeSet changes = new();
            changes.TrackIds.Add(track.Id);
            _ = session.ApplyEdit(_ => first.Velocity = 77, changes);
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.EnsureCurrentCompilationAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(injected, failure.InnerException);
            Assert.Same(previous, session.LastAttempt);
            Assert.False(session.IsCompilationCurrent);
            Assert.Equal(capacity ? injected.Message : null, session.DiagnosticCapacityFailureMessage);
            Assert.Equal(capacity ? injected.Message : "The current Project revision could not be compiled.", failure.Message);
            session.CompilationStartingForTests = null;
            CanonicalCompiledResult recovered = await session.RecompileAsync(new ProjectChangeSet())
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(session.DiagnosticCapacityFailureMessage);
            Assert.True(session.IsCompilationCurrent);
            using MidoraCompiler verifier = new();
            CanonicalCompiledResult full = verifier.CompileFull(project);
            Assert.Equal(full.Fingerprint, recovered.Fingerprint);
            Assert.True(full.Events.SequenceEqual(recovered.Events));
        }
    }

    [Fact]
    public async Task BackgroundCompilationNeverExecutesLegacyFreeCSharpAndCanRecover()
    {
        string markerPath = Path.Combine(
            Path.GetTempPath(),
            $"midora-background-compile-{Guid.NewGuid():N}.marker");
        try
        {
            (MidoraProject project, LogicalTrack _, LogicalNote _, LogicalNote _) =
                CreateNoteProject(2);
            EventInstrument instrument = Assert.Single(project.EventInstruments);
            TemplateEvent templateEvent = Assert.Single(Assert.Single(instrument.SubVoices).Events);
            CSharpMappingFunction function = new(project)
            {
                Name = "blocking-test",
                AbiVersion = MappingAbiV2.Version,
                Body = $"System.IO.File.WriteAllText(@\"{markerPath.Replace("\"", "\"\"")}\", \"started\")"
            };
            instrument.MappingFunctions.Add(function);
            templateEvent.ValueMappings.Add(new ValueMappingStep(project)
            {
                Operation = MappingOperation.CustomCSharp,
                MappingFunctionId = function.Id
            });
            using ProjectCompilationSession session = new(
                project,
                executionMode: ProjectCompilationExecutionMode.Background,
                backgroundDebounce: TimeSpan.Zero);
            ProjectChangeSet changes = new();
            changes.EventInstrumentIds.Add(instrument.Id);
            CanonicalCompiledResult rejected = await session.EnsureCurrentCompilationAsync();
            Assert.False(rejected.IsConsumable);
            Assert.False(File.Exists(markerPath));
            Assert.Contains(rejected.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("Free C# Mapping Functions are not executed", StringComparison.Ordinal));

            _ = session.ApplyEdit(_ =>
            {
                function.AbiVersion = MappingExpressionAbiV3.Version;
                function.Body = "value";
            }, changes);
            CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
            CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);
            Assert.True(current.IsConsumable);
            Assert.Equal(full.Fingerprint, current.Fingerprint);
            Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
    }

    private static (MidoraProject Project, LogicalTrack Track, LogicalNote First, LogicalNote Second)
        CreateNoteProject(int noteCount)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 120,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.LetOverlap
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project)
        {
            LengthTicks = noteCount * 120L
        };
        for (int index = 0; index < noteCount; index++)
        {
            segment.Notes.Add(new LogicalNote(project)
            {
                StartTick = index * 120L,
                LengthTicks = 120,
                Note = 60,
                Velocity = 100
            });
        }
        track.Segments.Add(segment);
        return (project, track, segment.Notes[0], segment.Notes[1]);
    }

    private sealed class InjectedCompilationFaultException : Exception;
}
