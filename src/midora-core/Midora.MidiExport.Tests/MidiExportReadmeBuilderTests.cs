using System.Text;
using System.Collections;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.MidiExport.Tests;

public sealed partial class MidiExportReadmeBuilderTests
{
    [Fact]
    public void BuildsDeterministicStrictUtf8ReadmeFromFrozenTaskSnapshot()
    {
        MidiExportReadmeRequest request = CreateRequest();

        byte[] first = MidiExportReadmeBuilder.Build(request);
        byte[] second = MidiExportReadmeBuilder.Build(request);
        string markdown = new UTF8Encoding(false, true).GetString(first);

        Assert.Equal(first, second);
        Assert.False(first.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.DoesNotContain('\r', markdown);
        Assert.EndsWith("\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Mode: Per Port", markdown, StringComparison.Ordinal);
        Assert.Contains("- Notes: 12345", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("SoundFont:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  >", markdown, StringComparison.Ordinal);
        Assert.Contains("- Range: \\[120, 480\\)", markdown, StringComparison.Ordinal);
        Assert.Contains("- Midora Port 3 → output Port 1", markdown, StringComparison.Ordinal);
        Assert.Contains("GS and Yamaha XG Normal Part", markdown, StringComparison.Ordinal);
        Assert.Contains("exactly one original Midora Channel Unit", markdown, StringComparison.Ordinal);
        Assert.Contains("Port 03.mid", markdown, StringComparison.Ordinal);
        Assert.Contains("2026-08-06T12:34:56.1234567Z", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsErrorDiagnosticsAndInvalidSnapshotRanges()
    {
        MidiExportReadmeRequest source = CreateRequest();
        MidiExportReadmeRequest withError = Clone(
            source,
            diagnostics: [new("Error", "E001", "failed")]);
        MidiExportReadmeRequest invalidRange = Clone(source, startTick: 500, endTick: 100);

        Assert.Throws<ArgumentException>(() => MidiExportReadmeBuilder.Build(withError));
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiExportReadmeBuilder.Build(invalidRange));
    }

    [Fact]
    public void StreamingOutputMatchesFrozenLegacyMarkdownBytes()
    {
        const string expected = """
            # Midora MIDI Export

            ## Project Metadata

            - Project name: Project \*One\*
            - Project version: v1
            - Author or team: Midora contributors
            - Original work / remix source: Original
            - Copyright: Copyright
            - Notes: 12345

            ## Export Configuration

            - Mode: Per Port
            - Range: \[120, 480\)
            - Range source: Manual range
            - Routing: Compact
            - TPQ: 192
            - Tempo events: 2
            - Time Signature events: 1
            - Key Signature events: 0

            ## Track Selection

            - 1. Lead — exported
            - 2. Bass — excluded: not selected

            ## Port Mapping

            - Midora Port 3 → output Port 1 — Port 03.mid

            ## Channel 10 Melodic Compatibility

            Midora writes Roland GS and Yamaha XG Normal Part initialization, in that order, for each event Track that actually uses Channel 10. It does not send a GS, XG, or GM reset and does not replace canonical Bank or Program events. The fixed default vendor device identifiers may be ignored by receivers configured for another identifier.

            ## Compatibility Boundary

            Each event Track contains exactly one original Midora Channel Unit (Port + Channel), and each used Unit appears in exactly one event Track in that file.

            The files contain standard MIDI 1.0 SMF Type 1 data. Third-party playback may differ for multi-Port interpretation, MIDI Port Meta events, Channel 10 vendor initialization, SoundFont selection, RPN/NRPN, Pitch Bend Range, and overlapping equal-pitch notes.

            ## Diagnostics

            - [Information] I001: No issue

            ## Files

            - Port 03.mid
            - README.md

            ## Software

            - Created with: 0.1.0
            - Last saved with: 0.1.0
            - Exported with: 0.1.0
            - Exported at UTC: 2026-08-06T12:34:56.1234567Z
            """;
        byte[] golden = new UTF8Encoding(false, true).GetBytes(expected.Replace("\r\n", "\n") + "\n");
        using MemoryStream output = new();
        MidiExportReadmeBuilder.WriteTo(output, CreateRequest());
        Assert.True(output.CanWrite);
        Assert.Equal(golden, output.ToArray());
        Assert.Equal(golden, MidiExportReadmeBuilder.Build(CreateRequest()));
    }

    [Fact]
    public void LargeLazyDiagnosticReadmeStreamsOnceAndStopsPromptlyOnCancellation()
    {
        CountingReadmeDiagnostics diagnostics = new(5_000);
        using CountingWriteStream output = new();
        MidiExportReadmeBuilder.WriteTo(output, Clone(CreateRequest(), diagnostics: diagnostics));
        Assert.Equal(1_000, diagnostics.ReadCount);
        Assert.InRange(output.TotalBytes, 50_000, 200_000);
        Assert.InRange(output.MaximumWriteBytes, 1, 65_536);

        using CancellationTokenSource cancellation = new();
        CountingReadmeDiagnostics huge = new(50_000_000, count =>
        {
            if (count == 17) cancellation.Cancel();
        });
        using CountingWriteStream canceledOutput = new();
        Assert.Throws<OperationCanceledException>(() => MidiExportReadmeBuilder.WriteTo(canceledOutput,
            Clone(CreateRequest(), diagnostics: huge), cancellation.Token));
        Assert.InRange(huge.ReadCount, 17, 18);
    }

    [Fact]
    public void ReadmeProjectionKeepsExactCountAndOnlyTheBoundedWarningPrefix()
    {
        CompilerDiagnosticList.Builder builder = new();
        CompilerDiagnosticList.OverlapDiagnosticSourceValue[] sources = Enumerable.Range(0, 1_000)
            .Select(index => new CompilerDiagnosticList.OverlapDiagnosticSourceValue(
                new MidoraId(2_001), new MidoraId(2_002), new MidoraId(index + 1),
                new MidoraId(2_003), index)).ToArray();
        int source = builder.AddOverlapSource(sources, DiagnosticSeverity.Warning);
        for (int index = 0; index < sources.Length - 1; index++)
            builder.AddRange(source, index + 1, sources.Length - index - 1);
        CompilerDiagnosticList compact = builder.Build();
        MidiExportReadmeDiagnosticProjection projected = MidiExportReadmeDiagnosticProjection.Create(compact);
        Assert.Equal(1_000, projected.Count);
        Assert.Equal(499_500, projected.TotalCount);
        Assert.Equal("Warning", projected[0].Severity);
        Assert.Equal("MIDORA2201", projected[^1].Code);
        MidiExportReadmeRequest frozen = MidiExportReadmeBuilder.Freeze(Clone(CreateRequest(), diagnostics: projected));
        Assert.Same(projected, frozen.Diagnostics);
        Assert.Equal(499_500, frozen.TotalDiagnosticCount);
    }

    private sealed class CountingReadmeDiagnostics(int count, Action<int>? onRead = null)
        : IReadOnlyList<MidiExportReadmeDiagnostic>
    {
        public int Count => count;
        public int ReadCount { get; private set; }
        public MidiExportReadmeDiagnostic this[int index]
        {
            get
            {
                ReadCount++;
                onRead?.Invoke(ReadCount);
                return new("Warning", "W001", "Repeated warning [value] with Unicode 音符.");
            }
        }
        public IEnumerator<MidiExportReadmeDiagnostic> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingWriteStream : Stream
    {
        public long TotalBytes { get; private set; }
        public int MaximumWriteBytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => TotalBytes;
        public override long Position { get => TotalBytes; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            TotalBytes += buffer.Length;
            MaximumWriteBytes = Math.Max(MaximumWriteBytes, buffer.Length);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static MidiExportReadmeRequest CreateRequest() => new()
    {
        ProjectName = "Project *One*",
        ProjectVersion = "v1",
        AuthorOrTeam = "Midora contributors",
        OriginalWork = "Original",
        Copyright = "Copyright",
        NoteOnEventCount = 12_345,
        Mode = MidiExportMode.PerPort,
        RangeSource = MidiExportRangeSource.Manual,
        StartTick = 120,
        EndTick = 480,
        Routing = MidiExportRoutingStrategy.Compact,
        TicksPerQuarterNote = 192,
        TempoEventCount = 2,
        TimeSignatureEventCount = 1,
        KeySignatureEventCount = 0,
        Tracks =
        [
            new("2", 2, "Bass", false, "not selected"),
            new("1", 1, "Lead", true)
        ],
        PortMappings = [new(3, 1, "Port 03.mid")],
        Diagnostics = [new("Information", "I001", "No issue")],
        FileNames = ["Port 03.mid", "README.md"],
        CreatedWithSoftwareVersion = "0.1.0",
        LastSavedWithSoftwareVersion = "0.1.0",
        ExportSoftwareVersion = "0.1.0",
        ExportedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 34, 56, TimeSpan.Zero)
            .AddTicks(1_234_567)
    };

    private static MidiExportReadmeRequest Clone(
        MidiExportReadmeRequest source,
        long? startTick = null,
        long? endTick = null,
        IReadOnlyList<MidiExportReadmeDiagnostic>? diagnostics = null,
        long? totalDiagnosticCount = null) => new()
        {
            ProjectName = source.ProjectName,
            ProjectVersion = source.ProjectVersion,
            AuthorOrTeam = source.AuthorOrTeam,
            OriginalWork = source.OriginalWork,
            Copyright = source.Copyright,
            NoteOnEventCount = source.NoteOnEventCount,
            Mode = source.Mode,
            RangeSource = source.RangeSource,
            StartTick = startTick ?? source.StartTick,
            EndTick = endTick ?? source.EndTick,
            Routing = source.Routing,
            TicksPerQuarterNote = source.TicksPerQuarterNote,
            TempoEventCount = source.TempoEventCount,
            TimeSignatureEventCount = source.TimeSignatureEventCount,
            KeySignatureEventCount = source.KeySignatureEventCount,
            Tracks = source.Tracks,
            PortMappings = source.PortMappings,
            Diagnostics = diagnostics ?? source.Diagnostics,
            TotalDiagnosticCount = totalDiagnosticCount ?? (diagnostics is null ? source.TotalDiagnosticCount : null),
            FileNames = source.FileNames,
            CreatedWithSoftwareVersion = source.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = source.LastSavedWithSoftwareVersion,
            ExportSoftwareVersion = source.ExportSoftwareVersion,
            ExportedAtUtc = source.ExportedAtUtc
        };
}
