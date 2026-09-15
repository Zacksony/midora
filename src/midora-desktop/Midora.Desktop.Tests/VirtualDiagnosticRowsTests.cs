using System.Collections;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Data;
using System.Windows.Threading;
using Midora.Compiler;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed partial class VirtualDiagnosticRowsTests
{
    [Fact]
    public void WpfListViewDoesNotMaterializeFiftyMillionDiagnosticRows()
    {
        RunOnSta(() =>
        {
            CountingDiagnostics source = new(50_000_000);
            VirtualDiagnosticRows rows = new(source, true);
            ListCollectionView view = new((IList)rows);
            Assert.Equal(source.Count, view.Count);
            Assert.InRange(source.ReadCount, 0, 4);
            DiagnosticRow selected = Assert.IsType<DiagnosticRow>(view.GetItemAt(25_000_000));
            Assert.Equal("CMP25000000", selected.Code);
            for (int index = 0; index < 1_000; index++) _ = rows[index];
            Assert.InRange(rows.CachedRowCount, 1, 256);
            int readsBeforeLookup = source.ReadCount;
            Assert.Equal(25_000_000, ((IList)rows).IndexOf(selected));
            Assert.Equal(readsBeforeLookup, source.ReadCount);
            VirtualDiagnosticRows nextRevision = new(source, false);
            Assert.Equal(-1, ((IList)nextRevision).IndexOf(selected));
            Assert.Equal(readsBeforeLookup, source.ReadCount);
        });
    }

    [Fact]
    public void DefaultAndUniformStatusFiltersDoNotEnumerateCompilerDiagnostics()
    {
        CountingDiagnostics source = new(50_000_000);
        VirtualDiagnosticRows rows = new(source, true);
        using DiagnosticsWorkspaceViewModel workspace = new();
        workspace.Replace(rows);
        Assert.Same(rows, workspace.Diagnostics);
        Assert.Equal(0, source.ReadCount);
        workspace.StatusFilter = "Prior Result";
        Assert.Empty(workspace.Diagnostics);
        Assert.Equal(0, source.ReadCount);
        workspace.StatusFilter = "All statuses";
        Assert.Same(rows, workspace.Diagnostics);
        Assert.Equal(0, source.ReadCount);
        workspace.ScopeFilter = "Current Task";
        Assert.Empty(workspace.Diagnostics);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void LargeFilterPublishesOnlyTheLatestCompletedSelection()
    {
        RunOnSta(() =>
        {
            CountingDiagnostics source = new(6_000);
            using DiagnosticsWorkspaceViewModel workspace = new();
            workspace.Replace(new VirtualDiagnosticRows(source, true));
            workspace.SearchText = "2999";
            workspace.SearchText = "17";
            PumpUntil(() => !workspace.IsFiltering);
            Assert.DoesNotContain("failed", workspace.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Enumerable.Range(0, source.Count).Count(index => index.ToString().Contains("17")),
                workspace.Diagnostics.Count);
            Assert.All(workspace.Diagnostics, row => Assert.Contains("17", row.Code));
            Assert.InRange(source.ReadCount, 6_000, 12_256);

            workspace.SearchText = "2999";
            workspace.SuspendPresentation();
            Assert.False(workspace.IsFiltering);
            workspace.SearchText = "42";
            Assert.False(workspace.IsFiltering);
            workspace.ResumePresentation();
            PumpUntil(() => !workspace.IsFiltering);
            Assert.All(workspace.Diagnostics, row => Assert.Contains("42", row.Code));
        });
    }

    [Fact]
    public void LegacyRowsAreFrozenAndKeepTheirReferenceIdentities()
    {
        DiagnosticRow original = new("Error", "Compile", "CMP1", "Failure", "Project", true, default);
        DiagnosticRow[] source = [original];
        using DiagnosticsWorkspaceViewModel workspace = new();
        workspace.Replace(source);
        source[0] = original with { Code = "CHANGED" };
        Assert.Same(original, Assert.Single(workspace.Diagnostics));
    }

    private static void PumpUntil(Func<bool> ready)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            DispatcherFrame frame = new();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Yield();
        }
        Assert.True(ready(), "Diagnostic filter preparation timed out.");
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Diagnostic WPF test timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class CountingDiagnostics(int count) : IReadOnlyList<CompilerDiagnostic>
    {
        private int _readCount;
        public int Count => count;
        public int ReadCount => Volatile.Read(ref _readCount);
        public CompilerDiagnostic this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                Interlocked.Increment(ref _readCount);
                return new($"CMP{index}", DiagnosticSeverity.Error, "Failure", new(Tick: index));
            }
        }
        public IEnumerator<CompilerDiagnostic> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
