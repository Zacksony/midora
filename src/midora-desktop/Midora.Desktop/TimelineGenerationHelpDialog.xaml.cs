using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

internal sealed record GenerationHelpField(string Label, string Text);
internal sealed record GenerationHelpExample(string Title, string Description, IReadOnlyList<GenerationHelpField> Fields);

public partial class TimelineGenerationHelpDialog : Window
{
    public TimelineGenerationHelpDialog(bool notes)
    {
        InitializeComponent();
        Title = TitleText.Text = notes ? "Batch Create Notes — Help" : "Batch Create Events — Help";
        DataContext = new
        {
            Variables = CreateVariables(notes),
            Examples = CreateExamples(notes)
        };
    }

    internal static IReadOnlyList<GenerationHelpField> CreateVariables(bool notes) =>
    [
        new("i", "Zero-based candidate index: 0, 1, 2… Includes the Initial object when enabled; collisions do not renumber candidates."),
        new(notes ? "v0, k0, g0, t0" : "p0, t0", notes
            ? "Previous normalized Velocity, Key, Gate and relative Tick. The first iteration reads normalized Initial values."
            : "Previous normalized Value and relative Tick. The first iteration reads normalized Initial values."),
        new(notes ? "v1, k1, g1, t1" : "p1, t1", "Current iteration's computed fields before final normalization. You may reference another field's result if the dependency graph has no cycle."),
        new("tr", "Exactly the input t0, not t1. If Initial Tick is 48, the first expression receives tr=48 (after normalization), not 0."),
        new("All variables", "Numeric double values. Tick results are relative to Base Tick; actual start = Base Tick + normalized relative Tick.")
    ];

    internal static IReadOnlyList<GenerationHelpExample> CreateExamples(bool notes) => notes
        ?
        [
            new("A. Sixteen evenly spaced notes", "A short repeated-note pattern beginning exactly at the Base Tick.",
                [new("Maximum Candidates", "16"), new("Initial V / K / G / T", "80 / 60 / 48 / 0"), new("Tick", "=i * 96")]),
            new("B. A chromatic staircase", "Rise by one semitone per candidate and repeat after two octaves.",
                [new("Maximum Candidates", "48"), new("Initial V / K / G / T", "80 / 48 / 36 / 0"), new("Key", "=48 + i % 24"), new("Tick", "=i * 48")]),
            new("C. A gradual velocity ramp", "Use the previous normalized Velocity, starting with the Initial object at i=0.",
                [new("Maximum Candidates", "32"), new("Initial V / K / G / T", "32 / 60 / 48 / 0"), new("Create initial object", "ON"), new("Velocity", "=v0 + 3"), new("Tick", "=t0 + 96")]),
            new("D. A chord grid without duplicate notes", "Each four-candidate group creates four different pitches at one Tick. i is the candidate index, not the number of unique Tick positions.",
                [new("Maximum Candidates", "64"), new("Initial V / K / G / T", "80 / 48 / 72 / 0"), new("Key", "=48 + (i % 4) * 4"), new("Tick", "=Floor(i / 4) * 96")]),
            new("E. Dependency-driven note lengths", "Gate reads the current computed Key. The dependency is evaluated correctly even though Gate appears later in the dialog.",
                [new("Maximum Candidates", "128"), new("Initial V / K / G / T", "90 / 60 / 24 / 0"), new("Key", "=60 + 12 * Sin(i * PI / 16)"), new("Gate", "=24 + (k1 - 48) * 2"), new("Tick", "=i * 24")]),
            new("F. A phase-shifted sine ribbon", "A dense note-art curve with a second velocity wave. Quarter-cycle negative phase begins at the bottom of the pitch wave. All current fields are normalized together.",
                [new("Maximum Candidates", "1024"), new("Initial V / K / G / T", "80 / 60 / 6 / 0"), new("Tick", "=i * 6"), new("Key", "=64 + 40 * Sin(2 * PI * t1 / 1536 - PI / 2)"), new("Velocity", "=72 + 40 * Sin(2 * PI * t1 / 768)"), new("Gate", "=6")])
        ]
        :
        [
            new("A. Sixteen evenly spaced events", "For a CC lane with range 0–127, create 16 points of value 64.",
                [new("Maximum Candidates", "16"), new("Initial Value / Tick", "64 / 0"), new("Tick", "=i * 96")]),
            new("B. An expression ramp", "For CC11, step from 0 toward 127. The first Initial point is included.",
                [new("Maximum Candidates", "128"), new("Initial Value / Tick", "0 / 0"), new("Create initial object", "ON"), new("Value", "=p0 + 1"), new("Tick", "=t0 + 12")]),
            new("C. A signed parameter sine wave", "Use a numeric logical-parameter lane whose range is −64…64. Its type determines whether fractional values are retained. The negative quarter-cycle phase starts at −64.",
                [new("Maximum Candidates", "257"), new("Initial Value / Tick", "0 / 0"), new("Tick", "=i * 6"), new("Value", "=64 * Sin(2 * PI * t1 / 1536 - PI / 2)")]),
            new("D. An alternating staircase", "A CC lane repeats a 16-point staircase, with denser sampling on the second half. Tick progression uses the previous normalized state.",
                [new("Maximum Candidates", "128"), new("Initial Value / Tick", "0 / 0"), new("Value", "=(i % 16) * 8"), new("Tick", "=i == 0 ? 0 : tr + (i % 16 < 8 ? 24 : 12)")]),
            new("E. A bounded back-and-forth pass", "For CC11, later candidates revisit the same Tick positions and overwrite earlier generated points. The final count may be smaller than Maximum Candidates.",
                [new("Maximum Candidates", "32"), new("Initial Value / Tick", "0 / 0"), new("Tick", "=(i < 16 ? i : 31 - i) * 24"), new("Value", "=i * 4")])
        ];

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
