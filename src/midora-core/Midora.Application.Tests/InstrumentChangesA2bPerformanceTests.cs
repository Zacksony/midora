using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class InstrumentChangesA2bPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasureBoundedLargeWrapperOperations()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MIDORA_A2B_GROUPS"), out int count)) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var guard = new Timer(_ => { using var p = Process.GetCurrentProcess(); if (p.PrivateMemorySize64 >= 8L * 1024 * 1024 * 1024) cancel.Cancel(); }, null, 0, 200);
        foreach (bool direct in new[] { true, false })
        {
            using var project = new MidoraProject(480);
            var root = new MidiChannelRoot(project) { Name = "R" }; project.MidiChannelRoots.Add(root);
            var track = new PureMidiTrack(project) { Name = "T", MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(track);
            var segment = new MidiSegment(project) { LengthTicks = count * 10L + 1000 }; track.Segments.Add(segment);
            var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0];
            voice.Events.Clear(); instrument.TemplateLengthTicks = count * 10L + 1000;
            var owner = direct ? new InstrumentChangeOwner(segment.Id) : new(voice.Id, instrument.Id);
            var phases = new PhaseTiming();
            using var scope = BulkEditPreparationContext.Enter(cancel.Token, phases, project: project);
            var watch = Stopwatch.StartNew();
            var append = ProjectDomainEditCommands.AppendInstrumentChanges(owner, Enumerable.Range(0, count)
                .Select(i => new InstrumentChangeValue(new(i + 1), i * 10L, i % 128, (i / 128) % 128, i % 128))).Prepare(project);
            append.Apply(project); (append as IDisposable)?.Dispose();
            output.WriteLine($"{(direct ? "MIDI" : "SubVoice")} append {count:N0}: {watch.Elapsed.TotalSeconds:F3}s; heap={GC.GetTotalMemory(false) / 1048576d:F1} MiB");
            var source = InstrumentChangeSelectionQuery.Capture(project, owner);
            // Newly allocated raw members are consecutive. A scalar interval
            // avoids a fixture-sized HashSet (the UI uses compressed ID pages).
            var ids = new RangeIds(source.Groups.Values.First().BankEventId.Value, count * (direct ? 3 : 2));
            watch.Restart();
            int resolved = InstrumentChangeSelectionQuery.EnumerateGroups(source.Groups, ids, cancel.Token).Count(group => source.Read(group) is not null);
            output.WriteLine($"Resolve {resolved:N0} wrappers: {watch.Elapsed.TotalSeconds:F3}s");
            foreach (var operation in new[] { InstrumentChangeOperation.Move, InstrumentChangeOperation.Properties, InstrumentChangeOperation.Quantize, InstrumentChangeOperation.Delete })
            {
                using var resources = new BoundedEditResourceLease(scope.Resources);
                watch.Restart(); phases.Reset();
                var command = ProjectDomainEditCommands.EditInstrumentChanges(owner, ids,
                    new(operation, TickDelta: 1, BankMsb: 30, Grid: TimelineQuantizeGrid.FromCustomTicks(3)));
                var edit = InstrumentChangeMaintenance.Wrap(project, ((IProgressReportingProjectEditCommand)command).Prepare(project, cancel.Token, phases));
                using var publication = resources.Complete(cancel.Token);
                double prepare = watch.Elapsed.TotalSeconds; watch.Restart(); edit.Apply(project); publication.MarkPublished();
                double apply = watch.Elapsed.TotalMilliseconds; watch.Restart(); edit.Undo(project);
                output.WriteLine($"{operation}: prepare={prepare:F3}s; apply={apply:F2}ms; undo={watch.Elapsed.TotalMilliseconds:F2}ms; heap={GC.GetTotalMemory(false)/1048576d:F1} MiB");
                output.WriteLine(phases.Describe());
                Assert.Equal(count, InstrumentChangeSelectionQuery.Capture(project, owner).Groups.Count);
                (edit as IDisposable)?.Dispose();
            }
            output.WriteLine($"Budget peaks: working={scope.Resources.PeakWorkingBytes/1048576d:F1} MiB; resident={scope.Resources.PeakResidentBytes/1048576d:F1} MiB; spill={scope.Resources.PeakSpillBytes/1048576d:F1} MiB");
        }
        using var process = Process.GetCurrentProcess();
        output.WriteLine($"Peak WS={process.PeakWorkingSet64/1048576d:F1} MiB; private={process.PrivateMemorySize64/1048576d:F1} MiB");
    }
    private sealed class PhaseTiming : IProgress<TimelineEditPreparationProgress>
    {
        private readonly Dictionary<TimelineEditPreparationPhase, double> _times = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimelineEditPreparationPhase _phase;
        public void Reset() { _times.Clear(); _clock.Restart(); _phase = default; }
        public void Report(TimelineEditPreparationProgress value)
        { _times[_phase] = _times.GetValueOrDefault(_phase) + _clock.Elapsed.TotalSeconds; _clock.Restart(); _phase = value.Phase; }
        public string Describe() => string.Join("; ", _times.Select(pair => $"{pair.Key}={pair.Value:F3}s"));
    }
    private sealed class RangeIds(long start, int count) : IReadOnlySet<MidoraId>
    {
        public int Count => count;
        public bool Contains(MidoraId id) => id.Value >= start && id.Value - start < count;
        public IEnumerator<MidoraId> GetEnumerator() { for (int i = 0; i < count; i++) yield return new(start + i); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<MidoraId> other) => IsSubsetOf(other) && other.Any(id => !Contains(id));
        public bool IsProperSupersetOf(IEnumerable<MidoraId> other) => IsSupersetOf(other) && !SetEquals(other);
        public bool IsSubsetOf(IEnumerable<MidoraId> other) => this.All(other.Contains);
        public bool IsSupersetOf(IEnumerable<MidoraId> other) => other.All(Contains);
        public bool Overlaps(IEnumerable<MidoraId> other) => other.Any(Contains);
        public bool SetEquals(IEnumerable<MidoraId> other) => IsSupersetOf(other) && other.Distinct().Count() == count;
    }
}
