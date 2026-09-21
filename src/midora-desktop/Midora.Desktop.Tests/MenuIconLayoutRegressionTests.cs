using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed partial class WpfInteractionRegressionTests
{
    // Called on the existing Application's STA with the actual shared BAML theme.
    private static void AssertMenuIconsHaveTheirOwnFullWidthSlot()
    {
        var menu = new ContextMenu();
        foreach (int size in new[] { 16, 20 })
            foreach (var descriptor in TimelineSelectionActions.All)
            {
                var icon = new FluentIcon { Width = size, Height = size };
                icon.SetResourceReference(FluentIcon.DataProperty, "Fluent." + descriptor.Icon);
                menu.Items.Add(new MenuItem { Header = descriptor.Label, Icon = icon, InputGestureText = "Ctrl+X" });
            }
        Layout();
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var icon = Assert.IsType<FluentIcon>(item.Icon);
            var presenter = Assert.IsType<ContentPresenter>(item.Template.FindName("IconPresenter", item));
            var header = Assert.IsType<ContentPresenter>(item.Template.FindName("HeaderPresenter", item));
            var column = Assert.IsType<ColumnDefinition>(item.Template.FindName("IconColumn", item));
            Assert.Equal(28, column.ActualWidth);
            Assert.True(presenter.ActualWidth >= icon.Width);
            Assert.Equal(icon.Width, icon.ActualWidth);
            Assert.Equal(icon.Height, icon.ActualHeight);
            Rect bounds = icon.TransformToAncestor(item).TransformBounds(new Rect(icon.RenderSize));
            double headerX = header.TranslatePoint(new Point(), item).X;
            Assert.True(headerX - bounds.Right >= 7.99, $"{item.Header}: icon/header gap {headerX - bounds.Right}");

            item.IsEnabled = false; Layout();
            Assert.True(presenter.ActualWidth >= icon.Width);
            item.IsEnabled = true;
            item.IsCheckable = true; item.IsChecked = true; Layout();
            Assert.Equal(Visibility.Collapsed, presenter.Visibility);
            var check = Assert.IsType<TextBlock>(item.Template.FindName("CheckMark", item));
            Assert.Equal(Visibility.Visible, check.Visibility);
            Rect checkBounds = check.TransformToAncestor(item).TransformBounds(new Rect(check.RenderSize));
            Assert.True(headerX - checkBounds.Right >= 7.99);
            item.IsChecked = false;
        }
        Layout();
        string path = Path.Combine(AppContext.BaseDirectory, ".tmp", "a3-visual", "menu-icons.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),
            (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);

        var menubar = new Menu();
        var top = new MenuItem { Header = "Application" }; top.Items.Add(new MenuItem { Header = "Settings" });
        menubar.Items.Add(top);
        menubar.Measure(new Size(500, 40)); menubar.Arrange(new Rect(0, 0, 500, 40)); menubar.UpdateLayout();
        Assert.Equal(0, Assert.IsType<ColumnDefinition>(top.Template.FindName("IconColumn", top)).ActualWidth);

        void Layout()
        {
            menu.Measure(new Size(600, 1500)); menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
        }
    }
}
