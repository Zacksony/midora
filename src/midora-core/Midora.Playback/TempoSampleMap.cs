using Midora.Compiler;
using Midora.Common;

namespace Midora.Playback;

public sealed class TempoSampleMap : IRetainedStorageSource
{
    private readonly CanonicalTempo[] _tempos;
    private readonly decimal[] _secondsAtTempo;

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 48)) return;
        collector.Array(_tempos);
        collector.Array(_secondsAtTempo);
    }

    public TempoSampleMap(int ticksPerQuarterNote, ReadOnlySpan<CanonicalTempo> tempos)
    {
        if (ticksPerQuarterNote <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        }
        if (tempos.IsEmpty || tempos[0].Tick < 0)
        {
            throw new ArgumentException("Tempo map must contain an effective non-negative starting state.", nameof(tempos));
        }
        TicksPerQuarterNote = ticksPerQuarterNote;
        _tempos = tempos.ToArray();
        _secondsAtTempo = new decimal[_tempos.Length];
        _secondsAtTempo[0] = SegmentSeconds(_tempos[0].Tick, _tempos[0].BeatsPerMinute);
        for (int i = 0; i < _tempos.Length; i++)
        {
            if (_tempos[i].Tick < 0 || _tempos[i].BeatsPerMinute <= 0
                || (i != 0 && _tempos[i].Tick <= _tempos[i - 1].Tick))
            {
                throw new ArgumentException("Tempo changes must be positive and strictly ordered.", nameof(tempos));
            }
            if (i != 0)
            {
                _secondsAtTempo[i] = checked(_secondsAtTempo[i - 1]
                    + SegmentSeconds(_tempos[i].Tick - _tempos[i - 1].Tick, _tempos[i - 1].BeatsPerMinute));
            }
        }
    }

    public int TicksPerQuarterNote { get; }

    public decimal SecondsAtTick(long tick)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }
        int index = FindTempoIndex(tick);
        return checked(_secondsAtTempo[index] + SegmentSeconds(tick - _tempos[index].Tick, _tempos[index].BeatsPerMinute));
    }

    public long TickToSampleFrame(long tick, long originTick, int sampleRate)
    {
        if (tick < originTick || sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(tick < originTick ? nameof(tick) : nameof(sampleRate));
        }
        decimal frames = checked((SecondsAtTick(tick) - SecondsAtTick(originTick)) * sampleRate);
        return checked((long)decimal.Round(frames, 0, MidpointRounding.AwayFromZero));
    }

    public long SampleFrameToTick(long sampleFrame, long originTick, int sampleRate, long maximumTick)
    {
        if (sampleFrame < 0 || originTick < 0 || sampleRate <= 0 || maximumTick < originTick)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleFrame));
        }
        long low = originTick;
        long high = maximumTick;
        while (low < high)
        {
            long distance = high - low;
            long middle = low + (distance / 2) + (distance % 2);
            if (TickToSampleFrame(middle, originTick, sampleRate) <= sampleFrame)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        return low;
    }

    private decimal SegmentSeconds(long ticks, decimal bpm) =>
        checked((decimal)ticks * 60m / (bpm * TicksPerQuarterNote));

    private int FindTempoIndex(long tick)
    {
        int low = 0;
        int high = _tempos.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (_tempos[middle].Tick <= tick)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        return low;
    }
}
