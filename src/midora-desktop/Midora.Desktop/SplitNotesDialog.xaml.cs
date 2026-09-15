using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Application;
using Midora.Compiler;

namespace Midora.Desktop;

public partial class SplitNotesDialog : Window
{
    private readonly NoteSplitPresetStore _presetStore;

    public SplitNotesDialog(NoteSplitPresetStore? presetStore = null)
    {
        _presetStore = presetStore ?? new();
        InitializeComponent();
        ExpressionBox.ConfigureProfile(NumericExpressionProfiles.NoteSplit);
        UpdateModeRows();
        Loaded += (_, _) => FixedLengthBox.Focus();
    }

    /// <summary>
    /// Gets the accepted options. If ExpressionProgram is non-null, ownership is
    /// transferred to the caller, which must dispose it after executing the edit.
    /// </summary>
    public NoteSplitOptions? Options { get; private set; }

    public static NoteSplitOptions ParseOptions(
        NoteSplitMode mode,
        string? fixedLengthText,
        string? pieceCountText,
        string? expression,
        string? maximumCutsText)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        long fixedLength = 1;
        if (mode == NoteSplitMode.FixedPieceLength
            && (!long.TryParse(
                    fixedLengthText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out fixedLength)
                || fixedLength < 1))
        {
            throw new ArgumentException("Piece Length must be a positive Int64 Tick value.");
        }
        int pieceCount = 2;
        if (mode == NoteSplitMode.MaximumPieceCount
            && (!int.TryParse(
                    pieceCountText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out pieceCount)
                || pieceCount < 1))
        {
            throw new ArgumentException("Maximum Piece Count must be a positive Int32 value.");
        }
        int maximumCuts = 65_535;
        if (mode == NoteSplitMode.Expression
            && (!int.TryParse(
                    maximumCutsText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out maximumCuts)
                || maximumCuts is < 1 or > NoteSplitPresetStore.MaximumAllowedCuts))
        {
            throw new ArgumentException(
                $"Maximum Cuts must be within 1–{NoteSplitPresetStore.MaximumAllowedCuts:N0}.");
        }

        NoteSplitExpressionProgram? program = mode == NoteSplitMode.Expression
            ? NoteSplitExpressionProgram.Compile(expression ?? string.Empty)
            : null;
        return new()
        {
            Mode = mode,
            FixedPieceLengthTicks = fixedLength,
            MaximumPieceCount = pieceCount,
            ExpressionProgram = program,
            MaximumCuts = maximumCuts
        };
    }

    private NoteSplitMode SelectedMode => ModeBox.SelectedIndex switch
    {
        0 => NoteSplitMode.FixedPieceLength,
        1 => NoteSplitMode.MaximumPieceCount,
        2 => NoteSplitMode.Expression,
        _ => NoteSplitMode.FixedPieceLength
    };

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeRows();
        ResetValidationStatus();
    }

    private void UpdateModeRows()
    {
        if (!IsInitialized) return;
        NoteSplitMode mode = SelectedMode;
        FixedLengthRow.Visibility = mode == NoteSplitMode.FixedPieceLength
            ? Visibility.Visible
            : Visibility.Collapsed;
        PieceCountRow.Visibility = mode == NoteSplitMode.MaximumPieceCount
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpressionRow.Visibility = mode == NoteSplitMode.Expression
            ? Visibility.Visible
            : Visibility.Collapsed;
        MaximumCutsRow.Visibility = ExpressionRow.Visibility;
    }

    private NoteSplitOptions CompileAndValidate() => ParseOptions(
        SelectedMode,
        FixedLengthBox.Text,
        PieceCountBox.Text,
        ExpressionBox.Text,
        MaximumCutsBox.Text);

    private void OnValidateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using NoteSplitExpressionProgram? program = CompileAndValidate().ExpressionProgram;
            SetValidationStatus("Validation succeeded.", ValidationStatus.Success);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => TryApply();
    private void OnExpressionCommitRequested(object? sender, EventArgs e) => TryApply();

    private void TryApply()
    {
        try
        {
            Options?.ExpressionProgram?.Dispose();
            Options = CompileAndValidate();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Split Notes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        NoteSplitHelpDialog dialog = new() { Owner = this };
        _ = dialog.ShowDialog();
    }

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        NoteSplitPresetDialog dialog = new(_presetStore) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPreset is not NoteSplitPreset preset) return;
        ApplyPreset(preset);
        SetValidationStatus(
            $"Preset '{preset.Name}' loaded. Validate or apply it to the selection.",
            ValidationStatus.Neutral);
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        TextInputDialog nameDialog = new(
            "Save Note Split Preset",
            "Enter a unique preset name.",
            string.Empty)
        {
            Owner = this
        };
        if (nameDialog.ShowDialog() != true) return;
        try
        {
            NoteSplitPresetInfo saved = _presetStore.Save(CapturePreset(nameDialog.Value));
            SetValidationStatus($"Preset '{saved.Name}' saved.", ValidationStatus.Neutral);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                   or InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        {
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Save Note Split Preset",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private NoteSplitPreset CapturePreset(string name)
    {
        NoteSplitOptions validation = CompileAndValidate();
        validation.ExpressionProgram?.Dispose();
        return new(
            NoteSplitPresetStore.CurrentSchemaVersion,
            NoteSplitPresetStore.CurrentToolId,
            NoteSplitPresetStore.CurrentToolVersion,
            NumericExpressionProfiles.NoteSplit.Id,
            NumericExpressionProfiles.NoteSplit.Version,
            name,
            SelectedMode,
            validation.FixedPieceLengthTicks,
            validation.MaximumPieceCount,
            SelectedMode == NoteSplitMode.Expression ? ExpressionBox.Text : "=192",
            validation.MaximumCuts);
    }

    private void ApplyPreset(NoteSplitPreset preset)
    {
        ModeBox.SelectedIndex = preset.Mode switch
        {
            NoteSplitMode.FixedPieceLength => 0,
            NoteSplitMode.MaximumPieceCount => 1,
            NoteSplitMode.Expression => 2,
            _ => 0
        };
        FixedLengthBox.Text = preset.FixedPieceLengthTicks.ToString(CultureInfo.InvariantCulture);
        PieceCountBox.Text = preset.MaximumPieceCount.ToString(CultureInfo.InvariantCulture);
        ExpressionBox.Text = preset.Expression;
        MaximumCutsBox.Text = preset.MaximumCuts.ToString(CultureInfo.InvariantCulture);
        UpdateModeRows();
    }

    private void OnInputChanged(object? sender, EventArgs e) => ResetValidationStatus();

    private void ResetValidationStatus()
    {
        if (!IsInitialized || ValidationText is null) return;
        SetValidationStatus("Not validated.", ValidationStatus.Neutral);
    }

    private void SetValidationStatus(string message, ValidationStatus status)
    {
        ValidationText.Text = message;
        string brushKey = status switch
        {
            ValidationStatus.Success => "Brush.Success",
            ValidationStatus.Error => "Brush.Red.Hover",
            _ => "Brush.Text.Tertiary"
        };
        ValidationText.Foreground = (Brush)FindResource(brushKey);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Options?.ExpressionProgram?.Dispose();
        Options = null;
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

    private enum ValidationStatus
    {
        Neutral,
        Success,
        Error
    }
}
