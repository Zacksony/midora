using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateValueCurveTargetSettings(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        MappingRounding rounding,
        MappingOverflow overflow) =>
        Command("Change value curve target settings", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            if (!Enum.IsDefined(rounding))
            {
                throw new ArgumentOutOfRangeException(nameof(rounding));
            }
            if (!Enum.IsDefined(overflow))
            {
                throw new ArgumentOutOfRangeException(nameof(overflow));
            }
            IntegerTargetSettingsValue old = new(
                curve.TargetSettings.Rounding,
                curve.TargetSettings.Overflow);
            IntegerTargetSettingsValue replacement = new(rounding, overflow);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetTargetSettings(curve.TargetSettings, replacement),
                _ => SetTargetSettings(curve.TargetSettings, old));
        });

    public static IProjectEditCommand UpdateValueCurvePoint(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        MidoraId pointId,
        long tick,
        double value,
        CurveInterpolation interpolation) =>
        Command("Change value curve point", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            CurvePoint point = FindCurvePoint(curve, pointId);
            ValidateValueCurvePoint(curve, pointId, tick, value, interpolation);
            CurvePoint replacement = new(project, point.Id, tick, value, interpolation);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(oldTemplateLength, checked(tick + 1));
            return Prepared(
                point != replacement || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    ReplaceRequired(curve.Points, point, replacement, "Value Curve point");
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    ReplaceRequired(curve.Points, replacement, point, "Value Curve point");
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });

    public static IProjectEditCommand DeleteValueCurvePoint(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        MidoraId pointId) =>
        Command("Delete value curve point", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            CurvePoint point = FindCurvePoint(curve, pointId);
            int originalIndex = curve.Points.IndexOf(point);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(curve.Points, point, "Value Curve point"),
                _ => InsertAt(curve.Points, originalIndex, point, "Value Curve point"));
        });

    public static IProjectEditCommand DeleteValueCurve(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId) =>
        Command("Delete value curve", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            int originalIndex = voice.Curves.IndexOf(curve);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(voice.Curves, curve, "Value Curve"),
                _ => InsertAt(voice.Curves, originalIndex, curve, "Value Curve"));
        });

    private static ValueCurve FindValueCurve(SubVoice voice, MidoraId curveId) =>
        voice.Curves.SingleOrDefault(value => value.Id == curveId)
        ?? throw new ArgumentOutOfRangeException(nameof(curveId));

    private static CurvePoint FindCurvePoint(ValueCurve curve, MidoraId pointId) =>
        curve.Points.TryGetById(pointId, out CurvePoint? value) && value is not null
            ? value
            : throw new ArgumentOutOfRangeException(nameof(pointId));

    private static void ValidateValueCurvePoint(
        ValueCurve curve,
        MidoraId pointId,
        long tick,
        double value,
        CurveInterpolation interpolation)
    {
        if (tick < 0 || tick == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation));
        }
        long endTick = tick == long.MaxValue ? long.MaxValue : tick + 1;
        if (curve.Points.CreateQuerySnapshot()
            .QueryValues(tick, endTick)
            .Any(candidate => candidate.Id != pointId && candidate.Tick == tick))
        {
            throw new InvalidOperationException(
                "Only one Value Curve point is allowed at a tick.");
        }
        (double minimum, double maximum) = ValueCurveTargetRange(curve.Target);
        if ((value < minimum || value > maximum)
            && curve.TargetSettings.Overflow == MappingOverflow.Fail)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static (double Minimum, double Maximum) ValueCurveTargetRange(
        MidiValueTarget target) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange when target.Number is >= 0 and <= 119
                && target.Number is not 91 and not 93 => (0, 127),
            MidiValueKind.PitchBend when target.Number == 0 => (-8192, 8191),
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter
                when target.Number is >= 0 and <= 16_383 => (0, 16_383),
            MidiValueKind.PitchBendRangeSemitones when target.Number == 0 => (0, 127),
            MidiValueKind.PitchBendRangeCents when target.Number == 0 => (0, 99),
            _ => throw new InvalidOperationException(
                "The Value Curve has an unsupported or invalid target.")
        };

    private static void SetTargetSettings(
        MidiIntegerTargetSettings target,
        IntegerTargetSettingsValue value)
    {
        target.Rounding = value.Rounding;
        target.Overflow = value.Overflow;
    }

    private readonly record struct IntegerTargetSettingsValue(
        MappingRounding Rounding,
        MappingOverflow Overflow);
}
