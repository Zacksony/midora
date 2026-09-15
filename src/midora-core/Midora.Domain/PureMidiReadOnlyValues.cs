using System.Collections.Immutable;

namespace Midora.Domain;

/// <summary>
/// Streams a frozen formal sequence without registering editable facades or
/// building a spatial/ordinal index. Source order (including invalid values
/// that a validator must see) is deliberately not a tick-range query.
/// </summary>
internal static class PureMidiReadOnlyValues
{
    internal static IEnumerable<TValue> Enumerate<TValue>(
        IPureMidiSegmentContentSource? source,
        int sourceCount,
        Func<IPureMidiSegmentContentSource, int, TValue> readSource,
        Func<TValue, MidoraId> getId,
        ImmutableHashSet<MidoraId> removed,
        ImmutableDictionary<MidoraId, TValue> replacements,
        PersistentFormalValueSequence<TValue> added,
        CancellationToken cancellationToken)
        where TValue : struct
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is not null)
        {
            bool unchanged = removed.Count == 0 && replacements.Count == 0;
            for (int index = 0; index < sourceCount; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                TValue value = readSource(source, index);
                if (!unchanged)
                {
                    MidoraId id = getId(value);
                    if (removed.Contains(id)) continue;
                    if (replacements.TryGetValue(id, out TValue replacement)) value = replacement;
                }
                yield return value;
            }
        }
        foreach (TValue value in added.Enumerate(cancellationToken)) yield return value;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
