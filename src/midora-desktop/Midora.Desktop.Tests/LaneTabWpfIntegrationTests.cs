using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Xunit;

namespace Midora.Desktop.Tests;

public static partial class TimelineObjectListIntegrationTests
{
    private static void VerifyLaneHeader(MainWindow window, FrameworkElement content, WorkspaceViewModel workspace, TimelineObjectListOwnerKind kind)
    {
        if (workspace is TimelineWorkspaceViewModel t) t.IsLowerEditorVisible = true;
        else ((InstrumentWorkspaceViewModel)workspace).IsLowerEditorVisible = true;
        Layout(content);
        var host = Descendants<LaneTabHost>(content).Single(h => h.IsVisible && ReferenceEquals(h.DataContext, workspace));
        var header = Assert.IsType<LaneTabHeader>(host.Header);
        // Content is in the pre-existing hidden HwndSource, not in a Window.
        header.CommandHost = window;
        header.Refresh(); Layout(content);
        var tabs = Assert.IsType<ListBox>(header.FindName("Tabs"));
        var rows = tabs.Items.Cast<LaneTabDescriptor>().ToArray();
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
        {
            var compact = new LaneTabHeader();
            VisualTreeHelper.SetRootDpi(compact, new DpiScale(dpi, dpi));
            ((ListBox)compact.FindName("Tabs")).ItemsSource = rows.Select(row => row with { Name = new string('W', 200) }).ToArray();
            Assert.Null(compact.FindName("StepLines"));
            ((TextBlock)compact.FindName("Coordinates")).Text = "(9223372036854775806, 16383)";
            compact.Measure(new Size(640, 32)); compact.Arrange(new Rect(0, 0, 640, 32)); compact.UpdateLayout();
            foreach (var name in new[] { "Snap", "Subdivision", "DirectoryButton" })
            {
                var control = (FrameworkElement)compact.FindName(name);
                var corner = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), compact);
                Assert.InRange(corner.X, 1, 640); Assert.InRange(corner.Y, 1, 32);
                Assert.InRange(control.ActualHeight, 20, 28);
            }
        }
        Assert.Equal(LaneTabKey.Velocity, rows[0].Key);
        Assert.Equal(kind != TimelineObjectListOwnerKind.LogicalSegment, rows.Any(r => r.Key == LaneTabKey.Instrument));
        Assert.All(rows, row => Assert.True(row.Count is null or >= 0));
        Assert.InRange(header.ActualHeight, 31, 33);
        var target = rows.First(r => r.Key.Type >= 2);
        var selection = workspace.Selection.SharedIds;
        tabs.SelectedItem = target; Layout(content);
        Assert.Same(selection, workspace.Selection.SharedIds);
        Assert.Equal(2, host.SelectedIndex);
        Assert.Single(Descendants<TimelineSurface>(host), s => s.IsVisible && s.IsHitTestVisible);
        var state = (LaneTabSession)Field(header, "_state")!;
        var eventSurface = Descendants<TimelineSurface>(host).Single(s => s.IsVisible && s.IsHitTestVisible);
        Assert.Null(header.FindName("StepLines"));
        Assert.NotNull(eventSurface.Snapshot!.StepSignalSource);
        var session = (DesktopSessionController)Field(window, "_session")!;
        var preferences = (ApplicationPreferences)Field(session, "_applicationPreferences")!;
        ApplicationPreferences? attempted = null;
        var preferencesDialog = new ApplicationPreferencesDialog(preferences, InstrumentCatalogState.Default,
            candidate => { attempted = candidate; return "Simulated save failure"; }, ApplicationPreferencesPage.Appearance);
        var checkbox = Assert.IsType<CheckBox>(preferencesDialog.FindName("EventLaneLinesBox"));
        Assert.True(checkbox.IsChecked);
        checkbox.IsChecked = false;
        Assert.True(session.ShowEventLaneLines); // Draft alone cannot affect the session.
        Invoke(preferencesDialog, "OnApplyClick", preferencesDialog, new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(attempted); Assert.False(attempted.Appearance.ShowEventLaneLines);
        Assert.Null(preferencesDialog.Result); Assert.True(session.ShowEventLaneLines);
        Invoke(preferencesDialog, "OnRestoreDefaultsClick", preferencesDialog, new RoutedEventArgs(Button.ClickEvent));
        Assert.True(checkbox.IsChecked);
        Assert.True(eventSurface.ShowStepSignal);
        SetLines(false);
        Assert.False(eventSurface.ShowStepSignal);
        var futureSurface = new TimelineSurface();
        window.BindEventLaneLines(futureSurface);
        Assert.False(futureSurface.ShowStepSignal);
        Assert.Same(selection, workspace.Selection.SharedIds);
        eventSurface.RestoreValueViewport((.2, .8));
        Layout(content);
        state.Hide(target.Key); header.Refresh(); Layout(content);
        Assert.Equal(0, host.SelectedIndex);
        Assert.DoesNotContain(tabs.Items.Cast<LaneTabDescriptor>(), r => r.Key == target.Key);
        Assert.Same(selection, workspace.Selection.SharedIds);
        header.Refresh(); Layout(content);
        Assert.DoesNotContain(tabs.Items.Cast<LaneTabDescriptor>(), r => r.Key == target.Key);
        state.Show(target.Key); header.Refresh(); Layout(content);
        Assert.Equal(2, host.SelectedIndex);
        var surface = Descendants<TimelineSurface>(host).Single(s => s.IsVisible && s.IsHitTestVisible);
        Assert.False(surface.ShowStepSignal);
        SetLines(true);
        Assert.True(surface.ShowStepSignal);
        Assert.True(futureSurface.ShowStepSignal);
        var axis = surface.CaptureValueViewport();
        Assert.InRange(axis.Minimum, .19, .21); Assert.InRange(axis.Maximum, .79, .81);
        Assert.Equal(rows.Length, state.Descriptors.Count);
        state.Show(LaneTabKey.Velocity); header.Refresh(); Layout(content);
        Assert.Null(Descendants<TimelineSurface>(host).Single(s => s.IsVisible && s.IsHitTestVisible).Snapshot!.StepSignalSource);
        VerifyLaneHeaderDrag(window, content, workspace, header, tabs, state);
        if (kind != TimelineObjectListOwnerKind.LogicalSegment)
            VerifyInstrumentLaneGestures(window, content, workspace, host, header, state);

        void SetLines(bool show)
        {
            var next = preferences with { Appearance = preferences.Appearance with { ShowEventLaneLines = show } };
            Assert.False(session.RequiresAudioWorkerRebuild(next));
            session.ApplyApplicationPreferencesAsync(next).GetAwaiter().GetResult();
            Layout(content);
        }
    }
}
