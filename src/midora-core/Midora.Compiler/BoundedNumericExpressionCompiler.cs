using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Midora.Compiler;

public sealed record NumericVariableDefinition(string Name, int Slot)
{
    public NumericVariableDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("A numeric expression variable name is required.", nameof(Name));
        if (!SyntaxFacts.IsValidIdentifier(Name))
            throw new ArgumentException($"'{Name}' is not a valid numeric expression variable name.", nameof(Name));
        ArgumentOutOfRangeException.ThrowIfNegative(Slot);
        return this;
    }
}

public sealed class NumericExpressionProfile
{
    private readonly IReadOnlyDictionary<string, NumericVariableDefinition> _variables;

    public NumericExpressionProfile(
        string id,
        int version,
        IEnumerable<NumericVariableDefinition> variables,
        int maximumScalarCount = 8192,
        int maximumSyntaxNodeCount = 512,
        int maximumSyntaxDepth = 64)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A numeric expression profile ID is required.", nameof(id));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        if (maximumScalarCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumScalarCount));
        if (maximumSyntaxNodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSyntaxNodeCount));
        if (maximumSyntaxDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSyntaxDepth));

        NumericVariableDefinition[] materialized = (variables ?? throw new ArgumentNullException(nameof(variables)))
            .Select(static value => value.Validate())
            .ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("A numeric expression profile requires at least one variable.", nameof(variables));
        if (materialized.Select(static value => value.Name).Distinct(StringComparer.Ordinal).Count()
            != materialized.Length)
        {
            throw new ArgumentException("Numeric expression variable names must be unique.", nameof(variables));
        }
        if (materialized.Select(static value => value.Slot).Distinct().Count() != materialized.Length
            || materialized.Min(static value => value.Slot) != 0
            || materialized.Max(static value => value.Slot) != materialized.Length - 1)
        {
            throw new ArgumentException("Numeric expression variable slots must be contiguous and unique.", nameof(variables));
        }

        Id = id.Trim();
        Version = version;
        MaximumScalarCount = maximumScalarCount;
        MaximumSyntaxNodeCount = maximumSyntaxNodeCount;
        MaximumSyntaxDepth = maximumSyntaxDepth;
        Variables = Array.AsReadOnly(materialized.OrderBy(static value => value.Slot).ToArray());
        _variables = Variables.ToDictionary(static value => value.Name, StringComparer.Ordinal);
    }

    public string Id { get; }
    public int Version { get; }
    public int MaximumScalarCount { get; }
    public int MaximumSyntaxNodeCount { get; }
    public int MaximumSyntaxDepth { get; }
    public IReadOnlyList<NumericVariableDefinition> Variables { get; }
    public int VariableCount => Variables.Count;

    internal bool TryGetVariable(string name, out NumericVariableDefinition variable) =>
        _variables.TryGetValue(name, out variable!);
}

public static class NumericExpressionProfiles
{
    private static readonly NumericVariableDefinition[] BatchNoteVariables =
    [
        new("v0", 0), new("v1", 1),
        new("k0", 2), new("k1", 3),
        new("g0", 4), new("g1", 5),
        new("t0", 6), new("t1", 7),
        new("tr", 8)
    ];

    private static readonly NumericVariableDefinition[] BatchEventVariables =
    [
        new("p0", 0), new("p1", 1),
        new("t0", 2), new("t1", 3),
        new("tr", 4)
    ];

    public static NumericExpressionProfile BatchNote { get; } =
        new("midora.tool.batch-note/v1", 1, BatchNoteVariables);

    public static NumericExpressionProfile BatchEvent { get; } =
        new("midora.tool.batch-event/v1", 1, BatchEventVariables);

    public static NumericExpressionProfile GenerateNote { get; } =
        new("midora.tool.generate-note/v1", 1, [.. BatchNoteVariables, new("i", 9)]);

    public static NumericExpressionProfile GenerateEvent { get; } =
        new("midora.tool.generate-event/v1", 1, [.. BatchEventVariables, new("i", 5)]);

    public static NumericExpressionProfile NoteSplit { get; } =
        new("midora.tool.note-split/v1", 1,
        [
            new("i", 0),
            new("tr", 1)
        ]);
}

public sealed class CompiledNumericExpression : IDisposable
{
    private Func<double[], double>? _evaluate;

    internal CompiledNumericExpression(
        NumericExpressionProfile profile,
        Func<double[], double> evaluate,
        IReadOnlySet<string> referencedVariables)
    {
        Profile = profile;
        _evaluate = evaluate;
        ReferencedVariables = referencedVariables;
    }

    public NumericExpressionProfile Profile { get; }
    public IReadOnlySet<string> ReferencedVariables { get; }

    public double Evaluate(double[] slots)
    {
        ObjectDisposedException.ThrowIf(_evaluate is null, this);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Length < Profile.VariableCount)
            throw new ArgumentException("The numeric expression variable buffer is too small.", nameof(slots));
        double result = _evaluate(slots);
        if (!double.IsFinite(result))
            throw new InvalidOperationException("The numeric expression returned a non-finite value.");
        return result;
    }

    public void Dispose() => _evaluate = null;
}

public sealed record NumericMathFunctionDescriptor(
    string Name,
    IReadOnlyList<string> Signatures);

/// <summary>
/// Compiles a deliberately small, versioned numeric expression language to a
/// System.Linq.Expressions delegate. Profiles define variables; the grammar
/// and exact System.Math surface are frozen here and never come from a preset.
/// </summary>
public static class BoundedNumericExpressionCompiler
{
    private const string MathPrefix = "System.Math.";
    private static readonly HashSet<string> FrozenMathSignatures =
        new(StringComparer.Ordinal)
        {
            "Abs(System.Decimal):System.Decimal", "Abs(System.Double):System.Double",
            "Abs(System.Int16):System.Int16", "Abs(System.Int32):System.Int32",
            "Abs(System.Int64):System.Int64", "Abs(System.SByte):System.SByte",
            "Abs(System.Single):System.Single", "Acos(System.Double):System.Double",
            "Acosh(System.Double):System.Double", "Asin(System.Double):System.Double",
            "Asinh(System.Double):System.Double", "Atan(System.Double):System.Double",
            "Atan2(System.Double,System.Double):System.Double", "Atanh(System.Double):System.Double",
            "BigMul(System.Int32,System.Int32):System.Int64", "BigMul(System.UInt32,System.UInt32):System.UInt64",
            "BitDecrement(System.Double):System.Double", "BitIncrement(System.Double):System.Double",
            "Cbrt(System.Double):System.Double", "Ceiling(System.Decimal):System.Decimal",
            "Ceiling(System.Double):System.Double", "Clamp(System.Byte,System.Byte,System.Byte):System.Byte",
            "Clamp(System.Decimal,System.Decimal,System.Decimal):System.Decimal",
            "Clamp(System.Double,System.Double,System.Double):System.Double",
            "Clamp(System.Int16,System.Int16,System.Int16):System.Int16",
            "Clamp(System.Int32,System.Int32,System.Int32):System.Int32",
            "Clamp(System.Int64,System.Int64,System.Int64):System.Int64",
            "Clamp(System.SByte,System.SByte,System.SByte):System.SByte",
            "Clamp(System.Single,System.Single,System.Single):System.Single",
            "Clamp(System.UInt16,System.UInt16,System.UInt16):System.UInt16",
            "Clamp(System.UInt32,System.UInt32,System.UInt32):System.UInt32",
            "Clamp(System.UInt64,System.UInt64,System.UInt64):System.UInt64",
            "CopySign(System.Double,System.Double):System.Double", "Cos(System.Double):System.Double",
            "Cosh(System.Double):System.Double", "Exp(System.Double):System.Double",
            "Floor(System.Decimal):System.Decimal", "Floor(System.Double):System.Double",
            "FusedMultiplyAdd(System.Double,System.Double,System.Double):System.Double",
            "IEEERemainder(System.Double,System.Double):System.Double", "ILogB(System.Double):System.Int32",
            "Log(System.Double):System.Double", "Log(System.Double,System.Double):System.Double",
            "Log10(System.Double):System.Double", "Log2(System.Double):System.Double",
            "Max(System.Byte,System.Byte):System.Byte", "Max(System.Decimal,System.Decimal):System.Decimal",
            "Max(System.Double,System.Double):System.Double", "Max(System.Int16,System.Int16):System.Int16",
            "Max(System.Int32,System.Int32):System.Int32", "Max(System.Int64,System.Int64):System.Int64",
            "Max(System.SByte,System.SByte):System.SByte", "Max(System.Single,System.Single):System.Single",
            "Max(System.UInt16,System.UInt16):System.UInt16", "Max(System.UInt32,System.UInt32):System.UInt32",
            "Max(System.UInt64,System.UInt64):System.UInt64", "MaxMagnitude(System.Double,System.Double):System.Double",
            "Min(System.Byte,System.Byte):System.Byte", "Min(System.Decimal,System.Decimal):System.Decimal",
            "Min(System.Double,System.Double):System.Double", "Min(System.Int16,System.Int16):System.Int16",
            "Min(System.Int32,System.Int32):System.Int32", "Min(System.Int64,System.Int64):System.Int64",
            "Min(System.SByte,System.SByte):System.SByte", "Min(System.Single,System.Single):System.Single",
            "Min(System.UInt16,System.UInt16):System.UInt16", "Min(System.UInt32,System.UInt32):System.UInt32",
            "Min(System.UInt64,System.UInt64):System.UInt64", "MinMagnitude(System.Double,System.Double):System.Double",
            "Pow(System.Double,System.Double):System.Double", "ReciprocalEstimate(System.Double):System.Double",
            "ReciprocalSqrtEstimate(System.Double):System.Double", "Round(System.Decimal):System.Decimal",
            "Round(System.Decimal,System.Int32):System.Decimal", "Round(System.Double):System.Double",
            "Round(System.Double,System.Int32):System.Double", "ScaleB(System.Double,System.Int32):System.Double",
            "Sign(System.Decimal):System.Int32", "Sign(System.Double):System.Int32",
            "Sign(System.Int16):System.Int32", "Sign(System.Int32):System.Int32",
            "Sign(System.Int64):System.Int32", "Sign(System.SByte):System.Int32",
            "Sign(System.Single):System.Int32", "Sin(System.Double):System.Double",
            "Sinh(System.Double):System.Double", "Sqrt(System.Double):System.Double",
            "Tan(System.Double):System.Double", "Tanh(System.Double):System.Double",
            "Truncate(System.Decimal):System.Decimal", "Truncate(System.Double):System.Double"
        };

    private static readonly IReadOnlyDictionary<(string Name, int Arity), MethodInfo[]> MathMethods =
        typeof(Math).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static method => FrozenMathSignatures.Contains(GetSignature(method)))
            .GroupBy(static method => (method.Name, method.GetParameters().Length))
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(GetSignature, StringComparer.Ordinal).ToArray());

    private static readonly HashSet<string> MathMethodNames =
        MathMethods.Keys.Select(static value => value.Name).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<NumericMathFunctionDescriptor> MathFunctions { get; } =
        MathMethods.GroupBy(static pair => pair.Key.Name, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new NumericMathFunctionDescriptor(
                group.Key,
                group.SelectMany(static pair => pair.Value)
                    .Select(FormatDisplaySignature)
                    .Order(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

    public static CompiledNumericExpression Compile(
        NumericExpressionProfile profile,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateText(source, profile);
        string expressionText = source.Trim();
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(
            expressionText,
            options: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
            consumeFullText: true);
        Diagnostic? syntaxError = syntax.GetDiagnostics()
            .FirstOrDefault(static value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (syntaxError is not null)
        {
            throw new ArgumentException(
                $"The expression is invalid: {syntaxError.GetMessage(CultureInfo.InvariantCulture)}",
                nameof(source));
        }

        ValidateLexicalSurface(syntax);
        SyntaxNode[] nodes = syntax.DescendantNodesAndSelf().ToArray();
        if (nodes.Length > profile.MaximumSyntaxNodeCount)
            throw new ArgumentException($"The expression exceeds the {profile.MaximumSyntaxNodeCount} syntax-node limit.", nameof(source));
        if (GetDepth(syntax) > profile.MaximumSyntaxDepth)
            throw new ArgumentException($"The expression exceeds the {profile.MaximumSyntaxDepth} syntax-depth limit.", nameof(source));
        ValidateExpressionSyntax(syntax, profile);
        cancellationToken.ThrowIfCancellationRequested();

        ParameterExpression slots = Expression.Parameter(typeof(double[]), "slots");
        Dictionary<string, System.Linq.Expressions.Expression> variables = profile.Variables.ToDictionary(
            static value => value.Name,
            value => (System.Linq.Expressions.Expression)Expression.ArrayIndex(slots, Expression.Constant(value.Slot)),
            StringComparer.Ordinal);
        try
        {
            BoundExpression bound = new NumericExpressionBinder(variables).Bind(syntax);
            if (bound.Kind != BoundKind.Number)
                throw new NumericExpressionBindingException("The expression result must be numeric.");
            System.Linq.Expressions.Expression result = bound.Expression.Type == typeof(double)
                ? bound.Expression
                : Expression.Convert(bound.Expression, typeof(double));
            Func<double[], double> evaluate = Expression
                .Lambda<Func<double[], double>>(result, slots)
                .Compile();
            HashSet<string> referenced = syntax.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Select(static value => value.Identifier.ValueText)
                .Where(name => profile.TryGetVariable(name, out _))
                .ToHashSet(StringComparer.Ordinal);
            return new(profile, evaluate, referenced);
        }
        catch (NumericExpressionBindingException exception)
        {
            throw new ArgumentException($"The expression cannot compile: {exception.Message}", nameof(source), exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException($"The expression cannot compile: {exception.Message}", nameof(source), exception);
        }
    }

    private static void ValidateText(string source, NumericExpressionProfile profile)
    {
        if (source.Length == 0 || source.Trim().Length == 0)
            throw new ArgumentException("A numeric expression is required.", nameof(source));
        if (source.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Numeric expressions must contain exactly one logical line.", nameof(source));
        int scalars = 0;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= source.Length || !char.IsLowSurrogate(source[index + 1]))
                    throw new ArgumentException("The expression contains invalid Unicode.", nameof(source));
                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                throw new ArgumentException("The expression contains invalid Unicode.", nameof(source));
            }
            scalars++;
            if (scalars > profile.MaximumScalarCount)
                throw new ArgumentException($"The expression exceeds the {profile.MaximumScalarCount} scalar limit.", nameof(source));
        }
    }

    private static int GetDepth(SyntaxNode node)
    {
        int maximum = 1;
        Stack<(SyntaxNode Node, int Depth)> stack = new();
        stack.Push((node, 1));
        while (stack.Count != 0)
        {
            (SyntaxNode current, int depth) = stack.Pop();
            maximum = Math.Max(maximum, depth);
            foreach (SyntaxNode child in current.ChildNodes()) stack.Push((child, checked(depth + 1)));
        }
        return maximum;
    }

    private static void ValidateExpressionSyntax(ExpressionSyntax expression, NumericExpressionProfile profile)
    {
        foreach (SyntaxNode node in expression.DescendantNodesAndSelf())
        {
            bool allowed = node switch
            {
                LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
                    || literal.IsKind(SyntaxKind.TrueLiteralExpression)
                    || literal.IsKind(SyntaxKind.FalseLiteralExpression),
                IdentifierNameSyntax identifier => IsAllowedIdentifier(identifier, profile),
                ParenthesizedExpressionSyntax => true,
                PrefixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.UnaryPlusExpression)
                    || unary.IsKind(SyntaxKind.UnaryMinusExpression)
                    || unary.IsKind(SyntaxKind.LogicalNotExpression),
                BinaryExpressionSyntax binary => IsAllowedBinary(binary.Kind()),
                ConditionalExpressionSyntax => true,
                InvocationExpressionSyntax invocation => IsAllowedInvocation(invocation),
                ArgumentListSyntax or ArgumentSyntax => true,
                MemberAccessExpressionSyntax member => IsAllowedMemberAccess(member),
                CastExpressionSyntax cast => cast.Type is PredefinedTypeSyntax predefined
                    && predefined.Keyword.IsKind(SyntaxKind.DoubleKeyword),
                PredefinedTypeSyntax predefined => predefined.Keyword.IsKind(SyntaxKind.DoubleKeyword),
                _ => false
            };
            if (!allowed)
                throw new ArgumentException($"The expression contains unsupported syntax '{node.Kind()}'.", nameof(expression));
        }
    }

    private static void ValidateLexicalSurface(ExpressionSyntax expression)
    {
        foreach (SyntaxTrivia trivia in expression.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                throw new ArgumentException(
                    $"The expression contains unsupported lexical trivia '{trivia.Kind()}'.",
                    nameof(expression));
            }
        }

        foreach (IdentifierNameSyntax identifier in expression.DescendantNodesAndSelf()
                     .OfType<IdentifierNameSyntax>())
        {
            if (!string.Equals(
                    identifier.Identifier.Text,
                    identifier.Identifier.ValueText,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Numeric expression identifiers must use their exact plain-text spelling.",
                    nameof(expression));
            }
        }
    }

    private static bool IsAllowedIdentifier(IdentifierNameSyntax identifier, NumericExpressionProfile profile)
    {
        string name = identifier.Identifier.ValueText;
        if (profile.TryGetVariable(name, out _) || name is "Math" or "PI" or "E" or "Tau") return true;
        return (identifier.Parent is InvocationExpressionSyntax or MemberAccessExpressionSyntax)
            && MathMethodNames.Contains(name);
    }

    private static bool IsAllowedInvocation(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => MathMethodNames.Contains(identifier.Identifier.ValueText),
        MemberAccessExpressionSyntax member => IsAllowedMemberAccess(member)
            && MathMethodNames.Contains(member.Name.Identifier.ValueText),
        _ => false
    };

    private static bool IsAllowedMemberAccess(MemberAccessExpressionSyntax member) =>
        member.Expression is IdentifierNameSyntax { Identifier.ValueText: "Math" }
        && (MathMethodNames.Contains(member.Name.Identifier.ValueText)
            || member.Name.Identifier.ValueText is "PI" or "E" or "Tau");

    private static bool IsAllowedBinary(SyntaxKind kind) => kind is
        SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression
        or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression
        or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
        or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
        or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
        or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression;

    private sealed class NumericExpressionBinder(
        IReadOnlyDictionary<string, System.Linq.Expressions.Expression> variables)
    {
        public BoundExpression Bind(ExpressionSyntax syntax) => syntax switch
        {
            ParenthesizedExpressionSyntax value => Bind(value.Expression),
            LiteralExpressionSyntax value => BindLiteral(value),
            IdentifierNameSyntax value => BindIdentifier(value),
            MemberAccessExpressionSyntax value => BindMember(value),
            PrefixUnaryExpressionSyntax value => BindUnary(value),
            BinaryExpressionSyntax value => BindBinary(value),
            ConditionalExpressionSyntax value => BindConditional(value),
            InvocationExpressionSyntax value => BindInvocation(value),
            CastExpressionSyntax value => BindCast(value),
            _ => throw Unsupported(syntax)
        };

        private static BoundExpression BindLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression)) return new(Expression.Constant(true), BoundKind.Boolean);
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression)) return new(Expression.Constant(false), BoundKind.Boolean);
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression)
                || literal.Token.Value is not { } value
                || !IsNumericType(value.GetType())) throw Unsupported(literal);
            if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f))
                throw new NumericExpressionBindingException($"Numeric literal '{literal.Token.Text}' must be finite.");
            return new(Expression.Constant(value, value.GetType()), BoundKind.Number);
        }

        private BoundExpression BindIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.ValueText;
            if (variables.TryGetValue(name, out System.Linq.Expressions.Expression? variable))
                return new(variable, BoundKind.Number);
            return name switch
            {
                "PI" => new(Expression.Constant(Math.PI), BoundKind.Number),
                "E" => new(Expression.Constant(Math.E), BoundKind.Number),
                "Tau" => new(Expression.Constant(Math.Tau), BoundKind.Number),
                _ => throw new NumericExpressionBindingException($"Unknown numeric expression name '{name}'.")
            };
        }

        private static BoundExpression BindMember(MemberAccessExpressionSyntax member)
        {
            if (member.Expression is IdentifierNameSyntax { Identifier.ValueText: "Math" })
            {
                return member.Name.Identifier.ValueText switch
                {
                    "PI" => new(Expression.Constant(Math.PI), BoundKind.Number),
                    "E" => new(Expression.Constant(Math.E), BoundKind.Number),
                    "Tau" => new(Expression.Constant(Math.Tau), BoundKind.Number),
                    _ => throw Unsupported(member)
                };
            }
            throw Unsupported(member);
        }

        private BoundExpression BindUnary(PrefixUnaryExpressionSyntax unary)
        {
            BoundExpression operand = Bind(unary.Operand);
            if (unary.IsKind(SyntaxKind.LogicalNotExpression))
            {
                Require(operand, BoundKind.Boolean, unary);
                return new(Expression.Not(operand.Expression), BoundKind.Boolean);
            }
            Require(operand, BoundKind.Number, unary);
            Type target = PromoteUnaryType(operand.Expression.Type, unary.Kind());
            System.Linq.Expressions.Expression promoted = ConvertIfNeeded(operand.Expression, target);
            return unary.Kind() switch
            {
                SyntaxKind.UnaryPlusExpression => new(promoted, BoundKind.Number),
                SyntaxKind.UnaryMinusExpression => new(Expression.Negate(promoted), BoundKind.Number),
                _ => throw Unsupported(unary)
            };
        }

        private BoundExpression BindBinary(BinaryExpressionSyntax binary)
        {
            BoundExpression left = Bind(binary.Left);
            BoundExpression right = Bind(binary.Right);
            SyntaxKind kind = binary.Kind();
            if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
            {
                Require(left, BoundKind.Boolean, binary.Left);
                Require(right, BoundKind.Boolean, binary.Right);
                return new(kind == SyntaxKind.LogicalAndExpression
                    ? Expression.AndAlso(left.Expression, right.Expression)
                    : Expression.OrElse(left.Expression, right.Expression), BoundKind.Boolean);
            }
            if ((kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
                && left.Kind == BoundKind.Boolean && right.Kind == BoundKind.Boolean)
            {
                return new(kind == SyntaxKind.EqualsExpression
                    ? Expression.Equal(left.Expression, right.Expression)
                    : Expression.NotEqual(left.Expression, right.Expression), BoundKind.Boolean);
            }
            Require(left, BoundKind.Number, binary.Left);
            Require(right, BoundKind.Number, binary.Right);
            (System.Linq.Expressions.Expression l, System.Linq.Expressions.Expression r) =
                PromoteBinary(left.Expression, right.Expression, binary);
            return kind switch
            {
                SyntaxKind.AddExpression => new(Expression.Add(l, r), BoundKind.Number),
                SyntaxKind.SubtractExpression => new(Expression.Subtract(l, r), BoundKind.Number),
                SyntaxKind.MultiplyExpression => new(Expression.Multiply(l, r), BoundKind.Number),
                SyntaxKind.DivideExpression => new(Expression.Divide(l, r), BoundKind.Number),
                SyntaxKind.ModuloExpression => new(Expression.Modulo(l, r), BoundKind.Number),
                SyntaxKind.LessThanExpression => new(Expression.LessThan(l, r), BoundKind.Boolean),
                SyntaxKind.LessThanOrEqualExpression => new(Expression.LessThanOrEqual(l, r), BoundKind.Boolean),
                SyntaxKind.GreaterThanExpression => new(Expression.GreaterThan(l, r), BoundKind.Boolean),
                SyntaxKind.GreaterThanOrEqualExpression => new(Expression.GreaterThanOrEqual(l, r), BoundKind.Boolean),
                SyntaxKind.EqualsExpression => new(Expression.Equal(l, r), BoundKind.Boolean),
                SyntaxKind.NotEqualsExpression => new(Expression.NotEqual(l, r), BoundKind.Boolean),
                _ => throw Unsupported(binary)
            };
        }

        private BoundExpression BindConditional(ConditionalExpressionSyntax conditional)
        {
            BoundExpression condition = Bind(conditional.Condition);
            Require(condition, BoundKind.Boolean, conditional.Condition);
            BoundExpression whenTrue = Bind(conditional.WhenTrue);
            BoundExpression whenFalse = Bind(conditional.WhenFalse);
            if (whenTrue.Kind != whenFalse.Kind)
                throw new NumericExpressionBindingException("Both conditional branches must have the same type.");
            if (whenTrue.Kind == BoundKind.Boolean)
                return new(Expression.Condition(condition.Expression, whenTrue.Expression, whenFalse.Expression), BoundKind.Boolean);
            (System.Linq.Expressions.Expression t, System.Linq.Expressions.Expression f) =
                PromoteBinary(whenTrue.Expression, whenFalse.Expression, conditional);
            return new(Expression.Condition(condition.Expression, t, f), BoundKind.Number);
        }

        private BoundExpression BindInvocation(InvocationExpressionSyntax invocation)
        {
            string name = invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Math" } } member =>
                    member.Name.Identifier.ValueText,
                _ => throw new NumericExpressionBindingException("Only approved System.Math calls are available.")
            };
            BoundExpression[] arguments = invocation.ArgumentList.Arguments.Select(argument =>
            {
                if (argument.NameColon is not null || !argument.RefKindKeyword.IsKind(SyntaxKind.None))
                    throw Unsupported(argument);
                BoundExpression bound = Bind(argument.Expression);
                Require(bound, BoundKind.Number, argument.Expression);
                return bound;
            }).ToArray();
            if (!MathMethods.TryGetValue((name, arguments.Length), out MethodInfo[]? candidates))
                throw new NumericExpressionBindingException($"System.Math method '{name}' with {arguments.Length} argument(s) is not available.");
            MethodCandidate[] applicable = candidates.Select(method => TryCreateCandidate(method, arguments))
                .OfType<MethodCandidate>().ToArray();
            if (applicable.Length == 0)
                throw new NumericExpressionBindingException($"No System.Math overload '{name}' accepts the supplied numeric argument types.");
            MethodCandidate[] best = applicable.Where(candidate => applicable.All(other =>
                ReferenceEquals(candidate, other) || CompareCandidates(candidate, other, arguments) >= 0)).ToArray();
            if (best.Length != 1)
                throw new NumericExpressionBindingException($"System.Math call '{name}' is ambiguous for the supplied numeric argument types.");
            MethodCandidate selected = best[0];
            return new(Expression.Call(selected.Method, arguments.Select((argument, index) =>
                ConvertIfNeeded(argument.Expression, selected.ParameterTypes[index]))), BoundKind.Number);
        }

        private BoundExpression BindCast(CastExpressionSyntax cast)
        {
            BoundExpression value = Bind(cast.Expression);
            Require(value, BoundKind.Number, cast.Expression);
            return new(ConvertIfNeeded(value.Expression, typeof(double)), BoundKind.Number);
        }
    }

    private static MethodCandidate? TryCreateCandidate(MethodInfo method, IReadOnlyList<BoundExpression> arguments)
    {
        Type[] parameterTypes = method.GetParameters().Select(static value => value.ParameterType).ToArray();
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!HasImplicitNumericConversion(arguments[index].Expression.Type, parameterTypes[index])) return null;
        }
        return new(method, parameterTypes);
    }

    private static int CompareCandidates(MethodCandidate left, MethodCandidate right, IReadOnlyList<BoundExpression> arguments)
    {
        bool leftBetter = false;
        bool rightBetter = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            Type source = arguments[index].Expression.Type;
            Type leftTarget = left.ParameterTypes[index];
            Type rightTarget = right.ParameterTypes[index];
            if (leftTarget == rightTarget) continue;
            if (source == leftTarget) { leftBetter = true; continue; }
            if (source == rightTarget) { rightBetter = true; continue; }
            bool leftToRight = HasImplicitNumericConversion(leftTarget, rightTarget);
            bool rightToLeft = HasImplicitNumericConversion(rightTarget, leftTarget);
            if (leftToRight && !rightToLeft) leftBetter = true;
            if (rightToLeft && !leftToRight) rightBetter = true;
        }
        return leftBetter == rightBetter ? 0 : leftBetter ? 1 : -1;
    }

    private static Type PromoteUnaryType(Type type, SyntaxKind kind)
    {
        if (type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort)) return typeof(int);
        if (kind == SyntaxKind.UnaryMinusExpression && type == typeof(uint)) return typeof(long);
        if (kind == SyntaxKind.UnaryMinusExpression && type == typeof(ulong))
            throw new NumericExpressionBindingException("Unary minus is not defined for an unsigned 64-bit value.");
        return type;
    }

    private static (System.Linq.Expressions.Expression Left, System.Linq.Expressions.Expression Right)
        PromoteBinary(System.Linq.Expressions.Expression left, System.Linq.Expressions.Expression right, SyntaxNode source)
    {
        Type target = GetBinaryPromotionType(left.Type, right.Type, source);
        return (ConvertIfNeeded(left, target), ConvertIfNeeded(right, target));
    }

    private static Type GetBinaryPromotionType(Type left, Type right, SyntaxNode source)
    {
        if (left == typeof(decimal) || right == typeof(decimal))
        {
            if (left == typeof(double) || right == typeof(double) || left == typeof(float) || right == typeof(float))
                throw new NumericExpressionBindingException("Decimal values cannot be combined directly with float or double values.");
            return typeof(decimal);
        }
        if (left == typeof(double) || right == typeof(double)) return typeof(double);
        if (left == typeof(float) || right == typeof(float)) return typeof(float);
        if (left == typeof(ulong) || right == typeof(ulong))
        {
            Type other = left == typeof(ulong) ? right : left;
            if (other == typeof(sbyte) || other == typeof(short) || other == typeof(int) || other == typeof(long))
                throw new NumericExpressionBindingException("Unsigned 64-bit values cannot be combined with signed integral values.");
            return typeof(ulong);
        }
        if (left == typeof(long) || right == typeof(long)) return typeof(long);
        if (left == typeof(uint) || right == typeof(uint))
        {
            Type other = left == typeof(uint) ? right : left;
            return other == typeof(sbyte) || other == typeof(short) || other == typeof(int) ? typeof(long) : typeof(uint);
        }
        if (!IsNumericType(left) || !IsNumericType(right)) throw Unsupported(source);
        return typeof(int);
    }

    private static bool HasImplicitNumericConversion(Type source, Type target)
    {
        if (source == target) return true;
        Type[] targets = source == typeof(sbyte)
            ? [typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)]
            : source == typeof(byte)
                ? [typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)]
                : source == typeof(short)
                    ? [typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)]
                    : source == typeof(ushort)
                        ? [typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)]
                        : source == typeof(int)
                            ? [typeof(long), typeof(float), typeof(double), typeof(decimal)]
                            : source == typeof(uint)
                                ? [typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)]
                                : source == typeof(long)
                                    ? [typeof(float), typeof(double), typeof(decimal)]
                                    : source == typeof(ulong)
                                        ? [typeof(float), typeof(double), typeof(decimal)]
                                        : source == typeof(float) ? [typeof(double)] : [];
        return Array.IndexOf(targets, target) >= 0;
    }

    private static bool IsNumericType(Type type) => type == typeof(sbyte) || type == typeof(byte)
        || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double)
        || type == typeof(decimal);

    private static System.Linq.Expressions.Expression ConvertIfNeeded(
        System.Linq.Expressions.Expression expression,
        Type target) => expression.Type == target ? expression : Expression.Convert(expression, target);

    private static void Require(BoundExpression value, BoundKind expected, SyntaxNode source)
    {
        if (value.Kind != expected) throw Unsupported(source);
    }

    private static NumericExpressionBindingException Unsupported(SyntaxNode node) =>
        new($"Unsupported numeric expression syntax '{node.Kind()}'.");

    private static string GetSignature(MethodInfo method) =>
        $"{method.Name}({string.Join(',', method.GetParameters().Select(static value => value.ParameterType.FullName))}):{method.ReturnType.FullName}";

    private static string FormatDisplaySignature(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(static value => value.ParameterType.Name))})";

    private readonly record struct BoundExpression(
        System.Linq.Expressions.Expression Expression,
        BoundKind Kind);
    private sealed record MethodCandidate(MethodInfo Method, Type[] ParameterTypes);
    private sealed class NumericExpressionBindingException(string message) : Exception(message);
    private enum BoundKind { Number, Boolean }
}

public static class ResultDependencyGraph
{
    public static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> nodes,
        Func<T, IEnumerable<T>> getDependencies,
        IComparer<T>? comparer = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(getDependencies);
        comparer ??= Comparer<T>.Default;
        T[] materialized = nodes.Distinct().Order(comparer).ToArray();
        HashSet<T> available = materialized.ToHashSet();
        Dictionary<T, byte> states = [];
        List<T> result = [];
        foreach (T node in materialized) Visit(node);
        return result;

        void Visit(T node)
        {
            if (states.TryGetValue(node, out byte state))
            {
                if (state == 1) throw new ArgumentException("Result variables contain a circular dependency.");
                if (state == 2) return;
            }
            states[node] = 1;
            foreach (T dependency in getDependencies(node).Distinct().Order(comparer))
            {
                if (!available.Contains(dependency))
                    throw new ArgumentException($"A result expression references unavailable dependency '{dependency}'.");
                Visit(dependency);
            }
            states[node] = 2;
            result.Add(node);
        }
    }
}

public sealed class NoteSplitExpressionProgram : IDisposable
{
    private readonly CompiledNumericExpression _expression;
    private readonly double[] _slots = new double[2];

    private NoteSplitExpressionProgram(CompiledNumericExpression expression) => _expression = expression;

    public static NoteSplitExpressionProgram Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.TrimStart()[0] != '=')
            throw new ArgumentException("A Note Split expression must begin with '='.", nameof(source));
        return new(BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            source.Trim()[1..].Trim()));
    }

    public double Evaluate(double i, double tr)
    {
        _slots[0] = i;
        _slots[1] = tr;
        return _expression.Evaluate(_slots);
    }

    public void Dispose() => _expression.Dispose();
}
