namespace Midora.Compiler.Tests;

public sealed class GeneratorExpressionProgramTests
{
    private static Dictionary<BatchEditField, string?> Note(string? v = null, string? k = null,
        string? g = null, string? t = null) => new()
    {
        [BatchEditField.Velocity] = v, [BatchEditField.KeyNumber] = k,
        [BatchEditField.Gate] = g, [BatchEditField.Tick] = t
    };

    [Fact]
    public void GeneratorProfilesAreDistinctAndDoNotExpandBatchVariables()
    {
        Assert.Equal("midora.tool.generate-note/v1", NumericExpressionProfiles.GenerateNote.Id);
        Assert.Equal("midora.tool.generate-event/v1", NumericExpressionProfiles.GenerateEvent.Id);
        Assert.Equal(10, NumericExpressionProfiles.GenerateNote.VariableCount);
        Assert.Equal(6, NumericExpressionProfiles.GenerateEvent.VariableCount);
        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(Note(t: "=i")));
        Assert.Throws<ArgumentException>(() => GeneratorExpressionProgram.Compile(Note(t: "=p0")));
    }

    [Fact]
    public void DependenciesUsePreviousFieldsAndThisCandidateResults()
    {
        using var program = GeneratorExpressionProgram.Compile(Note(v: "=k1+1", k: "=g1+2", g: "=v0*2", t: "=tr+i+t0"));
        var value = program.Evaluate(new(3, 0, 40, 2, 7, 999), 4);
        Assert.Equal(6, value.Gate);
        Assert.Equal(8, value.KeyNumber);
        Assert.Equal(9, value.Velocity);
        Assert.Equal(18, value.Tick);
    }

    [Fact]
    public void EmptyFieldsAreIdentityAndMathFunctionsAreImplicitlyImported()
    {
        using var program = GeneratorExpressionProgram.Compile(Note(k: "=Round(60+12*Sin(PI*i/2))"));
        var value = program.Evaluate(new(64, 0, 0, 120, 300, 300), 1);
        Assert.Equal(72, value.KeyNumber);
        Assert.Equal(64, value.Velocity);
        Assert.Equal(120, value.Gate);
        Assert.Equal(300, value.Tick);
    }

    [Theory]
    [InlineData("=v1", null)]
    [InlineData("=k1", "=v1")]
    [InlineData("=k1", "=g1+v1")]
    public void CyclicResultDependenciesAreRejected(string velocity, string? key)
        => Assert.Throws<ArgumentException>(() => GeneratorExpressionProgram.Compile(Note(v: velocity, k: key)));

    [Theory]
    [InlineData("12")]
    [InlineData("+1")]
    [InlineData("=")]
    [InlineData("=new Random().Next()")]
    [InlineData("=System.IO.File.ReadAllText(\"x\").Length")]
    [InlineData("=i++")]
    [InlineData("=(() => 1)()")]
    [InlineData("=v0; while(true){}")]
    public void NonExpressionFormsAndUnsafeSyntaxAreRejected(string expression)
        => Assert.Throws<ArgumentException>(() => GeneratorExpressionProgram.Compile(Note(v: expression)));

    [Theory]
    [InlineData("=1/0.0")]
    [InlineData("=Sqrt(-1)")]
    [InlineData("=Pow(1e300, 2)")]
    public void NonFiniteValuesFailDuringEvaluation(string expression)
    {
        using var program = GeneratorExpressionProgram.Compile(Note(v: expression));
        Assert.Throws<InvalidOperationException>(() => program.Evaluate(new(1, 0, 0, 1, 0, 0), 0));
    }

    [Fact]
    public void EventProfileAndRepeatedEvaluationsDoNotRetainResultSlots()
    {
        using var program = GeneratorExpressionProgram.Compile(new Dictionary<BatchEditField, string?>
        { [BatchEditField.PointValue] = "=t1*2", [BatchEditField.Tick] = "=t0+i" });
        Assert.Equal(22, program.Evaluate(new(0, 2, 0, 0, 10, 10), 1).PointValue);
        Assert.Equal(6, program.Evaluate(new(0, 2, 0, 0, 1, 1), 2).PointValue);
        program.Dispose();
        Assert.Throws<ObjectDisposedException>(() => program.Evaluate(default, 0));
    }

    [Fact]
    public void CandidateEvaluationDoesNotAllocatePerIteration()
    {
        using var program = GeneratorExpressionProgram.Compile(Note(v: "=64+i%32", k: "=60+i%12", g: "=24", t: "=t0+3"));
        var value = new BatchEditValues(1, 0, 0, 1, 0, 0);
        for (int i = 0; i < 100; i++) value = program.Evaluate(value, i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) value = program.Evaluate(value, i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(30_300, value.Tick);
    }
}
