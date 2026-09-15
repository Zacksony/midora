using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Interaction;

/// <summary>
/// Immutable, page-compressed set for formal Midora object identities.
/// Stable IDs remain the public identity; the page number is only an in-memory
/// storage address and is never persisted or used as business identity.
/// </summary>
public sealed class CompressedMidoraIdSet : ITimelineInMemoryIdSet
{
    private const int PageShift = 12;
    private const int PageSize = 1 << PageShift;
    private const int WordShift = 6;
    private const int WordCount = PageSize / 64;
    private const int SparseLimit = 48;

    private readonly ImmutableArray<PageEntry> _pages;

    private CompressedMidoraIdSet(ImmutableArray<PageEntry> pages, int count)
    {
        _pages = pages;
        Count = count;
    }

    public static CompressedMidoraIdSet Empty { get; } =
        new(ImmutableArray<PageEntry>.Empty, 0);

    public int Count { get; }

    internal int PageCount => _pages.Length;

    internal long EstimatedPayloadBytes
    {
        get
        {
            long bytes = 0;
            foreach (PageEntry page in _pages)
                bytes = checked(bytes + page.Container.EstimatedPayloadBytes);
            return bytes;
        }
    }

    public static CompressedMidoraIdSet Create(IEnumerable<MidoraId> ids)
        => Create(ids, CancellationToken.None);

    public static CompressedMidoraIdSet Create(
        IEnumerable<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        if (ids is CompressedMidoraIdSet compressed) return compressed;
        Builder builder = new();
        int index = 0;
        foreach (MidoraId id in ids)
        {
            if ((index++ & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            builder.Add(id);
        }
        cancellationToken.ThrowIfCancellationRequested();
        CompressedMidoraIdSet result = builder.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static Builder CreateBuilder() => new();

    public bool Contains(MidoraId item)
    {
        if (item.Value <= 0) return false;
        GetAddress(item, out long pageIndex, out ushort offset);
        int index = FindPage(pageIndex);
        return index >= 0 && _pages[index].Container.Contains(offset);
    }

    public CompressedMidoraIdSet Add(MidoraId item)
    {
        Validate(item);
        GetAddress(item, out long pageIndex, out ushort offset);
        int index = FindPage(pageIndex);
        if (index >= 0)
        {
            PageContainer replacement = _pages[index].Container.Add(offset);
            if (ReferenceEquals(replacement, _pages[index].Container)) return this;
            return new(
                _pages.SetItem(index, new(pageIndex, replacement)),
                checked(Count + 1));
        }

        int insertion = ~index;
        return new(
            _pages.Insert(insertion, new(pageIndex, PageContainer.Create(offset))),
            checked(Count + 1));
    }

    public CompressedMidoraIdSet Remove(MidoraId item)
    {
        if (item.Value <= 0 || Count == 0) return this;
        GetAddress(item, out long pageIndex, out ushort offset);
        int index = FindPage(pageIndex);
        if (index < 0) return this;
        PageContainer replacement = _pages[index].Container.Remove(offset);
        if (ReferenceEquals(replacement, _pages[index].Container)) return this;
        return replacement.Count == 0
            ? new(_pages.RemoveAt(index), Count - 1)
            : new(_pages.SetItem(index, new(pageIndex, replacement)), Count - 1);
    }

    public CompressedMidoraIdSet Union(CompressedMidoraIdSet other) =>
        Combine(other, SetOperation.Union);

    public CompressedMidoraIdSet Except(CompressedMidoraIdSet other) =>
        Combine(other, SetOperation.Except);

    public CompressedMidoraIdSet Intersect(CompressedMidoraIdSet other) =>
        Combine(other, SetOperation.Intersect);

    public CompressedMidoraIdSet SymmetricExcept(CompressedMidoraIdSet other) =>
        Combine(other, SetOperation.SymmetricExcept);

    public CompressedMidoraIdSet Intersect(IReadOnlySet<MidoraId> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other is CompressedMidoraIdSet compressed) return Intersect(compressed);
        if (Count == 0) return this;
        Builder builder = new();
        foreach (MidoraId id in this)
        {
            if (other.Contains(id)) builder.Add(id);
        }
        CompressedMidoraIdSet result = builder.Build();
        return result.Count == Count ? this : result;
    }

    public bool TryGetMinimum(out MidoraId id)
    {
        if (_pages.IsDefaultOrEmpty)
        {
            id = default;
            return false;
        }
        ushort offset = _pages[0].Container.GetMinimum();
        id = FromAddress(_pages[0].PageIndex, offset);
        return true;
    }

    public bool SetEquals(IEnumerable<MidoraId> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other)) return true;
        if (other is CompressedMidoraIdSet compressed)
        {
            if (Count != compressed.Count || _pages.Length != compressed._pages.Length)
                return false;
            for (int index = 0; index < _pages.Length; index++)
            {
                if (_pages[index].PageIndex != compressed._pages[index].PageIndex
                    || !_pages[index].Container.SetEquals(compressed._pages[index].Container))
                    return false;
            }
            return true;
        }

        HashSet<MidoraId> materialized = other.ToHashSet();
        return materialized.Count == Count && this.All(materialized.Contains);
    }

    public bool IsSubsetOf(IEnumerable<MidoraId> other) =>
        CompareSet(other, allowEqual: true, proper: false, reverse: false);

    public bool IsProperSubsetOf(IEnumerable<MidoraId> other) =>
        CompareSet(other, allowEqual: false, proper: true, reverse: false);

    public bool IsSupersetOf(IEnumerable<MidoraId> other) =>
        CompareSet(other, allowEqual: true, proper: false, reverse: true);

    public bool IsProperSupersetOf(IEnumerable<MidoraId> other) =>
        CompareSet(other, allowEqual: false, proper: true, reverse: true);

    public bool Overlaps(IEnumerable<MidoraId> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Any(Contains);
    }

    public IEnumerator<MidoraId> GetEnumerator()
    {
        foreach (PageEntry page in _pages)
        {
            foreach (ushort offset in page.Container.EnumerateOffsets())
                yield return FromAddress(page.PageIndex, offset);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private CompressedMidoraIdSet Combine(
        CompressedMidoraIdSet other,
        SetOperation operation)
    {
        ArgumentNullException.ThrowIfNull(other);
        if ((operation == SetOperation.Union && other.Count == 0)
            || (operation is SetOperation.Except or SetOperation.SymmetricExcept
                && other.Count == 0))
            return this;
        if (operation == SetOperation.Intersect && Count == 0) return this;
        if (operation == SetOperation.Intersect && other.Count == 0) return Empty;
        if (ReferenceEquals(this, other))
        {
            return operation switch
            {
                SetOperation.Union or SetOperation.Intersect => this,
                SetOperation.Except or SetOperation.SymmetricExcept => Empty,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }

        ImmutableArray<PageEntry>.Builder pages = ImmutableArray.CreateBuilder<PageEntry>();
        int left = 0;
        int right = 0;
        int count = 0;
        bool identicalToLeft = true;
        while (left < _pages.Length || right < other._pages.Length)
        {
            PageEntry? leftPage = left < _pages.Length ? _pages[left] : null;
            PageEntry? rightPage = right < other._pages.Length ? other._pages[right] : null;
            long leftIndex = leftPage?.PageIndex ?? long.MaxValue;
            long rightIndex = rightPage?.PageIndex ?? long.MaxValue;
            PageEntry? result;
            if (leftIndex == rightIndex)
            {
                PageContainer? container = leftPage!.Value.Container.Combine(
                    rightPage!.Value.Container,
                    operation);
                result = container is null ? null : new(leftIndex, container);
                identicalToLeft &= container is not null
                    && ReferenceEquals(container, leftPage.Value.Container);
                left++;
                right++;
            }
            else if (leftIndex < rightIndex)
            {
                result = operation is SetOperation.Union
                    or SetOperation.Except
                    or SetOperation.SymmetricExcept
                    ? leftPage
                    : null;
                identicalToLeft &= result is not null;
                left++;
            }
            else
            {
                result = operation is SetOperation.Union or SetOperation.SymmetricExcept
                    ? rightPage
                    : null;
                identicalToLeft = false;
                right++;
            }

            if (result is PageEntry page)
            {
                pages.Add(page);
                count = checked(count + page.Container.Count);
            }
        }

        if (identicalToLeft && count == Count && pages.Count == _pages.Length)
            return this;
        return count == 0 ? Empty : new(pages.ToImmutable(), count);
    }

    private bool CompareSet(
        IEnumerable<MidoraId> other,
        bool allowEqual,
        bool proper,
        bool reverse)
    {
        ArgumentNullException.ThrowIfNull(other);
        CompressedMidoraIdSet materialized = Create(other);
        if (proper && Count == materialized.Count) return false;
        CompressedMidoraIdSet candidate = reverse ? materialized : this;
        CompressedMidoraIdSet container = reverse ? this : materialized;
        if (!allowEqual && candidate.Count == container.Count) return false;
        foreach (MidoraId id in candidate)
        {
            if (!container.Contains(id)) return false;
        }
        return true;
    }

    private int FindPage(long pageIndex)
    {
        int low = 0;
        int high = _pages.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            long value = _pages[middle].PageIndex;
            if (value == pageIndex) return middle;
            if (value < pageIndex) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    private static void Validate(MidoraId id)
    {
        if (id.Value <= 0) throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static void GetAddress(MidoraId id, out long pageIndex, out ushort offset)
    {
        long zeroBased = id.Value - 1;
        pageIndex = zeroBased >> PageShift;
        offset = (ushort)(zeroBased & (PageSize - 1));
    }

    private static MidoraId FromAddress(long pageIndex, ushort offset) =>
        new(checked((pageIndex << PageShift) + offset + 1));

    private enum SetOperation
    {
        Union,
        Except,
        Intersect,
        SymmetricExcept
    }

    private readonly record struct PageEntry(long PageIndex, PageContainer Container);

    private sealed class PageContainer
    {
        private readonly ushort[]? _sparse;
        private readonly ulong[]? _bitmap;

        private PageContainer(ushort[] sparse)
        {
            _sparse = sparse;
            Count = sparse.Length;
        }

        private PageContainer(ulong[] bitmap, int count)
        {
            _bitmap = bitmap;
            Count = count;
        }

        public int Count { get; }

        public long EstimatedPayloadBytes => _sparse is not null
            ? checked(_sparse.LongLength * sizeof(ushort))
            : checked(_bitmap!.LongLength * sizeof(ulong));

        public static PageContainer Create(ushort offset) => new([offset]);

        public bool Contains(ushort offset)
        {
            if (_sparse is not null) return Array.BinarySearch(_sparse, offset) >= 0;
            ulong word = _bitmap![offset >> WordShift];
            return (word & (1UL << (offset & 63))) != 0;
        }

        public PageContainer Add(ushort offset)
        {
            if (Contains(offset)) return this;
            if (_sparse is not null && Count < SparseLimit)
            {
                int insertion = ~Array.BinarySearch(_sparse, offset);
                ushort[] values = new ushort[Count + 1];
                _sparse.AsSpan(0, insertion).CopyTo(values);
                values[insertion] = offset;
                _sparse.AsSpan(insertion).CopyTo(values.AsSpan(insertion + 1));
                return new(values);
            }
            ulong[] words = ToBitmap();
            words[offset >> WordShift] |= 1UL << (offset & 63);
            return new(words, Count + 1);
        }

        public PageContainer Remove(ushort offset)
        {
            if (!Contains(offset)) return this;
            if (_sparse is not null)
            {
                int index = Array.BinarySearch(_sparse, offset);
                ushort[] values = new ushort[Count - 1];
                _sparse.AsSpan(0, index).CopyTo(values);
                _sparse.AsSpan(index + 1).CopyTo(values.AsSpan(index));
                return new(values);
            }
            ulong[] words = (ulong[])_bitmap!.Clone();
            words[offset >> WordShift] &= ~(1UL << (offset & 63));
            return FromBitmap(words, Count - 1);
        }

        public PageContainer? Combine(PageContainer other, SetOperation operation)
        {
            ulong[] left = ToBitmap();
            ulong[] right = other.ToBitmap();
            int count = 0;
            for (int index = 0; index < WordCount; index++)
            {
                left[index] = operation switch
                {
                    SetOperation.Union => left[index] | right[index],
                    SetOperation.Except => left[index] & ~right[index],
                    SetOperation.Intersect => left[index] & right[index],
                    SetOperation.SymmetricExcept => left[index] ^ right[index],
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
                count += BitOperations.PopCount(left[index]);
            }
            if (count == 0) return null;
            if (count == Count && WordsEqual(left, _bitmap, _sparse)) return this;
            return FromBitmap(left, count);
        }

        public bool SetEquals(PageContainer other)
        {
            if (Count != other.Count) return false;
            if (_sparse is not null && other._sparse is not null)
                return _sparse.AsSpan().SequenceEqual(other._sparse);
            return ToBitmap().AsSpan().SequenceEqual(other.ToBitmap());
        }

        public ushort GetMinimum()
        {
            if (_sparse is not null) return _sparse[0];
            for (int wordIndex = 0; wordIndex < WordCount; wordIndex++)
            {
                ulong word = _bitmap![wordIndex];
                if (word != 0)
                    return checked((ushort)((wordIndex << WordShift)
                        + BitOperations.TrailingZeroCount(word)));
            }
            throw new InvalidOperationException("An empty selection page has no minimum.");
        }

        public IEnumerable<ushort> EnumerateOffsets()
        {
            if (_sparse is not null)
            {
                foreach (ushort value in _sparse) yield return value;
                yield break;
            }
            for (int wordIndex = 0; wordIndex < WordCount; wordIndex++)
            {
                ulong word = _bitmap![wordIndex];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    yield return checked((ushort)((wordIndex << WordShift) + bit));
                    word &= word - 1;
                }
            }
        }

        private ulong[] ToBitmap()
        {
            if (_bitmap is not null) return (ulong[])_bitmap.Clone();
            ulong[] words = new ulong[WordCount];
            foreach (ushort offset in _sparse!)
                words[offset >> WordShift] |= 1UL << (offset & 63);
            return words;
        }

        internal static PageContainer FromBitmap(ulong[] words, int count)
        {
            if (count > SparseLimit) return new(words, count);
            ushort[] sparse = new ushort[count];
            int destination = 0;
            for (int wordIndex = 0; wordIndex < WordCount; wordIndex++)
            {
                ulong word = words[wordIndex];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    sparse[destination++] = checked((ushort)((wordIndex << WordShift) + bit));
                    word &= word - 1;
                }
            }
            return new(sparse);
        }

        private static bool WordsEqual(
            ReadOnlySpan<ulong> candidate,
            ulong[]? bitmap,
            ushort[]? sparse)
        {
            if (bitmap is not null) return candidate.SequenceEqual(bitmap);
            Span<ulong> words = stackalloc ulong[WordCount];
            foreach (ushort offset in sparse!)
                words[offset >> WordShift] |= 1UL << (offset & 63);
            return candidate.SequenceEqual(words);
        }
    }

    public sealed class Builder
    {
        private readonly Dictionary<long, ulong[]> _pages = [];
        private int _count;
        private bool _built;

        public int Count => _count;

        public bool Add(MidoraId id)
        {
            if (_built) throw new InvalidOperationException("The builder has already been consumed.");
            Validate(id);
            GetAddress(id, out long pageIndex, out ushort offset);
            if (!_pages.TryGetValue(pageIndex, out ulong[]? words))
            {
                words = new ulong[WordCount];
                _pages.Add(pageIndex, words);
            }
            int wordIndex = offset >> WordShift;
            ulong mask = 1UL << (offset & 63);
            if ((words[wordIndex] & mask) != 0) return false;
            words[wordIndex] |= mask;
            _count = checked(_count + 1);
            return true;
        }

        public CompressedMidoraIdSet Build()
        {
            if (_built) throw new InvalidOperationException("The builder has already been consumed.");
            _built = true;
            if (_count == 0) return Empty;
            ImmutableArray<PageEntry>.Builder entries =
                ImmutableArray.CreateBuilder<PageEntry>(_pages.Count);
            foreach ((long pageIndex, ulong[] words) in _pages.OrderBy(static pair => pair.Key))
            {
                int count = 0;
                foreach (ulong word in words) count += BitOperations.PopCount(word);
                entries.Add(new(pageIndex, PageContainer.FromBitmap(words, count)));
            }
            return new(entries.MoveToImmutable(), _count);
        }
    }
}
