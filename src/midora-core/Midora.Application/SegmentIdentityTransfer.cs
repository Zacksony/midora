using Midora.Domain;

namespace Midora.Application;

/// <summary>Official identity correspondence for a move that changes Segment representation.
/// This describes navigation continuity, not music or a saved presentation snapshot.</summary>
public readonly record struct SegmentIdentityTransfer(MidoraId SourceId, MidoraId TargetId);

internal interface IPreparedSegmentIdentityTransfers
{
    IReadOnlyList<SegmentIdentityTransfer> SegmentIdentityTransfers { get; }
}
