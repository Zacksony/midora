using System.Buffers.Binary;
using System.Text;

namespace Midora.Application;

public sealed record Sf2PresetMetadata(
    ushort RawBank,
    ushort RawPreset,
    string DisplayName,
    int SourceOrdinal);

public sealed record Sf2PresetScanResult(
    string SourcePath,
    long SourceLength,
    IReadOnlyList<Sf2PresetMetadata> Presets);

public readonly record struct Sf2BankProjectionRule(
    ushort RawBank,
    byte TargetBankMsb,
    byte TargetBankLsb)
{
    public InstrumentBankAddress Target => new(TargetBankMsb, TargetBankLsb);

    public void Validate() => Target.Validate();
}

public static class Sf2PresetMetadataScanner
{
    private const int PresetHeaderRecordBytes = 38;

    public static Sf2PresetScanResult Scan(
        string soundFontPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        string fullPath = Path.GetFullPath(soundFontPath);
        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.RandomAccess);
        if (stream.Length < 12)
        {
            throw new InvalidDataException("The SF2 file is shorter than a RIFF header.");
        }

        Span<byte> riffHeader = stackalloc byte[12];
        stream.ReadExactly(riffHeader);
        if (!riffHeader[..4].SequenceEqual("RIFF"u8)
            || !riffHeader[8..12].SequenceEqual("sfbk"u8))
        {
            throw new InvalidDataException("The selected file is not a RIFF sfbk SoundFont.");
        }
        long riffEnd = checked(8L + BinaryPrimitives.ReadUInt32LittleEndian(riffHeader[4..8]));
        if (riffEnd < 12 || riffEnd > stream.Length)
        {
            throw new InvalidDataException("The SF2 RIFF size exceeds the file bounds.");
        }

        (long Offset, uint Size)? phdr = null;
        long position = 12;
        Span<byte> listType = stackalloc byte[4];
        while (position < riffEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChunkHeader top = ReadChunkHeader(stream, position, riffEnd);
            if (top.Id == "LIST")
            {
                if (top.Size < 4)
                {
                    throw new InvalidDataException("An SF2 LIST chunk is too short.");
                }
                ReadExactlyAt(stream, top.DataOffset, listType);
                if (listType.SequenceEqual("pdta"u8))
                {
                    long childPosition = top.DataOffset + 4;
                    while (childPosition < top.DataEnd)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ChunkHeader child = ReadChunkHeader(stream, childPosition, top.DataEnd);
                        if (child.Id == "phdr")
                        {
                            if (phdr.HasValue)
                            {
                                throw new InvalidDataException("The SF2 file contains more than one phdr chunk.");
                            }
                            phdr = (child.DataOffset, child.Size);
                        }
                        childPosition = child.PaddedEnd;
                    }
                    if (childPosition != top.DataEnd)
                    {
                        throw new InvalidDataException("The SF2 pdta chunk has invalid alignment.");
                    }
                }
            }
            position = top.PaddedEnd;
        }
        if (position != riffEnd)
        {
            throw new InvalidDataException("The SF2 RIFF chunk has invalid alignment.");
        }
        if (!phdr.HasValue)
        {
            throw new InvalidDataException("The SF2 file does not contain a pdta/phdr chunk.");
        }
        if (phdr.Value.Size % PresetHeaderRecordBytes != 0)
        {
            throw new InvalidDataException("The SF2 phdr chunk size is not record-aligned.");
        }
        int recordCount = checked((int)(phdr.Value.Size / PresetHeaderRecordBytes));
        if (recordCount < 1
            || recordCount > InstrumentCatalogLimits.MaximumSf2PresetRecords)
        {
            throw new InvalidDataException(
                $"The SF2 phdr record count must be between 1 and {InstrumentCatalogLimits.MaximumSf2PresetRecords}.");
        }

        List<Sf2PresetMetadata> presets = new(Math.Max(0, recordCount - 1));
        Span<byte> record = stackalloc byte[PresetHeaderRecordBytes];
        for (int ordinal = 0; ordinal < recordCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactlyAt(
                stream,
                phdr.Value.Offset + ((long)ordinal * PresetHeaderRecordBytes),
                record);
            if (ordinal == recordCount - 1)
            {
                // The final phdr entry is the mandatory terminal record, not a playable preset.
                if (!string.Equals(
                    DecodeRawPresetName(record[..20]),
                    "EOP",
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The final SF2 phdr record is not the required EOP terminal record.");
                }
                break;
            }
            ushort preset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..22]);
            ushort bank = BinaryPrimitives.ReadUInt16LittleEndian(record[22..24]);
            presets.Add(new(bank, preset, DecodePresetName(record[..20], preset), ordinal));
        }
        return new(fullPath, stream.Length, presets.AsReadOnly());
    }

    public static InstrumentCatalogProfile CreateImportedProfile(
        Sf2PresetScanResult scan,
        SoundFontEntryId soundFontEntryId,
        string displayName,
        IReadOnlyList<Sf2BankProjectionRule>? projectionRules = null,
        InstrumentCatalogProfileId? profileId = null,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(scan.Presets);
        soundFontEntryId.Validate();
        Dictionary<ushort, InstrumentBankAddress> projections = [];
        foreach (Sf2BankProjectionRule rule in projectionRules ?? [])
        {
            rule.Validate();
            if (!projections.TryAdd(rule.RawBank, rule.Target))
            {
                throw new ArgumentException(
                    $"The SF2 bank projection contains duplicate raw bank {rule.RawBank}.",
                    nameof(projectionRules));
            }
        }

        Dictionary<InstrumentBankAddress, Dictionary<byte, InstrumentCatalogProgram>> banks = [];
        foreach (Sf2PresetMetadata preset in scan.Presets.OrderBy(value => value.SourceOrdinal))
        {
            if (preset.RawPreset > 127)
            {
                throw new InvalidDataException(
                    $"SF2 preset {preset.RawPreset} cannot be represented by a MIDI Program value.");
            }
            InstrumentBankAddress targetBank;
            if (!projections.TryGetValue(preset.RawBank, out targetBank))
            {
                if (preset.RawBank > 127)
                {
                    throw new InvalidDataException(
                        $"SF2 raw bank {preset.RawBank} requires an explicit MSB/LSB projection.");
                }
                targetBank = new((byte)preset.RawBank, 0);
            }
            Dictionary<byte, InstrumentCatalogProgram> programs = banks.GetValueOrDefault(targetBank)
                ?? [];
            if (!banks.ContainsKey(targetBank))
            {
                banks.Add(targetBank, programs);
            }
            byte program = (byte)preset.RawPreset;
            if (!programs.TryAdd(program, new(program, preset.DisplayName)))
            {
                throw new InvalidDataException(
                    $"The SF2 projection produces duplicate Program {program} in Bank {targetBank.BankMsb}.{targetBank.BankLsb}.");
            }
        }

        return new InstrumentCatalogProfile(
            profileId ?? InstrumentCatalogProfileId.Create(),
            displayName,
            enabled,
            InstrumentCatalogSourceKind.ImportedSf2,
            soundFontEntryId,
            banks.Select(pair => new InstrumentCatalogBank(
                    pair.Key.BankMsb,
                    pair.Key.BankLsb,
                    DisplayName: null,
                    pair.Value.Values.OrderBy(value => value.Program).ToArray()))
                .OrderBy(value => value.BankMsb)
                .ThenBy(value => value.BankLsb)
                .ToArray())
            .Normalize();
    }

    private static ChunkHeader ReadChunkHeader(FileStream stream, long position, long containerEnd)
    {
        if (position < 0 || containerEnd - position < 8)
        {
            throw new InvalidDataException("An SF2 chunk header exceeds its container bounds.");
        }
        Span<byte> header = stackalloc byte[8];
        ReadExactlyAt(stream, position, header);
        string id = Encoding.ASCII.GetString(header[..4]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        long dataOffset = checked(position + 8);
        long dataEnd = checked(dataOffset + size);
        long paddedEnd = checked(dataEnd + (size & 1));
        if (dataEnd > containerEnd || paddedEnd > containerEnd)
        {
            throw new InvalidDataException($"SF2 chunk '{id}' exceeds its container bounds.");
        }
        return new(id, size, dataOffset, dataEnd, paddedEnd);
    }

    private static void ReadExactlyAt(FileStream stream, long position, Span<byte> buffer)
    {
        stream.Position = position;
        stream.ReadExactly(buffer);
    }

    private static string DecodePresetName(ReadOnlySpan<byte> bytes, ushort preset)
    {
        string result = DecodeRawPresetName(bytes);
        return result.Length == 0 ? $"Preset {preset}" : result;
    }

    private static string DecodeRawPresetName(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        if (zero >= 0)
        {
            bytes = bytes[..zero];
        }
        StringBuilder builder = new(bytes.Length);
        foreach (byte value in bytes)
        {
            builder.Append(value switch
            {
                >= 0x20 and <= 0x7E => (char)value,
                >= 0xA0 => (char)value,
                _ => '\uFFFD'
            });
        }
        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    private readonly record struct ChunkHeader(
        string Id,
        uint Size,
        long DataOffset,
        long DataEnd,
        long PaddedEnd);
}
