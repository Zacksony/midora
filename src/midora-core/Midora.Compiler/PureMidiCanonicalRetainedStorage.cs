using System.Runtime.CompilerServices;
using Midora.Common;
using Midora.Domain;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private sealed partial class PureMidiPagedCanonicalSource : IRetainedStorageSource
    {
        public void CollectRetainedStorage(RetainedStorageCollector collector)
        {
            if (!collector.Add(this, 192)) return;
            collector.Text(ContentFingerprint);
            collector.Dictionary(_unitByRoot);
            collector.Dictionary(_analysis);
            collector.Dictionary(_channelAnalysis);
            collector.Dictionary(_terminalNotes);
            foreach (long[] counts in _terminalNotes.Values) collector.Array(counts);
            // These are immutable analysis metadata, not note/event source pages.
            // Domain roots retain their own shared source/cache accounting.
            foreach (RootIntervalAnalysis value in _analysis.Values)
            {
                collector.Add(value, 40);
                collector.Array(value.UsedTargets);
                collector.Dictionary(value.RawBoundaryNotes);
                foreach (long[] keys in value.RawBoundaryNotes.Values) collector.Array(keys);
            }
            foreach (SegmentChannelAnalysis value in _channelAnalysis.Values)
            {
                collector.Add(value, 96);
                collector.Array(value.UsedTargets);
                collector.Array(value.RawBoundaryNoteKeys);
                collector.Array(value.ReferencedBanks);
                collector.Array(value.ReferencedPrograms);
                collector.Array(value.StateCheckpoints);
                foreach (ChannelStateCheckpoint checkpoint in value.StateCheckpoints)
                {
                    collector.Add(checkpoint, 40);
                    collector.Dictionary(checkpoint.State);
                }
            }
            collector.Add(_resetDefaults, 16 * 1024); // bounded MIDI initial-state maps
            collector.Add(_plan, 48);
            collector.Array(_plan.Roots);
            collector.Array(_plan.TrackDescriptors);
            collector.Array(_plan.ChannelModeSystemExclusiveEvents);
            if (collector.Array(_plan.OpaqueEvents))
                foreach (CanonicalOpaqueMidiEvent value in _plan.OpaqueEvents)
                    collector.Bytes(value.Payload);
            foreach (PureMidiRootPlan root in _plan.Roots)
            {
                collector.Add(root, 56);
                collector.Array(root.Tracks);
                collector.Array(root.Intervals);
                foreach (PureMidiRootInterval interval in root.Intervals) collector.Add(interval, 80);
                foreach (PureMidiTrackPlan track in root.Tracks)
                {
                    collector.Add(track, 56);
                    collector.Array(track.Segments);
                }
            }
            collector.Add(PureMidiPresetReferences, 32L +
                (long)PureMidiPresetReferences.Count * Unsafe.SizeOf<CanonicalMidiPresetReference>());
            collector.Add(PureMidiAudioFragments, 32L + 8L * PureMidiAudioFragments.Count);
            foreach (CanonicalPureMidiAudioFragmentDescriptor fragment in PureMidiAudioFragments)
            {
                collector.Add(fragment, 96);
                collector.Text(fragment.SemanticFingerprint);
                collector.Add(fragment.ContributingTrackIds, 32L + 8L * fragment.ContributingTrackIds.Count);
            }
        }
    }
}
