using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop.Presentation.Controls;

/// <summary>
/// Small device-resolution UI glyphs, isolated from the timeline's aliased
/// geometry renderer. One current DPI/theme entry per tool, never per note/tile.
/// </summary>
internal sealed class TimelineSelectionIconCache(Action invalidate)
{
    internal const int MaximumEntries = 18;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    internal int Count => _entries.Count;
    internal long PixelBytes => _entries.Values.Sum(entry => entry.Image is { } image
        ? (long)image.PixelWidth * image.PixelHeight * 4 : 0);

    internal BitmapSource Get(string key, Geometry geometry, Brush brush, DpiScale dpi)
    {
        if (_entries.TryGetValue(key, out Entry? entry)
            && (!ReferenceEquals(entry.Geometry, geometry) || !ReferenceEquals(entry.Brush, brush)
                || entry.Dpi.DpiScaleX != dpi.DpiScaleX || entry.Dpi.DpiScaleY != dpi.DpiScaleY))
        {
            entry.Dispose(); _entries.Remove(key); entry = null;
        }
        if (entry is null)
        {
            if (_entries.Count == MaximumEntries) Clear();
            entry = new(geometry, brush, dpi, invalidate);
            _entries.Add(key, entry);
        }
        return entry.GetImage();
    }

    internal static Rect Destination(BitmapSource image, Rect cell, DpiScale dpi)
    {
        double size = TimelineSelectionActions.IconSize;
        // One transparent device pixel surrounds the geometry. Snap the bitmap,
        // not its curved paths, so moving the toolbar does not resample its ink.
        return new((Math.Round((cell.X + (cell.Width - size) / 2) * dpi.DpiScaleX) - 1) / dpi.DpiScaleX,
            (Math.Round((cell.Y + (cell.Height - size) / 2) * dpi.DpiScaleY) - 1) / dpi.DpiScaleY,
            image.PixelWidth / dpi.DpiScaleX, image.PixelHeight / dpi.DpiScaleY);
    }

    internal void Clear()
    {
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
    }

    private sealed class Entry : IDisposable
    {
        internal Geometry Geometry { get; }
        internal Brush Brush { get; }
        internal DpiScale Dpi { get; }
        internal BitmapSource? Image { get; private set; }
        private readonly Action _invalidate;

        internal Entry(Geometry geometry, Brush brush, DpiScale dpi, Action invalidate)
        {
            Geometry = geometry; Brush = brush; Dpi = dpi; _invalidate = invalidate;
            if (!geometry.IsFrozen) geometry.Changed += OnResourceChanged;
            if (!brush.IsFrozen) brush.Changed += OnResourceChanged;
        }

        private void OnResourceChanged(object? sender, EventArgs args)
        {
            Image = null;
            _invalidate();
        }

        internal BitmapSource GetImage()
        {
            if (Image is { } cached) return cached;
            double size = TimelineSelectionActions.IconSize;
            var visual = new DrawingVisual(); // Independent, normal WPF antialiasing, like menu icons.
            using (var context = visual.RenderOpen())
                TimelineSurface.DrawSelectionToolGeometry(context, Geometry,
                    new Rect(1 / Dpi.DpiScaleX, 1 / Dpi.DpiScaleY, size, size), Brush);
            var image = new RenderTargetBitmap((int)Math.Ceiling(size * Dpi.DpiScaleX) + 2,
                (int)Math.Ceiling(size * Dpi.DpiScaleY) + 2,
                96 * Dpi.DpiScaleX, 96 * Dpi.DpiScaleY, PixelFormats.Pbgra32);
            image.Render(visual); image.Freeze(); Image = image;
            return image;
        }

        public void Dispose()
        {
            if (!Geometry.IsFrozen) Geometry.Changed -= OnResourceChanged;
            if (!Brush.IsFrozen) Brush.Changed -= OnResourceChanged;
            Image = null;
        }
    }
}
