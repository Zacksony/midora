using System.Diagnostics;

namespace Midora.Compiler.Tests;

public sealed class BatchEditExpressionProgramTests
{
    [Fact]
    public void BoundedExpressionsCompileWithoutTrustedPlatformAssemblyPaths()
    {
        object? original = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        AppContext.SetData("TRUSTED_PLATFORM_ASSEMBLIES", null);
        try
        {
            using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
                new Dictionary<BatchEditField, string?>
                {
                    [BatchEditField.Velocity] = "=Clamp(v0 * 1.15, 1, 127)",
                    [BatchEditField.KeyNumber] = "=k0 + 1",
                    [BatchEditField.Gate] = "=Round(g0, 2)",
                    [BatchEditField.Tick] = "=t0 + tr"
                });

            BatchEditValues result = program.Evaluate(
                new BatchEditValues(100, 0, 60, 12.346, 48, 24),
                Stopwatch.StartNew(),
                TimeSpan.FromSeconds(10));

            Assert.Equal(115, result.Velocity, 8);
            Assert.Equal(61, result.KeyNumber, 8);
            Assert.Equal(12.35, result.Gate, 8);
            Assert.Equal(72, result.Tick, 8);
        }
        finally
        {
            AppContext.SetData("TRUSTED_PLATFORM_ASSEMBLIES", original);
        }
    }

    [Fact]
    public void BoundedExpressionsPreserveCSharpNumericAndConditionalSemantics()
    {
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=1 / 2",
                [BatchEditField.KeyNumber] = "=Sign(k0 - 64)",
                [BatchEditField.Gate] = "=(double)(g0 > 10 ? 8 : 4)",
                [BatchEditField.Tick] = "=Math.Max(t0, 24.0)"
            });

        BatchEditValues result = program.Evaluate(
            new BatchEditValues(100, 0, 60, 12, 8, 0),
            Stopwatch.StartNew(),
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.Velocity);
        Assert.Equal(-1, result.KeyNumber);
        Assert.Equal(8, result.Gate);
        Assert.Equal(24, result.Tick);
    }

    [Fact]
    public void ResultDependenciesUseADeterministicDiamondTopologicalOrder()
    {
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=k1 + g1",
                [BatchEditField.KeyNumber] = "=t1 + 1",
                [BatchEditField.Gate] = "=t1 + 2",
                [BatchEditField.Tick] = "=t0 + 3"
            });

        BatchEditValues result = program.Evaluate(
            new BatchEditValues(100, 0, 60, 12, 10, 0),
            Stopwatch.StartNew(),
            TimeSpan.FromSeconds(10));

        Assert.Equal(29, result.Velocity);
        Assert.Equal(14, result.KeyNumber);
        Assert.Equal(15, result.Gate);
        Assert.Equal(13, result.Tick);
    }

    [Theory]
    [InlineData("=new Random().Next()")]
    [InlineData("=Environment.TickCount")]
    [InlineData("=v0 = 1")]
    [InlineData("=Math.Round(v0, t0)")]
    public void BoundedExpressionsRejectCodeOutsideTheExistingNumericSurface(string expression)
    {
        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = expression,
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            }));
    }
}
