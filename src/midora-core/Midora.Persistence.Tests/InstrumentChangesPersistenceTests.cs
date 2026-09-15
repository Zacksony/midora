using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Security.Cryptography;
using System.IO.Compression;
using Midora.Domain;
using Midora.Persistence.Wire.InstrumentChanges.V1;

namespace Midora.Persistence.Tests;

public sealed class InstrumentChangesPersistenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void AssociationDescriptorAndGoldenBytesAreFrozen()
    {
        FileDescriptorSet descriptor = new(); descriptor.File.Add(InstrumentChangesV1.Descriptor.File.ToProto());
        string hash = Convert.ToHexStringLower(SHA256.HashData(StrictProtobufWireV1.SerializeDeterministic(descriptor)));
        output.WriteLine("Association descriptor SHA256: " + hash);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schemas", "Proto",
            "midora-instrument-changes-v1.descriptor.sha256")).Trim(), hash);
        var wire = new InstrumentChangesV1 { SchemaVersion = 1 };
        wire.Changes.Add(new InstrumentChangeV1 { Id = 100, OwnerId = 101, DirectMidi = true,
            BankEventId = 1, BankLsbEventId = 2, ProgramEventId = 3 });
        Assert.Equal("0801120c086410651801200128023003", Convert.ToHexStringLower(StrictProtobufWireV1.SerializeDeterministic(wire)));
        Assert.Equal(1, InstrumentChangesV1.Descriptor.FindFieldByName("schema_version").FieldNumber);
        Assert.Equal(6, InstrumentChangeV1.Descriptor.FindFieldByName("program_event_id").FieldNumber);
        Assert.All(InstrumentChangeV1.Descriptor.Fields.InFieldNumberOrder(), field => Assert.True(field.HasPresence));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownAndDuplicateRootFieldsAreRejected(bool duplicate)
    {
        using var target = new MidoraProject(480);
        // Empty payload isolates field rejection from owner checks.
        using var input = new MemoryStream([8, 1, duplicate ? (byte)8 : (byte)24, 1]);
        Assert.Throws<InvalidDataException>(() => InstrumentChangesProtobufCodecV1.Restore(target, input, default));
    }

    [Fact]
    public void Format4ManifestSchemaIsFrozen()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Schemas", "Json", "manifest-v4.schema.json");
        Assert.Equal("9a9ab454dbca12ddd41f00f91ec7e7547ab38c7a88c374c5541f443da8804495",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task Format4RoundTripPreservesStableGroupsAndRawBytes()
    {
        using var source = CreateProject();
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "instrument-changes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "first.midora"), second = Path.Combine(directory, "second.midora");
        try
        {
            var service = new MidoraProjectPackageV1("1.0.0-dev", new FixedClock());
            await service.SaveCopyAsync(source, first); await service.SaveCopyAsync(source, second);
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
            var opened = await service.OpenAsync(first);
            using var restored = opened.Project;
            Assert.Equal(4, opened.SourceFileFormatVersion); Assert.False(opened.IsModified);
            Assert.Equal(source.NextStableId, restored.NextStableId);
            Assert.Equal(source.PureMidiTracks[0].Segments[0].InstrumentChanges.Values,
                restored.PureMidiTracks[0].Segments[0].InstrumentChanges.Values);
            Assert.Equal(source.EventInstruments[0].SubVoices[0].InstrumentChanges.Values,
                restored.EventInstruments[0].SubVoices[0].InstrumentChanges.Values);
            using var expected = new MemoryStream(); using var actual = new MemoryStream();
            InstrumentChangesProtobufCodecV1.Serialize(source, expected, default);
            InstrumentChangesProtobufCodecV1.Serialize(restored, actual, default);
            Assert.Equal(expected.ToArray(), actual.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void InvalidAssociationsAreStrictSourceErrors(int fault)
    {
        using var project = CreateProject();
        using var encoded = new MemoryStream(); InstrumentChangesProtobufCodecV1.Serialize(project, encoded, default);
        var wire = InstrumentChangesV1.Parser.ParseFrom(encoded.ToArray());
        var entry = wire.Changes[0];
        switch (fault)
        {
            case 0: entry.OwnerId = long.MaxValue; break;
            case 1: entry.BankEventId = entry.ProgramEventId; break;
            case 2: wire.Changes.Add(entry.Clone()); break;
            case 3: entry.ClearProgramEventId(); break;
            case 4: wire.ClearSchemaVersion(); break;
        }
        project.PureMidiTracks[0].Segments[0].InstrumentChanges = InstrumentChangeSet.Empty;
        project.EventInstruments[0].SubVoices[0].InstrumentChanges = InstrumentChangeSet.Empty;
        using var input = new MemoryStream(wire.ToByteArray());
        Assert.Throws<InvalidDataException>(() => InstrumentChangesProtobufCodecV1.Restore(project, input, default));
    }

    private static MidoraProject CreateProject()
    {
        var project = new MidoraProject(480, new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { LengthTicks = 960 };
        track.Segments.Add(segment); project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var bank = new DirectMidiChannelEvent(project) { Tick = 120, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 0, Data2 = 3, Order = 0 };
        var lsb = new DirectMidiChannelEvent(project) { Tick = 120, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 32, Data2 = 4, Order = 1 };
        var program = new DirectMidiChannelEvent(project) { Tick = 120, Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = 5, Order = 2 };
        segment.ChannelEvents.AddRange([bank, lsb, program]);
        segment.InstrumentChanges = InstrumentChangeSet.Empty.Add(new(project.AllocateStableId(), bank.Id, lsb.Id, program.Id), true);
        var instrument = new EventInstrument(project) { Name = "Instrument", TemplateLengthTicks = 960 };
        var voice = new SubVoice(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        var tb = TemplateEvent.Bank(project, 240, 1, 2);
        var tp = new TemplateEvent(project) { Kind = TemplateEventKind.Program, Tick = 240, Value = 3 };
        voice.Events.AddRange([tb, tp]);
        voice.InstrumentChanges = InstrumentChangeSet.Empty.Add(new(project.AllocateStableId(), tb.Id, null, tp.Id), false);
        return project;
    }

    [Fact]
    public async Task AcceptancePackagesHaveCurrentGroupsAndAReadableUnwrappedFormat3Source()
    {
        string? export = Environment.GetEnvironmentVariable("MIDORA_A2A_UAT_DIRECTORY");
        string directory = export is null
            ? Path.Combine(AppContext.BaseDirectory, ".tmp", "a2a-acceptance-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(export);
        Directory.CreateDirectory(directory);
        try
        {
            using var source = CreateProject();
            source.Metadata.ProjectName = "A2a Instrument Selection";
            var midi = source.PureMidiTracks[0].Segments[0];
            midi.Notes.Add(new DirectMidiNote(source) { StartTick = 120, LengthTicks = 240,
                Key = 60, NoteOnVelocity = 100, NoteOnOrder = 3, NoteOffOrder = 4 });
            var voice = source.EventInstruments[0].SubVoices[0];
            voice.Events.AddRange([
                TemplateEvent.Note(source, 0, 120, 60, 100),
                TemplateEvent.Bank(source, 480, 9, null),
                TemplateEvent.Bank(source, 600, null, 10),
                TemplateEvent.Program(source, 720, 11)
            ]);
            source.EventInstruments[0].InitialState.BankMsb = 8;
            voice.InitialState.Program = 3;
            var service = new MidoraProjectPackageV1("1.0.0-dev", new FixedClock());
            string current = Path.Combine(directory, "A2a-Instrument-Changes.midora");
            string legacy = Path.Combine(directory, "A2a-Legacy-Format-3.midora");
            await service.SaveCopyAsync(source, current);
            midi.InstrumentChanges = InstrumentChangeSet.Empty;
            voice.InstrumentChanges = InstrumentChangeSet.Empty;
            await service.SaveCopyAsync(source, legacy);
            // Generate a test-only pre-feature package using the frozen V3
            // manifest codec; raw music and presentation components are shared.
            using (var file = new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("manifest.json")!;
                using var bytes = new MemoryStream();
                using (var input = entry.Open()) input.CopyTo(bytes);
                var manifest = ManifestCodecV4.Parse(bytes.ToArray());
                byte[] oldManifest = ManifestCodecV3.Serialize(new ManifestJsonV3
                {
                    Magic = manifest.Magic, FileFormatVersion = 3, MinimumReadableVersion = 3, ManifestSchemaVersion = 3,
                    CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
                    LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
                    Files = manifest.Files.Where(value => value.Path != "settings/instrument-changes.pb").ToArray()
                });
                entry.Delete(); archive.GetEntry("settings/instrument-changes.pb")!.Delete();
                using var output = archive.CreateEntry("manifest.json").Open(); output.Write(oldManifest);
            }
            var opened = await service.OpenAsync(current);
            using (opened.Project)
            {
                Assert.Equal(4, opened.SourceFileFormatVersion);
                Assert.Single(opened.Project.PureMidiTracks[0].Segments[0].InstrumentChanges.Values);
                Assert.Single(opened.Project.EventInstruments[0].SubVoices[0].InstrumentChanges.Values);
            }
            opened = await service.OpenAsync(legacy);
            using (opened.Project)
            {
                Assert.Equal(3, opened.SourceFileFormatVersion);
                Assert.Empty(opened.Project.EventInstruments[0].SubVoices[0].InstrumentChanges.Values);
                Assert.Equal(6, opened.Project.EventInstruments[0].SubVoices[0].Events.Count);
            }
            output.WriteLine("Acceptance packages verified: " + directory);
        }
        finally { if (export is null) Directory.Delete(directory, recursive: true); }
    }
    private sealed class FixedClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero); }
}
