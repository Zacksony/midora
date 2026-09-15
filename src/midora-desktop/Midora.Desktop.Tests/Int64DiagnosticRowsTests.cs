using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using Midora.Compiler;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed partial class VirtualDiagnosticRowsTests
{
    [Theory]
    [InlineData(2_147_483_647L, false)]
    [InlineData(2_147_483_648L, true)]
    [InlineData(2_147_516_433L, true)]
    [InlineData(long.MaxValue, true)]
    public void WpfExposesOnlyAnInt32WindowOfTheLongSequence(long count, bool paged)
    {
        RunOnSta(() =>
        {
            CountingLongDiagnostics source = new(count);
            VirtualDiagnosticRows rows = new(source, true);
            Assert.Equal(count, rows.TotalCount);
            Assert.Equal(paged, rows.IsPaged);
            Assert.Equal(paged ? 4096 : int.MaxValue, rows.Count);
            ListCollectionView collection = new((IList)rows);
            Assert.Equal(rows.Count, collection.Count);
            Assert.InRange(source.Reads, 0, 4);
            VirtualDiagnosticRows last = rows.GetPage(rows.PageCount - 1);
            Assert.Equal(count - 1, last[last.Count - 1].SourceReference.Tick);
            Assert.InRange(source.Reads, 1, 5);
            for (int index = 0; index < Math.Min(last.Count, 1000); index++) _ = last[index];
            Assert.InRange(last.CachedRowCount, 1, 256);
            DiagnosticRow selected = last[last.Count - 1];
            long previousReads = source.Reads;
            Assert.Equal(last.Count - 1, last.IndexOf(selected));
            if (paged) Assert.Equal(-1, rows.IndexOf(selected));
            Assert.Equal(previousReads, source.Reads);
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetPage(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetPage(rows.PageCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => last[last.Count]);
        });
    }

    [Fact]
    public void PageJumpKeepsTotalAndRejectsInvalidInputWithoutChangingTheCurrentPage()
    {
        RunOnSta(() =>
        {
            const long count = 2_147_516_433;
            CountingLongDiagnostics source = new(count);
            using DiagnosticsWorkspaceViewModel workspace = new();
            workspace.Replace(new VirtualDiagnosticRows(source, true));
            Assert.True(workspace.IsDiagnosticPagingVisible);
            Assert.False(workspace.CanPreviousDiagnosticPage);
            Assert.True(workspace.CanNextDiagnosticPage);
            Assert.Equal(0, source.Reads);
            workspace.MoveDiagnosticPage(true);
            Assert.Equal(4096, workspace.Diagnostics[0].SourceReference.Tick);
            Assert.Equal("2", workspace.DiagnosticPageText);
            workspace.DiagnosticPageText = "524289";
            Assert.True(workspace.GoToDiagnosticPage());
            Assert.Equal(2_147_483_648L, workspace.Diagnostics[0].SourceReference.Tick);
            IReadOnlyList<DiagnosticRow> current = workspace.Diagnostics;
            workspace.SuspendPresentation();
            workspace.SetScope(null);
            workspace.ResumePresentation();
            workspace.SetScope(null);
            Assert.Same(current, workspace.Diagnostics);
            Assert.Equal("524289", workspace.DiagnosticPageText);
            foreach (string invalid in new[] { "", "0", "-1", "abc", "9999999999999999999999999", "9999999" })
            {
                workspace.DiagnosticPageText = invalid;
                Assert.False(workspace.GoToDiagnosticPage());
                Assert.Same(current, workspace.Diagnostics);
                Assert.NotEmpty(workspace.DiagnosticPageError);
            }
            VirtualDiagnosticRows rows = Assert.IsType<VirtualDiagnosticRows>(workspace.Diagnostics);
            workspace.DiagnosticPageText = rows.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(workspace.GoToDiagnosticPage());
            Assert.Equal(17, workspace.Diagnostics.Count);
            Assert.Equal(count - 1, workspace.Diagnostics[^1].SourceReference.Tick);
            Assert.False(workspace.CanNextDiagnosticPage);
            Assert.Empty(workspace.DiagnosticPageError);
            Assert.Contains(count.ToString("N0"), workspace.Summary);
            workspace.StatusFilter = "Prior Result";
            Assert.Empty(workspace.Diagnostics);
            Assert.False(workspace.IsDiagnosticPagingVisible);
            workspace.StatusFilter = "Active";
            Assert.Equal(0, workspace.Diagnostics[0].SourceReference.Tick);
            Assert.InRange(source.Reads, 1, 10);
            workspace.Replace(new VirtualDiagnosticRows(new CountingLongDiagnostics(5), true));
            Assert.Equal(5, workspace.Diagnostics.Count);
            Assert.False(workspace.IsDiagnosticPagingVisible);
            Assert.Equal("1", workspace.DiagnosticPageText);
        });
    }

    [Fact]
    public void DisposedWorkspaceDoesNotRetainLongSourcesOrOldPageRows()
    {
        RunOnSta(() =>
        {
            WeakReference[] references = CreateDisposedWorkspace();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Assert.All(references, reference => Assert.False(reference.IsAlive));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateDisposedWorkspace()
    {
        CountingLongDiagnostics source = new(2_147_516_433);
        VirtualDiagnosticRows rows = new(source, true);
        using DiagnosticsWorkspaceViewModel workspace = new();
        workspace.Replace(rows);
        workspace.MoveDiagnosticPage(true);
        DiagnosticRow selected = workspace.Diagnostics[0];
        return [new(source), new(rows), new(workspace.Diagnostics), new(selected)];
    }

    private sealed class CountingLongDiagnostics(long count) : ICompilerDiagnosticSequence
    {
        public long Count => count;
        public long Reads { get; private set; }
        public CompilerDiagnostic this[long index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                Reads++;
                return new($"CMP{index}", DiagnosticSeverity.Warning, "Warning", new(Tick: index));
            }
        }
        public IEnumerator<CompilerDiagnostic> GetEnumerator() =>
            throw new InvalidOperationException("Do not enumerate the complete logical sequence.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
