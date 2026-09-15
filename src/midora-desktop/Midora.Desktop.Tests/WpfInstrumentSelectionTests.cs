using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Application;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed partial class WpfInteractionRegressionTests
{
    private static void AssertInstrumentSelectionDialogBamlAndDraft()
    {
        int submissions = 0;
        var resolver = new InstrumentCatalogResolver(InstrumentCatalogState.Default, Array.Empty<ApplicationSoundFontPreference>());
        var dialog = new InstrumentSelectionDialog(new(8, null, null), new(1, 2, 3), true, resolver,
            InstrumentAuditionPreferences.Default, MidiChannelMode.Melodic, null,
            _ => { submissions++; return Task.FromResult<string?>(null); });
        // Construct and lay out real BAML without starting the application or an audio worker.
        dialog.Measure(new Size(900, 760)); dialog.Arrange(new Rect(0, 0, 900, 760)); dialog.UpdateLayout();
        var msb = Assert.IsType<TextBox>(dialog.FindName("MsbBox"));
        var lsb = Assert.IsType<TextBox>(dialog.FindName("LsbBox"));
        var program = Assert.IsType<TextBox>(dialog.FindName("ProgramBox"));
        Assert.Equal("8", msb.Text); Assert.Equal("2", lsb.Text); Assert.Equal("3", program.Text);
        Assert.True(msb.IsEnabled); Assert.False(lsb.IsEnabled); Assert.False(program.IsEnabled);
        Assert.Equal(0, submissions);
        var list = Assert.IsType<ListBox>(dialog.FindName("ProgramsList"));
        Assert.Equal(128, list.Items.Count);
        list.SelectedIndex = 127;
        Assert.True(msb.IsEnabled); Assert.True(lsb.IsEnabled); Assert.True(program.IsEnabled);
        Assert.Equal("127", program.Text); Assert.Equal(0, submissions);
        Assert.Equal(500, dialog.AuditionPreferences.DurationMilliseconds);
        Assert.NotNull(dialog.FindResource("Button.Dialog.Confirm"));
        var lane = new InstrumentChangeLane();
        lane.Measure(new Size(700, 180)); lane.Arrange(new Rect(0, 0, 700, 180));
        Assert.NotNull(lane.Content);

        // Off-screen native dialogs exercise the real modal dispatcher and
        // explicit keyboard commit/cancel handlers, without computer-use.
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
        {
            var longNames = new InstrumentCatalogResolver(InstrumentCatalogState.Default with
                { Overrides = [new(0, 0, 0, "A long bank name " + new string('B', 100), "A long program name " + new string('P', 100))] }, Array.Empty<ApplicationSoundFontPreference>());
            var measured = new InstrumentSelectionDialog(new(0, 0, 0), new(0, 0, 0), false, longNames,
                InstrumentAuditionPreferences.Default, MidiChannelMode.Percussion, null,
                _ => Task.FromResult<string?>(null));
            // An unshown Window does not measure its content. Measure the real
            // resource-backed content as a root at each controlled DPI instead.
            var content = Assert.IsAssignableFrom<FrameworkElement>(measured.Content); measured.Content = null;
            VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi, dpi));
            content.Measure(new Size(730, 650)); content.Arrange(new Rect(0, 0, 730, 650)); content.UpdateLayout();
            var ok = Assert.IsType<Button>(measured.FindName("OkButton"));
            Point bottom = ok.TranslatePoint(new Point(ok.ActualWidth, ok.ActualHeight), content);
            Assert.InRange(bottom.X, 1, 730); Assert.InRange(bottom.Y, 1, 650);
            Assert.InRange(Assert.IsType<ListBox>(measured.FindName("ProgramsList")).ActualHeight, 30, 450);
        }
        foreach (Key key in new[] { Key.Escape, Key.Enter })
        {
            int accepted = 0;
            var modal = new InstrumentSelectionDialog(new(1, null, 3), new(0, 2, 0), true, resolver,
                InstrumentAuditionPreferences.Default, MidiChannelMode.Melodic, null,
                values => { accepted++; Assert.Equal(new InstrumentSelectionValues(1, null, 3), values); return Task.FromResult<string?>(null); })
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Opacity = 0, ShowInTaskbar = false };
            modal.Loaded += (_, _) => modal.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var source = PresentationSource.FromVisual(modal)!;
                modal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }));
            Assert.Equal(key == Key.Enter, modal.ShowDialog());
            Assert.Equal(key == Key.Enter ? 1 : 0, accepted);
        }
    }
}
