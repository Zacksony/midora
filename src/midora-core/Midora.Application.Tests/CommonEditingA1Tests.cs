using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class CommonEditingA1Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(8, false, 1)]
    [InlineData(8, true, 1)]
    [InlineData(8, false, -1)]
    [InlineData(5000, false, 1)]
    [InlineData(5000, true, 1)]
    [InlineData(5000, false, -1)]
    public void PitchBendMoveUsesScalarCarryAndPreservesUndoAndOrder(int count, bool copy, int delta)
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Midi(project);
        int[] scalars = [127, 128, 8191, 8192, 126, 129, 1, 16382];
        segment.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiChannelEvent(project)
        { Tick = i * 2, Kind = DirectMidiChannelEventKind.PitchBend,
            Data1 = scalars[i % scalars.Length] & 127, Data2 = scalars[i % scalars.Length] >> 7, Order = i }));
        var before = segment.ChannelEvents.Select(e => (e.Id, e.Tick, e.Data1, e.Data2, e.Order)).ToArray();
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(segment.Id,
            before.Select(e => e.Id).ToArray(), count * 3, delta, copy).Prepare(project);
        Assert.Equal(before, segment.ChannelEvents.Select(e => (e.Id, e.Tick, e.Data1, e.Data2, e.Order)));
        edit.Apply(project);
        var current = track.Segments[0];
        var moved = current.ChannelEvents.Where(e => e.Tick >= count * 3).OrderBy(e => e.Tick).ToArray();
        Assert.Equal(count, moved.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(scalars[i % scalars.Length] + delta, (moved[i].Data2 << 7) | moved[i].Data1);
            Assert.Equal(count * 3 + i * 2, moved[i].Tick);
            if (!copy) { Assert.Equal(before[i].Id, moved[i].Id); Assert.Equal(before[i].Order, moved[i].Order); }
            else Assert.NotEqual(before[i].Id, moved[i].Id);
        }
        edit.Undo(project);
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(before, segment.ChannelEvents.Select(e => (e.Id, e.Tick, e.Data1, e.Data2, e.Order)));
        edit.Apply(project);
        Assert.Equal(copy ? count * 2 : count, track.Segments[0].ChannelEvents.Count);
        edit.Undo(project);
    }

    [Theory]
    [InlineData(DirectMidiChannelEventKind.ControlChange)]
    [InlineData(DirectMidiChannelEventKind.PolyphonicKeyPressure)]
    [InlineData(DirectMidiChannelEventKind.ProgramChange)]
    [InlineData(DirectMidiChannelEventKind.ChannelPressure)]
    public void ScalarMovePreservesSelectorAndOverwritesOnlyTouchedKeys(DirectMidiChannelEventKind kind)
    {
        using var project = new MidoraProject(480);
        var (_, segment) = Midi(project);
        bool scalarInData1 = kind is DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure;
        DirectMidiChannelEvent Make(long tick, int scalar) => new(project)
        { Tick = tick, Kind = kind, Data1 = scalarInData1 ? scalar : 11, Data2 = scalarInData1 ? 0 : scalar };
        var selected = Make(0, 20);
        segment.ChannelEvents.AddRange([selected, Make(20, 99), Make(100, 50), Make(100, 60)]);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ExactTimelineCollisionPolicy.Wrap(project,
            ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(segment.Id, [selected.Id], 20, 3, false).Prepare(project));
        edit.Apply(project);
        Assert.Equal(3, segment.ChannelEvents.Count);
        Assert.Equal(scalarInData1 ? 23 : 11, selected.Data1);
        Assert.Equal(scalarInData1 ? 0 : 23, selected.Data2);
        Assert.Equal(2, segment.ChannelEvents.Count(e => e.Tick == 100));
        edit.Undo(project);
        Assert.Equal(4, segment.ChannelEvents.Count);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(16383, 1)]
    public void ScalarOverflowIsRejectedBeforePublishingOrMaskingBytes(int value, int delta)
    {
        using var project = new MidoraProject(480);
        var (_, segment) = Midi(project);
        var point = new DirectMidiChannelEvent(project)
        { Kind = DirectMidiChannelEventKind.PitchBend, Data1 = value & 127, Data2 = value >> 7 };
        segment.ChannelEvents.Add(point);
        long nextId = project.NextStableId;
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(
            segment.Id, [point.Id], 0, delta, true).Prepare(project));
        Assert.Single(segment.ChannelEvents);
        Assert.Equal(value, (point.Data2 << 7) | point.Data1);
        Assert.Equal(nextId, project.NextStableId);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(100000)]
    public void TemplatePointCreationAndOneUndoIncludeTheTemplateBoundary(long tick)
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        var edit = ProjectDomainEditCommands.CreateTemplateControlChange(instrument.Id, voice.Id, tick, 11, 90).Prepare(project);
        edit.Apply(project);
        Assert.Equal(Math.Max(48, tick + 1), instrument.TemplateLengthTicks);
        Assert.Equal(tick, Assert.Single(voice.Events).Tick);
        edit.Undo(project);
        Assert.Empty(voice.Events); Assert.Equal(48, instrument.TemplateLengthTicks);
        edit.Apply(project); Assert.Equal(Math.Max(48, tick + 1), instrument.TemplateLengthTicks);
        edit.Undo(project);
    }

    [Fact]
    public void TemplateStreamLastSampleWinsAndRestoresTemplateWithItsRoot()
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.DrawTemplateEventPoints(instrument.Id, voice.Id, new(MidiValueKind.PitchBend),
            _ => [new(47, -8192), new(48, 0), new(50, 8191), new(48, 17)]).Prepare(project);
        edit.Apply(project);
        Assert.Equal(51, instrument.TemplateLengthTicks);
        Assert.Equal(new[] { -8192, 17, 8191 }, instrument.SubVoices[0].Events.OrderBy(e => e.Tick).Select(e => e.Value));
        edit.Undo(project);
        Assert.Same(voice, instrument.SubVoices[0]); Assert.Empty(voice.Events);
        Assert.Equal(48, instrument.TemplateLengthTicks);
        edit.Apply(project); Assert.Equal(51, instrument.TemplateLengthTicks);
        edit.Undo(project);
    }

    [Fact]
    public void StreamCancellationAndMaximumTickNeverPublishPartialTemplateContent()
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        long nextId = project.NextStableId;
        using var cancellation = new CancellationTokenSource();
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token, project: project);
        IEnumerable<TemplateEventPointEdit> Cancelled(CancellationToken token)
        {
            for (int i = 0; i < 12000; i++)
            {
                if (i == 7000) cancellation.Cancel();
                token.ThrowIfCancellationRequested(); yield return new(i, 40);
            }
        }
        Assert.ThrowsAny<OperationCanceledException>(() => ProjectDomainEditCommands.DrawTemplateEventPoints(
            instrument.Id, voice.Id, new(MidiValueKind.ControlChange, 11), Cancelled).Prepare(project));
        Assert.Empty(voice.Events); Assert.Equal(48, instrument.TemplateLengthTicks); Assert.Equal(nextId, project.NextStableId);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id, voice.Id, long.MaxValue, 11, 40).Prepare(project));
        Assert.Empty(voice.Events); Assert.Equal(nextId, project.NextStableId);
    }

    [Theory]
    [InlineData(-1L, 40, 11)]
    [InlineData(long.MaxValue, 40, 11)]
    [InlineData(99L, 128, 11)]
    [InlineData(99L, 40, 91)]
    public void InvalidStreamPointCannotPartiallyExtendOrModifyTheTemplate(long tick, int value, int controller)
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        long nextId = project.NextStableId;
        using var scope = BulkEditPreparationContext.Enter(project: project);
        Assert.ThrowsAny<ArgumentException>(() => ProjectDomainEditCommands.DrawTemplateEventPoints(
            instrument.Id, voice.Id, MidiValueTarget.ControlChange(controller),
            _ => [new(48, 40), new(tick, value)]).Prepare(project));
        Assert.Same(voice, instrument.SubVoices[0]);
        Assert.Empty(voice.Events); Assert.Equal(48, instrument.TemplateLengthTicks);
        Assert.Equal(nextId, project.NextStableId);
    }

    [Fact]
    public void PreparedTemplateDrawCannotOverwriteANewerOwnerRevision()
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.DrawTemplateEventPoints(instrument.Id, voice.Id,
            MidiValueTarget.ControlChange(11), _ => [new(99, 40)]).Prepare(project);
        var newer = TemplateEvent.ControlChange(project, 10, 11, 70);
        voice.Events.Add(newer);
        Assert.Throws<InvalidOperationException>(() => edit.Apply(project));
        Assert.Same(newer, Assert.Single(instrument.SubVoices[0].Events));
        Assert.Equal(48, instrument.TemplateLengthTicks);
    }

    [Theory]
    [InlineData(400000)]
    [InlineData(1000000)]
    public void LargeScalarMoveAndUndoRemainBounded(int count)
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Midi(project);
        segment.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiChannelEvent(project)
        { Tick = i * 2L, Kind = DirectMidiChannelEventKind.PitchBend, Data1 = 127, Data2 = i % 127, Order = i }));
        var ids = segment.ChannelEvents.Select(e => e.Id).ToArray();
        using var scope = BulkEditPreparationContext.Enter(project: project);
        Stopwatch clock = Stopwatch.StartNew();
        var edit = ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(segment.Id, ids, 0, 1, false).Prepare(project);
        TimeSpan prepare = clock.Elapsed;
        clock.Restart(); edit.Apply(project); TimeSpan apply = clock.Elapsed;
        Assert.Equal(count, track.Segments[0].ChannelEvents.Count);
        foreach (var e in track.Segments[0].ChannelEvents.QueryValues(0, 100)) Assert.Equal(0, e.Data1);
        clock.Restart(); edit.Undo(project); TimeSpan undo = clock.Elapsed;
        Assert.Same(segment, track.Segments[0]);
        Assert.True(scope.Resources.PeakResidentBytes <= scope.Resources.Budget.MaximumResidentBytes);
        output.WriteLine($"{count:N0}: prepare {prepare.TotalMilliseconds:F1} ms, apply {apply.TotalMilliseconds:F1} ms, undo {undo.TotalMilliseconds:F1} ms; bounded peak {scope.Resources.PeakResidentBytes:N0} bytes; process peak WS {Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes");
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    [InlineData("properties")]
    [InlineData("generate")]
    [InlineData("curve")]
    public void EveryTemplateCreationPathExtendsAndRestoresOneBoundary(string operation)
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        var existing = TemplateEvent.ControlChange(project, 3, 11, 40);
        voice.Events.Add(existing);
        var curve = new ValueCurve(project) { Target = MidiValueTarget.ControlChange(11) };
        voice.Curves.Add(curve);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        IProjectEditCommand command = operation switch
        {
            "move" or "copy" => ProjectDomainEditCommands.AdjustSubVoiceEventPoints(instrument.Id, voice.Id,
                [existing.Id], MidiValueTarget.ControlChange(11), 96, 1, operation == "copy"),
            "properties" => ProjectDomainEditCommands.UpdateTemplateControlChange(instrument.Id, voice.Id, existing.Id, 99, 11, 40),
            "generate" => ProjectDomainEditCommands.GenerateTemplateEventPoints(instrument.Id, voice.Id,
                MidiValueTarget.ControlChange(11), new() { BaseTick = 99, MaximumCandidates = 1,
                    CreateFirstFromInitialValues = true, InitialValue = 40 }),
            "curve" => ProjectDomainEditCommands.CreateValueCurvePoint(instrument.Id, voice.Id, curve.Id, 99, 40),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var edit = ExactTimelineCollisionPolicy.Wrap(project, command.Prepare(project));
        Assert.Equal(48, instrument.TemplateLengthTicks);
        edit.Apply(project); Assert.Equal(100, instrument.TemplateLengthTicks);
        if (operation == "curve") Assert.Single(instrument.SubVoices[0].Curves[0].Points);
        else Assert.Contains(instrument.SubVoices[0].Events, e => e.Tick == 99);
        edit.Undo(project); Assert.Equal(48, instrument.TemplateLengthTicks);
        Assert.Equal(3, Assert.Single(instrument.SubVoices[0].Events).Tick);
        Assert.Empty(instrument.SubVoices[0].Curves[0].Points);
        edit.Apply(project); Assert.Equal(100, instrument.TemplateLengthTicks);
        edit.Undo(project);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatingBeyondTemplateDoesNotReviveDeletedMappingOwners(bool streamed)
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        voice.Events.Add(TemplateEvent.ControlChange(project, 0, 11, 40));
        voice.EventMappings.RemoveAll(m => m.Target.EventKind == TemplateEventKind.ControlChange);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = (streamed
            ? ProjectDomainEditCommands.DrawTemplateEventPoints(instrument.Id, voice.Id, MidiValueTarget.ControlChange(11), _ => [new(99, 40)])
            : ProjectDomainEditCommands.CreateTemplateControlChange(instrument.Id, voice.Id, 99, 11, 40)).Prepare(project);
        edit.Apply(project);
        Assert.Equal(100, instrument.TemplateLengthTicks);
        Assert.DoesNotContain(instrument.SubVoices[0].EventMappings, m => m.Target.EventKind == TemplateEventKind.ControlChange);
        edit.Undo(project); Assert.Equal(48, instrument.TemplateLengthTicks);
    }

    [Fact]
    public void EmptyEventLaneDoesNotExtendTheTemplate()
    {
        using var project = new MidoraProject(480);
        var (instrument, voice) = Template(project);
        var edit = ProjectDomainEditCommands.CreateSubVoiceEventLane(instrument.Id, voice.Id, MidiValueTarget.PitchBend).Prepare(project);
        edit.Apply(project);
        Assert.Equal(48, instrument.TemplateLengthTicks); Assert.Empty(voice.Events);
        edit.Undo(project); Assert.Equal(48, instrument.TemplateLengthTicks);
    }

    [Fact]
    public void ScalarAndLegacyBytePathsHaveComparableBoundedPreparationWork()
    {
        const int count = 400000;
        using var project = new MidoraProject(480);
        var (track, segment) = Midi(project);
        segment.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiChannelEvent(project)
        { Tick = i * 2L, Kind = DirectMidiChannelEventKind.PitchBend, Data1 = i % 128, Data2 = i % 126, Order = i }));
        var ids = segment.ChannelEvents.Select(e => e.Id).ToArray();
        // ABBA order exposes first-use versus warm-source effects. Both commands
        // perform exactly +128 scalar units, with identical old/new event bytes.
        foreach (bool scalar in new[] { false, true, true, false })
        {
            using var scope = BulkEditPreparationContext.Enter(project: project);
            var command = scalar ? ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(segment.Id, ids, 0, 128, false)
                : ProjectDomainEditCommands.AdjustDirectMidiEventPoints(segment.Id, ids, 0, 0, 1, false);
            var clock = Stopwatch.StartNew(); var edit = command.Prepare(project);
            double elapsed = clock.Elapsed.TotalMilliseconds;
            edit.Apply(project);
            Assert.Equal(count, track.Segments[0].ChannelEvents.Count);
            foreach (var point in track.Segments[0].ChannelEvents.QueryValues(0, 1000))
                Assert.Equal((int)(point.Tick / 2 % 126) + 1, point.Data2);
            edit.Undo(project); Assert.Same(segment, track.Segments[0]);
            output.WriteLine($"{(scalar ? "scalar" : "legacy-byte")}: {elapsed:F1} ms; bounded peak {scope.Resources.PeakResidentBytes:N0} bytes");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5000)]
    public void EnumCannotUseRelativeValueChangesButCanMoveAndSetExactly(int count)
    {
        using var project = new MidoraProject(480);
        var (instrument, _) = Template(project);
        var track = new LogicalTrack(project) { Name = "Enum" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        var segment = new Segment(project) { LengthTicks = 20000 }; track.Segments.Add(segment);
        var parameter = new LogicalParameterDefinition(project) { Name = "Mode", Type = LogicalParameterType.Enum,
            Minimum = 0, Maximum = 1, DisplayMinimum = 0, DisplayMaximum = 1 };
        parameter.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "Off" });
        parameter.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "On" });
        instrument.LogicalParameters.Add(parameter);
        var lane = new LogicalParameterLane(project) { ParameterId = parameter.Id }; segment.ParameterLanes.Add(lane);
        lane.Points.AddRange(Enumerable.Range(0, count).Select(i => new CurvePoint(project, i * 2, 0, CurveInterpolation.Step)));
        var ids = lane.Points.Select(p => p.Id).ToArray();
        using var scope = BulkEditPreparationContext.Enter(project: project);
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.AdjustLogicalParameterPoints(segment.Id, lane.Id, ids, 0, 1).Prepare(project));
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.DuplicateLogicalParameterPoints(segment.Id, lane.Id, ids, 0, 1).Prepare(project));
        var moved = ProjectDomainEditCommands.AdjustLogicalParameterPoints(segment.Id, lane.Id, ids, 1, 0).Prepare(project);
        moved.Apply(project);
        Assert.All(track.Segments[0].ParameterLanes[0].Points, p => { Assert.Equal(1, p.Tick % 2); Assert.Equal(0, p.Value); });
        moved.Undo(project);
        var set = ProjectDomainEditCommands.SetLogicalParameterPointValues(segment.Id, lane.Id, ids, 1, ProjectBatchValueEditMode.ExactSet).Prepare(project);
        set.Apply(project); Assert.All(track.Segments[0].ParameterLanes[0].Points, p => Assert.Equal(1, p.Value));
        set.Undo(project);
        Assert.All(track.Segments[0].ParameterLanes[0].Points, p => Assert.Equal(0, p.Value));
    }

    [Fact]
    public async Task AcceptanceProjectRoundTripsAndCompiles()
    {
        using var project = new MidoraProject(480);
        project.Metadata.ProjectName = "A1 - Common UI and Event Editing";
        var (instrument, voice) = Template(project);
        instrument.Name = "A1 Template - end at 96"; instrument.TemplateLengthTicks = 96;
        voice.Name = "A1 Events";
        TemplateEvent Point(TemplateEventKind kind, long tick, int value, int number = 0) => new(project)
        { Kind = kind, Tick = tick, Value = value, Number = number };
        voice.Events.AddRange([
            TemplateEvent.Note(project, 0, 96, 60, 100),
            Point(TemplateEventKind.PitchBend, 12, -8065), Point(TemplateEventKind.PitchBend, 24, -8064),
            Point(TemplateEventKind.PitchBend, 36, -1), Point(TemplateEventKind.PitchBend, 48, 0),
            TemplateEvent.ControlChange(project, 0, 11, 20), TemplateEvent.ControlChange(project, 48, 11, 100),
            TemplateEvent.Bank(project, 0, 0, 1), Point(TemplateEventKind.RegisteredParameter, 0, 8192, 1),
            Point(TemplateEventKind.NonRegisteredParameter, 0, 64, 123)]);
        var second = new SubVoice(project) { Name = "A1 Shared Template Length" };
        second.Events.Add(TemplateEvent.ControlChange(project, 0, 1, 50)); instrument.SubVoices.Add(second);
        var logical = new LogicalTrack(project) { Name = "A1 Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logical, instrument.Id);
        var logicalSegment = new Segment(project) { LengthTicks = 960 };
        logical.Segments.Add(logicalSegment);
        for (int i = 0; i < 6; i++) logicalSegment.Notes.Add(new LogicalNote(project)
        { StartTick = i * 96, LengthTicks = 48, Note = 60 + i, Velocity = 90 });
        foreach (var type in new[] { LogicalParameterType.Integer, LogicalParameterType.Double })
        {
            var parameter = new LogicalParameterDefinition(project) { Name = $"A1 {type}", Type = type,
                Minimum = 0, Maximum = 127, DisplayMinimum = 0, DisplayMaximum = 127, DefaultValue = 0 };
            instrument.LogicalParameters.Add(parameter);
            var lane = new LogicalParameterLane(project) { ParameterId = parameter.Id };
            lane.Points.Add(new CurvePoint(project, 0, 20, CurveInterpolation.Step));
            lane.Points.Add(new CurvePoint(project, 240, 100, CurveInterpolation.Step));
            logicalSegment.ParameterLanes.Add(lane);
        }
        var (midi, direct) = Midi(project); midi.Name = "A1 MIDI"; direct.LengthTicks = 960;
        for (int i = 0; i < 6; i++) direct.Notes.Add(new DirectMidiNote(project)
        { StartTick = i * 96, LengthTicks = 48, Key = 60 + i, NoteOnVelocity = 90 });
        int order = 0;
        foreach (int scalar in new[] { 127, 128, 8191, 8192 })
            direct.ChannelEvents.Add(new DirectMidiChannelEvent(project) { Tick = ++order * 96,
                Kind = DirectMidiChannelEventKind.PitchBend, Data1 = scalar & 127, Data2 = scalar >> 7, Order = order });
        direct.ChannelEvents.Add(new DirectMidiChannelEvent(project) { Tick = 0, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 20 });
        direct.ChannelEvents.Add(new DirectMidiChannelEvent(project) { Tick = 240, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 100 });
        direct.ChannelEvents.Add(new DirectMidiChannelEvent(project) { Tick = 120, Kind = DirectMidiChannelEventKind.PolyphonicKeyPressure, Data1 = 60, Data2 = 42 });
        var bendIds = direct.ChannelEvents.Where(e => e.Kind == DirectMidiChannelEventKind.PitchBend).Select(e => e.Id).ToHashSet();
        ExactTimelineCollisionPolicy.Wrap(project, ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(
            direct.Id, bendIds, 0, 1, false).Prepare(project)).Apply(project);
        using var compiler = new MidoraCompiler();
        var compiled = compiler.CompileFull(project);
        Assert.True(compiled.IsConsumable, string.Join("\n", compiled.Diagnostics));
        Assert.Equal(new[] { 128, 129, 8192, 8193 }, compiled.QueryEventPages(compiled.StartTick, compiled.EndTick)
            .SelectMany(p => p.Items).Where(e => bendIds.Contains(e.Source.DirectMidiObjectId))
            .Select(e => (e.Message.Byte2 << 7) | e.Message.Byte1));
        string? retainedDirectory = Environment.GetEnvironmentVariable("MIDORA_A1_ACCEPTANCE_DIRECTORY");
        string directory = Path.GetFullPath(retainedDirectory ?? Path.Combine(AppContext.BaseDirectory, ".tmp", "A1Acceptance"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"a1-common-editing-{Guid.NewGuid():N}.midora");
        try
        {
            var packages = new MidoraProjectPackageV1("1.0.0-dev");
            await packages.SaveCopyAsync(project, path);
            var opened = await packages.OpenAsync(path);
            using var restored = opened.Project;
            Assert.False(opened.IsModified); Assert.Empty(opened.Diagnostics);
            var reopenedResult = compiler.CompileFull(restored);
            Assert.True(reopenedResult.IsConsumable);
            // Source storage identity changes when saving in-memory edits to a
            // Content Pack; compare all resident and paged consumer events.
            Assert.Equal(compiled.QueryEventPages(compiled.StartTick, compiled.EndTick).SelectMany(p => p.Items),
                reopenedResult.QueryEventPages(reopenedResult.StartTick, reopenedResult.EndTick).SelectMany(p => p.Items));
            Assert.Equal(compiled.Allocations.ToArray(), reopenedResult.Allocations.ToArray());
            if (retainedDirectory is not null) output.WriteLine($"Acceptance project: {path}");
        }
        finally { if (retainedDirectory is null) File.Delete(path); }
    }

    private static (PureMidiTrack Track, MidiSegment Segment) Midi(MidoraProject project)
    {
        var root = new MidiChannelRoot(project) { Name = "A1" }; project.MidiChannelRoots.Add(root);
        var track = new PureMidiTrack(project) { Name = "A1", MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var segment = new MidiSegment(project) { LengthTicks = 10000000 }; track.Segments.Add(segment);
        return (track, segment);
    }

    private static (EventInstrument Instrument, SubVoice Voice) Template(MidoraProject project)
    {
        var instrument = new EventInstrument(project) { Name = "A1", TemplateLengthTicks = 48 };
        project.EventInstruments.Add(instrument);
        var voice = new SubVoice(project) { Name = "A1" }; instrument.SubVoices.Add(voice);
        return (instrument, voice);
    }
}
