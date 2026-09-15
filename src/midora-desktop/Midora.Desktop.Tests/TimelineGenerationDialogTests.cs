using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Midora.Application;
using Midora.Compiler;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class TimelineGenerationDialogTests
{
    [Fact]
    public void NumericDraftParsingUsesFiniteInvariantValuesAndExactInt64Bounds()
    {
        Assert.Equal(49, TimelineGenerationDialog.ParseBaseTick("49"));
        Assert.Equal(long.MaxValue - 1, TimelineGenerationDialog.ParseBaseTick("9223372036854775806"));
        Assert.Equal(65_535, TimelineGenerationDialog.ParseMaximumCandidates("65535"));
        Assert.Equal(16_777_216, TimelineGenerationDialog.ParseMaximumCandidates("16777216"));
        Assert.Null(TimelineGenerationDialog.ParseMaximumRelativeStart("unused", false));
        Assert.Equal(long.MaxValue, TimelineGenerationDialog.ParseMaximumRelativeStart("9223372036854775807", true));
        Assert.Equal(-64, TimelineGenerationDialog.ParseInitial("", -64, "Value"));
        Assert.Equal(1.25, TimelineGenerationDialog.ParseInitial("1.25", 0, "Value"));
        Assert.Equal(-4, TimelineGenerationDialog.ParseInitial("-4", 0, "Tick"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseBaseTick("-1"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseBaseTick("9223372036854775807"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseMaximumCandidates("0"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseMaximumCandidates("16777217"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseMaximumRelativeStart("-1", true));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseInitial("NaN", 0, "Value"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseInitial("Infinity", 0, "Value"));
        Assert.Throws<ArgumentException>(() => TimelineGenerationDialog.ParseInitial("1,25", 0, "Value"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletionVariablesFollowTheGeneratorProfileNotBatchOrSplit(bool notes)
    {
        NumericExpressionProfile profile = notes ? NumericExpressionProfiles.GenerateNote : NumericExpressionProfiles.GenerateEvent;
        var variables = BatchExpressionCompletionProvider.CreateVariables(profile, "t1");
        Assert.Contains(variables, item => item.Name == "i" && item.Description.Contains("candidate", StringComparison.Ordinal));
        Assert.Contains(variables, item => item.Name == "tr" && item.Description.Contains("Input t0", StringComparison.Ordinal));
        Assert.DoesNotContain(variables, item => item.Name == "t1");
        Assert.Contains(variables, item => item.Name == "t0");
        Assert.DoesNotContain(variables, item => item.Description.Contains("cut", StringComparison.Ordinal));
        if (notes)
        {
            Assert.Contains(variables, item => item.Name == "v1");
            Assert.DoesNotContain(variables, item => item.Name == "p0");
        }
        else
        {
            Assert.Contains(variables, item => item.Name == "p1");
            Assert.DoesNotContain(variables, item => item.Name == "v0");
        }
        var completion = BatchExpressionCompletionProvider.GetCompletions("=Si", 3, variables);
        Assert.Contains(completion, item => item.Text == "Sin");
        Assert.Contains(BatchExpressionCompletionProvider.CreateVariables(NumericExpressionProfiles.NoteSplit),
            item => item.Name == "tr" && item.Description.Contains("cut", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryHelpExampleUsesItsActualProfileAndEvaluatesFiniteResults(bool notes)
    {
        IReadOnlyList<GenerationHelpExample> examples = TimelineGenerationHelpDialog.CreateExamples(notes);
        Assert.True(examples.Count >= 5);
        foreach (var example in examples)
        {
            var fields = example.Fields.ToDictionary(x => x.Label, x => x.Text);
            string? Read(string key) => fields.GetValueOrDefault(key);
            string[] initial = fields[notes ? "Initial V / K / G / T" : "Initial Value / Tick"].Split('/');
            double[] values = initial.Select(text => double.Parse(text, CultureInfo.InvariantCulture)).ToArray();
            using GeneratorExpressionProgram program = notes
                ? new NoteGenerationOptions
                {
                    InitialVelocity = values[0], InitialKey = values[1], InitialGate = values[2], InitialTick = values[3],
                    VelocityExpression = Read("Velocity"), KeyExpression = Read("Key"), GateExpression = Read("Gate"), TickExpression = Read("Tick")
                }.Compile()
                : new EventGenerationOptions
                {
                    InitialValue = values[0], InitialTick = values[1], ValueExpression = Read("Value"), TickExpression = Read("Tick")
                }.Compile();
            BatchEditValues previous = notes
                ? new(values[0], 0, values[1], values[2], values[3], values[3])
                : new(0, values[0], 0, 0, values[1], values[1]);
            int count = int.Parse(fields["Maximum Candidates"], CultureInfo.InvariantCulture);
            int first = Read("Create initial object") == "ON" ? 1 : 0;
            for (int i = first; i < count; i++)
            {
                BatchEditValues computed = program.Evaluate(previous, i);
                Assert.True(double.IsFinite(computed.Tick));
                Assert.True(double.IsFinite(notes ? computed.Velocity : computed.PointValue));
                double tick = Math.Max(0, Math.Round(computed.Tick, MidpointRounding.AwayFromZero));
                previous = notes
                    ? new(Math.Clamp(Math.Round(computed.Velocity), 1, 127), 0,
                        Math.Clamp(Math.Round(computed.KeyNumber), 0, 127), Math.Max(1, Math.Round(computed.Gate)), tick, tick)
                    : new(0, computed.PointValue, 0, 0, tick, tick);
            }
        }
    }

    [Theory]
    [InlineData("TimelineGenerationDialog.xaml")]
    [InlineData("TimelineGenerationPresetDialog.xaml")]
    [InlineData("TimelineGenerationHelpDialog.xaml")]
    public void DialogMarkupUsesSharedActionsAndOneExplicitEscapeTarget(string file)
    {
        XDocument document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "midora-desktop", "Midora.Desktop", file));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.Equal("OnDialogPreviewKeyDown", (string?)document.Root!.Attribute("PreviewKeyDown"));
        var buttons = document.Descendants(p + "Button").ToArray();
        Assert.Single(buttons, button => (string?)button.Attribute("IsCancel") == "True");
        Assert.Single(buttons, button => (string?)button.Attribute("Style") == "{StaticResource Button.Dialog.Confirm}");
        Assert.Single(buttons, button => (string?)button.Attribute("IsDefault") == "True");
        Assert.All(document.Descendants(p + "ScrollViewer"), scroll =>
            Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility")));
    }

    // Invoked on the existing shared-theme Application STA: WPF permits only one
    // Application per process, so a second independent test host would be invalid.
    internal static void VerifyThemeConstructionDraftAndValidation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Midora.GenerationDialogTests", Guid.NewGuid().ToString("N"));
        List<Window> windows = [];
        try
        {
            foreach (bool notes in new[] { true, false })
            {
                TimelineGenerationPresetStore store = new(notes, directory);
                TimelineGenerationDialog dialog = new(notes, 384, -64, 64, store);
                windows.Add(dialog);
                windows.Add(new TimelineGenerationHelpDialog(notes));
                windows.Add(new TimelineGenerationPresetDialog(store));
                Assert.Null(dialog.NoteOptions);
                Assert.Null(dialog.EventOptions);
                Assert.False(Get<CheckBox>(dialog, "CreateInitialBox").IsChecked);
                Assert.False(Get<TextBox>(dialog, "MaximumRelativeTickBox").IsEnabled);
                Assert.Equal("65535", Get<TextBox>(dialog, "MaximumCandidatesBox").Text);
                if (notes)
                {
                    NoteGenerationOptions initial = dialog.CaptureNoteOptions();
                    Assert.Equal(new NoteGenerationOptions { BaseTick = 384, VelocityExpression = "", KeyExpression = "", GateExpression = "", TickExpression = "" }, initial);
                    Assert.Equal(Visibility.Collapsed, Get<Grid>(dialog, "PointValueRow").Visibility);
                }
                else
                {
                    EventGenerationOptions initial = dialog.CaptureEventOptions();
                    Assert.Equal(-64, initial.InitialValue);
                    Assert.Equal(384, initial.BaseTick);
                    Assert.Equal(Visibility.Collapsed, Get<Grid>(dialog, "VelocityRow").Visibility);
                    Get<TextBox>(dialog, "InitialValueBox").Text = string.Empty;
                    Assert.Equal(-64, dialog.CaptureEventOptions().InitialValue);
                }
                TextBlock status = Get<TextBlock>(dialog, "ValidationText");
                Button validate = Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Validate"));
                validate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(dialog.FindResource("Brush.Success"), status.Foreground);
                BatchExpressionEditor tickEditor = Get<BatchExpressionEditor>(dialog, "TickBox");
                Assert.IsType<TextBox>(tickEditor.FindName("PlainTextBox")).Text = "123";
                Assert.Same(dialog.FindResource("Brush.Text.Tertiary"), status.Foreground);
                validate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(dialog.FindResource("Brush.Red.Hover"), status.Foreground);
                Assert.Null(dialog.NoteOptions);
                Assert.Null(dialog.EventOptions);
                TimelineGenerationPreset preset = notes
                    ? TimelineGenerationPreset.FromNotes("Example", new() { BaseTick = 9876, TickExpression = "=i * 24", CreateFirstFromInitialValues = true, MaximumCandidates = 16, MaximumRelativeStartTick = 240 })
                    : TimelineGenerationPreset.FromEvents("Example", new() { BaseTick = 9876, TickExpression = "=i * 24", CreateFirstFromInitialValues = true, MaximumCandidates = 16, MaximumRelativeStartTick = 240 });
                dialog.ApplyPreset(preset);
                Assert.Equal("384", Get<TextBox>(dialog, "BaseTickBox").Text);
                Assert.True(Get<CheckBox>(dialog, "CreateInitialBox").IsChecked);
                Assert.True(Get<TextBox>(dialog, "MaximumRelativeTickBox").IsEnabled);
                Assert.Equal("240", Get<TextBox>(dialog, "MaximumRelativeTickBox").Text);
                Assert.False(Directory.Exists(directory)); // Opening and editing drafts never writes a preset.
                Get<CheckBox>(dialog, "LimitRelativeTickBox").IsChecked = false;
                Get<TextBox>(dialog, "MaximumRelativeTickBox").Text = "invalid unused value";
                Assert.Null(notes ? dialog.CaptureNoteOptions().MaximumRelativeStartTick : dialog.CaptureEventOptions().MaximumRelativeStartTick);
            }
        }
        finally
        {
            foreach (Window window in windows) window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static T Get<T>(Window dialog, string name) => Assert.IsType<T>(dialog.FindName(name));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object item in LogicalTreeHelper.GetChildren(parent))
        {
            if (item is not DependencyObject child) continue;
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
