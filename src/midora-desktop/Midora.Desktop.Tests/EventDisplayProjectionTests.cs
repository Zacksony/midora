using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class EventDisplayProjectionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ControllerAxisListPropertiesAndInitialStateUseOneDisplayDomain(bool subVoice)
    {
        foreach (int cc in new[] { 10, 71, 72, 73, 74, 75, 76, 77, 78, 7, 11, 70, 79 })
        {
            using var project = new MidoraProject(480);
            var instrument = EventInstrumentLibrary.Create(project, "Instrument");
            var voice = instrument.SubVoices[0];
            var midi = CreateMidi(project);
            voice.Events.Add(TemplateEvent.ControlChange(project, 10, cc, 96));
            midi.ChannelEvents.Add(new(project) { Tick = 10, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = cc, Data2 = 96 });
            voice.InitialState.Controllers[cc] = 96; instrument.InitialState.Controllers[cc] = 96;
            WorkspaceViewModel workspace;
            int offset = MidiEditingValueDomain.ControllerOffset(cc);
            if (subVoice)
            {
                var vm = new InstrumentWorkspaceViewModel(instrument.Id, "Instrument");
                vm.Rebuild(project, 1);
                Assert.True(vm.TryActivateEventLane(MidiValueTarget.ControlChange(cc)));
                vm.Rebuild(project, 1);
                Assert.Equal(offset, vm.ActiveValueAxisMinimum);
                Assert.Equal(127 + offset, vm.ActiveValueAxisMaximum);
                Assert.Equal((96 + offset).ToString(), vm.InstrumentInitialStateFields.Single(f => f.Key == $"cc.{cc}").Value);
                Assert.Equal((96 + offset).ToString(), vm.ActiveSubVoiceInitialStateFields.Single(f => f.Key == $"cc.{cc}").Value);
                Assert.NotNull(vm.SubVoiceEventSnapshot!.StepSignalSource);
                workspace = vm; workspace.Selection.Replace(voice.Events[0].Id);
            }
            else
            {
                var vm = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, midi.Id), "MIDI", TimelineWorkspaceMode.Segment);
                vm.AddDirectMidiLaneTarget(new(DirectMidiChannelEventKind.ControlChange, cc));
                vm.Rebuild(project, 1);
                Assert.Equal(offset, vm.ActiveValueAxisMinimum);
                Assert.Equal(127 + offset, vm.ActiveEditingValueMaximum);
                Assert.NotNull(vm.ParameterSnapshot!.StepSignalSource);
                workspace = vm; workspace.Selection.Replace(midi.ChannelEvents[0].Id);
            }
            try
            {
                var row = new TimelineObjectListRow(workspace.Selection.Primary!.Value,
                    subVoice ? TimelineItemKind.TemplateEvent : TimelineItemKind.DirectMidiEvent, 10,
                    Value: 96, TemplateKind: TemplateEventKind.ControlChange, DirectKind: DirectMidiChannelEventKind.ControlChange, Number: cc);
                Assert.Equal((96 + offset).ToString(), TimelineObjectListSource.GetValueLabel(row));
                ObjectPropertiesViewModel properties = new();
                ObjectPropertiesProjection.Rebuild(properties, project, workspace, Resolver());
                string key = subVoice ? "template.value" : "midiEvent.value";
                Assert.Equal((96 + offset).ToString(), properties.Fields.Single(f => f.Key == key).Value);
                // Single-field and multi-field OK routes must each decode only once.
                foreach (bool multi in new[] { false, true })
                {
                    var command = multi ? ObjectPropertiesProjection.CreateEditCommand(project, workspace,
                        new Dictionary<string, string> { [key] = "16", [subVoice ? "template.tick" : "midiEvent.tick"] = "20" })
                        : ObjectPropertiesProjection.CreateEditCommand(project, workspace, key, "16");
                    var edit = command.Prepare(project); edit.Apply(project);
                    Assert.Equal(16 - offset, subVoice ? voice.Events[0].Value : midi.ChannelEvents[0].Data2);
                    edit.Undo(project);
                    Assert.Equal(96, subVoice ? voice.Events[0].Value : midi.ChannelEvents[0].Data2);
                }
            }
            finally { workspace.CancelBackgroundPresentationWork(); }
        }
    }

    [Fact]
    public void MixedCcPropertySummaryIsBasedOnDisplayValuesAndEncodesPerTarget()
    {
        using var project = new MidoraProject(480); var midi = CreateMidi(project);
        midi.ChannelEvents.Add(new(project) { Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 10, Data2 = 96 });
        midi.ChannelEvents.Add(new(project) { Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 32, Order = 1 });
        var ids = midi.ChannelEvents.Select(e => e.Id).ToHashSet();
        var selection = new ObjectPropertiesSelectionContext(TimelineWorkspaceMode.Segment, false, midi.Id, ids);
        var properties = ObjectPropertiesProjection.ReadMultiSelection(project, selection, default, null);
        var field = properties.Fields.Single(f => f.Key == "batch.midiEvent.value");
        Assert.Equal("32", field.Value); Assert.NotEqual(PropertyFieldValueState.Mixed, field.ValueState);
    }

    [Fact]
    public void ProjectInitialAndResetStateAcceptDisplayValuesWithoutChangingRawContract()
    {
        using var project = new MidoraProject(480);
        foreach (string prefix in new[] { "settings.initial", "settings.reset" })
        {
            var edit = ProjectSettingsProjection.CreateEditCommand(project, new PropertyField($"{prefix}.cc.10", "Pan", "16")).Prepare(project);
            edit.Apply(project);
            Assert.Equal(80, (prefix.EndsWith("initial") ? project.GlobalInitialState : project.GlobalResetDefaults).Controllers[10]);
            edit.Undo(project);
        }
    }

    [Fact]
    public void CcCurvePointPropertiesTranslateValuesButPreserveInterpolation()
    {
        using var project = new MidoraProject(480);
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        var voice = instrument.SubVoices[0];
        var curve = new ValueCurve(project) { Target = MidiValueTarget.ControlChange(10) };
        var point = new CurvePoint(project, 10, 96) { Interpolation = CurveInterpolation.Linear };
        curve.Points.Add(point); voice.Curves.Add(curve);
        var workspace = new InstrumentWorkspaceViewModel(instrument.Id, "Instrument");
        try
        {
            workspace.Rebuild(project, 1); workspace.Selection.Replace(point.Id);
            ObjectPropertiesViewModel properties = new();
            ObjectPropertiesProjection.Rebuild(properties, project, workspace, Resolver());
            Assert.Equal("32", properties.Fields.Single(f => f.Key == "valueCurvePoint.value").Value);
            foreach (bool multi in new[] { false, true })
            {
                var command = multi ? ObjectPropertiesProjection.CreateEditCommand(project, workspace,
                    new Dictionary<string, string> { ["valueCurvePoint.value"] = "16", ["valueCurvePoint.tick"] = "20" })
                    : ObjectPropertiesProjection.CreateEditCommand(project, workspace, "valueCurvePoint.value", "16");
                var edit = command.Prepare(project); edit.Apply(project);
                Assert.True(project.EventInstruments[0].SubVoices[0].Curves[0].Points[0].Value == 80,
                    $"Curve display edit (multi={multi}) must encode raw80; actual {project.EventInstruments[0].SubVoices[0].Curves[0].Points[0].Value}.");
                Assert.Equal(CurveInterpolation.Linear, project.EventInstruments[0].SubVoices[0].Curves[0].Points[0].Interpolation);
                edit.Undo(project); Assert.Equal(96, project.EventInstruments[0].SubVoices[0].Curves[0].Points[0].Value);
            }
        }
        finally { workspace.CancelBackgroundPresentationWork(); }
    }

    internal static InstrumentCatalogResolver Resolver() => new(new InstrumentCatalogState(true, [], []), Array.Empty<InstrumentCatalogSoundFontEntry>());
    internal static MidiSegment CreateMidi(MidoraProject project)
    {
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        var track = new PureMidiTrack(project) { Name = "MIDI", MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var midi = new MidiSegment(project) { LengthTicks = 4096 }; track.Segments.Add(midi); return midi;
    }
}
