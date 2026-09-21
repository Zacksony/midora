using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class A4aSampleAuditTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InspectExplicitA2bSampleWithoutChangingSource()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_A4A_AUDIT_PROJECT");
        if (string.IsNullOrWhiteSpace(path)) return;
        byte[] original = await File.ReadAllBytesAsync(path);
        var package = new MidoraProjectPackageV1("1.0.0-dev", instrumentChangeStorage: BoundedInstrumentChangeStorageLoader.Instance);
        using var opened = await package.OpenAsync(path);
        var project = opened.Project;
        using var compiler = new MidoraCompiler();
        var result = compiler.CompileFull(project);
        output.WriteLine($"Project: {project.Metadata.ProjectName}; format {opened.SourceFileFormatVersion}");
        foreach (var diagnostic in result.Diagnostics)
            output.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
        foreach (var track in project.Tracks)
        foreach (var segment in track.Segments)
        foreach (var lane in segment.ParameterLanes)
        foreach (var point in lane.Points)
            output.WriteLine($"{track.Name}: tick={point.Tick}, value={point.Value}, interpolation={point.Interpolation}");
        if (Environment.GetEnvironmentVariable("MIDORA_A4A_REPAIR_SAMPLE") == "1")
        {
            Assert.Equal(8, result.Diagnostics.Count(d => d.Code == "MIDORA1316"));
            var points = project.Tracks.SelectMany(t => t.Segments).SelectMany(s => s.ParameterLanes)
                .SelectMany(l => l.Points).Where(p => p.Interpolation != CurveInterpolation.Step).ToArray();
            Assert.Equal(8, points.Length);
            var expected = points.Select(p => (p.Id, p.Tick, p.Value)).ToArray();
            foreach (var lane in project.Tracks.SelectMany(t => t.Segments).SelectMany(s => s.ParameterLanes))
            {
                using var batch = lane.Points.BeginBatchChange();
                for (int i = 0; i < lane.Points.Count; i++)
                    if (lane.Points[i].Interpolation != CurveInterpolation.Step)
                        lane.Points[i] = lane.Points[i] with { Interpolation = CurveInterpolation.Step };
            }
            var fixedResult = compiler.CompileFull(project);
            Assert.True(fixedResult.IsConsumable, string.Join("\n", fixedResult.Diagnostics));
            string target = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-step-fixed.midora");
            Assert.False(File.Exists(target), "Do not overwrite an existing repaired copy.");
            await package.SaveCopyAsync(project, opened.Presentation, target, opened.FileInformation);
            using var reopened = await package.OpenAsync(target);
            var restored = reopened.Project.Tracks.SelectMany(t => t.Segments).SelectMany(s => s.ParameterLanes)
                .SelectMany(l => l.Points).ToArray();
            Assert.Equal(expected, restored.Select(p => (p.Id, p.Tick, p.Value)));
            Assert.All(restored, p => Assert.Equal(CurveInterpolation.Step, p.Interpolation));
            var recompiled = compiler.CompileFull(reopened.Project);
            Assert.True(recompiled.IsConsumable, string.Join("\n", recompiled.Diagnostics));
            Assert.Equal(fixedResult.Fingerprint, recompiled.Fingerprint);
            output.WriteLine($"Repaired copy: {target}; diagnostics after reopen: {recompiled.Diagnostics.Count}");
            using var oldZip = new System.IO.Compression.ZipArchive(new MemoryStream(original));
            using var newZip = System.IO.Compression.ZipFile.OpenRead(target);
            Assert.Equal(oldZip.Entries.Select(e => e.FullName).Order(), newZip.Entries.Select(e => e.FullName).Order());
            foreach (var oldEntry in oldZip.Entries)
            {
                using var oldContent = new MemoryStream(); using var newContent = new MemoryStream();
                using (var reader = oldEntry.Open()) reader.CopyTo(oldContent);
                using (var reader = newZip.GetEntry(oldEntry.FullName)!.Open()) reader.CopyTo(newContent);
                if (!oldContent.ToArray().SequenceEqual(newContent.ToArray()))
                    output.WriteLine($"Changed package entry: {oldEntry.FullName}");
            }
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }
}
