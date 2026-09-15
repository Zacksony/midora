using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    internal void ReportInstrumentLaneFailure(Exception exception) =>
        _session.SetStatusMessage("Instrument Changes display: " + exception.Message, isError: true);
    internal sealed record InstrumentLaneOwner(MidiSegment? Midi, EventInstrument? Instrument, SubVoice? Voice, MidiChannelMode Mode);
    internal InstrumentLaneOwner? GetInstrumentLaneOwner(object workspace)
    {
        if (_session.Project is not { } project) return null;
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment, ObjectId: { } segmentId })
        {
            var located = TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId);
            if (located is not { } midi) return null;
            var root = project.MidiChannelRoots.FirstOrDefault(value => value.Id == midi.Track.MidiChannelRootId);
            return new(midi.Segment, null, null, root?.ChannelMode ?? MidiChannelMode.Melodic);
        }
        if (workspace is InstrumentWorkspaceViewModel { ObjectId: { } instrumentId, ActiveSubVoiceId: { } voiceId })
        {
            var instrument = project.EventInstruments.FirstOrDefault(value => value.Id == instrumentId);
            var voice = instrument?.SubVoices.FirstOrDefault(value => value.Id == voiceId);
            return voice is null ? null : new(null, instrument, voice, MidiChannelMode.Melodic);
        }
        return null;
    }
    internal string InstrumentChangeLabel(InstrumentChangeValue value) =>
        $"{value.BankMsb}.{value.BankLsb}.{value.Program} — {_instrumentCatalogResolver.ResolveProgram(new((byte)value.BankMsb, (byte)value.BankLsb, (byte)value.Program)).DisplayName}";

    internal void EditInstrumentChange(InstrumentChangeLane lane, long tick, MidoraId? id)
    {
        if (lane.DataContext is WorkspaceViewModel workspace)
            EditInstrumentChange(workspace, tick, id, selectedTick => { lane.Refresh(); lane.SelectAt(selectedTick); });
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (lane.IsVisible) lane.Focus(); }));
    }

    internal void EditInstrumentChange(WorkspaceViewModel workspace, long tick, MidoraId? id, Action<long>? accepted = null)
    {
        if (!PrepareForModalSurface()) return;
        if (!_session.CanEditProject || GetInstrumentLaneOwner(workspace) is not { } owner) return;
        var values = new InstrumentSelectionValues(0, 0, 0);
        if (id is { } changeId)
        {
            bool exists = owner.Midi is { } midi
                ? midi.InstrumentChanges.TryGet(changeId, out var group) && InstrumentChangeResolver.TryRead(midi, group, out var read)
                    ? Assign(read) : false
                : owner.Voice is { } voice && voice.InstrumentChanges.TryGet(changeId, out var group2)
                    && InstrumentChangeResolver.TryRead(voice, group2, out var read2) && Assign(read2);
            if (!exists) return;
        }
        var document = _session.Document;
        InstrumentSelectionDialog? dialog = null;
        dialog = new InstrumentSelectionDialog(values, new(0, 0, 0), false, _instrumentCatalogResolver,
            _preferences.InstrumentAudition, owner.Mode, _session, async selected =>
            {
                if (!ReferenceEquals(document, _session.Document)) return "The Project is no longer open.";
                var address = selected.Resolve(new(0, 0, 0));
                long selectedTick = dialog!.TimelineTick ?? tick;
                IProjectEditCommand command = owner.Midi is { } midi
                    ? ProjectDomainEditCommands.SetMidiInstrumentChange(midi.Id, selectedTick, address, id)
                    : ProjectDomainEditCommands.SetSubVoiceInstrumentChange(owner.Instrument!.Id, owner.Voice!.Id, selectedTick, address, id);
                return await ExecuteWorkspaceEditAsync(command) ? null : "The instrument change was not applied.";
            }) { Owner = this };
        dialog.SetTimelineTick(tick);
        bool? result = ShowModalDialog(dialog);
        SaveInstrumentAuditionPreferences(dialog.AuditionPreferences);
        if (result == true) accepted?.Invoke(dialog.TimelineTick ?? tick);
        bool Assign(InstrumentChangeValue read)
        { tick = read.Tick; values = new(read.BankMsb, read.BankLsb, read.Program); return true; }
    }

    private void OnSelectInitialInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (!PrepareForModalSurface()) return;
        if (_session.Project is not { } project || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel { ObjectId: { } id } workspace
            || !_session.CanEditProject) return;
        var instrument = project.EventInstruments.FirstOrDefault(value => value.Id == id);
        if (instrument is null) return;
        bool subVoice = sender is FrameworkElement { Tag: "ActiveSubVoice" };
        var voice = subVoice ? instrument.SubVoices.FirstOrDefault(value => value.Id == workspace.ActiveSubVoiceId) : null;
        if (subVoice && voice is null) return;
        var inherited = InstrumentSelectionValues.From(project.GlobalInitialState).Resolve(new(0, 0, 0));
        if (subVoice) inherited = InstrumentSelectionValues.From(instrument.InitialState).Resolve(inherited);
        var dialog = new InstrumentSelectionDialog(InstrumentSelectionValues.From(voice?.InitialState ?? instrument.InitialState),
            inherited, true, _instrumentCatalogResolver, _preferences.InstrumentAudition, MidiChannelMode.Melodic, _session,
            async values => await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.SetInitialInstrument(id, voice?.Id, values))
                ? null : "The Initial State was not applied.") { Owner = this };
        _ = ShowModalDialog(dialog);
        SaveInstrumentAuditionPreferences(dialog.AuditionPreferences);
    }
    private void SaveInstrumentAuditionPreferences(InstrumentAuditionPreferences values)
    {
        if (values == _preferences.InstrumentAudition) return;
        var candidate = _preferences with { InstrumentAudition = values };
        var result = _preferenceStore.Save(candidate);
        if (result.Succeeded) _preferences = candidate;
        else _session.SetStatusMessage(result.Notice?.Message ?? "Preview preferences could not be saved.", isError: true);
    }
    private void OnInstrumentLaneTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TabControl { SelectedItem: TabItem { Header: "Inst." } tab }) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        { if (tab.Content is InstrumentChangeLane { IsVisible: true } lane) lane.Focus(); }));
    }
}
