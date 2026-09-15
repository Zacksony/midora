using System.Xml.Linq;
using Midora.Application;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class InstrumentCatalogDialogTests
{
    [Fact]
    public void DraftEditsAreDetachedUntilBuildAndPreserveStableSourceIdentity()
    {
        SoundFontEntryId soundFontId = new(new Guid("10000000-0000-0000-0000-000000000001"));
        InstrumentCatalogProfileId profileId = new(new Guid("20000000-0000-0000-0000-000000000001"));
        InstrumentCatalogState original = new(
            true,
            [new InstrumentCatalogProfile(
                profileId,
                "Original",
                true,
                InstrumentCatalogSourceKind.ImportedSf2,
                soundFontId,
                [new InstrumentCatalogBank(
                    1,
                    2,
                    "Bank One",
                    [new InstrumentCatalogProgram(3, "Program Three")])])],
            [new InstrumentCatalogOverride(4, 5, 6, "Override Bank", "Override Program")]);
        ApplicationSoundFontPreference soundFont = new(
            soundFontId,
            Path.Combine(Path.GetTempPath(), "catalog-source.sf2"),
            true);

        InstrumentCatalogDialogDraft draft = new(original, [soundFont]);
        InstrumentCatalogProfileDraft profile = Assert.Single(draft.Profiles);
        profile.DisplayName = "Edited";
        profile.Enabled = false;
        InstrumentCatalogBankDraft bank = Assert.Single(profile.Banks);
        bank.Update(7, 8, "Edited Bank");
        Assert.Single(bank.Programs).Update(9, "Edited Program");
        draft.GeneralMidiEnabled = false;
        draft.Overrides.Clear();

        Assert.True(original.GeneralMidiEnabled);
        Assert.Equal("Original", Assert.Single(original.Profiles).DisplayName);
        Assert.True(Assert.Single(original.Profiles).Enabled);
        Assert.Equal((byte)1, Assert.Single(Assert.Single(original.Profiles).Banks).BankMsb);
        Assert.Single(original.Overrides);

        InstrumentCatalogState result = draft.Build();
        InstrumentCatalogProfile resultProfile = Assert.Single(result.Profiles);
        InstrumentCatalogBank resultBank = Assert.Single(resultProfile.Banks);
        InstrumentCatalogProgram resultProgram = Assert.Single(resultBank.Programs);
        Assert.False(result.GeneralMidiEnabled);
        Assert.Equal(profileId, resultProfile.ProfileId);
        Assert.Equal(soundFontId, resultProfile.SourceSoundFontEntryId);
        Assert.Equal(InstrumentCatalogSourceKind.ImportedSf2, resultProfile.SourceKind);
        Assert.Equal("Edited", resultProfile.DisplayName);
        Assert.False(resultProfile.Enabled);
        Assert.Equal((byte)7, resultBank.BankMsb);
        Assert.Equal((byte)8, resultBank.BankLsb);
        Assert.Equal("Edited Bank", resultBank.DisplayName);
        Assert.Equal((byte)9, resultProgram.Program);
        Assert.Equal("Edited Program", resultProgram.DisplayName);
        Assert.Empty(result.Overrides);
    }

    [Fact]
    public void Sf2BankProjectionRequiresExplicitMappingOnlyAboveTheMidiByteRange()
    {
        Sf2BankProjectionDraft standard = new(127, 3);
        Sf2BankProjectionDraft extended = new(128, 4);

        Assert.Equal("127", standard.TargetMsbText);
        Assert.Equal("0", standard.TargetLsbText);
        Assert.Equal("", extended.TargetMsbText);
        Assert.Equal("", extended.TargetLsbText);
    }

    [Fact]
    public void PendingEditorDetectionUsesTheAcceptedItemInsteadOfTheRequestedSelection()
    {
        InstrumentCatalogBankDraft bank = new(1, 2, "Bank", []);
        InstrumentCatalogProgramDraft program = new(3, "Program");
        InstrumentCatalogOverrideDraft catalogOverride = new(
            new InstrumentCatalogOverride(4, 5, 6, "Override Bank", "Override Program"));

        Assert.False(InstrumentCatalogDialog.HasPendingBankEdits(false, bank, "1", "2", "Bank"));
        Assert.True(InstrumentCatalogDialog.HasPendingBankEdits(false, bank, "9", "2", "Bank"));
        Assert.True(InstrumentCatalogDialog.HasPendingBankEdits(true, null, "0", "0", "Draft Bank"));

        Assert.False(InstrumentCatalogDialog.HasPendingProgramEdits(false, program, "3", "Program"));
        Assert.True(InstrumentCatalogDialog.HasPendingProgramEdits(false, program, "3", "Edited"));
        Assert.True(InstrumentCatalogDialog.HasPendingProgramEdits(true, null, "1", ""));

        Assert.False(InstrumentCatalogDialog.HasPendingOverrideEdits(
            false, catalogOverride, "4", "5", "6", "Override Bank", "Override Program"));
        Assert.True(InstrumentCatalogDialog.HasPendingOverrideEdits(
            false, catalogOverride, "4", "5", "6", "Override Bank", "Edited"));
        Assert.True(InstrumentCatalogDialog.HasPendingOverrideEdits(
            true, null, "0", "0", "0", "", "Draft Program"));
    }

    [Fact]
    public void DialogContainsTheCompleteDetachedCatalogWorkflow()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "InstrumentCatalogDialog.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> names = document
            .Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);

        AssertSuperset(names,
            "CatalogTitleBar",
            "CatalogCloseButton",
            "GeneralMidiEnabledBox",
            "ProfileList",
            "BankList",
            "ProgramList",
            "OverrideList",
            "ScanOverlay",
            "ScanBankList",
            "ImportOverlay",
            "ImportTargetBox",
            "ImportConflictList");

        XElement catalogTabs = document.Descendants(presentation + "TabControl")
            .Single(element => (string?)element.Attribute(x + "Name") == "CatalogTabs");
        Assert.Equal("OnCatalogTabSelectionChanged", (string?)catalogTabs.Attribute("SelectionChanged"));

        foreach (string overlayName in new[] { "ScanOverlay", "ImportOverlay" })
        {
            XElement overlay = document.Descendants(presentation + "Grid")
                .Single(element => (string?)element.Attribute(x + "Name") == overlayName);
            Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));
            Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.ControlTabNavigation"));
        }

        XElement[] buttons = document.Descendants(presentation + "Button").ToArray();
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "Import…");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "Export…");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "Scan SF2…");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "Rescan");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "Replace Profile");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "Apply Merge");
        Assert.Contains(buttons, button =>
            (string?)button.Attribute("Content") == "OK"
            && string.Equals((string?)button.Attribute("IsDefault"), "True", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(buttons, button =>
            (string?)button.Attribute("Content") == "Cancel"
            && string.Equals((string?)button.Attribute("IsCancel"), "True", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSuperset(HashSet<string> actual, params string[] expected)
    {
        foreach (string value in expected)
        {
            Assert.Contains(value, actual);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "midora-desktop",
                    "Midora.Desktop")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The Midora repository root was not found.");
    }
}
