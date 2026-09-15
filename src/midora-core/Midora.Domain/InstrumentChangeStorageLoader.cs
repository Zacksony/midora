namespace Midora.Domain;

public readonly record struct InstrumentChangeOwnerRecord(MidoraId OwnerId, bool DirectMidi, InstrumentChange Change);

/// <summary>Physical loading policy only. Records have already passed wire and owner/member validation.</summary>
public interface IInstrumentChangeStorageLoader
{
    void Load(MidoraProject project, IEnumerable<InstrumentChangeOwnerRecord> records, CancellationToken token);
    void PrepareSource(TemplateEventQuerySnapshot source, CancellationToken token);
}
