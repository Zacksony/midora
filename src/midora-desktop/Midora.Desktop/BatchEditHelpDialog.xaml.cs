using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class BatchEditHelpDialog : Window
{
    internal static IReadOnlyList<BatchEditHelpExample> ExampleDefinitions { get; } =
    [
        new(
            "1",
            "BASIC",
            "Set a consistent velocity",
            "Velocity",
            "96",
            "Every selected note receives velocity 96. This is the simplest way to normalize a passage."),
        new(
            "2",
            "BASIC",
            "Lengthen every gate by 25%",
            "Gate",
            "*1.25",
            "Each note keeps its relative gate length while becoming one quarter longer."),
        new(
            "3",
            "EXPRESSION",
            "Increase velocity without exceeding MIDI limits",
            "Velocity",
            "=Clamp(v0 * 1.15, 1, 127)",
            "Uses the original velocity and clamps the result to the legal 1–127 range."),
        new(
            "4",
            "SYSTEM.MATH",
            "Quantize starts to a 24-tick grid",
            "Tick",
            "=Round(t0 / 24.0) * 24.0",
            "Rounds every original start tick to the nearest multiple of 24. Change 24.0 to the grid size you need."),
        new(
            "5",
            "ADVANCED",
            "Create a velocity crescendo across the selection",
            "Velocity",
            "=Clamp(32 + tr / 8.0, 1, 127)",
            "tr starts at 0 for the earliest selected object, so later notes become progressively louder while remaining in range."),
        new(
            "6",
            "ADVANCED · EVENT POINTS",
            "Turn dense event points into a phase-shifted sine wave",
            "Point Value",
            "=64 * Sin((2 * PI * tr / 384.0) - (PI / 2.0))",
            "For evenly spaced points, this uses a 384-tick period and a negative quarter-cycle phase offset: tr=0 starts at -64, then passes through 0 and reaches 64. Change 384.0 to set the period; use a lane whose range includes -64 through 64.")
    ];

    public BatchEditHelpDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public IReadOnlyList<BatchEditHelpSyntaxRow> InputModes { get; } =
    [
        new("blank", "Keep the original value", "Leave Velocity empty to preserve every selected note's velocity."),
        new("96", "Set one exact value", "Set the selected field to 96."),
        new("n%  *n  /n  +n  -n", "Apply one operation", "Scale, multiply, divide, add, or subtract from each original value."),
        new("= expression", "Evaluate a bounded expression", "Use variables, conditions, and System.Math.")
    ];

    public IReadOnlyList<BatchEditHelpShortcut> Shortcuts { get; } =
    [
        new("Ctrl+Space", "Open completion"),
        new("Up / Down", "Select a completion item"),
        new("Tab / Enter", "Insert the selected item"),
        new("Escape", "Close completion"),
        new("F1", "Show the next overload signature")
    ];

    public IReadOnlyList<string> MathMembers { get; } =
    [
        "Clamp", "Min / Max", "Round", "Floor / Ceiling", "Abs", "Pow / Sqrt", "Sin / Cos", "PI / E"
    ];

    public IReadOnlyList<string> ResultRules { get; } =
    [
        "Integral targets are rounded away from zero. Velocity and Point Value are clamped to their editing ranges.",
        "CC10 and CC71–78 use -64..63 in editors, Batch Edit and Batch Create (display = MIDI value - 64). Deltas are not offset; factors operate on the displayed value.",
        "Example: CC10 shown as 32 uses =p0*0.5 (or *0.5) to become 16. Project Mapping is different: value*0.5 operates on MIDI 96, produces MIDI 48, and is then shown as -16. Mapping functions, built-in steps and accumulators remain in the data domain.",
        "Gate is clamped to at least 1 tick. A note whose calculated Key Number is outside 0–127 is removed.",
        "A Segment object calculated before the exposed left edge expands the Segment left while preserving hidden objects. Crossing Project tick 0 removes that calculated object.",
        "A SubVoice object calculated below tick 0 is removed. Evaluation of the complete batch is limited to 10 seconds."
    ];

    public IReadOnlyList<BatchEditHelpExample> Examples => ExampleDefinitions;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}

public sealed record BatchEditHelpSyntaxRow(string Input, string Meaning, string Example);

public sealed record BatchEditHelpShortcut(string Keys, string Action);

public sealed record BatchEditHelpExample(
    string Number,
    string Level,
    string Title,
    string Field,
    string Code,
    string Explanation);
