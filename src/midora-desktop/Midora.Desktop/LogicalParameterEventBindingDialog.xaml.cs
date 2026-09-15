using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

public partial class LogicalParameterEventBindingDialog : Window
{
    internal static IReadOnlyList<MidiControlChangeInfo> SelectableControllers =>
        MidiControlChangeCatalog.EditableControllers;

    private readonly EventInstrument _instrument;
    private readonly MidoraId? _currentSubVoiceId;
    private readonly SubVoiceChoice[] _subVoiceChoices;
    private readonly IReadOnlyList<MidiControlChangeInfo> _controllerChoices;
    private readonly Func<LogicalParameterEventBindingRequest, string?>? _submit;
    private bool _initializationComplete;
    private bool _updatingDefaults;
    private bool _nameWasEdited;

    public LogicalParameterEventBindingDialog(
        EventInstrument instrument,
        MidoraId? currentSubVoiceId)
        : this(instrument, currentSubVoiceId, submit: null)
    {
    }

    internal LogicalParameterEventBindingDialog(
        EventInstrument instrument,
        MidoraId? currentSubVoiceId,
        Func<LogicalParameterEventBindingRequest, string?>? submit)
    {
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        _currentSubVoiceId = currentSubVoiceId;
        _submit = submit;
        _controllerChoices = SelectableControllers;
        _subVoiceChoices = instrument.SubVoices
            .Select((value, index) => new SubVoiceChoice(
                value.Id,
                string.IsNullOrWhiteSpace(value.Name) ? $"SubVoice {index + 1}" : value.Name,
                value.Id == currentSubVoiceId))
            .ToArray();

        InitializeComponent();

        KindBox.ItemsSource = Enum.GetValues<MidiValueKind>();
        ControllerBox.ItemsSource = _controllerChoices;
        ScopeBox.ItemsSource = new[]
        {
            new ScopeChoice(EventBindingScopeChoice.Current, "Current SubVoice"),
            new ScopeChoice(EventBindingScopeChoice.Selected, "Selected SubVoices"),
            new ScopeChoice(EventBindingScopeChoice.All, "All SubVoices")
        };
        RefreshConflictChoices(appendAllowed: true);
        OperationBox.ItemsSource = Enum.GetValues<LogicalParameterEventBindingOperation>();
        foreach (SubVoiceChoice choice in _subVoiceChoices)
        {
            choice.PropertyChanged += OnSubVoiceChoiceChanged;
        }
        SubVoiceList.ItemsSource = _subVoiceChoices;

        ScopeBox.SelectedIndex = currentSubVoiceId.HasValue ? 0 : 2;
        ConflictBox.SelectedIndex = 0;
        OperationBox.SelectedItem = LogicalParameterEventBindingOperation.Override;
        KindBox.SelectedItem = MidiValueKind.ControlChange;
        ControllerBox.SelectedItem = _controllerChoices
            .FirstOrDefault(value => value.Number == 11)
            ?? _controllerChoices.FirstOrDefault();
        _initializationComplete = true;
        UpdateTargetRowsAndDefaults(forceName: true, forceRange: true);
        UpdateScopePresentation();
    }

    public LogicalParameterEventBindingRequest? Request { get; private set; }

    private void OnTargetChanged(object sender, EventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            UpdateTargetRowsAndDefaults(forceName: false, forceRange: false);
        }
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializationComplete)
        {
            UpdateScopePresentation();
        }
    }

    private void OnOperationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            UpdateTargetRowsAndDefaults(forceName: false, forceRange: true);
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            _nameWasEdited = true;
        }
    }

    private void OnSubVoiceChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_initializationComplete
            && string.Equals(e.PropertyName, nameof(SubVoiceChoice.IsSelected), StringComparison.Ordinal)
            && TryGetTarget(out MidiValueTarget target, showValidation: false))
        {
            UpdateExistingMappingSummary(target);
        }
    }

    private void UpdateTargetRowsAndDefaults(bool forceName, bool forceRange)
    {
        if (!_initializationComplete || ControllerRow is null)
        {
            return;
        }

        _updatingDefaults = true;
        try
        {
            MidiValueKind? kind = KindBox.SelectedItem as MidiValueKind?;
            ControllerRow.Visibility = kind == MidiValueKind.ControlChange
                ? Visibility.Visible
                : Visibility.Collapsed;
            RpnRow.Visibility = kind == MidiValueKind.RegisteredParameter
                ? Visibility.Visible
                : Visibility.Collapsed;
            NrpnRow.Visibility = kind == MidiValueKind.NonRegisteredParameter
                ? Visibility.Visible
                : Visibility.Collapsed;

            LogicalParameterEventBindingOperation operation =
                OperationBox.SelectedItem is LogicalParameterEventBindingOperation value
                    ? value
                    : LogicalParameterEventBindingOperation.Override;
            FactorRangePanel.Visibility = operation == LogicalParameterEventBindingOperation.Multiply
                ? Visibility.Visible
                : Visibility.Collapsed;
            OperationHelpText.Text = operation switch
            {
                LogicalParameterEventBindingOperation.Override =>
                    "Absolute overwrite: the parameter value replaces the current target value. Its neutral default is the formal MIDI reset/default, not a user-authored Initial State.",
                LogicalParameterEventBindingOperation.Add =>
                    "Relative add: the parameter value is added to the current accumulated target value. The neutral default is 0.",
                LogicalParameterEventBindingOperation.Multiply =>
                    "Relative multiply: the source range is mapped to the factor range, then multiplied by the current accumulated target value. Factor 1 must map to an exact Integer source value.",
                _ => string.Empty
            };

            if (TryGetTarget(out MidiValueTarget target, showValidation: false))
            {
                if (forceName || !_nameWasEdited)
                {
                    NameBox.Text = DefaultParameterName(target);
                    _nameWasEdited = false;
                }
                if (forceRange)
                {
                    SetDefaultRange(target, operation);
                }
                UpdateExistingMappingSummary(target);
            }
            else
            {
                SummaryText.Text = "Complete the MIDI target to inspect existing mappings.";
                ExistingMappingOrderList.ItemsSource = null;
            }
        }
        finally
        {
            _updatingDefaults = false;
        }
    }

    private void UpdateScopePresentation()
    {
        if (!_initializationComplete || SubVoiceSelectionPanel is null)
        {
            return;
        }
        EventBindingScopeChoice scope = (ScopeBox.SelectedItem as ScopeChoice)?.Value
            ?? EventBindingScopeChoice.All;
        SubVoiceList.IsEnabled = scope == EventBindingScopeChoice.Selected;
        SubVoiceSelectionHint.Text = scope switch
        {
            EventBindingScopeChoice.Current when _currentSubVoiceId.HasValue =>
                "The currently active SubVoice is frozen when OK is pressed.",
            EventBindingScopeChoice.Current => "No current SubVoice is available.",
            EventBindingScopeChoice.Selected => "Check one or more current SubVoices.",
            _ => "All current SubVoices are frozen now; later SubVoices are not added automatically."
        };
        UpdateTargetRowsAndDefaults(forceName: false, forceRange: false);
    }

    private void SetDefaultRange(
        MidiValueTarget target,
        LogicalParameterEventBindingOperation operation)
    {
        (int minimum, int maximum) = MidiStateValueRules.GetRange(target);
        switch (operation)
        {
            case LogicalParameterEventBindingOperation.Override:
                SourceMinimumBox.Text = minimum.ToString(CultureInfo.InvariantCulture);
                SourceMaximumBox.Text = maximum.ToString(CultureInfo.InvariantCulture);
                break;
            case LogicalParameterEventBindingOperation.Add:
                int magnitude = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
                SourceMinimumBox.Text = (-magnitude).ToString(CultureInfo.InvariantCulture);
                SourceMaximumBox.Text = magnitude.ToString(CultureInfo.InvariantCulture);
                break;
            case LogicalParameterEventBindingOperation.Multiply:
                SourceMinimumBox.Text = "0";
                SourceMaximumBox.Text = "100";
                FactorMinimumBox.Text = "0";
                FactorMaximumBox.Text = "2";
                break;
        }
    }

    private void UpdateExistingMappingSummary(MidiValueTarget target)
    {
        MidoraId[] ids = ResolveSelectedSubVoiceIds(allowEmpty: true);
        HashSet<MidoraId> selectedIds = ids.ToHashSet();
        Dictionary<MidoraId, List<LogicalParameterMapping>> mappingsBySubVoice = [];
        int count = 0;
        bool appendAllowed = true;
        foreach (LogicalParameterMapping mapping in _instrument.ParameterMappings)
        {
            if (!selectedIds.Contains(mapping.SubVoiceId) || mapping.Target != target)
            {
                continue;
            }
            if (!mappingsBySubVoice.TryGetValue(
                    mapping.SubVoiceId,
                    out List<LogicalParameterMapping>? mappings))
            {
                mappings = [];
                mappingsBySubVoice.Add(mapping.SubVoiceId, mappings);
            }
            mappings.Add(mapping);
            count++;
            appendAllowed &= mapping.TargetSettings.Rounding == MappingRounding.Round
                && mapping.TargetSettings.Overflow == MappingOverflow.Clamp;
        }
        SummaryText.Text = count == 0
            ? "No exact-target Mapping currently exists for the chosen SubVoices."
            : appendAllowed
                ? $"{count:N0} exact-target Mapping(s) currently exist. Append preserves them; Replace removes only these exact-target mappings and preserves unrelated order."
                : $"{count:N0} exact-target Mapping(s) currently exist. Append is unavailable because their shared target settings are not Round + Clamp; Replace remains available.";
        RefreshConflictChoices(appendAllowed);
        ExistingMappingOrderList.ItemsSource = BuildExistingMappingOrder(
            selectedIds,
            mappingsBySubVoice);
    }

    private string[] BuildExistingMappingOrder(
        IReadOnlySet<MidoraId> selectedSubVoiceIds,
        IReadOnlyDictionary<MidoraId, List<LogicalParameterMapping>> mappingsBySubVoice)
    {
        Dictionary<MidoraId, string> parameterNames = _instrument.LogicalParameters
            .ToDictionary(value => value.Id, value => value.Name);
        List<string> rows = [];
        foreach (SubVoice subVoice in _instrument.SubVoices)
        {
            if (!selectedSubVoiceIds.Contains(subVoice.Id))
            {
                continue;
            }
            IReadOnlyList<LogicalParameterMapping> mappings =
                mappingsBySubVoice.GetValueOrDefault(subVoice.Id) ?? [];
            string subVoiceName = string.IsNullOrWhiteSpace(subVoice.Name)
                ? "Unnamed SubVoice"
                : subVoice.Name;
            if (mappings.Count == 0)
            {
                rows.Add($"{subVoiceName}: no existing Mapping");
                continue;
            }
            for (int index = 0; index < mappings.Count; index++)
            {
                LogicalParameterMapping mapping = mappings[index];
                string parameterName = parameterNames.GetValueOrDefault(
                    mapping.ParameterId,
                    "Missing Logical Parameter");
                rows.Add(
                    $"{subVoiceName}: {index + 1}. {parameterName} · "
                    + $"{mapping.TargetSettings.Rounding} / {mapping.TargetSettings.Overflow}");
            }
        }
        return rows.ToArray();
    }

    private void RefreshConflictChoices(bool appendAllowed)
    {
        ConflictChoice? previous = ConflictBox.SelectedItem as ConflictChoice;
        ConflictChoice[] choices =
        [
            new(
                LogicalParameterEventBindingConflictPolicy.Append,
                appendAllowed
                    ? "Append after existing mappings"
                    : "Append unavailable — shared settings are not Round + Clamp",
                appendAllowed),
            new(
                LogicalParameterEventBindingConflictPolicy.Replace,
                "Replace exact-target mappings",
                IsEnabled: true),
            new(null, "Cancel creation", IsEnabled: true)
        ];
        ConflictBox.ItemsSource = choices;
        if (previous is null)
        {
            return;
        }
        ConflictBox.SelectedItem = choices.FirstOrDefault(value =>
            value.Value == previous.Value && value.IsEnabled);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation("Parameter Name cannot be empty.", NameBox);
            return;
        }
        if (_instrument.LogicalParameters.Any(value =>
                string.Equals(value.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowValidation("Logical Parameter Name must be unique in this Event Instrument.", NameBox);
            return;
        }
        if (!TryGetTarget(out MidiValueTarget target, showValidation: true))
        {
            return;
        }
        if (OperationBox.SelectedItem is not LogicalParameterEventBindingOperation operation
            || ScopeBox.SelectedItem is not ScopeChoice scopeChoice
            || ConflictBox.SelectedItem is not ConflictChoice conflictChoice)
        {
            ShowValidation("Select an operation, target scope, and existing-mapping action.");
            return;
        }
        if (!conflictChoice.Value.HasValue)
        {
            DialogResult = false;
            return;
        }
        if (!TryParseInt(SourceMinimumBox, "Source Minimum", out int sourceMinimum)
            || !TryParseInt(SourceMaximumBox, "Source Maximum", out int sourceMaximum)
            || sourceMaximum < sourceMinimum)
        {
            if (ValidationText.Text.Length == 0)
            {
                ShowValidation("Source Minimum must not exceed Source Maximum.", SourceMinimumBox);
            }
            return;
        }

        double? factorMinimum = null;
        double? factorMaximum = null;
        if (operation == LogicalParameterEventBindingOperation.Multiply)
        {
            if (!TryParseDouble(FactorMinimumBox, "Factor Minimum", out double parsedMinimum)
                || !TryParseDouble(FactorMaximumBox, "Factor Maximum", out double parsedMaximum)
                || parsedMaximum <= parsedMinimum)
            {
                if (ValidationText.Text.Length == 0)
                {
                    ShowValidation("Factor Minimum must be less than Factor Maximum.", FactorMinimumBox);
                }
                return;
            }
            factorMinimum = parsedMinimum;
            factorMaximum = parsedMaximum;
        }

        string? semanticError = ValidateOperationRange(
            target,
            operation,
            sourceMinimum,
            sourceMaximum,
            factorMinimum,
            factorMaximum);
        if (semanticError is not null)
        {
            ShowValidation(semanticError, SourceMinimumBox);
            return;
        }

        MidoraId[] selectedIds = ResolveSelectedSubVoiceIds(allowEmpty: false);
        if (_instrument.SubVoices.Count == 0)
        {
            ShowValidation("The Event Instrument has no SubVoices to bind.", ScopeBox);
            return;
        }
        if (scopeChoice.Value != EventBindingScopeChoice.All && selectedIds.Length == 0)
        {
            ShowValidation("Select at least one SubVoice.", SubVoiceList);
            return;
        }
        if (scopeChoice.Value == EventBindingScopeChoice.Current
            && (!_currentSubVoiceId.HasValue || selectedIds.Length != 1))
        {
            ShowValidation("There is no current SubVoice to bind.", ScopeBox);
            return;
        }

        LogicalParameterEventBindingRequest request = new()
        {
            Name = name,
            Target = target,
            Operation = operation,
            SubVoiceScope = scopeChoice.Value == EventBindingScopeChoice.All
                ? LogicalParameterEventBindingSubVoiceScope.All
                : LogicalParameterEventBindingSubVoiceScope.Selected,
            SubVoiceIds = scopeChoice.Value == EventBindingScopeChoice.All
                ? Array.Empty<MidoraId>()
                : selectedIds,
            SourceMinimum = sourceMinimum,
            SourceMaximum = sourceMaximum,
            FactorMinimum = factorMinimum,
            FactorMaximum = factorMaximum,
            ConflictPolicy = conflictChoice.Value.Value
        };

        try
        {
            // Validate all purely numeric target constraints before closing. The formal command
            // repeats validation against the frozen Project revision and remains authoritative.
            MidiStateValueRules.Validate(target, value: null);
            string? submitError = _submit?.Invoke(request);
            if (submitError is not null)
            {
                ShowValidation(submitError);
                return;
            }
            Request = request;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShowValidation(exception.Message);
        }
    }

    private MidoraId[] ResolveSelectedSubVoiceIds(bool allowEmpty)
    {
        EventBindingScopeChoice scope = (ScopeBox.SelectedItem as ScopeChoice)?.Value
            ?? EventBindingScopeChoice.All;
        MidoraId[] result = scope switch
        {
            EventBindingScopeChoice.Current when _currentSubVoiceId is MidoraId id => [id],
            EventBindingScopeChoice.Selected => _subVoiceChoices
                .Where(value => value.IsSelected)
                .Select(value => value.Id)
                .ToArray(),
            EventBindingScopeChoice.All => _instrument.SubVoices.Select(value => value.Id).ToArray(),
            _ => []
        };
        return allowEmpty || result.Length != 0 ? result : [];
    }

    private bool TryGetTarget(out MidiValueTarget target, bool showValidation)
    {
        target = default;
        if (KindBox.SelectedItem is not MidiValueKind kind)
        {
            if (showValidation) ShowValidation("Select a target MIDI event kind.", KindBox);
            return false;
        }
        int number = 0;
        if (kind == MidiValueKind.ControlChange)
        {
            if (ControllerBox.SelectedItem is not MidiControlChangeInfo controller)
            {
                if (showValidation) ShowValidation("Select a Control Change.", ControllerBox);
                return false;
            }
            number = controller.Number;
        }
        else if (kind is MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter)
        {
            TextBox box = kind == MidiValueKind.RegisteredParameter ? RpnBox : NrpnBox;
            if (!int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number is < 0 or > 16_383)
            {
                if (showValidation) ShowValidation("RPN/NRPN Number must be an integer from 0 through 16,383.", box);
                return false;
            }
        }
        target = new(kind, number);
        try
        {
            MidiStateValueRules.Validate(target, value: null);
            return true;
        }
        catch (ArgumentException exception)
        {
            if (showValidation) ShowValidation(exception.Message, KindBox);
            return false;
        }
    }

    private static string DefaultParameterName(MidiValueTarget target)
    {
        if (target.Kind == MidiValueKind.ControlChange
            && MidiControlChangeCatalog.TryGet(target.Number, out MidiControlChangeInfo? info))
        {
            return info!.Name;
        }
        return TemplateEventMidiTargets.Format(target);
    }

    private static string? ValidateOperationRange(
        MidiValueTarget target,
        LogicalParameterEventBindingOperation operation,
        int sourceMinimum,
        int sourceMaximum,
        double? factorMinimum,
        double? factorMaximum)
    {
        switch (operation)
        {
            case LogicalParameterEventBindingOperation.Override:
                int targetDefault = MidiValueTargetDefaults.GetDefaultValue(target);
                return targetDefault < sourceMinimum || targetDefault > sourceMaximum
                    ? $"Override source range must contain the formal target default ({targetDefault})."
                    : null;
            case LogicalParameterEventBindingOperation.Add:
                return sourceMinimum > 0 || sourceMaximum < 0
                    ? "Add source range must contain the neutral offset 0."
                    : null;
            case LogicalParameterEventBindingOperation.Multiply:
                if (!factorMinimum.HasValue || !factorMaximum.HasValue)
                {
                    return "Multiply requires a Factor Minimum and Factor Maximum.";
                }
                if (factorMinimum.Value > 1 || factorMaximum.Value < 1)
                {
                    return "Multiply factor range must contain the neutral factor 1.";
                }
                if (sourceMinimum == sourceMaximum)
                {
                    return "Multiply source range must contain more than one value.";
                }
                double sourceDefault = sourceMinimum
                    + ((1 - factorMinimum.Value) / (factorMaximum.Value - factorMinimum.Value)
                       * (sourceMaximum - (double)sourceMinimum));
                return !double.IsFinite(sourceDefault)
                       || sourceDefault != Math.Truncate(sourceDefault)
                       || sourceDefault < sourceMinimum
                       || sourceDefault > sourceMaximum
                    ? "Factor 1 must map back to an exact Integer source value inside the source range."
                    : null;
            default:
                return "Select a supported operation.";
        }
    }

    private bool TryParseInt(TextBox box, string label, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        ShowValidation($"{label} must be a base-10 integer.", box);
        return false;
    }

    private bool TryParseDouble(TextBox box, string label, out double value)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }
        ShowValidation($"{label} must be a finite number.", box);
        return false;
    }

    private void ShowValidation(string message, Control? control = null)
    {
        ValidationText.Text = message;
        control?.Focus();
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private enum EventBindingScopeChoice
    {
        Current,
        Selected,
        All
    }

    private sealed record ScopeChoice(EventBindingScopeChoice Value, string Label);
    private sealed record ConflictChoice(
        LogicalParameterEventBindingConflictPolicy? Value,
        string Label,
        bool IsEnabled);
    private sealed class SubVoiceChoice : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SubVoiceChoice(MidoraId id, string name, bool isSelected)
        {
            Id = id;
            Name = name;
            _isSelected = isSelected;
        }

        public MidoraId Id { get; }
        public string Name { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
