using Midora.Application;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class InstrumentCatalogPropertiesProjectionTests
{
    [Fact]
    public async Task EventInstrumentPropertiesResolveCatalogNameAndTrackDraftValues()
    {
        await using DesktopSessionController session = await CreateInstrumentSessionAsync();
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        InstrumentCatalogResolver resolver = new(
            new InstrumentCatalogState(
                GeneralMidiEnabled: true,
                Profiles: [],
                Overrides:
                [
                    new InstrumentCatalogOverride(
                        BankMsb: 0,
                        BankLsb: 0,
                        Program: 0,
                        BankDisplayName: null,
                        ProgramDisplayName: "Studio Piano")
                ]),
            Array.Empty<InstrumentCatalogSoundFontEntry>());
        session.SetInstrumentCatalogResolver(resolver);

        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Clear();
        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);
        PropertyField bankMsb = Field(properties, "instrument.initial.bankMsb");
        PropertyField bankLsb = Field(properties, "instrument.initial.bankLsb");
        PropertyField program = Field(properties, "instrument.initial.program");
        PropertyField catalogName = Field(
            properties,
            "instrument.initial.catalogProgramName");

        Assert.True(bankMsb.IsEditable);
        Assert.True(bankLsb.IsEditable);
        Assert.True(program.IsEditable);
        Assert.False(catalogName.IsEditable);
        Assert.Equal("CATALOG NAME (AUXILIARY)", catalogName.Label);
        Assert.Equal("Studio Piano (User Override)", catalogName.Value);

        program.Value = "40";
        Assert.Equal("Violin (General MIDI)", catalogName.Value);

        bankLsb.Value = string.Empty;
        Assert.Equal(
            "Complete Bank MSB, Bank LSB, and Program to resolve a Catalog name",
            catalogName.Value);

        bankLsb.Value = "128";
        Assert.Equal(
            "Enter Bank MSB, Bank LSB, and Program values from 0 to 127",
            catalogName.Value);

        bankLsb.Value = "0";
        program.Value = "0";
        Assert.Equal("Studio Piano (User Override)", catalogName.Value);
    }

    [Fact]
    public async Task ReplacingResolverAffectsNewPropertiesButNotOpenDraftSnapshot()
    {
        await using DesktopSessionController session = await CreateInstrumentSessionAsync();
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Clear();
        session.SetInstrumentCatalogResolver(CreateOverrideResolver("First Name"));
        ObjectPropertiesViewModel openDraft = session.CreateObjectProperties(workspace);

        session.SetInstrumentCatalogResolver(CreateOverrideResolver("Second Name"));
        ObjectPropertiesViewModel nextDraft = session.CreateObjectProperties(workspace);

        Assert.Equal(
            "First Name (User Override)",
            Field(openDraft, "instrument.initial.catalogProgramName").Value);
        Assert.Equal(
            "Second Name (User Override)",
            Field(nextDraft, "instrument.initial.catalogProgramName").Value);
    }

    [Fact]
    public async Task SubVoiceTimelineProgramPropertiesKeepNumericPresentationOnly()
    {
        await using DesktopSessionController session = await CreateInstrumentSessionAsync();
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Voice"));
        SubVoice voice = Assert.Single(instrument.SubVoices, item => item.Name == "Voice");
        session.Execute(ProjectDomainEditCommands.CreateTemplateProgram(
            instrument.Id,
            voice.Id,
            tick: 24,
            program: 0));
        TemplateEvent programEvent = Assert.Single(voice.Events);
        session.SetInstrumentCatalogResolver(CreateOverrideResolver("Hidden Name"));

        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Replace(programEvent.Id);
        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);

        Assert.DoesNotContain(properties.Fields, field =>
            field.Key.Contains("catalog", StringComparison.OrdinalIgnoreCase));
        PropertyField value = Field(properties, "template.value");
        Assert.Equal("PROGRAM (0–127)", value.Label);
        Assert.Equal("0", value.Value);
    }

    private static async Task<DesktopSessionController> CreateInstrumentSessionAsync()
    {
        DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Catalog Properties",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
            instrument.Id,
            MidiValueTarget.BankMsb,
            0));
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
            instrument.Id,
            MidiValueTarget.BankLsb,
            0));
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
            instrument.Id,
            MidiValueTarget.Program,
            0));
        return session;
    }

    private static InstrumentCatalogResolver CreateOverrideResolver(string name) => new(
        new InstrumentCatalogState(
            GeneralMidiEnabled: true,
            Profiles: [],
            Overrides:
            [
                new InstrumentCatalogOverride(
                    BankMsb: 0,
                    BankLsb: 0,
                    Program: 0,
                    BankDisplayName: null,
                    ProgramDisplayName: name)
            ]),
        Array.Empty<InstrumentCatalogSoundFontEntry>());

    private static PropertyField Field(
        ObjectPropertiesViewModel properties,
        string key) => properties.Fields.Single(field => field.Key == key);
}
