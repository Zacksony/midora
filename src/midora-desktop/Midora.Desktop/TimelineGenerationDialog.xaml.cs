using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Application;
using Midora.Compiler;

namespace Midora.Desktop;

public partial class TimelineGenerationDialog : Window
{
    private readonly bool _notes;
    private readonly double _pointMinimum;
    private readonly TimelineGenerationPresetStore _presetStore;
    private bool _initialized;

    public TimelineGenerationDialog(bool notes, long baseTick, double pointMinimum = 0, double pointMaximum = 127)
        : this(notes, baseTick, pointMinimum, pointMaximum, new TimelineGenerationPresetStore(notes)) { }

    internal TimelineGenerationDialog(bool notes, long baseTick, double pointMinimum, double pointMaximum, TimelineGenerationPresetStore presetStore)
    {
        if (baseTick < 0 || baseTick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(baseTick));
        if (!double.IsFinite(pointMinimum) || !double.IsFinite(pointMaximum) || pointMaximum < pointMinimum)
            throw new ArgumentOutOfRangeException(nameof(pointMinimum));
        _notes = notes;
        _pointMinimum = pointMinimum;
        _presetStore = presetStore;
        if (presetStore.Notes != notes) throw new ArgumentException("The preset store belongs to the other generation tool.", nameof(presetStore));
        InitializeComponent();
        Title = TitleText.Text = notes ? "Batch Create Notes" : "Batch Create Events";
        ContextText.Text = notes
            ? "Create notes in the current piano roll. Existing notes at the same Tick and Key win; only surviving new notes are selected."
            : FormattableString.Invariant($"Create points in the current lane (value range {pointMinimum}–{pointMaximum}). At the same Tick and target, the last generated point wins.");
        VelocityRow.Visibility = KeyRow.Visibility = GateRow.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        PointValueRow.Visibility = notes ? Visibility.Collapsed : Visibility.Visible;
        BaseTickBox.Text = baseTick.ToString(CultureInfo.InvariantCulture);
        InitialValueBox.Text = Format(pointMinimum);
        NumericExpressionProfile profile = notes ? NumericExpressionProfiles.GenerateNote : NumericExpressionProfiles.GenerateEvent;
        VelocityBox.ConfigureProfile(profile, "v1");
        PointValueBox.ConfigureProfile(profile, "p1");
        KeyBox.ConfigureProfile(profile, "k1");
        GateBox.ConfigureProfile(profile, "g1");
        TickBox.ConfigureProfile(profile, "t1");
        _initialized = true;
        Loaded += (_, _) => (notes ? VelocityBox : PointValueBox).FocusEditor();
    }

    public NoteGenerationOptions? NoteOptions { get; private set; }
    public EventGenerationOptions? EventOptions { get; private set; }

    internal NoteGenerationOptions CaptureNoteOptions() => new NoteGenerationOptions
    {
        BaseTick = ParseBaseTick(BaseTickBox.Text), MaximumCandidates = ParseMaximumCandidates(MaximumCandidatesBox.Text),
        MaximumRelativeStartTick = ParseMaximumRelativeStart(MaximumRelativeTickBox.Text, LimitRelativeTickBox.IsChecked == true),
        CreateFirstFromInitialValues = CreateInitialBox.IsChecked == true,
        InitialVelocity = ParseInitial(InitialVelocityBox.Text, 1, "Velocity"), InitialKey = ParseInitial(InitialKeyBox.Text, 0, "Key"),
        InitialGate = ParseInitial(InitialGateBox.Text, 1, "Gate"), InitialTick = ParseInitial(InitialTickBox.Text, 0, "Tick"),
        VelocityExpression = VelocityBox.Text, KeyExpression = KeyBox.Text, GateExpression = GateBox.Text, TickExpression = TickBox.Text
    }.Validate();

    internal EventGenerationOptions CaptureEventOptions() => new EventGenerationOptions
    {
        BaseTick = ParseBaseTick(BaseTickBox.Text), MaximumCandidates = ParseMaximumCandidates(MaximumCandidatesBox.Text),
        MaximumRelativeStartTick = ParseMaximumRelativeStart(MaximumRelativeTickBox.Text, LimitRelativeTickBox.IsChecked == true),
        CreateFirstFromInitialValues = CreateInitialBox.IsChecked == true,
        InitialValue = ParseInitial(InitialValueBox.Text, _pointMinimum, "Value"), InitialTick = ParseInitial(InitialTickBox.Text, 0, "Tick"),
        ValueExpression = PointValueBox.Text, TickExpression = TickBox.Text
    }.Validate();

    internal static long ParseBaseTick(string text)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value == long.MaxValue)
            throw new ArgumentException("Base Tick must be within 0–9,223,372,036,854,775,806.");
        return value;
    }

    internal static int ParseMaximumCandidates(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value is < 1 or > TimelineGenerationLimits.MaximumCandidates)
            throw new ArgumentException($"Maximum Candidates must be within 1–{TimelineGenerationLimits.MaximumCandidates:N0}.");
        return value;
    }

    internal static long? ParseMaximumRelativeStart(string text, bool enabled)
    {
        if (!enabled) return null;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            throw new ArgumentException("Maximum Relative Start Tick must be a non-negative Int64 Tick value.");
        return value;
    }

    internal static double ParseInitial(string text, double fallback, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException($"{label} Initial must be a finite number (use a period for decimals).");
        return value;
    }

    private void OnValidateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_notes) _ = CaptureNoteOptions(); else _ = CaptureEventOptions();
            SetStatus("Expressions and settings are valid. Generation checks every candidate and may still reject runtime errors or resource limits.", "Brush.Success");
        }
        catch (Exception exception) when (TimelineGenerationPresetStore.IsPresetFailure(exception)) { SetStatus(exception.Message, "Brush.Red.Hover"); }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e) => TryCreate();
    private void OnExpressionCommitRequested(object? sender, EventArgs e) => TryCreate();

    private void TryCreate()
    {
        try
        {
            if (_notes) NoteOptions = CaptureNoteOptions(); else EventOptions = CaptureEventOptions();
            DialogResult = true;
        }
        catch (Exception exception) when (TimelineGenerationPresetStore.IsPresetFailure(exception)) { SetStatus(exception.Message, "Brush.Red.Hover"); }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) => _ = new TimelineGenerationHelpDialog(_notes) { Owner = this }.ShowDialog();

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TimelineGenerationPresetDialog dialog = new(_presetStore) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPreset is not { } preset) return;
            ApplyPreset(preset);
            SetStatus($"Preset '{preset.Name}' loaded. Base Tick and the target are unchanged.", "Brush.Text.Tertiary");
        }
        catch (Exception exception) when (TimelineGenerationPresetStore.IsPresetFailure(exception)) { SetStatus(exception.Message, "Brush.Red.Hover"); }
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TimelineGenerationPreset captured = _notes
                ? TimelineGenerationPreset.FromNotes("Preset", CaptureNoteOptions())
                : TimelineGenerationPreset.FromEvents("Preset", CaptureEventOptions());
            TextInputDialog nameDialog = new("Save Generation Preset", "Enter a unique preset name.", string.Empty) { Owner = this };
            if (nameDialog.ShowDialog() != true) return;
            TimelineGenerationPresetInfo saved = _presetStore.Save(captured with { Name = nameDialog.Value });
            SetStatus($"Preset '{saved.Name}' saved.", "Brush.Text.Tertiary");
        }
        catch (Exception exception) when (TimelineGenerationPresetStore.IsPresetFailure(exception)) { SetStatus(exception.Message, "Brush.Red.Hover"); }
    }

    internal void ApplyPreset(TimelineGenerationPreset preset)
    {
        preset = TimelineGenerationPresetStore.NormalizeAndValidate(preset, _notes);
        MaximumCandidatesBox.Text = preset.MaximumCandidates.ToString(CultureInfo.InvariantCulture);
        LimitRelativeTickBox.IsChecked = preset.MaximumRelativeStartTick.HasValue;
        MaximumRelativeTickBox.Text = (preset.MaximumRelativeStartTick ?? 0).ToString(CultureInfo.InvariantCulture);
        CreateInitialBox.IsChecked = preset.CreateFirstFromInitialValues;
        if (preset.Note is { } note)
        {
            VelocityBox.Text = note.VelocityExpression; KeyBox.Text = note.KeyExpression; GateBox.Text = note.GateExpression; TickBox.Text = note.TickExpression;
            InitialVelocityBox.Text = Format(note.InitialVelocity); InitialKeyBox.Text = Format(note.InitialKey);
            InitialGateBox.Text = Format(note.InitialGate); InitialTickBox.Text = Format(note.InitialTick);
        }
        else if (preset.Event is { } point)
        {
            PointValueBox.Text = point.ValueExpression; TickBox.Text = point.TickExpression;
            InitialValueBox.Text = Format(point.InitialValue); InitialTickBox.Text = Format(point.InitialTick);
        }
        SetStatus("Not validated.", "Brush.Text.Tertiary");
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private void OnInputChanged(object? sender, EventArgs e)
    {
        if (_initialized) SetStatus("Not validated.", "Brush.Text.Tertiary");
    }
    private void OnLimitChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        MaximumRelativeTickBox.IsEnabled = LimitRelativeTickBox.IsChecked == true;
        OnInputChanged(sender, e);
    }
    private void SetStatus(string text, string brushKey)
    {
        ValidationText.Text = text;
        ValidationText.Foreground = (Brush)FindResource(brushKey);
    }
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        NoteOptions = null; EventOptions = null;
        DialogResult = false;
    }
    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancelClick(sender, e);
    }
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
