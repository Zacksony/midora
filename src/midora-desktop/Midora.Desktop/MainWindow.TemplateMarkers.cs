using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    private EventInstrument RequireCurrentTemplateMarkerOwner(InstrumentWorkspaceViewModel workspace, long revision)
    {
        if (!_session.CanEditProject || !ReferenceEquals(_session.ActiveWorkspace, workspace)
            || workspace.PresentationDocumentRevision != revision || _session.Document?.PublicationRevision != revision
            || _session.Project?.EventInstruments.SingleOrDefault(v => v.Id == workspace.ObjectId) is not { } instrument)
            throw new InvalidOperationException("The Event Instrument changed or is no longer editable. Reopen this command and try again.");
        return instrument;
    }

    private void OnTemplateMarkerEditCompleted(object? sender, TemplateMarkerEditEventArgs e)
    {
        if (sender is not TimelineSurface { DataContext: InstrumentWorkspaceViewModel workspace } surface
            || workspace.TemplateEditRevision != e.Revision || surface.GetTemplateMarkerTick(e.Marker) != e.OldTick) return;
        ExecuteTemplateMarkerAction(surface, workspace, workspace.PresentationDocumentRevision,
            "Change " + TimelineSurface.TemplateMarkerName(e.Marker), instrument => TemplateMarkerEditing.Set(instrument, e.Marker, e.Tick));
    }

    // Handles get their own two-item menu; empty ruler space prepends creation
    // commands and retains the existing ruler menu below them.
    private bool PopulateTemplateMarkerMenu(ContextMenu menu, TimelineSurface surface, Point point)
    {
        if (surface.Tag as string != "SubVoiceNotes" || !surface.IsTemplateRulerPoint(point)
            || surface.DataContext is not InstrumentWorkspaceViewModel workspace) return false;
        long revision = workspace.PresentationDocumentRevision;
        if (surface.HitTemplateMarker(point) is { } marker && surface.GetTemplateMarkerTick(marker) is long tick)
        {
            Add("Delete", () => ExecuteTemplateMarkerAction(surface, workspace, revision, "Delete " + TimelineSurface.TemplateMarkerName(marker),
                instrument => TemplateMarkerEditing.Delete(instrument, marker)), marker != TemplateTimelineMarker.TemplateEnd);
            Add("Set Value…", () =>
            {
                TextInputDialog dialog = new("Set " + TimelineSurface.TemplateMarkerName(marker), "Tick", tick.ToString(CultureInfo.InvariantCulture), text =>
                    TrySubmitDialogEdit(() => _session.Execute(TemplateMarkerEditing.Set(
                        RequireCurrentTemplateMarkerOwner(workspace, revision), marker, TemplateMarkerEditing.ParseTick(text)))))
                { Height = 260 };
                ShowModalDialog(dialog);
            });
            return true;
        }
        long? clickedTick = surface.GetTemplateRulerTick(point);
        Add("Add Loop Start+End", () => ExecuteTemplateMarkerAction(surface, workspace, revision, "Add Loop Start+End", TemplateMarkerEditing.AddLoop));
        Add("Add Pre-Roll Point", () => ExecuteTemplateMarkerAction(surface, workspace, revision, "Add Pre-Roll Point", instrument =>
            TemplateMarkerEditing.Set(instrument, TemplateTimelineMarker.PreRoll,
                TemplateMarkerEditing.ResolveAddedPreRollTick(clickedTick, instrument.TemplateLengthTicks))));
        menu.Items.Add(new Separator());
        return false;

        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled && _session.CanEditProject };
            item.Click += (_, _) => action();
            SetTimelineCommandIcon(item);
            menu.Items.Add(item);
        }
    }

    private void ExecuteTemplateMarkerAction(TimelineSurface surface, InstrumentWorkspaceViewModel workspace, long revision,
        string title, Func<EventInstrument, IProjectEditCommand> command)
    {
        try { _session.Execute(command(RequireCurrentTemplateMarkerOwner(workspace, revision))); }
        catch (Exception error) { ShowError(title, error.Message); }
        finally { RestoreModalCommandFocus(workspace, surface); }
    }
}
