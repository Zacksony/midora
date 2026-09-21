using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Midora.Desktop.Presentation.Controls;

// Like the selection-tool glyphs, rounded caps need their own antialiased
// image: the timeline deliberately renders its remaining geometry aliased.
internal sealed class TimelineTemplateMarkerCapCache(Action invalidate)
{
    private readonly Dictionary<TemplateTimelineMarker, Entry> _entries = [];
    internal int Count => _entries.Count;
    internal long PixelBytes => _entries.Values.Sum(entry => entry.Image is { } image
        ? (long)image.PixelWidth * image.PixelHeight * 4 : 0);

    internal BitmapSource Get(TemplateTimelineMarker marker, Size size, Brush brush, DpiScale dpi)
    {
        if ((uint)marker >= 4) throw new ArgumentOutOfRangeException(nameof(marker));
        if (_entries.TryGetValue(marker, out Entry? entry)
            && (entry.Size != size || !ReferenceEquals(entry.Brush, brush)
                || entry.Dpi.DpiScaleX != dpi.DpiScaleX || entry.Dpi.DpiScaleY != dpi.DpiScaleY))
        {
            entry.Dispose(); _entries.Remove(marker); entry = null;
        }
        if (entry is null)
        {
            entry = new(size, brush, dpi, invalidate);
            _entries.Add(marker, entry);
        }
        return entry.GetImage();
    }

    internal static Rect Destination(BitmapSource image, Rect cap, DpiScale dpi) => new(
        (Math.Round(cap.X * dpi.DpiScaleX) - 1) / dpi.DpiScaleX,
        (Math.Round(cap.Y * dpi.DpiScaleY) - 1) / dpi.DpiScaleY,
        image.PixelWidth / dpi.DpiScaleX, image.PixelHeight / dpi.DpiScaleY);

    internal void Clear()
    {
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
    }

    private sealed class Entry : IDisposable
    {
        internal Size Size { get; }
        internal Brush Brush { get; }
        internal DpiScale Dpi { get; }
        internal BitmapSource? Image { get; private set; }
        private readonly Action _invalidate;

        internal Entry(Size size, Brush brush, DpiScale dpi, Action invalidate)
        {
            Size = size; Brush = brush; Dpi = dpi; _invalidate = invalidate;
            if (!brush.IsFrozen) brush.Changed += OnBrushChanged;
        }

        private void OnBrushChanged(object? sender, EventArgs args)
        {
            Image = null;
            _invalidate();
        }

        internal BitmapSource GetImage()
        {
            if (Image is { } cached) return cached;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRoundedRectangle(Brush, null,
                    new Rect(1 / Dpi.DpiScaleX, 1 / Dpi.DpiScaleY, Size.Width, Size.Height), 2, 2);
            // A transparent device-pixel gutter preserves the corner coverage.
            var image = new RenderTargetBitmap((int)Math.Ceiling(Size.Width * Dpi.DpiScaleX) + 2,
                (int)Math.Ceiling(Size.Height * Dpi.DpiScaleY) + 2,
                96 * Dpi.DpiScaleX, 96 * Dpi.DpiScaleY, PixelFormats.Pbgra32);
            image.Render(visual); image.Freeze(); Image = image;
            return image;
        }

        public void Dispose()
        {
            if (!Brush.IsFrozen) Brush.Changed -= OnBrushChanged;
            Image = null;
        }
    }
}
