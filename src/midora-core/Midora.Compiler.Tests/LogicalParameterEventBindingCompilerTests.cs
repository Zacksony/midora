using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class LogicalParameterEventBindingCompilerTests
{
    [Theory]
    [InlineData(MidiValueKind.ControlChange, 7, 100)]
    [InlineData(MidiValueKind.ControlChange, 10, 64)]
    [InlineData(MidiValueKind.ControlChange, 11, 127)]
    [InlineData(MidiValueKind.ControlChange, 1, 0)]
    [InlineData(MidiValueKind.PitchBend, 0, 0)]
    [InlineData(MidiValueKind.PitchBendRangeSemitones, 0, 2)]
    [InlineData(MidiValueKind.PitchBendRangeCents, 0, 0)]
    public void FormalMidiTargetDefaultsAreSharedByCompilerConsumers(
        MidiValueKind kind,
        int number,
        int expected)
    {
        Assert.Equal(expected, MidiValueTargetDefaults.GetDefaultValue(new(kind, number)));
    }

    [Fact]
    public void BoundedMultiplyExpressionRemapsParameterToFactorBeforeAccumulating()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 20);
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "Factor",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 100,
            DisplayMinimum = 0,
            DisplayMaximum = 100,
            DefaultValue = 50
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        CSharpMappingFunction function = new(fixture.Project)
        {
            Name = "Quick Multiply",
            Body = "value * ((0) + (((context.LogicalParameterValue - (0)) / (100)) * (2)))"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV2.LogicalParameterValue));
        fixture.Instrument.MappingFunctions.Add(function);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.CustomCSharp,
            LogicalParameterId = parameter.Id,
            MappingFunctionId = function.Id,
            SourceMinimum = 0,
            SourceMaximum = 100,
            TargetMinimum = 0,
            TargetMaximum = 2,
            DivideByZero = DivideByZeroPolicy.Fail
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 1, 40));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 10, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult neutral = new MidoraCompiler().CompileFull(fixture.Project);
        Assert.Equal(40, GetLogicalParameterControlChange(neutral, 1));

        LogicalParameterLane lane = new(fixture.Project) { ParameterId = parameter.Id };
        lane.Points.Add(new CurvePoint(
            fixture.Project,
            tick: 0,
            value: 75,
            interpolation: CurveInterpolation.Step));
        fixture.Segment.ParameterLanes.Add(lane);

        CanonicalCompiledResult multiplied = new MidoraCompiler().CompileFull(fixture.Project);
        Assert.Equal(60, GetLogicalParameterControlChange(multiplied, 1));
    }

    private static int GetLogicalParameterControlChange(
        CanonicalCompiledResult result,
        int controller) => Assert.Single(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.LogicalParameter
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == controller).Message.Byte2;
}
