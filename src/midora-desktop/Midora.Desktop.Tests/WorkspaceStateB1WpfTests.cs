using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public static partial class TimelineObjectListIntegrationTests
{
    private static void VerifyB1Restoration(MainWindow window, DesktopSessionController session, FrameworkElement content)
    {
        CurrentTestStage = "B1 actual template close/reopen, lane axes and local list positions";
        var project = session.Project!;
        foreach (int kind in new[] { 0, 1, 2 })
        {
            WorkspaceViewModel Open() => kind switch
            {
                0 => session.OpenSegment(project.Tracks[0].Segments[0].Id),
                1 => session.OpenSegment(project.PureMidiTracks[0].Segments[0].Id),
                _ => session.OpenInstrument(project.EventInstruments[0].Id)
            };
            var workspace = Open();
            if (workspace is InstrumentWorkspaceViewModel instrument) instrument.ActiveSectionIndex = 1;
            SetLanes(workspace, true);
            workspace.ObjectList.IsVisible = true;
            Layout(content);
            var header = Header(workspace);
            var tabs = (ListBox)header.FindName("Tabs");
            var descriptor = tabs.Items.Cast<LaneTabDescriptor>().First(x => x.Key.Type >= 2);
            tabs.SelectedItem = descriptor;
            Layout(content);
            var surface = Surface(header);
            surface.RestoreValueViewport((.23, .77)); Layout(content);
            var expectedAxis = surface.CaptureValueViewport();
            workspace.ObjectList.FirstRow = 19; Layout(content);
            workspace.ObjectList.LayoutWidth = 465;
            long revision = session.Document!.PublicationRevision;
            session.CloseWorkspace(workspace); Layout(content);
            var restored = Open(); Assert.NotSame(workspace, restored);
            if (restored is InstrumentWorkspaceViewModel voice) voice.ActiveSectionIndex = 1;
            Layout(content);
            var restoredHeader = Header(restored);
            Assert.Equal(descriptor.Key, ((LaneTabSession)Field(restoredHeader, "_state")!).Active);
            AxisEquals(expectedAxis, Surface(restoredHeader).CaptureValueViewport());
            Assert.Equal(19, restored.ObjectList.FirstRow);
            Assert.Equal(465, restored.ObjectList.LayoutWidth);
            SetLanes(restored, false); Layout(content); SetLanes(restored, true); Layout(content);
            restoredHeader = Header(restored);
            AxisEquals(expectedAxis, Surface(restoredHeader).CaptureValueViewport());
            Assert.Equal(revision, session.Document.PublicationRevision);
            // Device/layout projection must not publish a new user profile.
            var profileKey = restored.EditorState!.ProfileOwner!.Value;
            Assert.True(session.EditorStates.TryGetProfile(profileKey, out var beforeDpi));
            foreach (double scale in new[] { 1.25, 1.5, 2d, 1d })
            {
                VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale)); Layout(content);
                Assert.True(session.EditorStates.TryGetProfile(profileKey, out var afterDpi));
                Assert.Equal(beforeDpi, afterDpi);
            }
        }

        CurrentTestStage = "B1 switching SubVoice does not clamp restored rows against the previous source";
        var definition = project.EventInstruments[0];
        var first = definition.SubVoices[0].Id;
        var second = new SubVoice(project) { Name = "B1 empty voice" };
        definition.SubVoices.Add(second);
        var editor = session.OpenInstrument(definition.Id); editor.ActiveSectionIndex = 1;
        editor.Selection.Replace(first); session.RefreshWorkspace(editor); Layout(content);
        editor.ObjectList.IsVisible = true; editor.ObjectList.FirstRow = 29; Layout(content);
        editor.Selection.Replace(second.Id); session.RefreshWorkspace(editor); Layout(content);
        Assert.False(editor.ObjectList.IsVisible); Assert.Equal(0, editor.ObjectList.FirstRow);
        editor.ObjectList.IsVisible = true; Layout(content);
        editor.Selection.Replace(first); session.RefreshWorkspace(editor); Layout(content);
        Assert.Equal(29, editor.ObjectList.FirstRow);

        LaneTabHeader Header(WorkspaceViewModel workspace)
        {
            var host = Descendants<LaneTabHost>(content).Single(x => x.IsVisible && ReferenceEquals(x.DataContext, workspace));
            var header = Assert.IsType<LaneTabHeader>(host.Header); header.CommandHost = window; header.Refresh(); Layout(content);
            return header;
        }
        static TimelineSurface Surface(LaneTabHeader header) => Descendants<TimelineSurface>(header.Owner!)
            .Single(x => x.IsVisible && x.IsHitTestVisible);
        static void SetLanes(WorkspaceViewModel workspace, bool visible)
        {
            if (workspace is TimelineWorkspaceViewModel timeline) timeline.IsLowerEditorVisible = visible;
            else ((InstrumentWorkspaceViewModel)workspace).IsLowerEditorVisible = visible;
        }
        static void AxisEquals((double Minimum, double Maximum) expected, (double Minimum, double Maximum) actual)
        { Assert.InRange(actual.Minimum, expected.Minimum - .001, expected.Minimum + .001); Assert.InRange(actual.Maximum, expected.Maximum - .001, expected.Maximum + .001); }
    }
}
