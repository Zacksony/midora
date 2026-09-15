using Midora.Compiler;

namespace Midora.Desktop;

internal sealed record BatchExpressionVariable(string Name, string Description);

internal sealed record BatchExpressionCompletionItem(
    string Text,
    string InsertText,
    int CaretBacktrack,
    string Kind,
    string Description,
    IReadOnlyList<string>? Signatures = null);

internal static class BatchExpressionCompletionProvider
{
    private static readonly IReadOnlyList<BatchExpressionCompletionItem> MathMembers =
        CreateMathMembers();

    public static IReadOnlyList<BatchExpressionVariable> CreateVariables(
        BatchEditPresetKind presetKind,
        BatchEditField currentField)
    {
        (BatchEditField Field, string Source, string Result, string Label)[] fields =
            presetKind == BatchEditPresetKind.Note
                ?
                [
                    (BatchEditField.Velocity, "v0", "v1", "Velocity"),
                    (BatchEditField.KeyNumber, "k0", "k1", "Key Number"),
                    (BatchEditField.Gate, "g0", "g1", "Gate"),
                    (BatchEditField.Tick, "t0", "t1", "Tick")
                ]
                :
                [
                    (BatchEditField.PointValue, "p0", "p1", "Point Value"),
                    (BatchEditField.Tick, "t0", "t1", "Tick")
                ];

        List<BatchExpressionVariable> result = [];
        foreach ((BatchEditField field, string source, string calculated, string label) in fields)
        {
            result.Add(new(source, $"{label} before calculation (double)."));
            if (field != currentField)
            {
                result.Add(new(calculated, $"{label} after calculation (double)."));
            }
        }
        result.Add(new("tr", "Tick relative to the earliest selected object before calculation (double)."));
        return result;
    }

    public static IReadOnlyList<BatchExpressionVariable> CreateVariables(
        NumericExpressionProfile profile,
        string? excludedVariable = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Variables
            .Where(variable => !string.Equals(variable.Name, excludedVariable, StringComparison.Ordinal))
            .Select(variable => new BatchExpressionVariable(
                variable.Name,
                DescribeProfileVariable(profile, variable.Name)))
            .ToArray();
    }

    public static IReadOnlyList<BatchExpressionCompletionItem> GetCompletions(
        string text,
        int caretOffset,
        IReadOnlyList<BatchExpressionVariable> variables)
    {
        text ??= string.Empty;
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        if (!IsExpression(text)) return [];

        int prefixStart = caretOffset;
        while (prefixStart > 0 && IsIdentifierPart(text[prefixStart - 1])) prefixStart--;
        string prefix = text[prefixStart..caretOffset];
        bool mathMemberAccess = TryGetMemberReceiver(text, prefixStart, out string receiver)
                                && string.Equals(receiver, "Math", StringComparison.Ordinal);

        IEnumerable<BatchExpressionCompletionItem> candidates = mathMemberAccess
            ? MathMembers
            : CreateGlobalItems(variables);
        return candidates
            .Where(item => item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => Priority(item.Kind))
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsExpression(string? text) =>
        (text ?? string.Empty).TrimStart().StartsWith('=');

    public static bool IsMathMethod(string token) =>
        MathMembers.Any(item => item.Kind == "Method"
                               && string.Equals(item.Text, token, StringComparison.Ordinal));

    private static IEnumerable<BatchExpressionCompletionItem> CreateGlobalItems(
        IReadOnlyList<BatchExpressionVariable> variables)
    {
        foreach (BatchExpressionVariable variable in variables)
        {
            yield return new(
                variable.Name,
                variable.Name,
                0,
                "Variable",
                variable.Description);
        }

        yield return new("Math", "Math", 0, "Type", "System.Math static methods and constants.");
        yield return new("PI", "PI", 0, "Constant", "System.Math.PI.");
        yield return new("E", "E", 0, "Constant", "System.Math.E.");
        yield return new("Tau", "Tau", 0, "Constant", "System.Math.Tau.");
        yield return new("true", "true", 0, "Keyword", "Boolean true literal.");
        yield return new("false", "false", 0, "Keyword", "Boolean false literal.");
        yield return new("double", "double", 0, "Keyword", "C# double type; only double casts are supported.");

        foreach (BatchExpressionCompletionItem method in MathMembers)
        {
            yield return method;
        }
    }

    private static IReadOnlyList<BatchExpressionCompletionItem> CreateMathMembers()
    {
        return BoundedNumericExpressionCompiler.MathFunctions
            .Select(function =>
            {
                string[] signatures = function.Signatures
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new BatchExpressionCompletionItem(
                    function.Name,
                    function.Name + "()",
                    1,
                    "Method",
                    signatures.Length == 1
                        ? "System.Math method."
                        : $"System.Math method ({signatures.Length} overloads).",
                    signatures);
            })
            .Append(new("PI", "PI", 0, "Constant", "System.Math.PI."))
            .Append(new("E", "E", 0, "Constant", "System.Math.E."))
            .Append(new("Tau", "Tau", 0, "Constant", "System.Math.Tau."))
            .OrderBy(item => item.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static string DescribeProfileVariable(NumericExpressionProfile profile, string name)
    {
        if (profile.Id == NumericExpressionProfiles.GenerateNote.Id || profile.Id == NumericExpressionProfiles.GenerateEvent.Id)
        {
            return name switch
            {
                "i" => "Zero-based candidate index; the optional Initial object is candidate 0 (double).",
                "tr" => "Input t0: previous normalized relative Tick, or normalized Initial Tick on the first iteration (double).",
                "v0" => "Previous normalized Velocity (first iteration: Initial Velocity).",
                "k0" => "Previous normalized Key (first iteration: Initial Key).",
                "g0" => "Previous normalized Gate (first iteration: Initial Gate).",
                "p0" => "Previous normalized Value (first iteration: Initial Value).",
                "t0" => "Previous normalized Tick relative to Base Tick (first iteration: Initial Tick).",
                "v1" => "Current Velocity result before final normalization; dependency cycles are rejected.",
                "k1" => "Current Key result before final normalization; dependency cycles are rejected.",
                "g1" => "Current Gate result before final normalization; dependency cycles are rejected.",
                "p1" => "Current Value result before final normalization; dependency cycles are rejected.",
                "t1" => "Current relative Tick result before final normalization; dependency cycles are rejected.",
                _ => $"Numeric expression variable {name} (double)."
            };
        }
        return name switch
        {
            "i" => "Zero-based operation iteration index (double).",
            "tr" => "Previous cut position relative to the selection origin (double).",
            _ => $"Numeric expression variable {name} (double)."
        };
    }

    private static bool TryGetMemberReceiver(string text, int prefixStart, out string receiver)
    {
        receiver = string.Empty;
        int dot = prefixStart - 1;
        if (dot < 0 || text[dot] != '.') return false;
        int end = dot;
        int start = end;
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        if (start == end) return false;
        receiver = text[start..end];
        return true;
    }

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static int Priority(string kind) => kind switch
    {
        "Variable" => 0,
        "Method" => 1,
        "Constant" => 2,
        "Type" => 3,
        "Keyword" => 4,
        _ => 5
    };
}

internal readonly record struct BatchBracketMatch(int BracketOffset, int MatchingOffset, bool IsMatched);

internal static class BatchExpressionCompletionSnapshot
{
    public static IReadOnlyList<BatchExpressionCompletionItem> Create(
        IReadOnlyList<BatchExpressionCompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count == 0 ? [] : items.ToArray();
    }
}

internal static class BatchBracketMatcher
{
    public static bool TryFind(string text, int caretOffset, out BatchBracketMatch match)
    {
        text ??= string.Empty;
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        int bracketOffset = IsBracketAt(text, caretOffset - 1)
            ? caretOffset - 1
            : IsBracketAt(text, caretOffset)
                ? caretOffset
                : -1;
        if (bracketOffset < 0)
        {
            match = default;
            return false;
        }

        char bracket = text[bracketOffset];
        bool forward = bracket is '(' or '[' or '{';
        char counterpart = bracket switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            ')' => '(',
            ']' => '[',
            '}' => '{',
            _ => throw new InvalidOperationException()
        };
        int depth = 0;
        for (int i = bracketOffset; forward ? i < text.Length : i >= 0; i += forward ? 1 : -1)
        {
            char current = text[i];
            if (current == bracket) depth++;
            else if (current == counterpart)
            {
                depth--;
                if (depth == 0)
                {
                    match = new(bracketOffset, i, true);
                    return true;
                }
            }
        }

        match = new(bracketOffset, -1, false);
        return true;
    }

    private static bool IsBracketAt(string text, int offset) =>
        (uint)offset < (uint)text.Length && text[offset] is '(' or ')' or '[' or ']' or '{' or '}';
}
