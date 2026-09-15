namespace Midora.Domain;

/// <summary>
/// A selection whose membership test is bounded in-memory work and performs
/// no I/O or lazy whole-source materialization. Dense readers may scan a source
/// once against this set; ordinary IReadOnlySet implementations make no such
/// promise (they can be spill-backed).
/// </summary>
public interface ITimelineInMemoryIdSet : IReadOnlySet<MidoraId>
{
}
