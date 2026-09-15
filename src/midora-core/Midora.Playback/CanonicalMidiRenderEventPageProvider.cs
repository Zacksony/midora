using Midora.Audio;
using Midora.Common;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Playback;

internal sealed class CanonicalMidiRenderEventPageProvider :
    IMidiRenderEventDemandAwarePageProvider, IRetainedStorageSource
{
    private readonly CanonicalCompiledResult _compiled;
    private readonly TempoSampleMap _map;
    private readonly int _sampleRate;
    private readonly IReadOnlyDictionary<MidoraId, int> _sourceIndices;
    private readonly IReadOnlySet<MidoraId>? _audibleTrackIds;

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 64)) return;
        _compiled.CollectRetainedStorage(collector);
        _map.CollectRetainedStorage(collector);
        collector.Dictionary(_sourceIndices);
        if (_audibleTrackIds is not null)
            collector.Add(_audibleTrackIds, 128L + 64L * _audibleTrackIds.Count);
    }

    public CanonicalMidiRenderEventPageProvider(
        CanonicalCompiledResult compiled,
        TempoSampleMap map,
        int sampleRate,
        IReadOnlyDictionary<MidoraId, int> sourceIndices,
        IReadOnlySet<MidoraId>? audibleTrackIds)
    {
        _compiled = compiled ?? throw new ArgumentNullException(nameof(compiled));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _sourceIndices = sourceIndices ?? throw new ArgumentNullException(nameof(sourceIndices));
        _audibleTrackIds = audibleTrackIds;
    }

    public IEnumerable<ScheduledPortMidiMessage> Query(
        long startFrame,
        long endFrame,
        CancellationToken cancellationToken = default) =>
        QueryCore(startFrame, endFrame, demand: null, cancellationToken);

    public IEnumerable<ScheduledPortMidiMessage> Query(
        long startFrame,
        long endFrame,
        MidiRenderEventDemandSnapshot demand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        return QueryCore(startFrame, endFrame, demand, cancellationToken);
    }

    private IEnumerable<ScheduledPortMidiMessage> QueryCore(
        long startFrame,
        long endFrame,
        MidiRenderEventDemandSnapshot? demand,
        CancellationToken cancellationToken)
    {
        if (startFrame < 0 || endFrame <= startFrame)
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        long totalFrames = _map.TickToSampleFrame(
            _compiled.EndTick,
            _compiled.StartTick,
            _sampleRate);
        if (startFrame >= totalFrames) yield break;
        endFrame = Math.Min(endFrame, totalFrames);
        long startTick = _map.SampleFrameToTick(
            startFrame,
            _compiled.StartTick,
            _sampleRate,
            _compiled.EndTick);
        long endTick = _map.SampleFrameToTick(
            endFrame,
            _compiled.StartTick,
            _sampleRate,
            _compiled.EndTick);
        if (endTick < _compiled.EndTick) endTick++;
        if (endTick <= startTick) endTick = Math.Min(_compiled.EndTick, startTick + 1);
        if (endTick <= startTick) yield break;

        IReadOnlySet<MidoraId>? demandedSourceIds = null;
        if (demand is not null)
        {
            HashSet<MidoraId> selected = [];
            foreach ((MidoraId sourceId, int sourceIndex) in _sourceIndices)
            {
                if (demand.MayDemandSource(sourceIndex, startFrame, endFrame))
                {
                    selected.Add(sourceId);
                }
            }
            if (selected.Count == 0)
            {
                yield break;
            }
            demandedSourceIds = selected;
        }

        IEnumerable<CanonicalMidiRenderEventPage> pages = demandedSourceIds is null
            ? _compiled.QueryMidiRenderEventPages(
                startTick,
                endTick,
                includeStateAtStart: startFrame == 0,
                cancellationToken)
            : _compiled.QueryMidiRenderEventPages(
                startTick,
                endTick,
                includeStateAtStart: startFrame == 0,
                demandedSourceIds,
                cancellationToken);
        CanonicalMidiRenderEvent[] channelModeEvents = _compiled
            .ChannelModeSystemExclusiveEvents
            .ToArray()
            .Where(value => value.Tick >= startTick && value.Tick < endTick)
            .Select(ToRenderEvent)
            .Where(value => demandedSourceIds is null
                || demandedSourceIds.Contains(value.MonitoringSourceId))
            .OrderBy(value => value, RenderEventComparer.Instance)
            .ToArray();
        foreach (CanonicalMidiRenderEvent value in Merge(
            Enumerate(pages),
            channelModeEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.ChannelModeSystemExclusive is null
                && !MidiRenderPlanAdapter.IsSupportedByInitialReleaseAudioProjection(value.Message))
                continue;
            if (_audibleTrackIds is not null
                && value.TrackId != default
                && !_audibleTrackIds.Contains(value.TrackId))
            {
                continue;
            }
            long frame = _map.TickToSampleFrame(
                value.Tick,
                _compiled.StartTick,
                _sampleRate);
            if (frame < startFrame || frame >= endFrame) continue;
            int sourceIndex = _sourceIndices[value.MonitoringSourceId];
            if (demand is not null
                && !demand.IsSourceDemanded(sourceIndex, frame))
            {
                continue;
            }
            ScheduledMidiMessage scheduled = value.ChannelModeSystemExclusive is { } systemExclusive
                ? ScheduledMidiMessage.CreateChannelModeSystemExclusive(
                    frame,
                    value.Message.ChannelNumber,
                    systemExclusive,
                    sourceIndex)
                : new(frame, value.Message, sourceIndex);
            yield return new(value.ZeroBasedPort, scheduled);
        }
    }

    private static CanonicalMidiRenderEvent ToRenderEvent(
        CanonicalMidiChannelModeSystemExclusiveEvent value) => new(
        value.Tick,
        value.ZeroBasedPort,
        MidiMessage.ProgramChange(value.ZeroBasedChannel, 0),
        value.Source.TrackId,
        value.Source.TrackId,
        value.Role,
        value.StableOrder,
        value.SmfTrackOrder,
        value.SmfEventOrder,
        value.Source.DirectMidiObjectId,
        value.Value with { TargetChannel = value.ZeroBasedChannel });

    private static IEnumerable<CanonicalMidiRenderEvent> Enumerate(
        IEnumerable<CanonicalMidiRenderEventPage> pages)
    {
        foreach (CanonicalMidiRenderEventPage page in pages)
            foreach (CanonicalMidiRenderEvent value in page.Items)
                yield return value;
    }

    private static IEnumerable<CanonicalMidiRenderEvent> Merge(
        IEnumerable<CanonicalMidiRenderEvent> direct,
        IReadOnlyList<CanonicalMidiRenderEvent> channelMode)
    {
        using IEnumerator<CanonicalMidiRenderEvent> enumerator = direct.GetEnumerator();
        bool hasDirect = enumerator.MoveNext();
        int specialIndex = 0;
        while (hasDirect || specialIndex < channelMode.Count)
        {
            if (!hasDirect)
            {
                yield return channelMode[specialIndex++];
                continue;
            }
            if (specialIndex >= channelMode.Count
                || RenderEventComparer.Instance.Compare(
                    enumerator.Current,
                    channelMode[specialIndex]) <= 0)
            {
                yield return enumerator.Current;
                hasDirect = enumerator.MoveNext();
            }
            else
            {
                yield return channelMode[specialIndex++];
            }
        }
    }

    internal sealed class RenderEventComparer : IComparer<CanonicalMidiRenderEvent>
    {
        public static RenderEventComparer Instance { get; } = new();

        public int Compare(CanonicalMidiRenderEvent x, CanonicalMidiRenderEvent y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = x.Role.CompareTo(y.Role);
            if (value != 0) return value;
            value = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort);
            if (value != 0) return value;
            value = x.Message.ChannelNumber.CompareTo(y.Message.ChannelNumber);
            if (value != 0) return value;
            value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
            if (value != 0) return value;
            if (x.Role == CanonicalEventRole.DirectMidi
                && y.Role == CanonicalEventRole.DirectMidi)
            {
                value = x.SmfEventOrder.CompareTo(y.SmfEventOrder);
                if (value != 0) return value;
            }
            value = x.StableOrder.CompareTo(y.StableOrder);
            if (value != 0) return value;
            value = x.TrackId.CompareTo(y.TrackId);
            if (value != 0) return value;
            value = x.StableObjectId.CompareTo(y.StableObjectId);
            if (value != 0) return value;
            bool xHas = x.ChannelModeSystemExclusive.HasValue;
            bool yHas = y.ChannelModeSystemExclusive.HasValue;
            if (xHas != yHas) return xHas ? 1 : -1;
            if (!xHas) return x.Message.PackedValue.CompareTo(y.Message.PackedValue);
            MidiChannelModeSystemExclusive xMode = x.ChannelModeSystemExclusive!.Value;
            MidiChannelModeSystemExclusive yMode = y.ChannelModeSystemExclusive!.Value;
            value = xMode.Kind.CompareTo(yMode.Kind);
            if (value != 0) return value;
            value = xMode.DeviceId.CompareTo(yMode.DeviceId);
            if (value != 0) return value;
            return xMode.ModeValue.CompareTo(yMode.ModeValue);
        }
    }
}
