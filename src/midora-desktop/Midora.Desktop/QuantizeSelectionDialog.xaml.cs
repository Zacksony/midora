using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;

namespace Midora.Desktop;

public sealed record QuantizeGridChoice(
    string Label,
    int Numerator,
    int Denominator,
    bool IsCustom = false);

public partial class QuantizeSelectionDialog : Window
{
    public static IReadOnlyList<QuantizeGridChoice> GridChoices { get; } =
        TimelineSubdivision.Presets
            .Where(static value => !value.IsBar)
            .Select(static value => new QuantizeGridChoice(
                value.Label,
                value.Numerator,
                value.Denominator))
            .Append(new("Custom Ticks", 0, 0, IsCustom: true))
            .ToArray();

    private readonly bool _isNoteSelection;
    private readonly IReadOnlyList<QuantizeGridChoice> _gridChoices;

    public QuantizeSelectionDialog(
        bool isNoteSelection,
        TimelineSubdivision? initialSubdivision = null)
    {
        _isNoteSelection = isNoteSelection;
        InitializeComponent();
        TitleText.Text = isNoteSelection ? "Quantize Notes" : "Quantize Events";
        Title = TitleText.Text;
        NoteModeRow.Visibility = isNoteSelection ? Visibility.Visible : Visibility.Collapsed;
        _gridChoices = CreateGridChoices(initialSubdivision);
        GridBox.ItemsSource = _gridChoices;
        GridBox.SelectedItem = ResolveInitialGridChoice(_gridChoices, initialSubdivision);
    }

    public TimelineQuantizeGrid? Grid { get; private set; }
    public NoteQuantizeOptions? NoteOptions { get; private set; }

    public static IReadOnlyList<QuantizeGridChoice> CreateGridChoices(
        TimelineSubdivision? initialSubdivision)
    {
        if (initialSubdivision is not { IsBar: false, Numerator: > 0, Denominator: > 0 } initial
            || GridChoices.Any(value =>
                !value.IsCustom
                && value.Numerator == initial.Numerator
                && value.Denominator == initial.Denominator))
        {
            return GridChoices;
        }

        QuantizeGridChoice[] choices = new QuantizeGridChoice[GridChoices.Count + 1];
        int customIndex = GridChoices.Count - 1;
        for (int index = 0; index < customIndex; index++) choices[index] = GridChoices[index];
        choices[customIndex] = new(initial.ShortLabel, initial.Numerator, initial.Denominator);
        choices[^1] = GridChoices[^1];
        return choices;
    }

    public static QuantizeGridChoice ResolveInitialGridChoice(
        IReadOnlyList<QuantizeGridChoice> choices,
        TimelineSubdivision? initialSubdivision)
    {
        ArgumentNullException.ThrowIfNull(choices);
        TimelineSubdivision initial = initialSubdivision is { IsBar: false, Numerator: > 0, Denominator: > 0 }
            ? initialSubdivision.Value
            : TimelineSubdivision.Presets.First(static value =>
                value.Numerator == 1 && value.Denominator == 16);
        return choices.FirstOrDefault(value =>
                !value.IsCustom
                && value.Numerator == initial.Numerator
                && value.Denominator == initial.Denominator)
            ?? choices.First(value =>
                !value.IsCustom && value.Numerator == 1 && value.Denominator == 16);
    }

    public static TimelineQuantizeGrid ParseGrid(QuantizeGridChoice? choice, string? customTicksText)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!choice.IsCustom)
        {
            if (choice.Numerator <= 0 || choice.Denominator <= 0)
                throw new ArgumentException("The selected musical grid is invalid.");
            return TimelineQuantizeGrid.MusicalFraction(choice.Numerator, choice.Denominator);
        }
        if (!long.TryParse(
                customTicksText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long ticks)
            || ticks <= 0)
        {
            throw new ArgumentException("Custom Grid must be a positive Int64 Tick value.");
        }
        return TimelineQuantizeGrid.FromCustomTicks(ticks);
    }

    private void OnGridChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        CustomTicksRow.Visibility = GridBox.SelectedItem is QuantizeGridChoice { IsCustom: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearValidation();
    }

    private void OnInputChanged(object sender, EventArgs e) => ClearValidation();

    private void ClearValidation()
    {
        if (!IsInitialized || ValidationText is null) return;
        ValidationText.Text = string.Empty;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TimelineQuantizeGrid grid = ParseGrid(
                GridBox.SelectedItem as QuantizeGridChoice,
                CustomTicksBox.Text);
            Grid = grid;
            NoteOptions = _isNoteSelection
                ? new(
                    grid,
                    NoteModeBox.SelectedIndex == 1
                        ? NoteQuantizeMode.StartAndEnd
                        : NoteQuantizeMode.StartOnly)
                : null;
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
            _ = MessageDialog.Show(
                this,
                exception.Message,
                TitleText.Text,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
