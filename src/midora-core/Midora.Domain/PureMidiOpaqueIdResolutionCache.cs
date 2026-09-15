namespace Midora.Domain;

/// <summary>
/// An immutable source address cache, not a payload cache. Resolving many IDs
/// may scan decoded source pages, but only their ID/ordinal pairs survive the
/// scan. Values are fetched one at a time from the source's bounded page cache.
/// </summary>
internal sealed class PureMidiOpaqueIdResolutionCache
{
    private readonly PureMidiSourceIdResolutionCache<OpaqueSourceAddress> _addresses =
        new(static value => value.Id, static value => value.Index);

    internal int RetainedCount => _addresses.RetainedCount;
    internal long RetainedBytes => _addresses.RetainedBytes;
    internal long RetainedPayloadBytes => 0;

    public IReadOnlyList<OpaqueSourceAddress> ResolveAddresses(
        IReadOnlySet<MidoraId> ids, IPureMidiSegmentContentSource source) =>
        _addresses.Resolve(ids, requested => source.QueryOpaqueEventsByIds(requested)
            .Select(static match => new OpaqueSourceAddress(match.Value.Id, match.Index)));

    public IEnumerable<OpaqueMidiEventSourceMatch> Resolve(
        IReadOnlySet<MidoraId> ids, IPureMidiSegmentContentSource source)
    {
        foreach (OpaqueSourceAddress address in ResolveAddresses(ids, source))
            yield return new(address.Index, source.GetOpaqueEvent(address.Index));
    }
}

internal readonly record struct OpaqueSourceAddress(MidoraId Id, int Index);
