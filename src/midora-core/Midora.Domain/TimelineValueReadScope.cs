namespace Midora.Domain;

/// <summary>
/// Synchronous presentation reads may use prepared memory only. A missing page
/// or lazy index is Pending, not an empty musical range. This scope never flows
/// to the background task that performs the corresponding preparation.
/// </summary>
public static class TimelineValueReadScope
{
    [ThreadStatic] private static int _cacheOnlyDepth;
    public static bool IsCacheOnly => _cacheOnlyDepth != 0;
    public static Scope EnterCacheOnly()
    {
        _cacheOnlyDepth++;
        return new Scope(Environment.CurrentManagedThreadId);
    }
    public static void ThrowIfReadWouldBlock()
    {
        if (IsCacheOnly) throw new TimelineValueReadPendingException();
    }
    public readonly struct Scope : IDisposable
    {
        private readonly int _thread;
        internal Scope(int thread) => _thread = thread;
        public void Dispose()
        {
            if (_thread != Environment.CurrentManagedThreadId || _cacheOnlyDepth <= 0)
                throw new InvalidOperationException("A cache-only timeline read scope must end on its owning thread.");
            _cacheOnlyDepth--;
        }
    }
}

/// <summary>Internal control flow caught only by presentation cache-only entry points.</summary>
public sealed class TimelineValueReadPendingException : Exception
{
    public TimelineValueReadPendingException() : base("The immutable timeline page or index is not prepared in memory.") { }
}
