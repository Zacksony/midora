using Midora.Midi;
using Midora.OutputPlanning;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportOutputTransactionTests
{
    [Fact]
    public void FrozenPlansUseInitialReleaseNamesAndRecordExistingConflicts()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("exports");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, "Song.mid"), [1]);

        MidiExportFrozenOutputPlan whole = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Song",
            null,
            includeReadme: true);
        MidiExportFrozenOutputPlan tracks = MidiExportOutputPlanner.PlanLogicalTracks(
            outputDirectory,
            [
                new LogicalTrackOutputName("b", 2, "Lead"),
                new LogicalTrackOutputName("a", 1, "Lead")
            ],
            totalProjectTrackCount: 12,
            includeReadme: true);
        MidiExportFrozenOutputPlan ports = MidiExportOutputPlanner.PlanPorts(
            outputDirectory,
            [3, 1],
            includeReadme: false);

        Assert.True(whole.Succeeded);
        Assert.Equal(["Song.mid", "README.md"], whole.Targets.Select(target => target.FileName));
        Assert.True(whole.RequiresOverwriteAuthorization);
        Assert.True(whole.Targets[0].ExistedAtFreeze);
        Assert.Equal(["01 - Lead.mid", "02 - Lead.mid", "README.md"],
            tracks.Targets.Select(target => target.FileName));
        Assert.Equal(["Port 01.mid", "Port 03.mid"], ports.Targets.Select(target => target.FileName));
        Assert.All(whole.Targets, target => Assert.True(Path.IsPathFullyQualified(target.FullPath)));
    }

    [Fact]
    public void FrozenPlansRejectInvalidPathsAndInvalidOutputTopology()
    {
        using TemporaryDirectory temporary = new();
        string fileInsteadOfDirectory = temporary.PathFor("not-a-directory");
        File.WriteAllBytes(fileInsteadOfDirectory, [1]);
        MidiExportFrozenOutputPlan filePlan = MidiExportOutputPlanner.PlanWholeProject(
            fileInsteadOfDirectory,
            "Song",
            null,
            includeReadme: false);

        string targetDirectory = temporary.PathFor("target-is-directory");
        Directory.CreateDirectory(Path.Combine(targetDirectory, "Song.mid"));
        MidiExportFrozenOutputPlan directoryTargetPlan = MidiExportOutputPlanner.PlanWholeProject(
            targetDirectory,
            "Song",
            null,
            includeReadme: false);
        MidiExportFrozenOutputPlan noMidiPlan = MidiExportOutputPlanner.PlanLogicalTracks(
            temporary.PathFor("no-midi"),
            [],
            totalProjectTrackCount: 0,
            includeReadme: true);
        MidiExportFrozenOutputPlan invalidPathPlan = MidiExportOutputPlanner.PlanWholeProject(
            "\0",
            "Song",
            null,
            includeReadme: false);

        Assert.False(filePlan.Succeeded);
        Assert.Contains(filePlan.Diagnostics, value => value.Code == "MIDORA-MIDI-EXPORT-OUTPUT-PATH"
            && value.Message.Contains("existing file", StringComparison.Ordinal));
        Assert.False(directoryTargetPlan.Succeeded);
        Assert.Contains(directoryTargetPlan.Diagnostics,
            value => value.Message.Contains("existing directory", StringComparison.Ordinal));
        Assert.False(noMidiPlan.Succeeded);
        Assert.Contains(noMidiPlan.Diagnostics,
            value => value.Message.Contains("at least one .mid", StringComparison.Ordinal));
        Assert.False(invalidPathPlan.Succeeded);
        Assert.Empty(invalidPathPlan.Targets);
    }

    [Fact]
    public async Task PublishesAllArtifactsByAtomicallyCreatingTheExactMissingDirectory()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("selected-output");
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanPorts(
            outputDirectory,
            [1, 3],
            includeReadme: true);

        MidiExportOutputResult result = await new MidiExportOutputTransaction().PublishAsync(
            plan,
            CreateArtifacts(plan),
            overwriteAuthorized: false);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.True(Directory.Exists(outputDirectory));
        Assert.Equal(
            ["Port 01.mid", "Port 03.mid", "README.md"],
            Directory.GetFiles(outputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        AssertNoTransactionDirectories(temporary.Path);
    }

    [Fact]
    public async Task ExistingTargetNeedsFrozenAuthorizationAndIsReplacedOnlyAfterValidation()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("exports");
        Directory.CreateDirectory(outputDirectory);
        string targetPath = Path.Combine(outputDirectory, "Song.mid");
        byte[] original = [9, 8, 7];
        await File.WriteAllBytesAsync(targetPath, original);
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Song",
            null,
            includeReadme: false);

        MidiExportOutputException denied = await Assert.ThrowsAsync<MidiExportOutputException>(() =>
            new MidiExportOutputTransaction().PublishAsync(
                plan,
                CreateArtifacts(plan),
                overwriteAuthorized: false));

        Assert.Equal(MidiExportOutputStage.Preflight, denied.Stage);
        Assert.Equal(original, await File.ReadAllBytesAsync(targetPath));

        MidiExportOutputResult result = await new MidiExportOutputTransaction().PublishAsync(
            plan,
            CreateArtifacts(plan),
            overwriteAuthorized: true);

        Assert.True(result.Succeeded);
        StandardMidiFile.ValidateType1(await File.ReadAllBytesAsync(targetPath));
        AssertNoTransactionDirectories(temporary.Path);
    }

    [Fact]
    public async Task InvalidMidiAndCancellationLeaveNoPartialOutput()
    {
        using TemporaryDirectory temporary = new();
        string invalidDirectory = temporary.PathFor("invalid");
        MidiExportFrozenOutputPlan invalidPlan = MidiExportOutputPlanner.PlanWholeProject(
            invalidDirectory,
            "Invalid",
            null,
            includeReadme: false);

        MidiExportOutputException invalid = await Assert.ThrowsAsync<MidiExportOutputException>(() =>
            new MidiExportOutputTransaction().PublishAsync(
                invalidPlan,
                CreateArtifacts(invalidPlan, invalidMidi: true),
                overwriteAuthorized: false));

        Assert.Equal(MidiExportOutputStage.SelfValidation, invalid.Stage);
        Assert.NotNull(invalid.EncodingDiagnostic);
        Assert.Equal(MidiExportDiagnosticCategory.Encoding, invalid.EncodingDiagnostic.Diagnostic.Category);
        Assert.False(Directory.Exists(invalidDirectory));
        AssertNoTransactionDirectories(temporary.Path);

        string canceledDirectory = temporary.PathFor("canceled");
        MidiExportFrozenOutputPlan canceledPlan = MidiExportOutputPlanner.PlanWholeProject(
            canceledDirectory,
            "Canceled",
            null,
            includeReadme: false);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MidiExportOutputTransaction().PublishAsync(
                canceledPlan,
                CreateArtifacts(canceledPlan),
                overwriteAuthorized: false,
                cancellation.Token));

        Assert.False(Directory.Exists(canceledDirectory));
        AssertNoTransactionDirectories(temporary.Path);
    }

    [Fact]
    public async Task MidPublishFailureRestoresEveryOriginalTarget()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("exports");
        Directory.CreateDirectory(outputDirectory);
        byte[] firstOriginal = [1, 2, 3];
        byte[] secondOriginal = [4, 5, 6];
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "Port 01.mid"), firstOriginal);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "Port 02.mid"), secondOriginal);
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanPorts(
            outputDirectory,
            [1, 2],
            includeReadme: false);
        MidiExportOutputTransaction transaction = new(
            new ScheduledFaultInjector((MidiExportOutputFaultPoint.BeforePublish, 2)));

        MidiExportOutputException failure = await Assert.ThrowsAsync<MidiExportOutputException>(() =>
            transaction.PublishAsync(plan, CreateArtifacts(plan), overwriteAuthorized: true));

        Assert.Equal(MidiExportOutputStage.Finalizing, failure.Stage);
        Assert.True(failure.RollbackSucceeded);
        Assert.Equal(firstOriginal, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "Port 01.mid")));
        Assert.Equal(secondOriginal, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "Port 02.mid")));
        Assert.Contains(failure.Items, item => item.State == MidiExportOutputItemState.Cleaned);
        AssertNoTransactionDirectories(temporary.Path);
    }

    [Fact]
    public async Task RollbackFailureRetainsRecoveryMaterialAndReportsIt()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("exports");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "Port 01.mid"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "Port 02.mid"), [2]);
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanPorts(
            outputDirectory,
            [1, 2],
            includeReadme: false);
        MidiExportOutputTransaction transaction = new(new ScheduledFaultInjector(
            (MidiExportOutputFaultPoint.BeforePublish, 2),
            (MidiExportOutputFaultPoint.BeforeRollback, 1)));

        MidiExportOutputException failure = await Assert.ThrowsAsync<MidiExportOutputException>(() =>
            transaction.PublishAsync(plan, CreateArtifacts(plan), overwriteAuthorized: true));

        Assert.Equal(MidiExportOutputStage.Rollback, failure.Stage);
        Assert.False(failure.RollbackSucceeded);
        Assert.NotNull(failure.StagingDirectory);
        Assert.True(Directory.Exists(failure.StagingDirectory));
        Assert.NotEmpty(Directory.GetFiles(failure.StagingDirectory));
    }

    [Fact]
    public async Task CleanupFailureDoesNotReverseSuccessfulPublication()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("exports");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "Song.mid"), [1]);
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Song",
            null,
            includeReadme: false);
        MidiExportOutputTransaction transaction = new(
            new ScheduledFaultInjector((MidiExportOutputFaultPoint.BeforeCleanup, 1)));

        MidiExportOutputResult result = await transaction.PublishAsync(
            plan,
            CreateArtifacts(plan),
            overwriteAuthorized: true);

        Assert.True(result.Succeeded);
        MidiExportOutputDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal("MIDORA-MIDI-EXPORT-CLEANUP-FAILED", warning.Code);
        StandardMidiFile.ValidateType1(await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "Song.mid")));
        Assert.NotEmpty(Directory.GetDirectories(temporary.Path, ".midora-midi-export-*"));
    }

    [Fact]
    public async Task TargetAppearingAfterFreezeIsNeverSilentlyOverwritten()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = temporary.PathFor("exports");
        Directory.CreateDirectory(outputDirectory);
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Race",
            null,
            includeReadme: false);
        string targetPath = Path.Combine(outputDirectory, "Race.mid");
        byte[] appeared = [7, 7, 7];
        await File.WriteAllBytesAsync(targetPath, appeared);

        MidiExportOutputException failure = await Assert.ThrowsAsync<MidiExportOutputException>(() =>
            new MidiExportOutputTransaction().PublishAsync(
                plan,
                CreateArtifacts(plan),
                overwriteAuthorized: true));

        Assert.Equal(MidiExportOutputStage.Finalizing, failure.Stage);
        Assert.True(failure.RollbackSucceeded);
        Assert.Equal(appeared, await File.ReadAllBytesAsync(targetPath));
        AssertNoTransactionDirectories(temporary.Path);
    }

    private static MidiExportPreparedArtifact[] CreateArtifacts(
        MidiExportFrozenOutputPlan plan,
        bool invalidMidi = false)
    {
        byte[] midi = invalidMidi ? [1, 2, 3] : CreateValidMidi();
        return plan.Targets.Select(target => new MidiExportPreparedArtifact(
            target.SourceKey,
            target.FileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase)
                ? midi
                : "# Midora MIDI Export\n"u8)).ToArray();
    }

    private static byte[] CreateValidMidi() => StandardMidiFile.EncodeType1(
        192,
        [new StandardMidiFileTrack(0, [
            StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Conductor")])]);

    private static void AssertNoTransactionDirectories(string directory) =>
        Assert.Empty(Directory.GetDirectories(directory, ".midora-midi-export-*"));

    private sealed class ScheduledFaultInjector(
        params (MidiExportOutputFaultPoint Point, int Occurrence)[] requests)
        : IMidiExportOutputFaultInjector
    {
        private readonly Dictionary<MidiExportOutputFaultPoint, int> _seen = [];

        public void ThrowIfRequested(MidiExportOutputFaultPoint point, string path)
        {
            int seen = _seen.GetValueOrDefault(point) + 1;
            _seen[point] = seen;
            if (requests.Any(request => request.Point == point && request.Occurrence == seen))
            {
                throw new IOException($"Injected MIDI output fault at {point} for '{path}'.");
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-midi-output-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string value) => System.IO.Path.Combine(Path, value);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
