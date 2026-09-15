using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

/// <summary>Read-only, frozen source coordinates. Never passed to a hit-test or selection index.</summary>
public sealed record TimelineOnionClip(ITimelineRenderItemSource Source, long StartTick, long EndTick, long Shift);
public sealed record TimelineOnionTrack(MidoraId Id, uint Color, IReadOnlyList<TimelineOnionClip> Clips);

public sealed class TimelineOnionSnapshot
{
    public const int TracksPerBlock = 8;
    public TimelineOnionSnapshot(string identity, IEnumerable<TimelineOnionTrack> tracks, double opacity, int tracksPerBlock = TracksPerBlock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        Identity = identity;
        Opacity = opacity;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tracksPerBlock);
        Blocks = tracks.Chunk(tracksPerBlock).Select(group => (IReadOnlyList<TimelineOnionTrack>)group).ToArray();
    }
    public string Identity { get; }
    public double Opacity { get; }
    public IReadOnlyList<IReadOnlyList<TimelineOnionTrack>> Blocks { get; }

    public TimelineOnionSnapshot FitVisibleCacheBudget(int maximumBlocks)
    {
        maximumBlocks = Math.Max(1, maximumBlocks);
        if (Blocks.Count <= maximumBlocks) return this;
        int trackCount = Blocks.Sum(b => b.Count);
        return new(Identity, Blocks.SelectMany(b => b), Opacity, (trackCount + maximumBlocks - 1) / maximumBlocks);
    }

    public ulong Fingerprint(int block, long start, long end, int firstLane, int lastLane)
    {
        ulong result = unchecked((ulong)BitConverter.DoubleToInt64Bits(Opacity));
        foreach (var track in Blocks[block])
        {
            result = TimelineContentFingerprint.Combine(result, (ulong)track.Id.Value);
            result = TimelineContentFingerprint.Combine(result, track.Color);
            foreach (var clip in track.Clips)
            {
                long left = Math.Max(clip.StartTick, SaturatingSubtract(start, clip.Shift));
                long right = Math.Min(clip.EndTick, SaturatingSubtract(end, clip.Shift));
                if (right <= left) continue;
                result = TimelineContentFingerprint.Combine(result, unchecked((ulong)clip.Shift));
                result = TimelineContentFingerprint.Combine(result, unchecked((ulong)clip.StartTick));
                result = TimelineContentFingerprint.Combine(result, unchecked((ulong)clip.EndTick));
                result = TimelineContentFingerprint.Combine(result,
                    clip.Source is INonBlockingTimelineFingerprintSource { CanComputeRangeFingerprintWithoutBlocking: true }
                        ? clip.Source.GetRangeFingerprint(left, right, firstLane, lastLane)
                        : clip.Source.ContentFingerprint);
            }
        }
        return result;
    }

    internal static long SaturatingSubtract(long value, long offset) => offset > 0 && value < long.MinValue + offset
        ? long.MinValue : offset < 0 && value > long.MaxValue + offset ? long.MaxValue : value - offset;
}

/// <summary>One bounded device tile; overlapping notes are aggregated before painting.</summary>
public static class TimelineOnionRasterizer
{
    public const int Width = 512;
    public const int Height = 128;

    public static TimelineRasterBuffer Render(TimelineOnionSnapshot snapshot, int block,
        long tileX, long tileY, double pixelsPerTick, double pixelsPerLane,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(pixelsPerTick) || pixelsPerTick <= 0
            || !double.IsFinite(pixelsPerLane) || pixelsPerLane <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerTick));
        double originX = (double)tileX * Width, originY = (double)tileY * Height;
        long start = (long)Math.Clamp(Math.Floor(originX / pixelsPerTick) - 1, 0, long.MaxValue);
        long end = (long)Math.Clamp(Math.Ceiling((originX + Width) / pixelsPerTick) + 1, 0, long.MaxValue);
        int first = (int)Math.Clamp(Math.Floor(originY / pixelsPerLane), 0, 127);
        int last = (int)Math.Clamp(Math.Ceiling((originY + Height) / pixelsPerLane), 0, 128);
        byte[] pixels = new byte[Width * Height * 4];
        int[] coverage = new int[(last - first) * (Width + 1)];
        TimelineRasterColumnSummary[] summaries = new TimelineRasterColumnSummary[Width];
        int candidates = 0;
        foreach (var track in snapshot.Blocks[block])
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(coverage);
            foreach (var clip in track.Clips)
            {
                long left = Math.Max(clip.StartTick, TimelineOnionSnapshot.SaturatingSubtract(start, clip.Shift));
                long right = Math.Min(clip.EndTick, TimelineOnionSnapshot.SaturatingSubtract(end, clip.Shift));
                if (right <= left) continue;
                // Reuse the existing immutable hierarchy rather than visiting millions of
                // hidden-by-density notes for every tile. Ghosts need occupancy, not edges.
                if (clip.Source.Count > 4096 && clip.Source is ITimelineRasterAggregateSource aggregate)
                {
                    Array.Clear(summaries);
                    var projection = new TimelineRasterColumnProjection(left, right, left,
                        ((double)left + clip.Shift) * pixelsPerTick - originX, pixelsPerTick, Width);
                    if (aggregate.TryAccumulateRasterColumns(TimelineRasterAggregateKind.PianoNotes,
                        projection, first, last, summaries, out int work, cancellationToken))
                    {
                        candidates = (int)Math.Min(int.MaxValue, (long)candidates + work);
                        for (int lane = first; lane < last; lane++)
                        for (int x = 0; x < Width; x++)
                        {
                            bool occupied = lane < 64
                                ? (summaries[x].LaneMaskLow & (1UL << lane)) != 0
                                : (summaries[x].LaneMaskHigh & (1UL << (lane - 64))) != 0;
                            if (!occupied) continue;
                            int row = (lane - first) * (Width + 1);
                            coverage[row + x]++; coverage[row + x + 1]--;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }
                }
                clip.Source.VisitInto(left, right, first, last, note =>
                {
                    if (candidates < int.MaxValue) candidates++;
                    if ((candidates & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (note.Lane < first || note.Lane >= last) return;
                    long a = Math.Max(note.StartTick, clip.StartTick);
                    long b = Math.Min(note.EndTick, clip.EndTick);
                    if (b <= a && (note.StartTick != note.EndTick || note.StartTick < clip.StartTick
                        || note.StartTick >= clip.EndTick)) return;
                    double x = ((double)a + clip.Shift) * pixelsPerTick - originX;
                    double xEnd = Math.Max(x + 1, ((double)b + clip.Shift) * pixelsPerTick - originX);
                    int x0 = (int)Math.Clamp(Math.Floor(x), 0, Width);
                    int x1 = (int)Math.Clamp(Math.Ceiling(xEnd), 0, Width);
                    if (x1 <= x0) return;
                    int row = (note.Lane - first) * (Width + 1);
                    coverage[row + x0]++;
                    coverage[row + x1]--;
                });
            }
            int alpha = (int)Math.Round(snapshot.Opacity * 255);
            int red = (int)(track.Color >> 16 & 255) * alpha / 255;
            int green = (int)(track.Color >> 8 & 255) * alpha / 255;
            int blue = (int)(track.Color & 255) * alpha / 255;
            for (int lane = first; lane < last; lane++)
            {
                int top = (int)Math.Clamp(Math.Round(lane * pixelsPerLane - originY), 0, Height);
                int bottom = (int)Math.Clamp(Math.Round((lane + 1) * pixelsPerLane - originY), 0, Height);
                int count = 0, row = (lane - first) * (Width + 1);
                for (int x = 0; x < Width; x++)
                {
                    count += coverage[row + x];
                    if (count == 0) continue;
                    for (int y = top; y < bottom; y++)
                    {
                        int p = (y * Width + x) * 4;
                        pixels[p] = (byte)(blue + pixels[p] * (255 - alpha) / 255);
                        pixels[p + 1] = (byte)(green + pixels[p + 1] * (255 - alpha) / 255);
                        pixels[p + 2] = (byte)(red + pixels[p + 2] * (255 - alpha) / 255);
                        pixels[p + 3] = (byte)(alpha + pixels[p + 3] * (255 - alpha) / 255);
                    }
                }
            }
        }
        return new(Width, Height, pixels, candidates);
    }
}
