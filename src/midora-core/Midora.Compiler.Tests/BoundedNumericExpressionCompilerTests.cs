using System.Globalization;

namespace Midora.Compiler.Tests;

public sealed class BoundedNumericExpressionCompilerTests
{
    [Fact]
    public void ProfilesExposeOnlyTheirVersionedVariables()
    {
        Assert.Equal("midora.tool.batch-note/v1", NumericExpressionProfiles.BatchNote.Id);
        Assert.Equal("midora.tool.batch-event/v2", NumericExpressionProfiles.BatchEvent.Id);
        Assert.Equal("midora.tool.note-split/v1", NumericExpressionProfiles.NoteSplit.Id);
        Assert.Equal(["v0", "v1", "k0", "k1", "g0", "g1", "t0", "t1", "tr"],
            NumericExpressionProfiles.BatchNote.Variables.Select(static value => value.Name));
        Assert.Equal(["p0", "p1", "t0", "t1", "tr"],
            NumericExpressionProfiles.BatchEvent.Variables.Select(static value => value.Name));
        Assert.Equal(["i", "tr"],
            NumericExpressionProfiles.NoteSplit.Variables.Select(static value => value.Name));
    }

    [Fact]
    public void CompilerSupportsImplicitAndQualifiedMathNames()
    {
        using CompiledNumericExpression implicitMath = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "Round(Sin(i) * 10 + PI)");
        using CompiledNumericExpression qualifiedMath = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "Math.Max(i, tr)");

        Assert.Equal(Math.Round(Math.Sin(2) * 10 + Math.PI), implicitMath.Evaluate([2, 4]));
        Assert.Equal(4, qualifiedMath.Evaluate([2, 4]));
    }

    [Theory]
    [InlineData("v0")]
    [InlineData("new Random().Next()")]
    [InlineData("Environment.TickCount")]
    [InlineData("i = 2")]
    [InlineData("x => x")]
    [InlineData("new[] { 1.0 }")]
    [InlineData("System.Math.Sin(i)")]
    [InlineData("((Func<double>)(() => i))()")]
    [InlineData("delegate { return i; }()")]
    [InlineData("new double[] { i }[0]")]
    [InlineData("new object()")]
    [InlineData("i; i")]
    [InlineData("for (;;) { }")]
    [InlineData("typeof(Math).GetMethods().Length")]
    [InlineData("System.IO.File.ReadAllText(\"x\").Length")]
    [InlineData("DateTime.UtcNow.Ticks")]
    [InlineData("Math.Sin(i).GetType()")]
    [InlineData("Math.DivRem(5, 2)")]
    [InlineData("i++")]
    [InlineData("++i")]
    [InlineData("i += 1")]
    public void CompilerRejectsNamesAndSyntaxOutsideTheProfile(string source)
    {
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            source));
    }

    [Fact]
    public void CompilerEnforcesExactFormalScalarBoundary()
    {
        string atLimit = "i" + new string(' ', 8_189) + "+0";
        string aboveLimit = "i" + new string(' ', 8_190) + "+0";

        Assert.Equal(8_192, atLimit.Length);
        Assert.Equal(8_193, aboveLimit.Length);
        using CompiledNumericExpression accepted = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            atLimit);
        Assert.Equal(2, accepted.Evaluate([2, 0]));
        Assert.Throws<ArgumentException>(() =>
            BoundedNumericExpressionCompiler.Compile(
                NumericExpressionProfiles.NoteSplit,
                aboveLimit));
    }

    [Fact]
    public void CompilerEnforcesExactFormalSyntaxNodeBoundary()
    {
        // The balanced helper contributes 3N-2 nodes (identifier, binary and
        // explicit parenthesis nodes). N=171 therefore contributes 511.
        string body = CreateBalancedAddition(171);
        string atLimit = $"({body})";
        string aboveLimit = $"(({body}))";

        using CompiledNumericExpression accepted = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            atLimit);
        Assert.Equal(171, accepted.Evaluate([1, 0]));
        Assert.Throws<ArgumentException>(() =>
            BoundedNumericExpressionCompiler.Compile(
                NumericExpressionProfiles.NoteSplit,
                aboveLimit));
    }

    [Fact]
    public void CompilerEnforcesExactFormalSyntaxDepthBoundary()
    {
        string atLimit = new string('(', 63) + "i" + new string(')', 63);
        string aboveLimit = new string('(', 64) + "i" + new string(')', 64);

        using CompiledNumericExpression accepted = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            atLimit);
        Assert.Equal(7, accepted.Evaluate([7, 0]));
        Assert.Throws<ArgumentException>(() =>
            BoundedNumericExpressionCompiler.Compile(
                NumericExpressionProfiles.NoteSplit,
                aboveLimit));
    }

    [Fact]
    public void CompilerRejectsNewlinesAndUnpairedSurrogatesButAllowsReplacementScalar()
    {
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "i\n+ tr"));
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "i + \ud800"));
        ArgumentException replacement = Assert.Throws<ArgumentException>(() =>
            BoundedNumericExpressionCompiler.Compile(
                NumericExpressionProfiles.NoteSplit,
                "i + \ufffd"));
        Assert.DoesNotContain("invalid Unicode", replacement.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompilerRejectsNonFiniteEvaluationResults()
    {
        using CompiledNumericExpression infinity = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "1.0 / (i - i)");
        using CompiledNumericExpression notANumber = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "Sqrt(-1.0)");

        Assert.Throws<InvalidOperationException>(() => infinity.Evaluate([2, 0]));
        Assert.Throws<InvalidOperationException>(() => notANumber.Evaluate([2, 0]));
    }

    [Fact]
    public void CompilerSurfacesArithmeticOverflowWithoutReturningAWrappedResult()
    {
        using CompiledNumericExpression expression = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "Abs(-9223372036854775807L - 1L)");

        Assert.Throws<OverflowException>(() => expression.Evaluate([0, 0]));
    }

    [Fact]
    public void CompilerIsCultureIndependentAndDeterministicAcrossColdHotAndParallelEvaluation()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            const string source = "Round(Sin(i / 10.0) * 64.0 + tr, 6)";
            using CompiledNumericExpression first = BoundedNumericExpressionCompiler.Compile(
                NumericExpressionProfiles.NoteSplit,
                source);
            using CompiledNumericExpression second = BoundedNumericExpressionCompiler.Compile(
                NumericExpressionProfiles.NoteSplit,
                source);
            double expected = first.Evaluate([17, 23]);

            Assert.Equal(expected, first.Evaluate([17, 23]));
            Assert.Equal(expected, second.Evaluate([17, 23]));
            double[] results = new double[64];
            Parallel.For(0, results.Length, index =>
                results[index] = first.Evaluate([17, 23]));
            Assert.All(results, value => Assert.Equal(expected, value));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("i/*comment*/+tr")]
    [InlineData("i//comment")]
    [InlineData("@i+tr")]
    [InlineData("\\u0069+tr")]
    public void CompilerRejectsCommentsAndEscapedIdentifierSpellings(string source)
    {
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            source));
    }

    [Fact]
    public void CompilerRejectsCrossProfileNamesAndIdentifierCaseVariants()
    {
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "v0"));
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.BatchEvent,
            "k0"));
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.BatchNote,
            "p0"));
        Assert.Throws<ArgumentException>(() => BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "I + tr"));
    }

    [Fact]
    public void CompiledProgramsRejectEvaluationAfterDisposeAndAllowRepeatedDispose()
    {
        CompiledNumericExpression expression = BoundedNumericExpressionCompiler.Compile(
            NumericExpressionProfiles.NoteSplit,
            "i + tr");
        expression.Dispose();
        expression.Dispose();
        Assert.Throws<ObjectDisposedException>(() => expression.Evaluate([1, 2]));

        NoteSplitExpressionProgram split = NoteSplitExpressionProgram.Compile("=i + tr");
        split.Dispose();
        split.Dispose();
        Assert.Throws<ObjectDisposedException>(() => split.Evaluate(1, 2));
    }

    [Fact]
    public void NoteSplitAdapterRequiresMarkerAndReusesCompiledProgram()
    {
        Assert.Throws<ArgumentException>(() => NoteSplitExpressionProgram.Compile("i + 1"));
        using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile("=Pow(2, i) + tr");
        Assert.Equal(1, program.Evaluate(0, 0));
        Assert.Equal(5, program.Evaluate(1, 3));
    }

    private static string CreateBalancedAddition(int operandCount)
    {
        if (operandCount < 1) throw new ArgumentOutOfRangeException(nameof(operandCount));
        return Build(operandCount);

        static string Build(int count)
        {
            if (count == 1) return "i";
            int left = count / 2;
            return $"({Build(left)}+{Build(count - left)})";
        }
    }
}
