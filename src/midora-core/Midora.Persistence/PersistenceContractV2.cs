namespace Midora.Persistence;

public static class PersistenceContractV2
{
    public const int FileFormatVersion = 2;
    public const int ManifestSchemaVersion = 2;
    public const int EventInstrumentSchemaVersion = 2;
    public const int ReusedComponentSchemaVersion = PersistenceContractV1.SchemaVersion;

    public const string JsonSchemaDialect = PersistenceContractV1.JsonSchemaDialect;
    public const string ProtobufEdition = PersistenceContractV1.ProtobufEdition;
    public const string GoogleProtobufVersion = PersistenceContractV1.GoogleProtobufVersion;
    public const string GrpcToolsVersion = PersistenceContractV1.GrpcToolsVersion;
}
