namespace Midora.Persistence;

public static class PersistenceContractV4
{
    public const int FileFormatVersion = 4;
    public const int ManifestSchemaVersion = 4;
    public const int ProjectPresentationSchemaVersion = 4;
    public const int EventInstrumentSchemaVersion = PersistenceContractV3.EventInstrumentSchemaVersion;
    public const int ReusedComponentSchemaVersion = PersistenceContractV3.ReusedComponentSchemaVersion;
    public const int InstrumentChangesSchemaVersion = 1;
    public const string InstrumentChangesPath = "settings/instrument-changes.pb";
}
