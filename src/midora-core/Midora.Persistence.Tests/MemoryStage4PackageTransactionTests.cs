using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class MemoryStage4PackageTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedPagedLogicalAndSubVoiceContentRemainsEditableAndResaves(bool subVoice)
    {
        using Fixture fixture = new();
        MidoraProject project = fixture.Project;
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Paged instrument");
        instrument.TemplateLengthTicks = 20_000;
        instrument.LoopStartTick = 192;
        instrument.LoopEndTick = 384;
        instrument.PreRollTicks = 96;
        EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack track = new(project) { Name = "Paged track", EventInstrumentUsageId = usage.Id };
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        Segment segment = new(project) { LengthTicks = 20_000 };
        track.Segments.Add(segment);
        for (int i = 0; i < 4097; i++)
            if (subVoice) instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, i * 4L, 3, i % 128, 100));
            else segment.Notes.Add(new LogicalNote(project) { StartTick = i * 4L, LengthTicks = 3, Note = i % 128, Velocity = 100 });
        var packages = new MidoraProjectPackageV1("1.0.0-dev");
        await packages.SaveCopyAsync(project, fixture.Target, overwriteAuthorized: true);
        using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(fixture.Target);
        Assert.Empty(opened.Diagnostics);
        var restored = opened.Project;
        if (subVoice)
        {
            var events = restored.EventInstruments[0].SubVoices[0].Events;
            var before = events.CreateQuerySnapshot();
            events[4000].Value = 57;
            Assert.Equal(100, before.GetByOrdinal(4000).Value);
            Assert.Equal(57, events.CreateQuerySnapshot().GetByOrdinal(4000).Value);
        }
        else
        {
            var notes = restored.Tracks[0].Segments[0].Notes;
            var before = notes.CreateQuerySnapshot();
            notes[4000].Velocity = 57;
            Assert.Equal(100, before.GetByOrdinal(4000).Velocity);
            Assert.Equal(57, notes.CreateQuerySnapshot().GetByOrdinal(4000).Velocity);
        }
        string edited = Path.Combine(fixture.Directory, "edited.midora");
        await packages.SaveCopyAsync(restored, edited);
        using MidoraProjectOpenResultV1 final = await packages.OpenAsync(edited);
        Assert.Empty(final.Diagnostics);
        Assert.Equal(57, subVoice ? final.Project.EventInstruments[0].SubVoices[0].Events[4000].Value
            : final.Project.Tracks[0].Segments[0].Notes[4000].Velocity);
        Assert.Equal(96, final.Project.EventInstruments[0].PreRollTicks);
        Assert.Equal(192, final.Project.EventInstruments[0].LoopStartTick);
        Assert.Equal(384, final.Project.EventInstruments[0].LoopEndTick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterStreamingContentOrZipKeepsOriginalTarget(bool afterZip)
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        bool reached = false;
        var packages = new MidoraProjectPackageV1("1.0.0-dev", null, new CallbackFault((point, _) =>
        {
            if (point != (afterZip ? MidoraPackageFaultPointV1.BeforeSelfValidation : MidoraPackageFaultPointV1.BeforeZipWrite)) return;
            string staging = Directory.GetDirectories(fixture.Directory, ".midora-save-*").Single();
            Assert.True(new FileInfo(Path.Combine(staging, "conductor-track.json")).Length > 100_000);
            reached = true;
            cancellation.Cancel();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packages.SaveProjectAsync(
            fixture.Project, fixture.Target, overwriteAuthorized: true, cancellationToken: cancellation.Token));
        Assert.True(reached);
        fixture.AssertUnchangedAndClean();
    }

    [Fact]
    public async Task SelfValidationStillComparesEveryStagedByteBeforeReplacingTarget()
    {
        using Fixture fixture = new();
        bool corrupted = false;
        var packages = new MidoraProjectPackageV1("1.0.0-dev", null, new CallbackFault((point, _) =>
        {
            if (point != MidoraPackageFaultPointV1.BeforeSelfValidation) return;
            string staging = Directory.GetDirectories(fixture.Directory, ".midora-save-*").Single();
            // ZIP and manifest are already correct. Alter only the staging source
            // used by the second serializer: a digest-only comparison would miss it.
            using FileStream file = new(Path.Combine(staging, "conductor-track.json"), FileMode.Open, FileAccess.ReadWrite);
            file.Position = file.Length / 2;
            int value = file.ReadByte();
            file.Position--;
            file.WriteByte((byte)(value ^ 1));
            corrupted = true;
        }));
        MidoraPackageExceptionV1 error = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.SaveProjectAsync(fixture.Project, fixture.Target, overwriteAuthorized: true));
        Assert.True(corrupted);
        Assert.Equal(MidoraPackageStageV1.SelfValidation, error.Stage);
        fixture.AssertUnchangedAndClean();
    }

    private sealed class CallbackFault(Action<MidoraPackageFaultPointV1, string> callback) : IMidoraPackageFaultInjectorV1
    {
        public void ThrowIfRequested(MidoraPackageFaultPointV1 point, string path) => callback(point, path);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, ".tmp", "stage4-transaction-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Directory, "existing.midora");
        public MidoraProject Project { get; } = new(480);
        private readonly byte[] _original = [1, 4, 9, 16, 25];
        private readonly DateTimeOffset _modified;
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Target, _original);
            _modified = Project.Metadata.ModifiedAtUtc;
            Project.Conductor.Tempos.AddRange(Enumerable.Range(1, 4097)
                .Select(i => new TempoChange(Project, i * 4L, 100m + i % 60)));
        }
        public void AssertUnchangedAndClean()
        {
            Assert.Equal(_original, File.ReadAllBytes(Target));
            Assert.Equal(_modified, Project.Metadata.ModifiedAtUtc);
            Assert.Equal(4098, Project.Conductor.Tempos.Count);
            Assert.Equal([Target], System.IO.Directory.GetFileSystemEntries(Directory));
        }
        public void Dispose()
        {
            Project.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
