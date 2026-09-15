using Midora.Domain;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private static CompilerDiagnosticList CollectOverlapDiagnostics(
        List<RawInstance> instances,
        CancellationToken cancellationToken)
    {
        CompilerDiagnosticList.Builder diagnostics = new();
        foreach (IGrouping<(MidoraId UsageId, MidoraId InstrumentId), RawInstance> binding in instances
            .GroupBy(value => (value.UsageId, value.InstrumentId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance first = binding.First();
            if (first.OverlapPolicy is not OverlapPolicy.Reject and not OverlapPolicy.Warn) continue;
            RawInstance[] ordered = binding.OrderBy(value => value.StartTick)
                .ThenBy(value => value.SourceOrder).ToArray();
            if (ordered.Length < 2) continue;

            bool samePitch = first.OverlapScope == OverlapScope.SamePitch;
            int[] pitchBounds = new int[129];
            if (samePitch)
            {
                for (int index = 0; index < ordered.Length; index++)
                {
                    if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    pitchBounds[ordered[index].Pitch + 1]++;
                }
                for (int pitch = 1; pitch < pitchBounds.Length; pitch++)
                    pitchBounds[pitch] += pitchBounds[pitch - 1];
            }
            int[] nextPosition = (int[])pitchBounds.Clone();
            CompilerDiagnosticList.OverlapDiagnosticSourceValue[] sources = new
                CompilerDiagnosticList.OverlapDiagnosticSourceValue[ordered.Length];
            for (int index = 0; index < ordered.Length; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                RawInstance instance = ordered[index];
                int position = samePitch ? nextPosition[instance.Pitch]++ : index;
                sources[position] = new(instance.TrackId, instance.SegmentId,
                    instance.InstanceId, instance.InstrumentId, instance.StartTick);
            }
            DiagnosticSeverity severity = first.OverlapPolicy == OverlapPolicy.Reject
                ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
            int sourceIndex = -1;
            pitchBounds.CopyTo(nextPosition, 0);
            for (int index = 0; index < ordered.Length; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                RawInstance instance = ordered[index];
                int start = (samePitch ? nextPosition[instance.Pitch]++ : index) + 1;
                int low = start;
                int high = samePitch ? pitchBounds[instance.Pitch + 1] : ordered.Length;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (sources[middle].StartTick < instance.EndTick) low = middle + 1;
                    else high = middle;
                }
                if (low != start)
                {
                    if (sourceIndex < 0) sourceIndex = diagnostics.AddOverlapSource(sources, severity);
                    diagnostics.AddRange(sourceIndex, start, low - start);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return diagnostics.Build();
    }
}
