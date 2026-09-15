using System.Runtime.CompilerServices;
using Midora.Common;

namespace Midora.Audio;

public sealed partial class MidiRenderPlan : IRetainedStorageSource
{
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 160)) return;
        collector.Array(_sourceIds);
        collector.Array(_initiallyDisabledSourceIndices);
        collector.Array(_unitDescriptors);
        collector.Array(_cacheSourceBindings);
        collector.Array(_referencedPresetKeys);
        // Monitoring clones share these immutable array subtrees. Account each
        // subtree as one identity, not a bookkeeping object per musical event.
        long bytes = 24L + 8L * _ports.Length;
        foreach (MidiPortRenderPlan port in _ports)
            bytes = checked(bytes + 56 + (long)port.Events.Length * Unsafe.SizeOf<ScheduledMidiMessage>());
        collector.Add(_ports, bytes);
        bytes = 24L + 8L * _units.Length;
        foreach (MidiUnitRenderPlan unit in _units)
            bytes = checked(bytes + 56 + (long)unit.Events.Length * Unsafe.SizeOf<ScheduledMidiMessage>());
        collector.Add(_units, bytes);
        bytes = 24L + 8L * _unitFragments.Length;
        foreach (MidiUnitFragmentRenderPlan fragment in _unitFragments)
            bytes = checked(bytes + 200 + TextBytes(fragment.SemanticFingerprint) + TextBytes(fragment.PcmCacheKey)
                + (long)fragment.Events.Length * Unsafe.SizeOf<ScheduledMidiMessage>());
        collector.Add(_unitFragments, bytes);
        bytes = 24L + 8L * _segments.Length;
        foreach (MidiSegmentRenderPlan segment in _segments)
            bytes = checked(bytes + 112 + TextBytes(segment.SemanticFingerprint) + TextBytes(segment.PcmCacheKey));
        collector.Add(_segments, bytes);
        if (EventPageProvider is IRetainedStorageSource source)
            source.CollectRetainedStorage(collector);
    }

    private static long TextBytes(string? text) => text is null ? 0 : (24L + 2L * text.Length + 7) & ~7L;
}
