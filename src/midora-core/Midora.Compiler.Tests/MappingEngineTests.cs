using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Compiler.Tests;

public sealed class MappingEngineTests
{
    [Fact]
    public void FriendlyCcEditingDoesNotChangeMappingCurrentValueOrContextDomain()
    {
        using var project = new MidoraProject(480);
        var step = new ValueMappingStep(project)
        { Source = MappingSource.Constant, Constant = .5, Operation = MappingOperation.Multiply };
        Assert.Equal(48, Apply(step, 96, Context() with { TargetOriginalValue = 96 }));
        var function = new CSharpMappingFunction(project) { Name = "Half", Body = "value * 0.5" };
        using MappingExpressionCompiler compiler = new();
        var context = Context() with { CurrentValue = 96, TargetOriginalValue = 96 };
        Assert.Equal(48, compiler.GetOrCompile(function)(96, in context));
    }

    [Theory]
    [InlineData(MappingOperation.Override, 4)]
    [InlineData(MappingOperation.Add, 14)]
    [InlineData(MappingOperation.Multiply, 40)]
    [InlineData(MappingOperation.Remap, 50)]
    [InlineData(MappingOperation.Clamp, 5)]
    [InlineData(MappingOperation.Ignore, 10)]
    [InlineData(MappingOperation.ConstantPlusValue, 7)]
    [InlineData(MappingOperation.ConstantMultiplyValue, 12)]
    [InlineData(MappingOperation.ConstantMinusValue, -1)]
    [InlineData(MappingOperation.ValueMinusConstant, 1)]
    [InlineData(MappingOperation.ConstantDivideValue, 0.75)]
    [InlineData(MappingOperation.ValueDivideConstant, 4.0 / 3.0)]
    public void EveryBuiltInOperationHasItsSpecifiedCompositionSemantics(
        MappingOperation operation,
        double expected)
    {
        MidoraProject project = new(480);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.TriggerVelocity,
            Operation = operation,
            Constant = 3,
            SourceMinimum = 0,
            SourceMaximum = 8,
            TargetMinimum = 0,
            TargetMaximum = operation == MappingOperation.Clamp ? 5 : 100
        };

        double actual = Apply(step, current: 10, Context(triggerVelocity: 4));

        Assert.Equal(expected, actual, precision: 12);
    }

    [Theory]
    [InlineData(MappingSource.CurrentValue, 10)]
    [InlineData(MappingSource.TriggerNote, 61)]
    [InlineData(MappingSource.TriggerVelocity, 99)]
    [InlineData(MappingSource.GateLength, 480)]
    [InlineData(MappingSource.PitchDelta, -2)]
    [InlineData(MappingSource.TemplateTick, 7)]
    [InlineData(MappingSource.ProjectTick, 1007)]
    [InlineData(MappingSource.TemplateNote, 64)]
    [InlineData(MappingSource.TemplateVelocity, 88)]
    [InlineData(MappingSource.LogicalParameter, 2.5)]
    [InlineData(MappingSource.Envelope, 0.75)]
    [InlineData(MappingSource.Constant, 3.25)]
    public void EveryBuiltInSourceReadsTheExpectedContextValue(
        MappingSource source,
        double expected)
    {
        MidoraProject project = new(480);
        MidoraId parameterId = project.AllocateStableId();
        MidoraId envelopeId = project.AllocateStableId();
        ValueMappingStep step = new(project)
        {
            Source = source,
            Operation = MappingOperation.Override,
            LogicalParameterId = parameterId,
            EnvelopeId = envelopeId,
            Constant = 3.25
        };
        Dictionary<MidoraId, double> parameters = new() { [parameterId] = 2.5 };
        Dictionary<MidoraId, double> envelopes = new() { [envelopeId] = 0.75 };

        double actual = Apply(
            step,
            current: 10,
            Context(),
            parameters,
            envelopes);

        Assert.Equal(expected, actual, precision: 12);
    }

    [Theory]
    [InlineData(MappingInputOverflow.Clamp, 10)]
    [InlineData(MappingInputOverflow.Extrapolate, 20)]
    public void RemapInputOverflowPolicyControlsOutOfRangeInput(
        MappingInputOverflow inputOverflow,
        double expected)
    {
        MidoraProject project = new(480);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Remap,
            Constant = 2,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 10,
            InputOverflow = inputOverflow
        };

        double actual = Apply(step, current: 0, Context());

        Assert.Equal(expected, actual, precision: 12);
    }

    [Fact]
    public void RemapFailPolicyRejectsOutOfRangeInput()
    {
        MidoraProject project = new(480);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Remap,
            Constant = 2,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 10,
            InputOverflow = MappingInputOverflow.Fail
        };

        Assert.Throws<MappingException>(() => Apply(step, current: 0, Context()));
    }

    [Theory]
    [InlineData(DivideByZeroPolicy.TargetMaximum, 127)]
    [InlineData(DivideByZeroPolicy.TargetDefault, 64)]
    [InlineData(DivideByZeroPolicy.Zero, 0)]
    public void DivideByZeroFallbackReturnsConfiguredTargetValue(
        DivideByZeroPolicy policy,
        double expected)
    {
        MidoraProject project = new(480);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.ConstantDivideValue,
            Constant = 0,
            DivideByZero = policy
        };

        double actual = Apply(
            step,
            current: 10,
            Context(),
            legalMaximum: 127,
            targetDefault: 64);

        Assert.Equal(expected, actual, precision: 12);
    }

    [Theory]
    [InlineData(MappingOperation.ConstantDivideValue)]
    [InlineData(MappingOperation.ValueDivideConstant)]
    public void DivideByZeroFailPolicyRejectsBothDivisionDirections(
        MappingOperation operation)
    {
        MidoraProject project = new(480);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.Constant,
            Operation = operation,
            Constant = 0,
            DivideByZero = DivideByZeroPolicy.Fail
        };

        Assert.Throws<MappingException>(() => Apply(step, current: 10, Context()));
    }

    [Theory]
    [InlineData(DivideByZeroPolicy.TargetMaximum, 127)]
    [InlineData(DivideByZeroPolicy.TargetDefault, 64)]
    [InlineData(DivideByZeroPolicy.Zero, 0)]
    public void EmptyRemapRangeUsesTheSameDivideByZeroFallback(
        DivideByZeroPolicy policy,
        double expected)
    {
        MidoraProject project = new(480);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 1,
            SourceMaximum = 1,
            TargetMinimum = -20,
            TargetMaximum = 20,
            DivideByZero = policy
        };

        double actual = Apply(
            step,
            current: 10,
            Context(),
            legalMaximum: 127,
            targetDefault: 64);

        Assert.Equal(expected, actual, precision: 12);
    }

    private static MappingContextV2 Context(int triggerVelocity = 99) =>
        new(
            CurrentValue: 999,
            TriggerNote: 61,
            TriggerVelocity: triggerVelocity,
            GateLength: 480,
            PitchDelta: -2,
            TemplateTick: 7,
            ProjectTick: 1007,
            TemplateNote: 64,
            TemplateVelocity: 88);

    private static double Apply(
        ValueMappingStep step,
        double current,
        MappingContextV2 context,
        IReadOnlyDictionary<MidoraId, double>? parameters = null,
        IReadOnlyDictionary<MidoraId, double>? envelopes = null,
        double legalMaximum = 10_000,
        double targetDefault = 0)
    {
        using MappingEngine engine = new();
        return engine.Apply(
            current,
            [step],
            context,
            parameters ?? new Dictionary<MidoraId, double>(),
            envelopes ?? new Dictionary<MidoraId, double>(),
            new Dictionary<MidoraId, CSharpMappingFunction>(),
            legalMinimum: -10_000,
            legalMaximum,
            targetDefault,
            MappingOverflow.Fail,
            allowClamp: true);
    }
}
