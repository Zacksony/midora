namespace Midora.Persistence;

public static class PersistenceContractV3
{
    public const int FileFormatVersion = 3;
    public const int ManifestSchemaVersion = 3;
    public const int ProjectPresentationSchemaVersion = 3;
    public const int EventInstrumentSchemaVersion = PersistenceContractV2.EventInstrumentSchemaVersion;
    public const int ReusedComponentSchemaVersion = PersistenceContractV2.ReusedComponentSchemaVersion;

    public const string JsonSchemaDialect = PersistenceContractV2.JsonSchemaDialect;
    public const string ProtobufEdition = PersistenceContractV2.ProtobufEdition;
    public const string GoogleProtobufVersion = PersistenceContractV2.GoogleProtobufVersion;
    public const string GrpcToolsVersion = PersistenceContractV2.GrpcToolsVersion;
}
