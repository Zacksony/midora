namespace Midora.Application;

internal interface IBoundedPublicationValueSource
{
    void PrepareForPublication(CancellationToken cancellationToken);
}

/// <summary>Includes secondary indexes allocated inside Domain adoption, not
/// merely providers explicitly returned by a planner. Failed preparation must
/// release all of them immediately instead of waiting for a GC or Project close.</summary>
internal sealed class BoundedEditResourceLease : IDisposable
{
    private static readonly AsyncLocal<BoundedEditResourceLease?> Current = new();
    private readonly BoundedEditResourceLease? _parent;
    private readonly BoundedEditResources _resources;
    private List<IDisposable>? _values = [];
    private bool _ended;
    internal BoundedEditResourceLease(BoundedEditResources resources)
    {
        _resources = resources; _parent = Current.Value; Current.Value = this;
    }
    internal static void Track(BoundedEditResources resources, IDisposable value)
    {
        for (var lease = Current.Value; lease is not null; lease = lease._parent)
            if (ReferenceEquals(lease._resources, resources)) lease._values?.Add(value);
    }
    internal static void RetainForSourceLifetime(IDisposable value)
    {
        // Only independent, fully built source caches use this transfer. New
        // edit roots remain provisional and are not detached from cancellation.
        for (var lease = Current.Value; lease is not null; lease = lease._parent)
            lease._values?.RemoveAll(candidate => ReferenceEquals(candidate, value));
    }
    public BoundedEditPublicationResources Complete(CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null)
    {
        if (_ended) throw new InvalidOperationException("The resource lease has already ended.");
        // This is part of cancellable preparation, never root publication or
        // Undo/Redo. Every secondary index and selection provider is registered
        // here as well as the musical pages explicitly returned by the planner.
        int total = _values!.Count;
        progress?.Report(new(TimelineEditPreparationPhase.WritingStorage, 0, total));
        for (int index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values[index] is IBoundedPublicationValueSource value)
                value.PrepareForPublication(cancellationToken);
            progress?.Report(new(TimelineEditPreparationPhase.WritingStorage, index + 1, total));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var values = _values!; _values = null; End();
        return new(values);
    }
    private void End()
    {
        if (_ended) return;
        if (!ReferenceEquals(Current.Value, this)) throw new InvalidOperationException("Resource leases must end in nesting order.");
        Current.Value = _parent; _ended = true;
    }
    public void Dispose()
    {
        End();
        var values = Interlocked.Exchange(ref _values, null);
        if (values is null) return;
        BoundedEditPublicationResources.DisposeOwnedValues(values);
    }
}

internal sealed class BoundedEditPublicationResources(List<IDisposable> values) : IDisposable
{
    private List<IDisposable>? _values = values;
    public int Count => _values?.Count ?? 0;
    public void MarkPublished() => Interlocked.Exchange(ref _values, null)?.Clear();
    public void Dispose()
    {
        var captured = Interlocked.Exchange(ref _values, null);
        if (captured is null) return;
        DisposeOwnedValues(captured);
    }

    internal static void DisposeOwnedValues(List<IDisposable> values)
    {
        List<Exception>? failures = null;
        for (int i = values.Count - 1; i >= 0; i--)
        {
            try { values[i].Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        values.Clear();
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException("Provisional edit resource cleanup failed.", failures);
    }
}
