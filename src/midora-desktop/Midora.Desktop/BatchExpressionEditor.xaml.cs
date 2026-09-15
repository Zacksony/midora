using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Midora.Compiler;

namespace Midora.Desktop;

public partial class BatchExpressionEditor : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(BatchExpressionEditor),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChangedExternally));

    private readonly BatchExpressionColorizer _colorizer;
    private readonly BatchBracketRenderer _bracketRenderer;
    private IReadOnlyList<BatchExpressionCompletionItem> _completionItems = [];
    private IReadOnlyList<BatchExpressionVariable> _variables = [];
    private bool _synchronizing;
    private bool _refreshCompletionAfterChange;
    private bool _expressionMode;
    private int _signatureIndex;

    public BatchExpressionEditor()
    {
        InitializeComponent();
        ExpressionEditor.Options.ConvertTabsToSpaces = true;
        ExpressionEditor.Options.IndentationSize = 2;
        ExpressionEditor.Options.EnableHyperlinks = false;
        ExpressionEditor.Options.EnableEmailHyperlinks = false;
        ExpressionEditor.Options.HighlightCurrentLine = false;
        SingleLineCodeEditorInput.Attach(ExpressionEditor);

        _colorizer = new BatchExpressionColorizer(() => _variables);
        _bracketRenderer = new BatchBracketRenderer();
        ExpressionEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        ExpressionEditor.TextArea.TextView.BackgroundRenderers.Add(_bracketRenderer);
        ExpressionEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateBracketMatch();
        SetEditorsFromOutside(string.Empty);
    }

    public event EventHandler? TextChanged;
    public event EventHandler? CommitRequested;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, Sanitize(value));
    }

    public void ConfigureContext(BatchEditPresetKind presetKind, BatchEditField field)
    {
        _variables = BatchExpressionCompletionProvider.CreateVariables(presetKind, field);
        ExpressionEditor.TextArea.TextView.Redraw();
        if (CompletionPopup.IsOpen) ShowCompletion();
    }

    public void ConfigureProfile(NumericExpressionProfile profile, string? excludedVariable = null)
    {
        _variables = BatchExpressionCompletionProvider.CreateVariables(profile, excludedVariable);
        ExpressionEditor.TextArea.TextView.Redraw();
        if (CompletionPopup.IsOpen) ShowCompletion();
    }

    public void FocusEditor()
    {
        if (_expressionMode)
        {
            _ = ExpressionEditor.Focus();
        }
        else
        {
            _ = PlainTextBox.Focus();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) FocusEditor();
    }

    private static void OnTextChangedExternally(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        BatchExpressionEditor editor = (BatchExpressionEditor)dependencyObject;
        if (editor._synchronizing) return;
        editor.SetEditorsFromOutside(Sanitize(eventArgs.NewValue as string));
    }

    private void SetEditorsFromOutside(string text)
    {
        text = Sanitize(text);
        _synchronizing = true;
        try
        {
            PlainTextBox.Text = text;
            if (!string.Equals(ExpressionEditor.Text, text, StringComparison.Ordinal))
            {
                ExpressionEditor.Text = text;
            }
            SetExpressionMode(BatchExpressionCompletionProvider.IsExpression(text));
            int caret = text.Length;
            PlainTextBox.CaretIndex = caret;
            ExpressionEditor.CaretOffset = caret;
        }
        finally
        {
            _synchronizing = false;
        }
        UpdateBracketMatch();
    }

    private void OnPlainTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_synchronizing) return;
        string text = Sanitize(PlainTextBox.Text);
        int caret = Math.Min(PlainTextBox.CaretIndex, text.Length);
        if (!string.Equals(text, PlainTextBox.Text, StringComparison.Ordinal))
        {
            _synchronizing = true;
            PlainTextBox.Text = text;
            PlainTextBox.CaretIndex = caret;
            _synchronizing = false;
        }
        PushText(text);
        if (BatchExpressionCompletionProvider.IsExpression(text))
        {
            SwitchToExpression(text, caret);
        }
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnExpressionTextChanged(object? sender, EventArgs e)
    {
        if (_synchronizing) return;
        string text = Sanitize(ExpressionEditor.Text);
        int caret = Math.Min(ExpressionEditor.CaretOffset, text.Length);
        PushText(text);
        if (!BatchExpressionCompletionProvider.IsExpression(text))
        {
            SwitchToPlain(text, caret);
        }
        ExpressionEditor.TextArea.TextView.Redraw();
        UpdateBracketMatch();
        TextChanged?.Invoke(this, EventArgs.Empty);

        if (_refreshCompletionAfterChange || CompletionPopup.IsOpen)
        {
            _refreshCompletionAfterChange = false;
            _ = Dispatcher.BeginInvoke(
                ShowCompletion,
                DispatcherPriority.Background);
        }
    }

    private void PushText(string text)
    {
        _synchronizing = true;
        try
        {
            SetCurrentValue(TextProperty, text);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void SwitchToExpression(string text, int caret)
    {
        _synchronizing = true;
        try
        {
            if (!string.Equals(ExpressionEditor.Text, text, StringComparison.Ordinal))
            {
                ExpressionEditor.Text = text;
            }
            ExpressionEditor.CaretOffset = Math.Clamp(caret, 0, text.Length);
            SetExpressionMode(true);
        }
        finally
        {
            _synchronizing = false;
        }
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _ = ExpressionEditor.Focus();
                ExpressionEditor.CaretOffset = Math.Clamp(caret, 0, ExpressionEditor.Text.Length);
                UpdateBracketMatch();
                ShowCompletion();
            }),
            DispatcherPriority.Input);
    }

    private void SwitchToPlain(string text, int caret)
    {
        CloseCompletion();
        _synchronizing = true;
        try
        {
            PlainTextBox.Text = text;
            PlainTextBox.CaretIndex = Math.Clamp(caret, 0, text.Length);
            SetExpressionMode(false);
        }
        finally
        {
            _synchronizing = false;
        }
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _ = PlainTextBox.Focus();
                PlainTextBox.CaretIndex = Math.Clamp(caret, 0, PlainTextBox.Text.Length);
            }),
            DispatcherPriority.Input);
    }

    private void SetExpressionMode(bool expressionMode)
    {
        _expressionMode = expressionMode;
        PlainTextBox.Visibility = expressionMode ? Visibility.Collapsed : Visibility.Visible;
        ExpressionChrome.Visibility = expressionMode ? Visibility.Visible : Visibility.Collapsed;
        if (!expressionMode) CloseCompletion();
    }

    private void OnExpressionPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.Length == 1 && TryHandlePairInput(e.Text[0]))
        {
            e.Handled = true;
            return;
        }

        _refreshCompletionAfterChange = e.Text.Any(value =>
            char.IsLetterOrDigit(value) || value is '_' or '.');
        if (!_refreshCompletionAfterChange) CloseCompletion();
    }

    private void OnExpressionPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.Space)
        {
            ShowCompletion();
            e.Handled = true;
            return;
        }

        if (CompletionPopup.IsOpen)
        {
            if (key == Key.Down)
            {
                MoveCompletionSelection(1);
                e.Handled = true;
                return;
            }
            if (key == Key.Up)
            {
                MoveCompletionSelection(-1);
                e.Handled = true;
                return;
            }
            if (key == Key.F1)
            {
                CycleSignature();
                e.Handled = true;
                return;
            }
            if (key is Key.Tab or Key.Enter)
            {
                CommitCompletion();
                e.Handled = true;
                return;
            }
            if (key == Key.Escape)
            {
                CloseCompletion();
                e.Handled = true;
                return;
            }
        }

        if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            CommitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            _ = MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _refreshCompletionAfterChange = CompletionPopup.IsOpen;
        }
    }

    private bool TryHandlePairInput(char value)
    {
        (char Open, char Close) pair = value switch
        {
            '(' => ('(', ')'),
            '[' => ('[', ']'),
            '{' => ('{', '}'),
            ')' => ('\0', ')'),
            ']' => ('\0', ']'),
            '}' => ('\0', '}'),
            _ => ('\0', '\0')
        };
        if (pair.Close == '\0') return false;

        int caret = ExpressionEditor.CaretOffset;
        string text = ExpressionEditor.Text;
        if (pair.Open == '\0')
        {
            if (ExpressionEditor.SelectionLength == 0
                && caret < text.Length
                && text[caret] == pair.Close)
            {
                SetCaret(caret + 1);
                return true;
            }
            return false;
        }

        int start = ExpressionEditor.SelectionLength > 0
            ? ExpressionEditor.SelectionStart
            : caret;
        string selected = ExpressionEditor.SelectionLength > 0
            ? ExpressionEditor.SelectedText
            : string.Empty;
        string insert = pair.Open + selected + pair.Close;
        ExpressionEditor.Document.Replace(start, ExpressionEditor.SelectionLength, insert);
        SetCaret(start + 1 + selected.Length);
        CloseCompletion();
        return true;
    }

    private void ShowCompletion()
    {
        if (!_expressionMode || !ExpressionEditor.IsKeyboardFocusWithin)
        {
            CloseCompletion();
            return;
        }
        IReadOnlyList<BatchExpressionCompletionItem> items =
            BatchExpressionCompletionProvider.GetCompletions(
                ExpressionEditor.Text,
                ExpressionEditor.CaretOffset,
                _variables);
        if (items.Count == 0)
        {
            CloseCompletion();
            return;
        }

        IReadOnlyList<BatchExpressionCompletionItem> snapshot =
            BatchExpressionCompletionSnapshot.Create(items);
        _completionItems = snapshot;
        CompletionList.ItemsSource = snapshot;
        CompletionList.SelectedIndex = 0;
        _signatureIndex = 0;
        UpdateSignaturePanel();
        PositionCompletionPopup();
        CompletionPopup.IsOpen = true;
    }

    private void PositionCompletionPopup()
    {
        try
        {
            TextLocation location = ExpressionEditor.Document.GetLocation(ExpressionEditor.CaretOffset);
            TextViewPosition viewPosition = new(location);
            Point visual = ExpressionEditor.TextArea.TextView.GetVisualPosition(
                viewPosition,
                VisualYPosition.LineBottom);
            Point insideView = visual - ExpressionEditor.TextArea.TextView.ScrollOffset;
            Point relative = ExpressionEditor.TextArea.TextView.TranslatePoint(
                insideView,
                ExpressionEditor);
            CompletionPopup.HorizontalOffset = Math.Max(0, relative.X);
            CompletionPopup.VerticalOffset = Math.Max(0, relative.Y + 2);
        }
        catch
        {
            CompletionPopup.HorizontalOffset = 8;
            CompletionPopup.VerticalOffset = 32;
        }
    }

    private BatchExpressionCompletionItem? CurrentCompletion =>
        CompletionList.SelectedItem as BatchExpressionCompletionItem;

    private void MoveCompletionSelection(int delta)
    {
        if (_completionItems.Count == 0) return;
        CompletionList.SelectedIndex = Math.Clamp(
            CompletionList.SelectedIndex + delta,
            0,
            _completionItems.Count - 1);
        CompletionList.ScrollIntoView(CompletionList.SelectedItem);
    }

    private void CommitCompletion()
    {
        if (CurrentCompletion is not BatchExpressionCompletionItem item) return;
        int caret = ExpressionEditor.CaretOffset;
        int start = caret;
        string text = ExpressionEditor.Text;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        CloseCompletion();
        ExpressionEditor.Document.Replace(start, caret - start, item.InsertText);
        SetCaret(start + item.InsertText.Length - item.CaretBacktrack);
        _ = ExpressionEditor.Focus();
    }

    private void OnCompletionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _signatureIndex = 0;
        UpdateSignaturePanel();
    }

    private void OnCompletionListPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ListBoxWheelScroll.ScrollOneItemPerNotch(CompletionList, e);

    private void OnCompletionMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(CompletionList, source) is not ListBoxItem item)
        {
            return;
        }

        CompletionList.SelectedItem = item.DataContext;
        CommitCompletion();
        e.Handled = true;
    }

    private void CycleSignature()
    {
        if (CurrentCompletion?.Signatures is not { Count: > 1 } signatures) return;
        _signatureIndex = (_signatureIndex + 1) % signatures.Count;
        UpdateSignaturePanel();
    }

    private void UpdateSignaturePanel()
    {
        if (CurrentCompletion is not BatchExpressionCompletionItem item)
        {
            SignatureText.Text = string.Empty;
            SignaturePanel.Visibility = Visibility.Collapsed;
            return;
        }
        IReadOnlyList<string> signatures = item.Signatures is { Count: > 0 }
            ? item.Signatures
            : [item.Description];
        _signatureIndex = Math.Clamp(_signatureIndex, 0, signatures.Count - 1);
        string suffix = signatures.Count > 1
            ? $"  ({_signatureIndex + 1}/{signatures.Count}, F1)"
            : string.Empty;
        SignatureText.Text = signatures[_signatureIndex] + suffix;
        SignaturePanel.Visibility = Visibility.Visible;
    }

    private void CloseCompletion()
    {
        CompletionPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        _completionItems = [];
        SignatureText.Text = string.Empty;
        SignaturePanel.Visibility = Visibility.Collapsed;
        _signatureIndex = 0;
    }

    private void OnExpressionLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CloseCompletion();
    }

    private void SetCaret(int offset)
    {
        ExpressionEditor.CaretOffset = Math.Clamp(offset, 0, ExpressionEditor.Text.Length);
        ExpressionEditor.Select(ExpressionEditor.CaretOffset, 0);
        UpdateBracketMatch();
    }

    private void UpdateBracketMatch()
    {
        _bracketRenderer.Match = _expressionMode
                               && BatchBracketMatcher.TryFind(
                                   ExpressionEditor.Text,
                                   ExpressionEditor.CaretOffset,
                                   out BatchBracketMatch match)
            ? match
            : null;
        ExpressionEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private static string Sanitize(string? text) => SingleLineCodeEditorInput.Normalize(text);

    private sealed partial class BatchExpressionColorizer(
        Func<IReadOnlyList<BatchExpressionVariable>> variables)
        : DocumentColorizingTransformer
    {
        private static readonly Brush KeywordBrush = FrozenBrush(CodeEditorDarkPalette.Keyword);
        private static readonly Brush VariableBrush = FrozenBrush(CodeEditorDarkPalette.Variable);
        private static readonly Brush TypeBrush = FrozenBrush(CodeEditorDarkPalette.Type);
        private static readonly Brush MethodBrush = FrozenBrush(CodeEditorDarkPalette.Method);
        private static readonly Brush NumberBrush = FrozenBrush(CodeEditorDarkPalette.Number);
        private static readonly Brush OperatorBrush = FrozenBrush(CodeEditorDarkPalette.Operator);

        [GeneratedRegex(@"\b(?:\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|[A-Za-z_][A-Za-z0-9_]*)\b")]
        private static partial Regex TokenRegex();

        protected override void ColorizeLine(DocumentLine line)
        {
            string document = CurrentContext.Document.Text;
            if (!BatchExpressionCompletionProvider.IsExpression(document)) return;
            string lineText = CurrentContext.Document.GetText(line);
            int first = 0;
            while (first < lineText.Length && char.IsWhiteSpace(lineText[first])) first++;
            if (first < lineText.Length && lineText[first] == '=')
            {
                ChangeLinePart(
                    line.Offset + first,
                    line.Offset + first + 1,
                    element => element.TextRunProperties.SetForegroundBrush(OperatorBrush));
            }

            HashSet<string> variableNames = variables()
                .Select(variable => variable.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (Match match in TokenRegex().Matches(lineText))
            {
                string token = match.Value;
                Brush? brush = token switch
                {
                    "true" or "false" or "double" => KeywordBrush,
                    "Math" => TypeBrush,
                    "PI" or "E" => NumberBrush,
                    _ when variableNames.Contains(token) => VariableBrush,
                    _ when BatchExpressionCompletionProvider.IsMathMethod(token) => MethodBrush,
                    _ when char.IsDigit(token[0]) => NumberBrush,
                    _ => null
                };
                if (brush is null) continue;
                ChangeLinePart(
                    line.Offset + match.Index,
                    line.Offset + match.Index + match.Length,
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    private sealed class BatchBracketRenderer : IBackgroundRenderer
    {
        private static readonly Brush MatchFill = FrozenBrush("#303E50");
        private static readonly Pen MatchPen = FrozenPen("#6FA7D8");
        private static readonly Brush ErrorFill = FrozenBrush("#42191D");
        private static readonly Pen ErrorPen = FrozenPen("#E5484D");

        public BatchBracketMatch? Match { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Match is not BatchBracketMatch match || !textView.VisualLinesValid) return;
            DrawBracket(textView, drawingContext, match.BracketOffset, match.IsMatched);
            if (match.IsMatched)
            {
                DrawBracket(textView, drawingContext, match.MatchingOffset, true);
            }
        }

        private static void DrawBracket(
            TextView textView,
            DrawingContext context,
            int offset,
            bool matched)
        {
            TextSegment segment = new() { StartOffset = offset, Length = 1 };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                Rect marker = new(rect.X, rect.Y + 1, Math.Max(1, rect.Width), Math.Max(1, rect.Height - 2));
                context.DrawRoundedRectangle(
                    matched ? MatchFill : ErrorFill,
                    matched ? MatchPen : ErrorPen,
                    marker,
                    2,
                    2);
            }
        }
    }

    private static Brush FrozenBrush(string value)
    {
        Brush brush = (Brush)new BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string value)
    {
        Pen pen = new(FrozenBrush(value), 1);
        pen.Freeze();
        return pen;
    }
}
