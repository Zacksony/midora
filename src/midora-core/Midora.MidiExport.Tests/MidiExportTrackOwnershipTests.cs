using System.Reflection;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportTrackOwnershipTests
{
    private delegate MidoraId Resolve(CanonicalCompiledResult result, CanonicalMidiEvent value,
        List<MidiExportDiagnostic> diagnostics);
    private static readonly Resolve ResolveOwner = typeof(CanonicalMidiFileExporter)
        .GetMethod("ResolveTrackId", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Resolve>();

    [Fact]
    public void SourceOwnedFastPathDoesNotAllocateOneClosurePerEvent()
    {
        CanonicalCompiledResult result = Result([], 100);
        CanonicalMidiEvent value = Event(0, MidoraId.FromSequence(7));
        List<MidiExportDiagnostic> diagnostics = [];
        MidoraId resolved = default;
        for (int i = 0; i < 1024; i++) resolved = ResolveOwner(result, value, diagnostics);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) resolved = ResolveOwner(result, value, diagnostics);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(value.Source.TrackId, resolved);
        Assert.Empty(diagnostics);
        // Permit a small fixed runtime overhead, not the former multi-MB
        // growth proportional to the 10,000 event calls.
        Assert.InRange(allocated, 0, 4096);
    }

    [Fact]
    public void StreamingOwnershipMatchesOriginalDistinctOwnerRuleAtAllBoundaries()
    {
        Random random = new(20260910);
        for (int test = 0; test < 1000; test++)
        {
            long tick = random.Next(0, 5) * 25;
            long end = test % 2 == 0 ? tick : 200;
            ChannelUnitAllocation[] allocations = Enumerable.Range(0, random.Next(0, 33)).Select(_ =>
            {
                long start = random.Next(0, 5) * 25;
                int track = random.Next(0, 5);
                return new ChannelUnitAllocation(track == 0 ? default : MidoraId.FromSequence(track),
                    default, default, default, default, default, start, start + random.Next(0, 5) * 25,
                    (byte)random.Next(0, 2), (byte)random.Next(0, 2));
            }).ToArray();
            CanonicalCompiledResult result = Result(allocations, end);
            CanonicalMidiEvent value = Event(tick, default);
            MidoraId[] expected = allocations
                .Where(a => a.ZeroBasedPort == value.ZeroBasedPort && a.ZeroBasedChannel == value.ZeroBasedChannel
                    && (tick == end ? a.StartTick < tick && a.EndTick >= tick : a.StartTick <= tick && a.EndTick > tick))
                .OrderByDescending(a => a.StartTick).Select(a => a.TrackId).Distinct().ToArray();
            List<MidiExportDiagnostic> diagnostics = [];
            MidoraId actual = ResolveOwner(result, value, diagnostics);
            if (expected.Length == 1)
            {
                Assert.Equal(expected[0], actual);
                Assert.Empty(diagnostics);
            }
            else
            {
                Assert.Equal(default, actual);
                MidiExportDiagnostic error = Assert.Single(diagnostics);
                Assert.Equal("MIDORA-MIDI-EXPORT-OWNER", error.Code);
                Assert.Equal(MidiExportDiagnosticCategory.CanonicalConsistency, error.Category);
                Assert.Equal(expected.Length == 0
                    ? "A generated canonical event has no resolvable Logical Track owner."
                    : "A generated canonical event has more than one possible Logical Track owner.", error.Message);
                Assert.Equal(value.Source, error.Source);
            }
        }
    }

    private static CanonicalMidiEvent Event(long tick, MidoraId track) => new(tick, 0, 0,
        MidiMessage.NoteOff(0, 60, 0), CanonicalEventRole.NoteOff, 0, long.MinValue, long.MinValue,
        new(TrackId: track, Tick: tick, Origin: SourceOrigin.CompilerBoundaryCleanup));

    private static CanonicalCompiledResult Result(ChannelUnitAllocation[] allocations, long end) => new(480,
        new(new CompilationRequest { Purpose = CompilationPurpose.MidiExport, EndTick = end }, end,
            CompilationEndTickSource.ExplicitRequest), [],
        new([new CanonicalTempo(0, 120m)], [], [], [], [], null), allocations, [], false, true, null, 0,
        new(0, 0, 0, 0));
}
