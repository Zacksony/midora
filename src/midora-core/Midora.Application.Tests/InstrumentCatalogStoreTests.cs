using System.Text;

namespace Midora.Application.Tests;

public sealed class InstrumentCatalogStoreTests
{
    [Fact]
    public void MissingFileReturnsBuiltInDefaultWithoutNotice()
    {
        using TemporaryDirectory directory = new();

        InstrumentCatalogLoadResult result = new InstrumentCatalogStore(
            Path.Combine(directory.Path, "missing.json")).Load();

        Assert.Equal(InstrumentCatalogState.Default, result.Catalog);
        Assert.Null(result.Notice);
        Assert.True(result.CanPublish);
    }

    [Fact]
    public void RoundTripIsDeterministicAndPreservesProfilePriorityOrder()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "catalogs.json");
        InstrumentCatalogStore store = new(path);
        SoundFontEntryId soundFontEntryId = SoundFontEntryId.Create();
        InstrumentCatalogProfileId firstId = InstrumentCatalogProfileId.Create();
        InstrumentCatalogProfileId secondId = InstrumentCatalogProfileId.Create();
        InstrumentCatalogState state = new(
            GeneralMidiEnabled: false,
            Profiles:
            [
                new(
                    firstId,
                    "  First  ",
                    true,
                    InstrumentCatalogSourceKind.ImportedSf2,
                    soundFontEntryId,
                    [new(2, 0, "  Bank Two  ", [new(9, "  Nine  ")])]),
                new(
                    secondId,
                    "Second",
                    false,
                    InstrumentCatalogSourceKind.User,
                    null,
                    [new(1, 0, null, [new(3, "Three")])])
            ],
            Overrides: [new(4, 5, 6, " Override Bank ", " Override Program ")]);

        Assert.True(store.Save(state).Succeeded);
        byte[] firstWrite = File.ReadAllBytes(path);
        InstrumentCatalogLoadResult loaded = store.Load();
        Assert.True(store.Save(loaded.Catalog).Succeeded);
        byte[] secondWrite = File.ReadAllBytes(path);

        Assert.Null(loaded.Notice);
        Assert.True(loaded.CanPublish);
        Assert.Equal(firstWrite, secondWrite);
        Assert.False(loaded.Catalog.GeneralMidiEnabled);
        Assert.Equal([firstId, secondId], loaded.Catalog.Profiles.Select(value => value.ProfileId));
        Assert.Equal("First", loaded.Catalog.Profiles[0].DisplayName);
        Assert.Equal("Bank Two", loaded.Catalog.Profiles[0].Banks[0].DisplayName);
        Assert.Equal("Nine", loaded.Catalog.Profiles[0].Banks[0].Programs[0].DisplayName);
        Assert.Equal(soundFontEntryId, loaded.Catalog.Profiles[0].SourceSoundFontEntryId);
        Assert.Equal("Override Program", loaded.Catalog.Overrides[0].ProgramDisplayName);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"generalMidiEnabled\":true,\"profiles\":[],\"overrides\":[],\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"generalMidiEnabled\":true,\"profiles\":[],\"overrides\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"generalMidiEnabled\":true,\"profiles\":[],\"overrides\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"generalMidiEnabled\":true,\"profiles\":null,\"overrides\":[]}")]
    [InlineData("not-json")]
    public void UnknownDuplicateUnsupportedAndCorruptInputFailsClosed(string json)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "catalogs.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));

        InstrumentCatalogLoadResult result = new InstrumentCatalogStore(path).Load();

        Assert.Equal(InstrumentCatalogState.Default, result.Catalog);
        Assert.Equal("InstrumentCatalogReadFailed", result.Notice?.Code);
        Assert.NotNull(result.Notice?.Error);
        Assert.False(result.CanPublish);
    }

    [Fact]
    public void OversizedInputIsRejectedBeforeAllocation()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "catalogs.json");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(InstrumentCatalogLimits.MaximumCatalogFileBytes + 1L);
        }

        InstrumentCatalogLoadResult result = new InstrumentCatalogStore(path).Load();

        Assert.Equal(InstrumentCatalogState.Default, result.Catalog);
        Assert.Equal("InstrumentCatalogReadFailed", result.Notice?.Code);
        Assert.False(result.CanPublish);
    }

    [Fact]
    public void CorruptLoadBlocksSaveAndPreservesTheOriginalBytes()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "catalogs.json");
        byte[] corrupt = "{not valid catalog json"u8.ToArray();
        File.WriteAllBytes(path, corrupt);
        InstrumentCatalogStore store = new(path);

        InstrumentCatalogLoadResult loaded = store.Load();
        InstrumentCatalogSaveResult saved = store.Save(InstrumentCatalogState.Default);

        Assert.False(loaded.CanPublish);
        Assert.False(saved.Succeeded);
        Assert.Equal("InstrumentCatalogWriteBlockedAfterReadFailure", saved.Notice?.Code);
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void FailedAtomicReplaceLeavesPreviouslyPublishedCatalogIntact()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "catalogs.json");
        InstrumentCatalogStore store = new(path);
        InstrumentCatalogState original = new(false, [], []);
        InstrumentCatalogState replacement = new(true, [], []);
        Assert.True(store.Save(original).Succeeded);

        InstrumentCatalogSaveResult failed;
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = store.Save(replacement);
        }

        Assert.False(failed.Succeeded);
        Assert.False(store.Load().Catalog.GeneralMidiEnabled);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-catalog-store-tests-{Guid.NewGuid():N}");
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
