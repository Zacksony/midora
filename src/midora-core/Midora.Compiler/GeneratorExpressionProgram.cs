namespace Midora.Compiler;

/// <summary>Compile-once, allocation-free per-candidate tool expression DAG.
/// Unlike Batch Edit, old values belong to the previous normalized candidate.</summary>
public sealed class GeneratorExpressionProgram : IDisposable
{
    private readonly Dictionary<BatchEditField, CompiledNumericExpression?> _expressions;
    private readonly BatchEditField[] _order;
    private readonly double[] _slots;
    private bool _disposed;

    private GeneratorExpressionProgram(NumericExpressionProfile profile,
        Dictionary<BatchEditField, CompiledNumericExpression?> expressions,
        IReadOnlyList<BatchEditField> order)
        => (Profile, _expressions, _order, _slots) = (profile, expressions, order.ToArray(), new double[profile.VariableCount]);

    public NumericExpressionProfile Profile { get; }

    public static GeneratorExpressionProgram Compile(IReadOnlyDictionary<BatchEditField, string?> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var keys = expressions.Keys.ToHashSet();
        NumericExpressionProfile profile = keys.SetEquals([BatchEditField.PointValue, BatchEditField.Tick])
            ? NumericExpressionProfiles.GenerateEvent
            : keys.SetEquals([BatchEditField.Velocity, BatchEditField.KeyNumber, BatchEditField.Gate, BatchEditField.Tick])
                ? NumericExpressionProfiles.GenerateNote
                : throw new ArgumentException("Generator fields must be Velocity/Key/Gate/Tick or Point Value/Tick.", nameof(expressions));
        Dictionary<BatchEditField, CompiledNumericExpression?> compiled = [];
        Dictionary<BatchEditField, HashSet<BatchEditField>> dependencies = [];
        try
        {
            foreach (BatchEditField field in keys.Order())
            {
                string value = expressions[field]?.Trim() ?? string.Empty;
                if (value.Length == 0) { compiled.Add(field, null); dependencies.Add(field, []); continue; }
                if (value[0] != '=' || string.IsNullOrWhiteSpace(value[1..]))
                    throw new ArgumentException($"The {field} expression must start with '=' followed by an expression.");
                var expression = BoundedNumericExpressionCompiler.Compile(profile, value[1..].Trim());
                compiled.Add(field, expression);
                dependencies.Add(field, expression.ReferencedVariables.Select(ResultField)
                    .Where(static value => value.HasValue).Select(static value => value!.Value).ToHashSet());
            }
            var order = ResultDependencyGraph.Sort(keys, field => dependencies[field], Comparer<BatchEditField>.Default);
            return new(profile, compiled, order);
        }
        catch { foreach (var expression in compiled.Values) expression?.Dispose(); throw; }
    }

    public BatchEditValues Evaluate(BatchEditValues source, int iteration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(iteration);
        var current = source;
        bool note = ReferenceEquals(Profile, NumericExpressionProfiles.GenerateNote);
        if (note)
        {
            _slots[0] = _slots[1] = source.Velocity;
            _slots[2] = _slots[3] = source.KeyNumber;
            _slots[4] = _slots[5] = source.Gate;
            _slots[6] = _slots[7] = _slots[8] = source.Tick;
            _slots[9] = iteration;
        }
        else
        {
            _slots[0] = _slots[1] = source.PointValue;
            _slots[2] = _slots[3] = _slots[4] = source.Tick;
            _slots[5] = iteration;
        }
        foreach (BatchEditField field in _order)
        {
            if (_expressions[field] is not { } expression) continue;
            double result = expression.Evaluate(_slots);
            int resultSlot = field switch
            {
                BatchEditField.Velocity or BatchEditField.PointValue => 1,
                BatchEditField.KeyNumber => 3, BatchEditField.Gate => 5,
                BatchEditField.Tick => note ? 7 : 3,
                _ => throw new InvalidOperationException("Unknown generator field.")
            };
            _slots[resultSlot] = result;
            current = field switch
            {
                BatchEditField.Velocity => current with { Velocity = result },
                BatchEditField.PointValue => current with { PointValue = result },
                BatchEditField.KeyNumber => current with { KeyNumber = result },
                BatchEditField.Gate => current with { Gate = result },
                BatchEditField.Tick => current with { Tick = result },
                _ => throw new InvalidOperationException("Unknown generator field.")
            };
        }
        if (!double.IsFinite(current.Tick)
            || (note && (!double.IsFinite(current.Velocity) || !double.IsFinite(current.KeyNumber) || !double.IsFinite(current.Gate)))
            || (!note && !double.IsFinite(current.PointValue)))
            throw new InvalidOperationException("The generator returned a non-finite field value.");
        return current;
    }

    private static BatchEditField? ResultField(string name) => name switch
    {
        "v1" => BatchEditField.Velocity, "p1" => BatchEditField.PointValue,
        "k1" => BatchEditField.KeyNumber, "g1" => BatchEditField.Gate,
        "t1" => BatchEditField.Tick, _ => null
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var expression in _expressions.Values) expression?.Dispose();
        Array.Clear(_slots);
    }
}
