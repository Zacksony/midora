using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectTimelineOwnerRootReplacementTests
{
    [Fact]
    public void MultipleLogicalOwnersExchangeAsOnePreparedEdit()
    {
        using MidoraProject project = new(480);
        LogicalTrack firstOwner = new(project) { Name = "First" };
        LogicalTrack secondOwner = new(project) { Name = "Second" };
        Segment first = new(project) { LengthTicks = 100 };
        Segment second = new(project) { LengthTicks = 200 };
        firstOwner.Segments.Add(first);
        secondOwner.Segments.Add(second);
        project.Tracks.AddRange([firstOwner, secondOwner]);
        Segment firstReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, first);
        Segment secondReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        firstReplacement.LengthTicks = 101;
        secondReplacement.LengthTicks = 201;
        ProjectChangeSet changes = new();
        changes.TrackIds.UnionWith([firstOwner.Id, secondOwner.Id]);

        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    firstOwner, first, firstReplacement),
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    secondOwner, second, secondReplacement)
            ],
            changes);

        Assert.Same(first, firstOwner.Segments[0]);
        Assert.Same(second, secondOwner.Segments[0]);
        prepared.Apply(project);
        Assert.Same(firstReplacement, firstOwner.Segments[0]);
        Assert.Same(secondReplacement, secondOwner.Segments[0]);
        prepared.Undo(project);
        Assert.Same(first, firstOwner.Segments[0]);
        Assert.Same(second, secondOwner.Segments[0]);
        prepared.Apply(project);
        Assert.Same(firstReplacement, firstOwner.Segments[0]);
        Assert.Same(secondReplacement, secondOwner.Segments[0]);
    }

    [Fact]
    public void MixedThreeKindBatchExchangesAllRootsAndNeverRollsBackAllocatorHighWater()
    {
        using MidoraProject project = new(480);
        LogicalTrack logicalOwner = new(project) { Name = "Logical" };
        Segment logical = new(project) { LengthTicks = 100 };
        logicalOwner.Segments.Add(logical);
        project.Tracks.Add(logicalOwner);

        MidiChannelRoot channelRoot = new(project) { Name = "Root" };
        PureMidiTrack directOwner = new(project)
        {
            Name = "Direct",
            MidiChannelRootId = channelRoot.Id
        };
        MidiSegment direct = new(project) { LengthTicks = 100 };
        directOwner.Segments.Add(direct);
        project.MidiChannelRoots.Add(channelRoot);
        project.PureMidiTracks.Add(directOwner);

        EventInstrument instrument = new(project) { Name = "Instrument" };
        SubVoice voice = new(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        Segment logicalReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, logical);
        MidiSegment directReplacement = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(project, direct);
        SubVoice voiceReplacement = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice);
        long expectedHighWater = project.NextStableId;
        long replacementHighWater = checked(expectedHighWater + 3);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    logicalOwner, logical, logicalReplacement),
                ProjectTimelineOwnerRootReplacementSpec.DirectMidiSegment(
                    directOwner, direct, directReplacement),
                ProjectTimelineOwnerRootReplacementSpec.SubVoice(
                    instrument, voice, voiceReplacement)
            ],
            new(),
            expectedHighWater,
            replacementHighWater);

        Assert.Equal(expectedHighWater, project.NextStableId);
        prepared.Apply(project);
        Assert.Same(logicalReplacement, logicalOwner.Segments[0]);
        Assert.Same(directReplacement, directOwner.Segments[0]);
        Assert.Same(voiceReplacement, instrument.SubVoices[0]);
        Assert.Equal(replacementHighWater, project.NextStableId);

        prepared.Undo(project);
        Assert.Same(logical, logicalOwner.Segments[0]);
        Assert.Same(direct, directOwner.Segments[0]);
        Assert.Same(voice, instrument.SubVoices[0]);
        Assert.Equal(replacementHighWater, project.NextStableId);

        prepared.Apply(project);
        Assert.Same(logicalReplacement, logicalOwner.Segments[0]);
        Assert.Same(directReplacement, directOwner.Segments[0]);
        Assert.Same(voiceReplacement, instrument.SubVoices[0]);
        Assert.Equal(replacementHighWater, project.NextStableId);
    }

    [Fact]
    public void MultiOwnerApplyRejectsOneStaleRootBeforeWritingAnyOtherSlot()
    {
        using MidoraProject project = new(480);
        (LogicalTrack firstOwner, Segment first) = AddLogicalOwner(project, "First");
        (LogicalTrack secondOwner, Segment second) = AddLogicalOwner(project, "Second");
        Segment firstReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, first);
        Segment secondReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    firstOwner, first, firstReplacement),
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    secondOwner, second, secondReplacement)
            ],
            new());
        Segment unexpected = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        secondOwner.Segments[0] = unexpected;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(first, firstOwner.Segments[0]);
        Assert.Same(unexpected, secondOwner.Segments[0]);
    }

    [Fact]
    public void MultiOwnerApplyRejectsOwnerReorderBeforeWritingAnyRoot()
    {
        using MidoraProject project = new(480);
        (LogicalTrack firstOwner, Segment first) = AddLogicalOwner(project, "First");
        (LogicalTrack secondOwner, Segment second) = AddLogicalOwner(project, "Second");
        Segment firstReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, first);
        Segment secondReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    firstOwner, first, firstReplacement),
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    secondOwner, second, secondReplacement)
            ],
            new());
        project.Tracks.Reverse();

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(first, firstOwner.Segments[0]);
        Assert.Same(second, secondOwner.Segments[0]);
    }

    [Fact]
    public void MultiOwnerApplyRejectsStaleAllocatorBeforeWritingAnyRoot()
    {
        using MidoraProject project = new(480);
        (LogicalTrack firstOwner, Segment first) = AddLogicalOwner(project, "First");
        (LogicalTrack secondOwner, Segment second) = AddLogicalOwner(project, "Second");
        Segment firstReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, first);
        Segment secondReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        long expectedHighWater = project.NextStableId;
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    firstOwner, first, firstReplacement),
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    secondOwner, second, secondReplacement)
            ],
            new(),
            expectedHighWater,
            checked(expectedHighWater + 2));
        _ = new LogicalNote(project);

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(first, firstOwner.Segments[0]);
        Assert.Same(second, secondOwner.Segments[0]);
        Assert.Equal(expectedHighWater + 1, project.NextStableId);
    }

    [Fact]
    public void MultiOwnerUndoAndRedoValidateEverySlotBeforeWritingAnyRoot()
    {
        using MidoraProject project = new(480);
        (LogicalTrack firstOwner, Segment first) = AddLogicalOwner(project, "First");
        (LogicalTrack secondOwner, Segment second) = AddLogicalOwner(project, "Second");
        Segment firstReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, first);
        Segment secondReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    firstOwner, first, firstReplacement),
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    secondOwner, second, secondReplacement)
            ],
            new());
        prepared.Apply(project);
        Segment unexpectedNew = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, secondReplacement);
        secondOwner.Segments[0] = unexpectedNew;

        Assert.Throws<InvalidOperationException>(() => prepared.Undo(project));
        Assert.Same(firstReplacement, firstOwner.Segments[0]);
        Assert.Same(unexpectedNew, secondOwner.Segments[0]);

        secondOwner.Segments[0] = secondReplacement;
        prepared.Undo(project);
        Segment unexpectedOld = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, second);
        secondOwner.Segments[0] = unexpectedOld;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(first, firstOwner.Segments[0]);
        Assert.Same(unexpectedOld, secondOwner.Segments[0]);
    }

    [Fact]
    public void LogicalSegmentCloneAndRootSwapKeepOldAndNewObjectGraphsIsolated()
    {
        using MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment source = new(project)
        {
            ProjectStartTick = 100,
            ContentOffsetTick = 20,
            LengthTicks = 480
        };
        LogicalNote sourceNote = new(project)
        {
            StartTick = 24,
            LengthTicks = 48,
            Note = 61,
            Velocity = 82
        };
        LogicalParameterLane sourceLane = new(project) { ParameterId = project.AllocateStableId() };
        CurvePoint sourcePoint = new(project, 30, 0.5, CurveInterpolation.Step);
        source.Notes.Add(sourceNote);
        sourceLane.Points.Add(sourcePoint);
        source.ParameterLanes.Add(sourceLane);
        track.Segments.Add(source);
        project.Tracks.Add(track);

        Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, source);
        Assert.Equal(source.Id, replacement.Id);
        Assert.NotSame(source.Notes[0], replacement.Notes[0]);
        Assert.NotSame(source.ParameterLanes[0], replacement.ParameterLanes[0]);
        Assert.NotSame(source.ParameterLanes[0].Points, replacement.ParameterLanes[0].Points);
        Assert.Equal(sourceNote.Id, replacement.Notes[0].Id);
        Assert.Equal(sourcePoint.Id, replacement.ParameterLanes[0].Points[0].Id);

        replacement.Notes[0].Velocity = 100;
        Assert.Equal(82, source.Notes[0].Velocity);
        ProjectChangeSet mutableChanges = new();
        mutableChanges.TrackIds.Add(track.Id);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            source,
            replacement,
            mutableChanges);
        mutableChanges.TrackIds.Clear();

        prepared.Apply(project);
        Assert.Same(replacement, track.Segments[0]);
        Assert.Contains(track.Id, prepared.Changes.TrackIds);
        source.Notes[0].Velocity = 1;
        Assert.Equal(100, replacement.Notes[0].Velocity);

        prepared.Undo(project);
        Assert.Same(source, track.Segments[0]);
        replacement.Notes[0].Velocity = 127;
        Assert.Equal(1, source.Notes[0].Velocity);

        prepared.Apply(project);
        Assert.Same(replacement, track.Segments[0]);
        Assert.Equal(127, track.Segments[0].Notes[0].Velocity);
    }

    [Fact]
    public void RootSwapRejectsStaleExpectedRootAndOwnerWithoutPartialPublication()
    {
        using MidoraProject project = new(480);
        LogicalTrack owner = new(project) { Name = "Owner" };
        Segment source = new(project) { LengthTicks = 100 };
        owner.Segments.Add(source);
        project.Tracks.Add(owner);
        Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, source);
        IPreparedProjectEdit staleRoot = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            owner,
            source,
            replacement,
            new());
        Segment unexpected = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, source);
        owner.Segments[0] = unexpected;

        Assert.Throws<InvalidOperationException>(() => staleRoot.Apply(project));
        Assert.Same(unexpected, owner.Segments[0]);

        owner.Segments[0] = source;
        IPreparedProjectEdit staleOwner = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            owner,
            source,
            replacement,
            new());
        LogicalTrack unexpectedOwner = new(project, owner.Id) { Name = owner.Name };
        unexpectedOwner.Segments.Add(source);
        project.Tracks[0] = unexpectedOwner;

        Assert.Throws<InvalidOperationException>(() => staleOwner.Apply(project));
        Assert.Same(source, unexpectedOwner.Segments[0]);
        Assert.Same(source, owner.Segments[0]);
    }

    [Fact]
    public void SubVoiceClonePreservesMappingsCurvesAndDeletedOptionalMappingWithoutAliasing()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice source = new(project) { Name = "Voice", RootNoteOverride = 64 };
        source.InitialState.Program = 8;
        source.InitialState.Controllers.Add(11, 90);
        TemplateEvent note = TemplateEvent.Note(project, 0, 120, 60, 100);
        TemplateEvent control = TemplateEvent.ControlChange(project, 10, 11, 80);
        source.Events.Add(note);
        source.Events.Add(control);
        TemplateEventMappingTarget controlTarget = TemplateEventMappingTarget.Create(
            TemplateEventKind.ControlChange,
            11,
            TemplateEventMappingParameter.Value);
        SubVoiceEventMapping? removedOptional = source.FindEventMapping(controlTarget);
        Assert.NotNull(removedOptional);
        Assert.True(source.EventMappings.Remove(removedOptional));
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        curve.TargetSettings.Overflow = MappingOverflow.Clamp;
        curve.Points.Add(new CurvePoint(project, 12, 0.25, CurveInterpolation.Step));
        source.Curves.Add(curve);
        instrument.SubVoices.Add(source);
        project.EventInstruments.Add(instrument);

        SubVoice replacement = ProjectTimelineOwnerRootClone.CloneSubVoice(project, source);

        Assert.Equal(source.Id, replacement.Id);
        Assert.Equal(8, replacement.InitialState.Program);
        Assert.Equal(90, replacement.InitialState.Controllers[11]);
        Assert.Equal(source.Events.Select(static value => value.Id),
            replacement.Events.Select(static value => value.Id));
        Assert.Null(replacement.FindEventMapping(controlTarget));
        Assert.Equal(source.EventMappings.Count, replacement.EventMappings.Count);
        Assert.Equal(
            source.EventMappings.Select(static value => value.Target),
            replacement.EventMappings.Select(static value => value.Target));
        Assert.NotSame(source.Events[0], replacement.Events[0]);
        Assert.NotSame(source.Curves[0], replacement.Curves[0]);
        Assert.NotSame(source.Curves[0].Points, replacement.Curves[0].Points);

        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(
            project,
            instrument,
            source,
            replacement,
            new());
        prepared.Apply(project);
        source.Events[0].Value = 1;
        source.InitialState.Controllers[11] = 2;
        Assert.Equal(100, instrument.SubVoices[0].Events[0].Value);
        Assert.Equal(90, instrument.SubVoices[0].InitialState.Controllers[11]);
        prepared.Undo(project);
        Assert.Same(source, instrument.SubVoices[0]);
    }

    [Fact]
    public void DirectMidiCloneSharesPagedSourceAndDoesNotEnumerateItsBaseRecords()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment source = new(project) { LengthTicks = 1_000 };
        ThrowingPagedSource paged = new();
        source.AttachPagedContent(paged);
        DirectMidiNote added = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 30
        };
        source.Notes.Add(added);
        track.Segments.Add(source);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);

        MidiSegment replacement = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(
            project,
            source);

        Assert.True(replacement.UsesPagedContent);
        Assert.Equal(source.PagedContentFingerprint, replacement.PagedContentFingerprint);
        Assert.Equal(2_000_001, replacement.Notes.Count);
        Assert.Equal(0, paged.BaseRecordReadCount);
        Assert.True(replacement.Notes.TryGetById(added.Id, out DirectMidiNote? copiedAdded));
        Assert.NotNull(copiedAdded);
        Assert.NotSame(added, copiedAdded);
        copiedAdded.NoteOnVelocity = 55;
        Assert.Equal(100, added.NoteOnVelocity);
        Assert.Equal(0, paged.BaseRecordReadCount);

        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(
            project,
            track,
            source,
            replacement,
            new());
        prepared.Apply(project);
        Assert.Same(replacement, track.Segments[0]);
        prepared.Undo(project);
        Assert.Same(source, track.Segments[0]);
    }

    [Fact]
    public async Task LogicalRootSwapWorksThroughHistoryCompilationAndFormat3Persistence()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MidoraTests",
            $"owner-root-swap-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "owner-root-swap.midora");
        Directory.CreateDirectory(directory);
        try
        {
            using MidoraProject project = CreateCompilableLogicalProject(out LogicalTrack track);
            Segment source = Assert.Single(track.Segments);
            MidoraId sourceNoteId = Assert.Single(source.Notes).Id;
            using ProjectCompilationSession compilation = new(project);
            using ProjectDocumentSession document = new(
                compilation,
                ProjectDocumentOrigin.Persisted);
            Segment? published = null;
            IProjectEditCommand command = new DeferredPreparedCommand(
                "Replace Logical Segment root",
                value =>
                {
                    Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(
                        value,
                        source);
                    replacement.Notes[0].StartTick = 96;
                    replacement.Notes[0].Velocity = 77;
                    published = replacement;
                    ProjectChangeSet changes = new();
                    changes.TrackIds.Add(track.Id);
                    return ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
                        value,
                        track,
                        source,
                        replacement,
                        changes);
                });

            ProjectEditExecution execution = document.Execute(command);

            Assert.True(execution.Changed);
            Assert.Same(published, track.Segments[0]);
            Assert.Equal(sourceNoteId, track.Segments[0].Notes[0].Id);
            Assert.Equal((96L, 77), (
                track.Segments[0].Notes[0].StartTick,
                track.Segments[0].Notes[0].Velocity));
            AssertFullCompileParity(compilation);

            document.Undo();
            Assert.Same(source, track.Segments[0]);
            Assert.Equal((0L, 100), (source.Notes[0].StartTick, source.Notes[0].Velocity));
            AssertFullCompileParity(compilation);

            document.Redo();
            Assert.Same(published, track.Segments[0]);
            AssertFullCompileParity(compilation);
            _ = await new MidoraProjectPackageV1("1.0.0-dev").SaveProjectAsync(project, path);
            await using MidoraProjectOpenResultV1 opened =
                await new MidoraProjectPackageV1("1.0.0-dev").OpenAsync(path);
            LogicalNote reopened = Assert.Single(
                Assert.Single(Assert.Single(opened.Project.Tracks).Segments).Notes);
            Assert.Equal((sourceNoteId, 96L, 77),
                (reopened.Id, reopened.StartTick, reopened.Velocity));
            using MidoraCompiler compiler = new();
            AssertCanonicalEqual(compilation.LastAttempt, compiler.CompileFull(opened.Project));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static MidoraProject CreateCompilableLogicalProject(out LogicalTrack track)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        track = new LogicalTrack(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return project;
    }

    private static (LogicalTrack Owner, Segment Root) AddLogicalOwner(
        MidoraProject project,
        string name)
    {
        LogicalTrack owner = new(project) { Name = name };
        Segment root = new(project) { LengthTicks = 100 };
        owner.Segments.Add(root);
        project.Tracks.Add(owner);
        return (owner, root);
    }

    private static void AssertFullCompileParity(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        AssertCanonicalEqual(compiler.CompileFull(compilation.Project), compilation.LastAttempt);
    }

    private static void AssertCanonicalEqual(
        CanonicalCompiledResult expected,
        CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed class DeferredPreparedCommand(
        string name,
        Func<MidoraProject, IPreparedProjectEdit> prepare) : IProjectEditCommand
    {
        public string Name { get; } = name;
        public IPreparedProjectEdit Prepare(MidoraProject project) => prepare(project);
    }

    private sealed class ThrowingPagedSource : IPureMidiSegmentContentSource
    {
        public int NoteCount => 2_000_000;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => "immutable-source";
        public int BaseRecordReadCount { get; private set; }

        public DirectMidiNoteValue GetNote(int index)
        {
            BaseRecordReadCount++;
            throw new InvalidOperationException("The immutable base must not be materialized by cloning.");
        }

        public DirectMidiChannelEventValue GetChannelEvent(int index)
        {
            BaseRecordReadCount++;
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            BaseRecordReadCount++;
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public int FindNoteIndex(MidoraId id) => -1;
        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;
        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127)
        {
            BaseRecordReadCount++;
            throw new InvalidOperationException("The immutable base must not be queried by cloning.");
        }
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(
            long startTick,
            long endTick)
        {
            BaseRecordReadCount++;
            throw new InvalidOperationException("The immutable base must not be queried by cloning.");
        }
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick)
        {
            BaseRecordReadCount++;
            throw new InvalidOperationException("The immutable base must not be queried by cloning.");
        }
    }
}
