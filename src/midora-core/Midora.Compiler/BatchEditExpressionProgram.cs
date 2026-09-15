using System.Diagnostics;
using System.Globalization;

namespace Midora.Compiler;

public enum BatchEditField
{
    Velocity,
    PointValue,
    KeyNumber,
    Gate,
    Tick
}

public readonly record struct BatchEditValues(
    double Velocity,
    double PointValue,
    double KeyNumber,
    double Gate,
    double Tick,
    double RelativeTick);

public enum BatchEditFormulaKind
{
    Identity,
    DirectValue,
    Percentage,
    Multiply,
    Divide,
    Add,
    Subtract,
    BoundedExpression
}

/// <summary>
/// Adapts Batch Edit's direct values and one-step operators to the shared,
/// versioned bounded-numeric-expression compiler. Instances are deliberately
/// single-consumer: an edit command evaluates one program sequentially and
/// reuses the same slot buffer for every selected object.
/// </summary>
public sealed class BatchEditExpressionProgram : IDisposable
{
    private const string ResultVariableList = "v1,p1,k1,g1,t1";
    private static readonly IReadOnlyDictionary<BatchEditField, string> OldVariables =
        new Dictionary<BatchEditField, string>
        {
            [BatchEditField.Velocity] = "v0",
            [BatchEditField.PointValue] = "p0",
            [BatchEditField.KeyNumber] = "k0",
            [BatchEditField.Gate] = "g0",
            [BatchEditField.Tick] = "t0"
        };
    private static readonly IReadOnlyDictionary<string, BatchEditField> OldFields =
        OldVariables.ToDictionary(static value => value.Value, static value => value.Key, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<BatchEditField, string> ResultVariablesByField =
        new Dictionary<BatchEditField, string>
        {
            [BatchEditField.Velocity] = "v1",
            [BatchEditField.PointValue] = "p1",
            [BatchEditField.KeyNumber] = "k1",
            [BatchEditField.Gate] = "g1",
            [BatchEditField.Tick] = "t1"
        };
    private static readonly IReadOnlyDictionary<string, BatchEditField> ResultFields =
        ResultVariablesByField.ToDictionary(static value => value.Value, static value => value.Key, StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<BatchEditField, CompiledFormula> _formulas;
    private readonly IReadOnlyList<BatchEditField> _evaluationOrder;
    private readonly NumericExpressionProfile _profile;
    private readonly double[] _slots;
    private bool _disposed;

    private BatchEditExpressionProgram(
        IReadOnlyDictionary<BatchEditField, CompiledFormula> formulas,
        IReadOnlyList<BatchEditField> evaluationOrder,
        NumericExpressionProfile profile)
    {
        _formulas = formulas;
        _evaluationOrder = evaluationOrder;
        _profile = profile;
        _slots = new double[profile.VariableCount];
    }

    public IReadOnlyCollection<BatchEditField> Fields => _formulas.Keys.ToArray();

    public static BatchEditExpressionProgram Compile(
        IReadOnlyDictionary<BatchEditField, string?> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Count == 0)
            throw new ArgumentException("At least one batch-edit field is required.", nameof(expressions));
        if (expressions.Keys.Any(static field => !Enum.IsDefined(field)))
            throw new ArgumentOutOfRangeException(nameof(expressions));

        HashSet<BatchEditField> availableFields = expressions.Keys.ToHashSet();
        NumericExpressionProfile profile = ResolveProfile(availableFields);
        Dictionary<BatchEditField, CompiledFormula> formulas = [];
        try
        {
            foreach ((BatchEditField field, string? text) in expressions)
                formulas.Add(field, CompileFormula(field, text ?? string.Empty, availableFields, profile));
            IReadOnlyList<BatchEditField> order = ResultDependencyGraph.Sort(
                formulas.Keys,
                field => formulas[field].Dependencies,
                Comparer<BatchEditField>.Default);
            return new(formulas, order, profile);
        }
        catch
        {
            foreach (CompiledFormula formula in formulas.Values) formula.Dispose();
            throw;
        }
    }

    public BatchEditFormulaKind GetFormulaKind(BatchEditField field) => GetFormula(field).Kind;

    public double? GetConstant(BatchEditField field) => GetFormula(field).Constant;

    public BatchEditValues Evaluate(
        BatchEditValues source,
        Stopwatch timeoutClock,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(timeoutClock);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        BatchEditValues current = source;
        foreach (BatchEditField field in _evaluationOrder)
        {
            if (timeoutClock.Elapsed >= timeout)
                throw new TimeoutException($"Batch edit evaluation exceeded {timeout.TotalSeconds:0.###} seconds.");
            PopulateSlots(source, current);
            double result = _formulas[field].Evaluate(Read(source, field), _slots);
            if (!double.IsFinite(result))
                throw new InvalidOperationException($"The {field} expression returned a non-finite value.");
            current = Write(current, field, result);
        }
        return current;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (CompiledFormula formula in _formulas.Values) formula.Dispose();
        Array.Clear(_slots);
    }

    private static NumericExpressionProfile ResolveProfile(IReadOnlySet<BatchEditField> fields)
    {
        bool eventProfile = fields.SetEquals([BatchEditField.PointValue, BatchEditField.Tick]);
        bool noteProfile = fields.SetEquals(
            [BatchEditField.Velocity, BatchEditField.KeyNumber, BatchEditField.Gate, BatchEditField.Tick]);
        if (eventProfile) return NumericExpressionProfiles.BatchEvent;
        if (noteProfile) return NumericExpressionProfiles.BatchNote;
        throw new ArgumentException(
            "Batch Edit fields must be either Velocity/Key Number/Gate/Tick or Point Value/Tick.",
            nameof(fields));
    }

    private CompiledFormula GetFormula(BatchEditField field) =>
        _formulas.TryGetValue(field, out CompiledFormula? formula)
            ? formula
            : throw new ArgumentOutOfRangeException(nameof(field));

    private static CompiledFormula CompileFormula(
        BatchEditField field,
        string text,
        IReadOnlySet<BatchEditField> availableFields,
        NumericExpressionProfile profile)
    {
        string value = text.Trim();
        if (value.Length == 0) return CompiledFormula.Identity();
        if (value[0] == '=')
        {
            string expressionText = value[1..].Trim();
            if (expressionText.Length == 0)
                throw new ArgumentException($"The {field} expression is empty.");
            CompiledNumericExpression expression;
            try
            {
                expression = BoundedNumericExpressionCompiler.Compile(profile, expressionText);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"The {field} expression cannot compile: {exception.Message}", exception);
            }

            BatchEditField[] unavailableOldFields = expression.ReferencedVariables
                .Where(OldFields.ContainsKey)
                .Select(name => OldFields[name])
                .Where(candidate => !availableFields.Contains(candidate))
                .Distinct()
                .Order()
                .ToArray();
            if (unavailableOldFields.Length != 0)
            {
                expression.Dispose();
                throw new ArgumentException(
                    $"The {field} expression references unavailable source field(s): {string.Join(", ", unavailableOldFields)}.");
            }

            HashSet<BatchEditField> dependencies = expression.ReferencedVariables
                .Where(ResultFields.ContainsKey)
                .Select(name => ResultFields[name])
                .ToHashSet();
            if (dependencies.Contains(field))
            {
                expression.Dispose();
                throw new ArgumentException(
                    $"The {field} expression cannot reference its own result variable. Result variables are {ResultVariableList}.");
            }
            return CompiledFormula.Expression(dependencies, expression);
        }

        BatchEditFormulaKind kind;
        string numberText;
        if (value.EndsWith('%'))
        {
            kind = BatchEditFormulaKind.Percentage;
            numberText = value[..^1].Trim();
        }
        else if (value.Length > 1 && value[0] is '*' or '/' or '+' or '-')
        {
            kind = value[0] switch
            {
                '*' => BatchEditFormulaKind.Multiply,
                '/' => BatchEditFormulaKind.Divide,
                '+' => BatchEditFormulaKind.Add,
                '-' => BatchEditFormulaKind.Subtract,
                _ => throw new UnreachableException()
            };
            numberText = value[1..].Trim();
        }
        else
        {
            kind = BatchEditFormulaKind.DirectValue;
            numberText = value;
        }
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out double constant)
            || !double.IsFinite(constant))
        {
            throw new ArgumentException($"The {field} value must use invariant decimal notation.");
        }
        if (kind != BatchEditFormulaKind.DirectValue && constant < 0)
            throw new ArgumentException($"The {field} single-step operand must be non-negative.");
        if (kind == BatchEditFormulaKind.Divide && constant == 0)
            throw new ArgumentException($"The {field} divisor cannot be zero.");
        return CompiledFormula.CreateConstant(kind, constant);
    }

    private void PopulateSlots(BatchEditValues source, BatchEditValues current)
    {
        foreach (NumericVariableDefinition variable in _profile.Variables)
        {
            _slots[variable.Slot] = variable.Name switch
            {
                "v0" => source.Velocity,
                "v1" => current.Velocity,
                "p0" => source.PointValue,
                "p1" => current.PointValue,
                "k0" => source.KeyNumber,
                "k1" => current.KeyNumber,
                "g0" => source.Gate,
                "g1" => current.Gate,
                "t0" => source.Tick,
                "t1" => current.Tick,
                "tr" => source.RelativeTick,
                _ => throw new UnreachableException()
            };
        }
    }

    private static double Read(BatchEditValues values, BatchEditField field) => field switch
    {
        BatchEditField.Velocity => values.Velocity,
        BatchEditField.PointValue => values.PointValue,
        BatchEditField.KeyNumber => values.KeyNumber,
        BatchEditField.Gate => values.Gate,
        BatchEditField.Tick => values.Tick,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static BatchEditValues Write(BatchEditValues values, BatchEditField field, double value) => field switch
    {
        BatchEditField.Velocity => values with { Velocity = value },
        BatchEditField.PointValue => values with { PointValue = value },
        BatchEditField.KeyNumber => values with { KeyNumber = value },
        BatchEditField.Gate => values with { Gate = value },
        BatchEditField.Tick => values with { Tick = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private sealed class CompiledFormula : IDisposable
    {
        private readonly CompiledNumericExpression? _expression;

        private CompiledFormula(
            BatchEditFormulaKind kind,
            double? constant,
            IReadOnlySet<BatchEditField>? dependencies = null,
            CompiledNumericExpression? expression = null)
        {
            Kind = kind;
            Constant = constant;
            Dependencies = dependencies ?? new HashSet<BatchEditField>();
            _expression = expression;
        }

        public BatchEditFormulaKind Kind { get; }
        public double? Constant { get; }
        public IReadOnlySet<BatchEditField> Dependencies { get; }

        public static CompiledFormula Identity() => new(BatchEditFormulaKind.Identity, null);
        public static CompiledFormula CreateConstant(BatchEditFormulaKind kind, double value) => new(kind, value);
        public static CompiledFormula Expression(
            IReadOnlySet<BatchEditField> dependencies,
            CompiledNumericExpression expression) =>
            new(BatchEditFormulaKind.BoundedExpression, null, dependencies, expression);

        public double Evaluate(double oldValue, double[] slots) => Kind switch
        {
            BatchEditFormulaKind.Identity => oldValue,
            BatchEditFormulaKind.DirectValue => Constant!.Value,
            BatchEditFormulaKind.Percentage => oldValue * Constant!.Value / 100,
            BatchEditFormulaKind.Multiply => oldValue * Constant!.Value,
            BatchEditFormulaKind.Divide => oldValue / Constant!.Value,
            BatchEditFormulaKind.Add => oldValue + Constant!.Value,
            BatchEditFormulaKind.Subtract => oldValue - Constant!.Value,
            BatchEditFormulaKind.BoundedExpression => _expression!.Evaluate(slots),
            _ => throw new UnreachableException()
        };

        public void Dispose() => _expression?.Dispose();
    }
}
