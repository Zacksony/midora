using System.Xml.Linq;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class AdvancedEditDialogsTests
{
    [Fact]
    public void HumanizeFieldValidationMatchesModeSemantics()
    {
        Assert.Equal(
            TimelineHumanizeField.Disabled,
            HumanizeSelectionDialog.ParseField(
                TimelineHumanizeMode.Disabled,
                "not used",
                "not used",
                "Tick"));

        TimelineHumanizeField additive = HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Add,
            "-12",
            "24",
            "Tick");
        Assert.Equal(-12, additive.IntegerMinimum);
        Assert.Equal(24, additive.IntegerMaximum);

        TimelineHumanizeField multiply = HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Multiply,
            "0.75",
            "1.25",
            "Velocity");
        Assert.Equal(0.75, multiply.FactorMinimum);
        Assert.Equal(1.25, multiply.FactorMaximum);

        Assert.Throws<ArgumentException>(() => HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Add,
            "0.5",
            "1",
            "Gate"));
        Assert.Throws<ArgumentException>(() => HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Multiply,
            "-0.1",
            "1",
            "Gate"));
        Assert.Throws<ArgumentException>(() => HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Override,
            "8",
            "7",
            "Velocity"));
    }

    [Fact]
    public void HumanizeIntegerFieldParsingPreservesTheEntireInt64DomainExactly()
    {
        TimelineHumanizeField fullRange = HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Add,
            long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Tick");
        Assert.Equal(long.MinValue, fullRange.IntegerMinimum);
        Assert.Equal(long.MaxValue, fullRange.IntegerMaximum);

        const long beyondBinary64ExactInteger = 9_007_199_254_740_993;
        TimelineHumanizeField precise = HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Override,
            beyondBinary64ExactInteger.ToString(System.Globalization.CultureInfo.InvariantCulture),
            beyondBinary64ExactInteger.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Gate");
        Assert.Equal(beyondBinary64ExactInteger, precise.IntegerMinimum);
        Assert.Equal(beyondBinary64ExactInteger, precise.IntegerMaximum);

        Assert.Throws<ArgumentException>(() => HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Add,
            "-9223372036854775809",
            "0",
            "Tick"));
        Assert.Throws<ArgumentException>(() => HumanizeSelectionDialog.ParseField(
            TimelineHumanizeMode.Override,
            "0",
            "9223372036854775808",
            "Gate"));
    }

    [Fact]
    public void SplitValidationBuildsEveryModeAndRequiresPrefixedBoundedExpression()
    {
        NoteSplitOptions fixedOptions = SplitNotesDialog.ParseOptions(
            NoteSplitMode.FixedPieceLength,
            "192",
            "4",
            "=96",
            "65535");
        Assert.Equal(192, fixedOptions.FixedPieceLengthTicks);
        Assert.Null(fixedOptions.ExpressionProgram);

        NoteSplitOptions countOptions = SplitNotesDialog.ParseOptions(
            NoteSplitMode.MaximumPieceCount,
            "192",
            "7",
            "=96",
            "65535");
        Assert.Equal(7, countOptions.MaximumPieceCount);

        NoteSplitOptions expressionOptions = SplitNotesDialog.ParseOptions(
            NoteSplitMode.Expression,
            "192",
            "4",
            "=Max(1, 96 - i + tr * 0)",
            "2048");
        using (expressionOptions.ExpressionProgram)
        {
            Assert.NotNull(expressionOptions.ExpressionProgram);
            Assert.Equal(96, expressionOptions.ExpressionProgram.Evaluate(0, 0));
            Assert.Equal(95, expressionOptions.ExpressionProgram.Evaluate(1, 96));
        }

        Assert.Throws<ArgumentException>(() => SplitNotesDialog.ParseOptions(
            NoteSplitMode.Expression,
            "192",
            "4",
            "96",
            "65535"));
        Assert.Throws<ArgumentException>(() => SplitNotesDialog.ParseOptions(
            NoteSplitMode.Expression,
            "192",
            "4",
            "=96",
            "16777217"));

        NoteSplitOptions fixedIgnoresHiddenInvalidFields = SplitNotesDialog.ParseOptions(
            NoteSplitMode.FixedPieceLength,
            "24",
            "invalid hidden value",
            "invalid hidden expression",
            "invalid hidden value");
        Assert.Equal(24, fixedIgnoresHiddenInvalidFields.FixedPieceLengthTicks);
        Assert.Equal(2, fixedIgnoresHiddenInvalidFields.MaximumPieceCount);
        Assert.Equal(65_535, fixedIgnoresHiddenInvalidFields.MaximumCuts);

        NoteSplitOptions countIgnoresHiddenInvalidFields = SplitNotesDialog.ParseOptions(
            NoteSplitMode.MaximumPieceCount,
            "invalid hidden value",
            "9",
            "invalid hidden expression",
            "invalid hidden value");
        Assert.Equal(1, countIgnoresHiddenInvalidFields.FixedPieceLengthTicks);
        Assert.Equal(9, countIgnoresHiddenInvalidFields.MaximumPieceCount);
    }

    [Fact]
    public void JoinAndQuantizeValidationEnforceFormalBounds()
    {
        Assert.Equal(0, JoinNotesDialog.ParseOptions("0").MaximumGapTicks);
        Assert.Equal(long.MaxValue, JoinNotesDialog.ParseOptions(long.MaxValue.ToString()).MaximumGapTicks);
        Assert.Throws<ArgumentException>(() => JoinNotesDialog.ParseOptions("-1"));

        Assert.DoesNotContain(QuantizeSelectionDialog.GridChoices, static value =>
            string.Equals(value.Label, "Bar", StringComparison.OrdinalIgnoreCase));
        QuantizeGridChoice sixteenth = Assert.Single(
            QuantizeSelectionDialog.GridChoices,
            static value => value.Numerator == 1 && value.Denominator == 16);
        TimelineQuantizeGrid musical = QuantizeSelectionDialog.ParseGrid(sixteenth, null);
        Assert.Equal(TimelineQuantizeGridKind.MusicalFraction, musical.Kind);
        Assert.Equal(1, musical.Numerator);
        Assert.Equal(16, musical.Denominator);

        QuantizeGridChoice custom = Assert.Single(
            QuantizeSelectionDialog.GridChoices,
            static value => value.IsCustom);
        TimelineQuantizeGrid ticks = QuantizeSelectionDialog.ParseGrid(custom, "37");
        Assert.Equal(TimelineQuantizeGridKind.CustomTicks, ticks.Kind);
        Assert.Equal(37, ticks.CustomTicks);
        Assert.Throws<ArgumentException>(() => QuantizeSelectionDialog.ParseGrid(custom, "0"));

        TimelineSubdivision uncommonCurrentGrid = new(1, 7, "1/7");
        IReadOnlyList<QuantizeGridChoice> choices =
            QuantizeSelectionDialog.CreateGridChoices(uncommonCurrentGrid);
        QuantizeGridChoice uncommon = QuantizeSelectionDialog.ResolveInitialGridChoice(
            choices,
            uncommonCurrentGrid);
        Assert.Equal("1/7", uncommon.Label);
        Assert.Equal(1, uncommon.Numerator);
        Assert.Equal(7, uncommon.Denominator);
        Assert.False(uncommon.IsCustom);

        QuantizeGridChoice barFallback = QuantizeSelectionDialog.ResolveInitialGridChoice(
            QuantizeSelectionDialog.CreateGridChoices(TimelineSubdivision.Presets[0]),
            TimelineSubdivision.Presets[0]);
        Assert.Equal(1, barFallback.Numerator);
        Assert.Equal(16, barFallback.Denominator);
    }

    [Fact]
    public void AdvancedEditDialogsUseSharedChromeAndExplicitConfirmCancelActions()
    {
        string[] names =
        [
            "HumanizeSelectionDialog.xaml",
            "SplitNotesDialog.xaml",
            "JoinNotesDialog.xaml",
            "QuantizeSelectionDialog.xaml"
        ];
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (string name in names)
        {
            XDocument document = XDocument.Load(Path.Combine(FindRepositoryRoot(),
                "src", "midora-desktop", "Midora.Desktop", name));
            Assert.Contains(document.Descendants(), element =>
                element.Name.LocalName == "WindowChrome");
            Assert.Contains(document.Descendants(presentation + "Button"), element =>
                string.Equals((string?)element.Attribute("IsDefault"), "True", StringComparison.Ordinal));
            Assert.Contains(document.Descendants(presentation + "Button"), element =>
                string.Equals((string?)element.Attribute("IsCancel"), "True", StringComparison.Ordinal));
            Assert.DoesNotContain(document.DescendantNodes().OfType<XText>(), text =>
                text.Value.Any(static value => value is >= '\u4e00' and <= '\u9fff'));
        }
    }

    [Fact]
    public void SubVoiceNoteCommandsCoverBothPianoRollAndVelocitySurfaces()
    {
        Assert.True(MainWindow.IsSubVoiceNoteOperationSurface(
            TimelineSurfaceMode.PianoRoll,
            "SubVoiceNotes"));
        Assert.True(MainWindow.IsSubVoiceNoteOperationSurface(
            TimelineSurfaceMode.Velocity,
            "SubVoiceVelocity"));
        Assert.False(MainWindow.IsSubVoiceNoteOperationSurface(
            TimelineSurfaceMode.EventLanes,
            "SubVoiceEvents"));
        Assert.False(MainWindow.IsSubVoiceNoteOperationSurface(
            TimelineSurfaceMode.Velocity,
            "ParameterLanes"));

        XDocument mainWindow = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src", "midora-desktop", "Midora.Desktop", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string name in new[] { "SegmentVelocityTimeline", "SubVoiceVelocityTimeline" })
        {
            XElement surface = Assert.Single(mainWindow.Descendants(), element =>
                string.Equals((string?)element.Attribute(x + "Name"), name, StringComparison.Ordinal));
            Assert.Equal("{StaticResource TimelineContextMenu}",
                (string?)surface.Attribute("ContextMenu"));
        }
    }

    [Fact]
    public void SplitExpressionEscapeAndNestedDialogsKeepExplicitKeyboardActions()
    {
        string root = FindRepositoryRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XDocument split = XDocument.Load(Path.Combine(root,
            "src", "midora-desktop", "Midora.Desktop", "SplitNotesDialog.xaml"));
        Assert.Equal("OnDialogPreviewKeyDown", (string?)split.Root?.Attribute("PreviewKeyDown"));

        foreach (string name in new[]
                 {
                     "NoteSplitHelpDialog.xaml",
                     "NoteSplitPresetDialog.xaml",
                     "TextInputDialog.xaml"
                 })
        {
            XDocument document = XDocument.Load(Path.Combine(root,
                "src", "midora-desktop", "Midora.Desktop", name));
            Assert.Contains(document.Descendants(presentation + "Button"), element =>
                string.Equals((string?)element.Attribute("IsDefault"), "True", StringComparison.Ordinal));
            Assert.Contains(document.Descendants(presentation + "Button"), element =>
                string.Equals((string?)element.Attribute("IsCancel"), "True", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ForegroundTaskCancellationIsSealedBeforePublication()
    {
        using DesktopTaskViewModel cancelled = new(
            "Cancelled preparation",
            canCancel: true,
            DesktopTaskLockLevel.MainWindow);
        cancelled.RequestCancel();
        Assert.Throws<OperationCanceledException>(cancelled.SealCancellationBeforePublication);
        Assert.False(cancelled.CanRequestCancel);

        using DesktopTaskViewModel publishing = new(
            "Publishing",
            canCancel: true,
            DesktopTaskLockLevel.MainWindow);
        publishing.SealCancellationBeforePublication();
        publishing.RequestCancel();
        Assert.False(publishing.CancellationToken.IsCancellationRequested);
        Assert.False(publishing.CanRequestCancel);
    }

    [Fact]
    public void SplitHelpDocumentsZeroBasedKnifeStateAndSafetyStop()
    {
        string text = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "midora-desktop", "Midora.Desktop", "NoteSplitHelpDialog.xaml"));
        Assert.Contains("Zero-based knife index", text, StringComparison.Ordinal);
        Assert.Contains("Previous knife position", text, StringComparison.Ordinal);
        Assert.Contains("Maximum Cuts", text, StringComparison.Ordinal);
        Assert.Contains("must start with =", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "midora-desktop")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Midora repository.");
    }
}
