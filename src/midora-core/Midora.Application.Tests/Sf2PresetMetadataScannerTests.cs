using System.Text;

namespace Midora.Application.Tests;

public sealed class Sf2PresetMetadataScannerTests
{
    [Fact]
    public void ScanReadsOnlyPlayablePhdrRecordsAndPreservesRawUshortBank()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "catalog.sf2");
        File.WriteAllBytes(path, CreateSf2(
            new Preset("Grand", 0, 0),
            new Preset("Drums", 4, 128),
            new Preset("Étude", 9, 400)));

        Sf2PresetScanResult result = Sf2PresetMetadataScanner.Scan(path);

        Assert.Equal(Path.GetFullPath(path), result.SourcePath);
        Assert.Equal(3, result.Presets.Count);
        Assert.Equal(new Sf2PresetMetadata(0, 0, "Grand", 0), result.Presets[0]);
        Assert.Equal((ushort)128, result.Presets[1].RawBank);
        Assert.Equal((ushort)400, result.Presets[2].RawBank);
        Assert.Equal("Étude", result.Presets[2].DisplayName);
    }

    [Fact]
    public void DefaultProjectionOnlyMapsRawBanksRepresentableAsMidiMsb()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "extended.sf2");
        File.WriteAllBytes(path, CreateSf2(
            new Preset("Normal", 1, 5),
            new Preset("Extended", 2, 128)));
        Sf2PresetScanResult scan = Sf2PresetMetadataScanner.Scan(path);
        SoundFontEntryId soundFontId = SoundFontEntryId.Create();

        InvalidDataException withoutRule = Assert.Throws<InvalidDataException>(() =>
            Sf2PresetMetadataScanner.CreateImportedProfile(scan, soundFontId, "Profile"));
        InstrumentCatalogProfile mapped = Sf2PresetMetadataScanner.CreateImportedProfile(
            scan,
            soundFontId,
            "Profile",
            [new Sf2BankProjectionRule(128, 10, 20)]);

        Assert.Contains("requires an explicit", withoutRule.Message, StringComparison.Ordinal);
        Assert.Equal([(byte)5, (byte)10], mapped.Banks.Select(value => value.BankMsb));
        Assert.Equal([(byte)0, (byte)20], mapped.Banks.Select(value => value.BankLsb));
        Assert.Equal("Extended", mapped.Banks[1].Programs.Single().DisplayName);
        Assert.Equal(soundFontId, mapped.SourceSoundFontEntryId);
    }

    [Fact]
    public void ProjectionRejectsUnrepresentablePresetAndMappedCollisions()
    {
        SoundFontEntryId soundFontId = SoundFontEntryId.Create();
        Sf2PresetScanResult invalidPreset = new(
            Path.GetFullPath("invalid.sf2"),
            0,
            [new(0, 128, "Invalid", 0)]);
        Sf2PresetScanResult collision = new(
            Path.GetFullPath("collision.sf2"),
            0,
            [new(1, 2, "One", 0), new(2, 2, "Two", 1)]);

        Assert.Throws<InvalidDataException>(() =>
            Sf2PresetMetadataScanner.CreateImportedProfile(invalidPreset, soundFontId, "Invalid"));
        Assert.Throws<InvalidDataException>(() =>
            Sf2PresetMetadataScanner.CreateImportedProfile(
                collision,
                soundFontId,
                "Collision",
                [new(1, 3, 4), new(2, 3, 4)]));
    }

    [Fact]
    public void MalformedOrCancelledScansFailWithoutProducingAPartialProfile()
    {
        using TemporaryDirectory directory = new();
        string malformedPath = Path.Combine(directory.Path, "malformed.sf2");
        byte[] malformed = CreateSf2(new Preset("Only", 0, 0));
        Array.Resize(ref malformed, malformed.Length - 1);
        File.WriteAllBytes(malformedPath, malformed);
        string cancelledPath = Path.Combine(directory.Path, "cancelled.sf2");
        File.WriteAllBytes(cancelledPath, CreateSf2(new Preset("Only", 0, 0)));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<InvalidDataException>(() => Sf2PresetMetadataScanner.Scan(malformedPath));
        Assert.Throws<OperationCanceledException>(() =>
            Sf2PresetMetadataScanner.Scan(cancelledPath, cancellation.Token));
    }

    [Fact]
    public void ScanHonorsOddChunkPaddingAndKeepsRawBank65535()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "padded.sf2");
        File.WriteAllBytes(
            path,
            CreateSf2WithLeadingJunk(
                [0x5A],
                new Preset("Maximum Bank", 127, ushort.MaxValue)));

        Sf2PresetScanResult scan = Sf2PresetMetadataScanner.Scan(path);

        Sf2PresetMetadata preset = Assert.Single(scan.Presets);
        Assert.Equal(ushort.MaxValue, preset.RawBank);
        Assert.Equal((ushort)127, preset.RawPreset);
    }

    [Fact]
    public void ScanSeeksAcrossLargeSampleLikeChunkInsteadOfMaterializingIt()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "large-skip.sf2");
        const uint skippedBytes = 32 * 1024 * 1024;
        byte[] pdta = CreatePdta(new Preset("After Samples", 7, 3));
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteFourCc(writer, "RIFF");
            writer.Write(checked((uint)(4 + 8L + skippedBytes + pdta.Length)));
            WriteFourCc(writer, "sfbk");
            WriteFourCc(writer, "JUNK");
            writer.Write(skippedBytes);
            stream.Position = checked(stream.Position + skippedBytes);
            writer.Write(pdta);
            stream.SetLength(stream.Position);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Sf2PresetScanResult scan = Sf2PresetMetadataScanner.Scan(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("After Samples", Assert.Single(scan.Presets).DisplayName);
        Assert.True(allocated < 256 * 1024, $"Scanner allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void ScanRejectsAPhdrWithoutTheRequiredTerminalEop()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "missing-eop.sf2");
        byte[] bytes = CreateSf2(new Preset("Playable", 0, 0));
        int eopOffset = bytes.AsSpan().LastIndexOf("EOP"u8);
        Assert.True(eopOffset >= 0);
        bytes[eopOffset] = (byte)'X';
        File.WriteAllBytes(path, bytes);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Sf2PresetMetadataScanner.Scan(path));

        Assert.Contains("EOP", error.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateSf2(params Preset[] presets)
    {
        byte[] pdta = CreatePdta(presets);
        using MemoryStream result = new();
        using BinaryWriter riff = new(result, Encoding.UTF8, leaveOpen: true);
        WriteFourCc(riff, "RIFF");
        riff.Write(checked((uint)(4 + pdta.Length)));
        WriteFourCc(riff, "sfbk");
        riff.Write(pdta);
        riff.Flush();
        return result.ToArray();
    }

    private static byte[] CreateSf2WithLeadingJunk(byte[] junk, params Preset[] presets)
    {
        byte[] pdta = CreatePdta(presets);
        using MemoryStream result = new();
        using BinaryWriter riff = new(result, Encoding.UTF8, leaveOpen: true);
        WriteFourCc(riff, "RIFF");
        riff.Write(checked((uint)(4 + 8 + junk.Length + (junk.Length & 1) + pdta.Length)));
        WriteFourCc(riff, "sfbk");
        WriteFourCc(riff, "JUNK");
        riff.Write(checked((uint)junk.Length));
        riff.Write(junk);
        if ((junk.Length & 1) != 0)
        {
            riff.Write((byte)0);
        }
        riff.Write(pdta);
        riff.Flush();
        return result.ToArray();
    }

    private static byte[] CreatePdta(params Preset[] presets)
    {
        using MemoryStream phdr = new();
        using (BinaryWriter writer = new(phdr, Encoding.UTF8, leaveOpen: true))
        {
            foreach (Preset preset in presets)
            {
                WritePresetHeader(writer, preset);
            }
            WritePresetHeader(writer, new Preset("EOP", 0, 0));
        }

        using MemoryStream result = new();
        using BinaryWriter riff = new(result, Encoding.UTF8, leaveOpen: true);
        WriteFourCc(riff, "LIST");
        riff.Write(checked((uint)(4 + 8 + phdr.Length)));
        WriteFourCc(riff, "pdta");
        WriteFourCc(riff, "phdr");
        riff.Write(checked((uint)phdr.Length));
        riff.Write(phdr.ToArray());
        riff.Flush();
        return result.ToArray();
    }

    private static void WritePresetHeader(BinaryWriter writer, Preset preset)
    {
        byte[] name = new byte[20];
        byte[] encoded = Encoding.Latin1.GetBytes(preset.Name);
        encoded.AsSpan(0, Math.Min(encoded.Length, name.Length)).CopyTo(name);
        writer.Write(name);
        writer.Write(preset.Program);
        writer.Write(preset.Bank);
        writer.Write((ushort)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
    }

    private static void WriteFourCc(BinaryWriter writer, string value)
    {
        Assert.Equal(4, value.Length);
        writer.Write(Encoding.ASCII.GetBytes(value));
    }

    private sealed record Preset(string Name, ushort Program, ushort Bank);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-sf2-scan-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
