using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Xunit;

namespace Midora.Desktop.Tests;

public static partial class TimelineObjectListIntegrationTests
{
    private static void VerifyTemplateMarkersAndCompileFeedback(MainWindow window, DesktopSessionController session,
        FrameworkElement content, InstrumentWorkspaceViewModel workspace)
    {
        var instrument = session.Project!.EventInstruments.Single();
        workspace.ActiveSectionIndex = 1;
        workspace.TimelineStartTick = 0; workspace.TimelineTickSpan = 8192;
        workspace.IsLowerEditorVisible = true; workspace.ActiveLowerEditorIndex = 2;
        Layout(content);
        var piano = Descendants<TimelineSurface>(content).Single(s => s.IsVisible && Equals(s.Tag, "SubVoiceNotes"));
        PumpUntil(() => piano.MinimumTemplateLength is > 0);

        // This is the real SubVoice surface/menu handler, including the disabled
        // isolation case. The unavailable Loop command remains discoverable.
        var emptyMenu = new ContextMenu { PlacementTarget = piano };
        Assert.False((bool)Invoke(window, "PopulateTemplateMarkerMenu", emptyMenu, piano, new Point(100, 3))!);
        var addLoop = Assert.IsType<MenuItem>(emptyMenu.Items[0]);
        Assert.Equal("Add Loop Start+End", addLoop.Header); Assert.True(addLoop.IsEnabled);
        if (!instrument.RequiresChannelIsolation)
            session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentIsolation(instrument.Id, true));
        session.Execute(TemplateMarkerEditing.Set(instrument, TemplateTimelineMarker.PreRoll, 96));
        Layout(content);
        // Reopen after the revision change; stale menus must not target new state.
        emptyMenu.Items.Clear();
        Invoke(window, "PopulateTemplateMarkerMenu", emptyMenu, piano, new Point(100, 3));
        int history = session.Document!.History.Count;
        Invoke(emptyMenu.Items[0], "OnClick"); Layout(content);
        Assert.Equal(history + 1, session.Document.History.Count);
        Assert.Equal(0, instrument.LoopStartTick); Assert.Equal(instrument.TemplateLengthTicks, instrument.LoopEndTick);
        Assert.Equal(0, piano.TemplatePreviewLoopStartTick); Assert.Equal(instrument.TemplateLengthTicks, piano.TemplatePreviewLoopEndTick);
        Assert.Equal(workspace.PresentationDocumentRevision, session.Document.PublicationRevision);

        var points = FindMarkerCaps(piano);
        foreach (var (marker, point) in points)
        {
            var menu = new ContextMenu { PlacementTarget = piano };
            Assert.True((bool)Invoke(window, "PopulateTemplateMarkerMenu", menu, piano, point)!);
            Assert.Equal(2, menu.Items.Count);
            Assert.Equal("Delete", Assert.IsType<MenuItem>(menu.Items[0]).Header);
            Assert.Equal(marker != TemplateTimelineMarker.TemplateEnd, ((MenuItem)menu.Items[0]).IsEnabled);
            Assert.Equal("Set Value…", Assert.IsType<MenuItem>(menu.Items[1]).Header);
        }
        Assert.Equal(4, points.Count);

        // Preview traverses the actual piano and event template/name scopes;
        // formal source data remains unchanged until one release command.
        var start = points[TemplateTimelineMarker.PreRoll];
        Set(piano, "_replayingConductorPosition", start);
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
        Invoke(piano, "HandleTimelineMouseDown", down); Assert.True(down.Handled); Assert.True(piano.IsMouseCaptured);
        var end = new Point(start.X + 80, -200);
        Invoke(piano, "UpdateTemplateMarkerDrag", end); Layout(content);
        Assert.True(piano.TemplatePreviewPreRollTicks > 96);
        long preview = piano.TemplatePreviewPreRollTicks;
        Assert.Equal(96, instrument.PreRollTicks);
        Assert.Equal(history + 1, session.Document.History.Count);
        AssertOverlays(preview);
        Invoke(piano, "FinishTemplateMarkerDrag", end); Layout(content);
        Assert.Equal(preview, instrument.PreRollTicks); AssertOverlays(preview);
        Assert.Equal(history + 2, session.Document.History.Count);
        session.Undo(); Layout(content); Assert.Equal(96, instrument.PreRollTicks); AssertOverlays(96);
        session.Redo(); Layout(content); Assert.Equal(preview, instrument.PreRollTicks); AssertOverlays(preview);
        workspace.ActiveLowerEditorIndex = 0; Layout(content); AssertOverlays(preview);

        // A disappeared owner/revision is rejected before building a command.
        Assert.Throws<InvalidOperationException>(() => Invoke(window, "RequireCurrentTemplateMarkerOwner", workspace, session.Document.PublicationRevision - 1));
        var deleteMenu = new ContextMenu { PlacementTarget = piano };
        Invoke(window, "PopulateTemplateMarkerMenu", deleteMenu, piano, FindMarkerCaps(piano)[TemplateTimelineMarker.PreRoll]);
        Invoke(deleteMenu.Items[0], "OnClick"); Layout(content);
        Assert.Equal(0, instrument.PreRollTicks); Assert.False(FindMarkerCaps(piano).ContainsKey(TemplateTimelineMarker.PreRoll));
        session.Undo(); Layout(content); Assert.Equal(preview, instrument.PreRollTicks);

        // The template caption uses the same live preview binding as the handle,
        // and follows normal command publication / Undo without reopening the tab.
        var rulerOverlay = Descendants<TimelineSemanticOverlay>(content).Single(o => o.IsVisible && o.ShowRulerLabels);
        long originalLength = instrument.TemplateLengthTicks;
        Assert.Equal(originalLength, rulerOverlay.TemplateEndTick);
        session.Execute(TemplateMarkerEditing.Set(instrument, TemplateTimelineMarker.TemplateEnd, originalLength + 192));
        Layout(content); Assert.Equal(originalLength + 192, rulerOverlay.TemplateEndTick);
        session.Undo(); Layout(content); Assert.Equal(originalLength, rulerOverlay.TemplateEndTick);

        VerifyValidatedTickDialog();
        VerifyCompileSpinner(content);
        return;

        void AssertOverlays(long expected)
        {
            var overlays = Descendants<TimelineSemanticOverlay>(content).Where(o => o.IsVisible).ToArray();
            Assert.Equal(2, overlays.Length);
            Assert.All(overlays, o => Assert.Equal(expected, o.PreRollTicks));
        }
    }

    private static Dictionary<TemplateTimelineMarker, Point> FindMarkerCaps(TimelineSurface piano)
    {
        Dictionary<TemplateTimelineMarker, Point> points = [];
        // Exercise the public hit path rather than duplicating the layout algorithm.
        for (double x = piano.LaneHeaderWidth; x < piano.ActualWidth; x += .5)
            if (piano.HitTemplateMarker(new(x, 18)) is { } marker) points.TryAdd(marker, new(x + 2, 18));
        return points;
    }

    private static void VerifyValidatedTickDialog()
    {
        int submissions = 0;
        var dialog = new TextInputDialog("Set Pre-Roll", "Tick", "96", text =>
        {
            long tick = TemplateMarkerEditing.ParseTick(text);
            if (tick < 0 || tick > 768) return "Pre-Roll must be between zero and Template Length.";
            submissions++; return null;
        }) { Height = 260 };
        try
        {
            dialog.Measure(new Size(420, 260)); dialog.Arrange(new Rect(0, 0, 420, 260)); Pump();
            dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var box = Assert.IsType<TextBox>(dialog.FindName("ValueBox"));
            Assert.Equal("96", box.SelectedText);
            foreach (string invalid in new[] { "bad", "-1", "769", "9223372036854775808" })
            {
                box.Text = invalid; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Invoke(dialog, "OnAcceptClick", box, new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Null(dialog.DialogResult); Assert.Equal(invalid, dialog.Value);
                Assert.Equal(Visibility.Visible, dialog.ValidationErrorVisibility);
                Assert.False(string.IsNullOrWhiteSpace(dialog.ValidationError)); Assert.Equal(0, submissions);
            }
            dialog.Value = "192"; Assert.True(dialog.TrySubmit()); Assert.Equal(1, submissions);
            Assert.Equal(Visibility.Collapsed, dialog.ValidationErrorVisibility);
        }
        finally { dialog.Close(); }
    }

    private static void VerifyCompileSpinner(FrameworkElement content)
    {
        var spinner = Descendants<Ellipse>(content).Single(e => e.Name == "CompilationSpinner");
        var button = Descendants<Button>(content).Single(b => AutomationProperties.GetName(b) == "Compile");
        var label = Assert.IsType<TextBlock>(button.Content);
        var probe = new CompilationStatusProbe();
        spinner.DataContext = label.DataContext = probe;
        var enabledBinding = button.GetBindingExpression(Button.IsEnabledProperty)!.ParentBinding;
        try
        {
            button.IsEnabled = false; Layout(content);
            Assert.Equal(Visibility.Collapsed, spinner.Visibility);
            Assert.Equal(Color.FromRgb(138, 147, 159), Assert.IsType<SolidColorBrush>(label.Foreground).Color);
            probe.IsCompiling = true; Layout(content);
            Assert.Equal(Visibility.Visible, spinner.Visibility);
            Assert.True(Assert.IsType<RotateTransform>(spinner.RenderTransform).HasAnimatedProperties);
            Assert.Equal(Color.FromRgb(255, 196, 0), Assert.IsType<SolidColorBrush>(label.Foreground).Color);
            probe.IsCompiling = false; Layout(content);
            Assert.Equal(Visibility.Collapsed, spinner.Visibility);
            Assert.False(Assert.IsType<RotateTransform>(spinner.RenderTransform).HasAnimatedProperties);
            Assert.Equal(Color.FromRgb(138, 147, 159), Assert.IsType<SolidColorBrush>(label.Foreground).Color);
            button.IsEnabled = true; Layout(content);
            Assert.Equal(Color.FromRgb(241, 243, 245), Assert.IsType<SolidColorBrush>(label.Foreground).Color);
        }
        finally
        {
            spinner.ClearValue(FrameworkElement.DataContextProperty); label.ClearValue(FrameworkElement.DataContextProperty);
            button.SetBinding(Button.IsEnabledProperty, enabledBinding);
        }
    }

    private sealed class CompilationStatusProbe : INotifyPropertyChanged
    {
        private bool _compiling;
        public bool IsCompiling { get => _compiling; set { _compiling = value; PropertyChanged?.Invoke(this, new(nameof(IsCompiling))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
