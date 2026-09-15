using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.InstrumentChanges.V1;

namespace Midora.Persistence;

internal static class InstrumentChangesProtobufCodecV1
{
    public static void Serialize(MidoraProject project, Stream destination, CancellationToken token, IInstrumentChangeStorageLoader? storage = null)
    {
        using var output = new CodedOutputStream(destination, leaveOpen: true) { Deterministic = true };
        output.WriteRawTag(8); output.WriteUInt32(1);
        foreach (var segment in project.PureMidiTracks.SelectMany(track => track.Segments).OrderBy(owner => owner.Id))
        {
            if (segment.InstrumentChanges.Count == 0) continue;
            var snapshot = segment.ChannelEvents.CreateQuerySnapshot();
            foreach (var group in segment.InstrumentChanges.Values)
            {
                token.ThrowIfCancellationRequested();
                if (!InstrumentChangeResolver.TryRead(snapshot, group, out _)) throw Invalid();
                Write(segment.Id, true, group);
            }
        }
        foreach (var voice in project.EventInstruments.SelectMany(instrument => instrument.SubVoices).OrderBy(owner => owner.Id))
        {
            if (voice.InstrumentChanges.Count == 0) continue;
            var snapshot = voice.Events.CreateQuerySnapshot();
            storage?.PrepareSource(snapshot, token);
            foreach (var group in voice.InstrumentChanges.Values)
            {
                token.ThrowIfCancellationRequested();
                if (!InstrumentChangeResolver.TryRead(snapshot, group, out _)) throw Invalid();
                Write(voice.Id, false, group);
            }
        }
        output.Flush();

        void Write(MidoraId ownerId, bool direct, InstrumentChange group)
        {
            group.ValidateShape(direct);
            var wire = new InstrumentChangeV1
            {
                Id = group.Id.Value, OwnerId = ownerId.Value, DirectMidi = direct,
                BankEventId = group.BankEventId.Value, ProgramEventId = group.ProgramEventId.Value
            };
            if (group.BankLsbEventId is { } lsb) wire.BankLsbEventId = lsb.Value;
            output.WriteRawTag(18); output.WriteMessage(wire);
        }
    }

    public static void Restore(MidoraProject project, Stream input, CancellationToken token, IInstrumentChangeStorageLoader? storage = null)
    {
        var midi = project.PureMidiTracks.SelectMany(track => track.Segments).ToDictionary(owner => owner.Id);
        var voices = project.EventInstruments.SelectMany(instrument => instrument.SubVoices).ToDictionary(owner => owner.Id);
        var midiSnapshots = new Dictionary<MidoraId, DirectMidiChannelEventQuerySnapshot>();
        var voiceSnapshots = new Dictionary<MidoraId, TemplateEventQuerySnapshot>();
        try
        {
            if (storage is not null) storage.Load(project, Read(), token);
            else foreach (var record in Read())
            {
                if (record.DirectMidi)
                { var owner = midi[record.OwnerId]; owner.InstrumentChanges = owner.InstrumentChanges.Add(record.Change, true); }
                else
                { var owner = voices[record.OwnerId]; owner.InstrumentChanges = owner.InstrumentChanges.Add(record.Change, false); }
            }
        }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid Instrument Change identity or membership.", exception); }

        IEnumerable<InstrumentChangeOwnerRecord> Read()
        {
            var reader = new StreamingProtobufReader(input, token);
            var frame = reader.Root(InstrumentChangesV1.Descriptor);
            bool versionSeen = false;
            Google.Protobuf.Reflection.FieldDescriptor? field;
            while ((field = reader.Field(ref frame)) is not null)
            {
                if (field.FieldNumber == 1)
                {
                    if (reader.Integer(frame, field) != 1) throw new InvalidDataException("Unsupported Instrument Changes schema version.");
                    versionSeen = true; continue;
                }
                var wire = (InstrumentChangeV1)reader.SmallMessage(reader.Child(frame, field));
                if (!wire.HasId || !wire.HasOwnerId || !wire.HasDirectMidi || !wire.HasBankEventId || !wire.HasProgramEventId
                    || wire.Id <= 0 || wire.OwnerId <= 0 || wire.BankEventId <= 0 || wire.ProgramEventId <= 0
                    || wire.DirectMidi != wire.HasBankLsbEventId || wire.HasBankLsbEventId && wire.BankLsbEventId <= 0)
                    throw Invalid();
                var group = new InstrumentChange(new(wire.Id), new(wire.BankEventId),
                    wire.HasBankLsbEventId ? new MidoraId(wire.BankLsbEventId) : null, new(wire.ProgramEventId));
                group.ValidateShape(wire.DirectMidi);
                var ownerId = new MidoraId(wire.OwnerId);
                if (wire.DirectMidi)
                {
                    if (!midi.TryGetValue(ownerId, out var owner)) throw Invalid();
                    if (!midiSnapshots.TryGetValue(owner.Id, out var snapshot))
                        midiSnapshots.Add(owner.Id, snapshot = owner.ChannelEvents.CreateQuerySnapshot());
                    if (!InstrumentChangeResolver.TryRead(snapshot, group, out _)) throw Invalid();
                }
                else
                {
                    if (!voices.TryGetValue(ownerId, out var owner)) throw Invalid();
                    if (!voiceSnapshots.TryGetValue(owner.Id, out var snapshot))
                    {
                        voiceSnapshots.Add(owner.Id, snapshot = owner.Events.CreateQuerySnapshot());
                        storage?.PrepareSource(snapshot, token);
                    }
                    if (!InstrumentChangeResolver.TryRead(snapshot, group, out _)) throw Invalid();
                }
                yield return new(ownerId, wire.DirectMidi, group);
            }
            if (!versionSeen) throw new InvalidDataException("Instrument Changes schema version is missing.");
            reader.ThrowSemanticFailure();
        }
    }

    private static InvalidDataException Invalid() => new("An Instrument Change has an invalid owner, member, target or Tick.");
}
