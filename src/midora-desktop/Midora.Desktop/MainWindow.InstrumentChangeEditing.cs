using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop;

public partial class MainWindow
{
    internal InstrumentChangeOwner? InstrumentOwner(WorkspaceViewModel workspace) => GetInstrumentLaneOwner(workspace) is { } owner
        ? owner.Midi is { } midi ? new(midi.Id) : new(owner.Voice!.Id, owner.Instrument!.Id) : null;

    internal void PublishInstrumentSelection(WorkspaceViewModel workspace, CompressedMidoraIdSet ids)
    {
        workspace.Selection.AdoptMaterialized(ids);
        ClearTimelineCommandTargets();
        _session.RefreshWorkspaceSelection(workspace);
    }

    internal async void RunInstrumentAction(WorkspaceViewModel workspace, string action, Action? completed = null,
        long delta = 0, bool duplicate = false)
    {
        if (_session.Document is not { } document || InstrumentOwner(workspace) is not { } owner
            || !_session.CanEditProject || !ReferenceEquals(workspace, _session.ActiveWorkspace)) return;
        var ids = workspace.Selection.SharedIds;
        try
        {
            if (action is "Copy" or "Cut")
            {
                await RunClipboardTransferAsync(document, workspace,
                    () => ProjectObjectClipboard.CopyInstrumentChanges(document, owner, ids),
                    action == "Cut" ? ProjectDomainEditCommands.EditInstrumentChanges(owner, ids, new(InstrumentChangeOperation.Delete)) : null);
                return;
            }
            if (action == "Paste")
            {
                if (_projectClipboard is not { Kind: ProjectObjectClipboardKind.InstrumentChanges } payload) return;
                long tick = workspace switch { TimelineWorkspaceViewModel t => t.EditCursorTick ?? 0,
                    InstrumentWorkspaceViewModel v => v.EditCursorTick ?? 0, _ => 0 };
                await ExecuteWorkspaceEditAsync(ProjectObjectClipboard.CreatePasteInstrumentChangesCommand(document, payload, owner, tick, ids));
                return;
            }
            if (action == "Move" && duplicate)
            { await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateInstrumentChanges(owner, ids, delta)); return; }
            InstrumentChangeEdit edit = new(action switch
            {
                "Move" => InstrumentChangeOperation.Move, "Delete" => InstrumentChangeOperation.Delete,
                "Flip" => InstrumentChangeOperation.Flip, "Scale" => InstrumentChangeOperation.Scale,
                "Quantize" => InstrumentChangeOperation.Quantize, _ => InstrumentChangeOperation.Properties
            }, TickDelta: delta);
            if (action is "Scale" or "Properties")
            {
                var read = await ReadSelectionInputAsync((project, token) =>
                {
                    var source = InstrumentChangeSelectionQuery.Capture(project, owner);
                    long min = long.MaxValue, max = 0; InstrumentChangeValue first = default;
                    int count = 0; bool tickSame = true, msbSame = true, lsbSame = true, programSame = true;
                    foreach (var group in InstrumentChangeSelectionQuery.EnumerateGroups(source.Groups, ids, token))
                    {
                        var value = source.Read(group) ?? throw new InvalidOperationException("Incomplete Instrument Change.");
                        if (count++ == 0) first = value;
                        min = Math.Min(min, value.Tick); max = Math.Max(max, value.Tick);
                        tickSame &= first.Tick == value.Tick; msbSame &= first.BankMsb == value.BankMsb;
                        lsbSame &= first.BankLsb == value.BankLsb; programSame &= first.Program == value.Program;
                    }
                    return (min, max, first, count, tickSame, msbSame, lsbSame, programSame);
                });
                if (!read.Completed || read.Value.count == 0) return;
                var value = read.Value;
                if (action == "Properties" && value.count == 1)
                { EditInstrumentChange(workspace, value.first.Tick, value.first.Id); return; }
                if (!PrepareForModalSurface()) return;
                if (action == "Scale")
                {
                    var dialog = new ScaleSelectionDialog(value.max - value.min, false) { Owner = this };
                    if (ShowModalDialog(dialog) != true) return;
                    edit = edit with { Scale = dialog.ScaleFactor };
                }
                else
                {
                    var properties = new ObjectPropertiesViewModel();
                    PropertyField Field(string key, string label, long number, bool same) => new(key, label,
                        number.ToString(CultureInfo.InvariantCulture), valueState: same ? PropertyFieldValueState.SameValue : PropertyFieldValueState.Mixed);
                    properties.Replace("Instrument Changes", $"{value.count:N0} selected", [
                        Field("Tick", "Tick", value.first.Tick, value.tickSame),
                        Field("MSB", "Bank MSB", value.first.BankMsb, value.msbSame),
                        Field("LSB", "Bank LSB", value.first.BankLsb, value.lsbSame),
                        Field("Program", "Program", value.first.Program, value.programSame)]);
                    var dialog = new ObjectPropertiesDialog(_session, workspace, properties, changes =>
                        changes.Count == 0 ? null : ProjectDomainEditCommands.EditInstrumentChanges(owner, ids, new(InstrumentChangeOperation.Properties,
                            Tick: Number("Tick"), BankMsb: Small("MSB"), BankLsb: Small("LSB"), Program: Small("Program")))) { Owner = this };
                    ShowModalDialog(dialog);
                    long? Number(string key) => properties.Fields.First(field => field.Key == key) is { HasPendingChange: true } field
                        ? long.Parse(field.Value, CultureInfo.InvariantCulture) : null;
                    int? Small(string key) => Number(key) is { } n ? checked((int)n) : null;
                    return;
                }
            }
            if (action == "Quantize")
            {
                if (!PrepareForModalSurface()) return;
                var dialog = new QuantizeSelectionDialog(false) { Owner = this };
                if (ShowModalDialog(dialog) != true) return;
                edit = edit with { Grid = dialog.Grid };
            }
            await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.EditInstrumentChanges(owner, ids, edit));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportInstrumentLaneFailure(exception); }
        finally { completed?.Invoke(); }
    }

    internal ContextMenu InstrumentMenu(WorkspaceViewModel workspace, bool hasSelection, Action? completed = null)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        Add("Copy", "Ctrl+C"); Add("Cut", "Ctrl+X"); Add("Paste", "Ctrl+V"); Add("Delete", "Del");
        menu.Items.Add(new Separator()); Add("Flip", "", "Flip Horizontally"); Add("Scale", "Ctrl+Q", "Scale…");
        Add("Quantize", "", "Quantize…"); Add("Properties", "Ctrl+P", "Properties…");
        return menu;
        void Add(string action, string gesture, string? title = null)
        {
            var item = new MenuItem { Header = title ?? action, InputGestureText = gesture,
                IsEnabled = _session.CanEditProject && (action == "Paste" ? _projectClipboard?.Kind == ProjectObjectClipboardKind.InstrumentChanges : hasSelection) };
            item.Click += (_, _) => RunInstrumentAction(workspace, action, completed);
            menu.Items.Add(item);
        }
    }
}
