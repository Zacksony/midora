using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class TextInputDialog : Window, INotifyPropertyChanged
{
    private string _value;
    private readonly Func<string, string?>? _submit;
    private string? _validationError;

    public TextInputDialog(string title, string prompt, string value, Func<string, string?>? submit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        InitializeComponent();
        Title = title;
        Prompt = prompt ?? string.Empty;
        _value = value ?? string.Empty;
        _submit = submit;
        DataContext = this;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Prompt { get; }
    public string? ValidationError => _validationError;
    public Visibility ValidationErrorVisibility => string.IsNullOrEmpty(_validationError) ? Visibility.Collapsed : Visibility.Visible;
    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool TrySubmit()
    {
        try { _validationError = _submit?.Invoke(Value); }
        catch (Exception error) { _validationError = error.Message; }
        PropertyChanged?.Invoke(this, new(nameof(ValidationError)));
        PropertyChanged?.Invoke(this, new(nameof(ValidationErrorVisibility)));
        return string.IsNullOrEmpty(_validationError);
    }
    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (TrySubmit()) DialogResult = true;
        else ValueBox.Focus();
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
