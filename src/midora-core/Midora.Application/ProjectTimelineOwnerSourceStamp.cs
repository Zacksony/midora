using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Captures the live, mutable source revision of a detached timeline-owner
/// replacement.  Owner references alone are not a sufficient publication
/// gate: the same owner root can be edited in place while an expensive
/// detached plan is waiting to publish.
/// </summary>
internal abstract class ProjectTimelineOwnerSourceStamp
{
    public static ProjectTimelineOwnerSourceStamp Capture(Segment root) =>
        new LogicalSegmentStamp(root);

    public static ProjectTimelineOwnerSourceStamp Capture(MidiSegment root) =>
        new DirectMidiSegmentStamp(root);

    public static ProjectTimelineOwnerSourceStamp Capture(
        EventInstrument owner,
        SubVoice root) =>
        new SubVoiceStamp(
            owner ?? throw new ArgumentNullException(nameof(owner)),
            root ?? throw new ArgumentNullException(nameof(root)));

    public abstract bool Matches(object root);

    private sealed class LogicalSegmentStamp : ProjectTimelineOwnerSourceStamp
    {
        private readonly long _projectStartTick;
        private readonly long _lengthTicks;
        private readonly long _contentOffsetTick;
        private readonly LogicalNoteCollection _notes;
        private readonly long _noteRevision;
        private readonly LogicalParameterLaneStamp[] _lanes;

        public LogicalSegmentStamp(Segment root)
        {
            _projectStartTick = root.ProjectStartTick;
            _lengthTicks = root.LengthTicks;
            _contentOffsetTick = root.ContentOffsetTick;
            _notes = root.Notes;
            _noteRevision = root.Notes.Generation;
            _lanes = root.ParameterLanes
                .Select(static lane => new LogicalParameterLaneStamp(lane))
                .ToArray();
        }

        public override bool Matches(object root)
        {
            if (root is not Segment value
                || value.ProjectStartTick != _projectStartTick
                || value.LengthTicks != _lengthTicks
                || value.ContentOffsetTick != _contentOffsetTick
                || !ReferenceEquals(value.Notes, _notes)
                || value.Notes.Generation != _noteRevision
                || value.ParameterLanes.Count != _lanes.Length)
            {
                return false;
            }

            for (int index = 0; index < _lanes.Length; index++)
                if (!_lanes[index].Matches(value.ParameterLanes[index])) return false;
            return true;
        }
    }

    private sealed class DirectMidiSegmentStamp : ProjectTimelineOwnerSourceStamp
    {
        private readonly long _projectStartTick;
        private readonly long _lengthTicks;
        private readonly long _contentOffsetTick;
        private readonly DirectMidiNoteCollection _notes;
        private readonly DirectMidiChannelEventCollection _channelEvents;
        private readonly OpaqueMidiEventCollection _opaqueEvents;
        private readonly long _noteRevision;
        private readonly long _channelEventRevision;
        private readonly long _opaqueEventRevision;

        public DirectMidiSegmentStamp(MidiSegment root)
        {
            _projectStartTick = root.ProjectStartTick;
            _lengthTicks = root.LengthTicks;
            _contentOffsetTick = root.ContentOffsetTick;
            _notes = root.Notes;
            _channelEvents = root.ChannelEvents;
            _opaqueEvents = root.OpaqueEvents;
            _noteRevision = root.Notes.Generation;
            _channelEventRevision = root.ChannelEvents.Generation;
            _opaqueEventRevision = root.OpaqueEvents.Generation;
        }

        public override bool Matches(object root) => root is MidiSegment value
            && value.ProjectStartTick == _projectStartTick
            && value.LengthTicks == _lengthTicks
            && value.ContentOffsetTick == _contentOffsetTick
            && ReferenceEquals(value.Notes, _notes)
            && ReferenceEquals(value.ChannelEvents, _channelEvents)
            && ReferenceEquals(value.OpaqueEvents, _opaqueEvents)
            && value.Notes.Generation == _noteRevision
            && value.ChannelEvents.Generation == _channelEventRevision
            && value.OpaqueEvents.Generation == _opaqueEventRevision;
    }

    private sealed class SubVoiceStamp : ProjectTimelineOwnerSourceStamp
    {
        private readonly EventInstrument _owner;
        private readonly long _templateLengthTicks;
        private readonly string? _name;
        private readonly int? _rootNoteOverride;
        private readonly TemplateEventCollection _events;
        private readonly long _eventRevision;
        private readonly MidiInitialStateStamp _initialState;
        private readonly SubVoiceEventMappingStamp[] _eventMappings;
        private readonly ValueCurveStamp[] _curves;

        public SubVoiceStamp(EventInstrument owner, SubVoice root)
        {
            _owner = owner;
            _templateLengthTicks = owner.TemplateLengthTicks;
            _name = root.Name;
            _rootNoteOverride = root.RootNoteOverride;
            _events = root.Events;
            _eventRevision = root.Events.Generation;
            _initialState = new(root.InitialState);
            _eventMappings = root.EventMappings
                .Select(static mapping => new SubVoiceEventMappingStamp(mapping))
                .ToArray();
            _curves = root.Curves
                .Select(static curve => new ValueCurveStamp(curve))
                .ToArray();
        }

        public override bool Matches(object root)
        {
            if (root is not SubVoice value
                || _owner.TemplateLengthTicks != _templateLengthTicks
                || value.Name != _name
                || value.RootNoteOverride != _rootNoteOverride
                || !ReferenceEquals(value.Events, _events)
                || value.Events.Generation != _eventRevision
                || !_initialState.Matches(value.InitialState)
                || value.EventMappings.Count != _eventMappings.Length
                || value.Curves.Count != _curves.Length)
            {
                return false;
            }

            for (int index = 0; index < _eventMappings.Length; index++)
            {
                if (!_eventMappings[index].Matches(value.EventMappings[index])) return false;
            }
            for (int index = 0; index < _curves.Length; index++)
                if (!_curves[index].Matches(value.Curves[index])) return false;
            return true;
        }
    }

    private sealed class LogicalParameterLaneStamp
    {
        private readonly LogicalParameterLane _lane;
        private readonly MidoraId _id;
        private readonly MidoraId _parameterId;
        private readonly CurvePointCollection _points;
        private readonly long _pointRevision;

        public LogicalParameterLaneStamp(LogicalParameterLane lane)
        {
            _lane = lane;
            _id = lane.Id;
            _parameterId = lane.ParameterId;
            _points = lane.Points;
            _pointRevision = lane.Points.Generation;
        }

        public bool Matches(LogicalParameterLane lane) => ReferenceEquals(lane, _lane)
            && lane.Id == _id
            && lane.ParameterId == _parameterId
            && ReferenceEquals(lane.Points, _points)
            && lane.Points.Generation == _pointRevision;
    }

    private sealed class ValueCurveStamp
    {
        private readonly ValueCurve _curve;
        private readonly MidoraId _id;
        private readonly MidiValueTarget _target;
        private readonly MappingRounding _rounding;
        private readonly MappingOverflow _overflow;
        private readonly CurvePointCollection _points;
        private readonly long _pointRevision;

        public ValueCurveStamp(ValueCurve curve)
        {
            _curve = curve;
            _id = curve.Id;
            _target = curve.Target;
            _rounding = curve.TargetSettings.Rounding;
            _overflow = curve.TargetSettings.Overflow;
            _points = curve.Points;
            _pointRevision = curve.Points.Generation;
        }

        public bool Matches(ValueCurve curve) => ReferenceEquals(curve, _curve)
            && curve.Id == _id
            && curve.Target == _target
            && curve.TargetSettings.Rounding == _rounding
            && curve.TargetSettings.Overflow == _overflow
            && ReferenceEquals(curve.Points, _points)
            && curve.Points.Generation == _pointRevision;
    }

    private sealed class MidiInitialStateStamp
    {
        private readonly int? _bankMsb;
        private readonly int? _bankLsb;
        private readonly int? _program;
        private readonly int? _pitchBend;
        private readonly int? _pitchBendRangeSemitones;
        private readonly int? _pitchBendRangeCents;
        private readonly KeyValuePair<int, int>[] _controllers;
        private readonly KeyValuePair<int, int>[] _registeredParameters;
        private readonly KeyValuePair<int, int>[] _nonRegisteredParameters;

        public MidiInitialStateStamp(MidiInitialState state)
        {
            _bankMsb = state.BankMsb;
            _bankLsb = state.BankLsb;
            _program = state.Program;
            _pitchBend = state.PitchBend;
            _pitchBendRangeSemitones = state.PitchBendRangeSemitones;
            _pitchBendRangeCents = state.PitchBendRangeCents;
            _controllers = Freeze(state.Controllers);
            _registeredParameters = Freeze(state.RegisteredParameters);
            _nonRegisteredParameters = Freeze(state.NonRegisteredParameters);
        }

        public bool Matches(MidiInitialState state) => state.BankMsb == _bankMsb
            && state.BankLsb == _bankLsb
            && state.Program == _program
            && state.PitchBend == _pitchBend
            && state.PitchBendRangeSemitones == _pitchBendRangeSemitones
            && state.PitchBendRangeCents == _pitchBendRangeCents
            && Matches(_controllers, state.Controllers)
            && Matches(_registeredParameters, state.RegisteredParameters)
            && Matches(_nonRegisteredParameters, state.NonRegisteredParameters);

        private static KeyValuePair<int, int>[] Freeze(IReadOnlyDictionary<int, int> values) =>
            values.OrderBy(static value => value.Key).ToArray();

        private static bool Matches(
            IReadOnlyList<KeyValuePair<int, int>> expected,
            IReadOnlyDictionary<int, int> current)
        {
            if (expected.Count != current.Count) return false;
            for (int index = 0; index < expected.Count; index++)
            {
                KeyValuePair<int, int> item = expected[index];
                if (!current.TryGetValue(item.Key, out int value) || value != item.Value)
                    return false;
            }
            return true;
        }
    }

    private sealed class SubVoiceEventMappingStamp
    {
        private readonly SubVoiceEventMapping _mapping;
        private readonly TemplateEventMappingTarget _target;
        private readonly MappingRounding _rounding;
        private readonly MappingOverflow _overflow;
        private readonly MappingChain _steps;
        private readonly MidoraId _stepsId;
        private readonly bool _isEnabled;
        private readonly ValueMappingStepStamp[] _stepValues;

        public SubVoiceEventMappingStamp(SubVoiceEventMapping mapping)
        {
            _mapping = mapping;
            _target = mapping.Target;
            _rounding = mapping.TargetSettings.Rounding;
            _overflow = mapping.TargetSettings.Overflow;
            _steps = mapping.Steps;
            _stepsId = mapping.Steps.Id;
            _isEnabled = mapping.Steps.IsEnabled;
            _stepValues = mapping.Steps
                .Select(static step => new ValueMappingStepStamp(step))
                .ToArray();
        }

        public bool Matches(SubVoiceEventMapping mapping)
        {
            if (!ReferenceEquals(mapping, _mapping)
                || mapping.Target != _target
                || mapping.TargetSettings.Rounding != _rounding
                || mapping.TargetSettings.Overflow != _overflow
                || !ReferenceEquals(mapping.Steps, _steps)
                || mapping.Steps.Id != _stepsId
                || mapping.Steps.IsEnabled != _isEnabled
                || mapping.Steps.Count != _stepValues.Length)
            {
                return false;
            }
            for (int index = 0; index < _stepValues.Length; index++)
                if (!_stepValues[index].Matches(mapping.Steps[index])) return false;
            return true;
        }
    }

    private sealed class ValueMappingStepStamp
    {
        private readonly ValueMappingStep _step;
        private readonly MidoraId _id;
        private readonly bool _isEnabled;
        private readonly MappingSource _source;
        private readonly MappingOperation _operation;
        private readonly MidoraId? _logicalParameterId;
        private readonly MidoraId? _envelopeId;
        private readonly MidoraId? _mappingFunctionId;
        private readonly double _constant;
        private readonly double _sourceMinimum;
        private readonly double _sourceMaximum;
        private readonly double _targetMinimum;
        private readonly double _targetMaximum;
        private readonly MappingInputOverflow _inputOverflow;
        private readonly DivideByZeroPolicy _divideByZero;

        public ValueMappingStepStamp(ValueMappingStep step)
        {
            _step = step;
            _id = step.Id;
            _isEnabled = step.IsEnabled;
            _source = step.Source;
            _operation = step.Operation;
            _logicalParameterId = step.LogicalParameterId;
            _envelopeId = step.EnvelopeId;
            _mappingFunctionId = step.MappingFunctionId;
            _constant = step.Constant;
            _sourceMinimum = step.SourceMinimum;
            _sourceMaximum = step.SourceMaximum;
            _targetMinimum = step.TargetMinimum;
            _targetMaximum = step.TargetMaximum;
            _inputOverflow = step.InputOverflow;
            _divideByZero = step.DivideByZero;
        }

        public bool Matches(ValueMappingStep step) => ReferenceEquals(step, _step)
            && step.Id == _id
            && step.IsEnabled == _isEnabled
            && step.Source == _source
            && step.Operation == _operation
            && step.LogicalParameterId == _logicalParameterId
            && step.EnvelopeId == _envelopeId
            && step.MappingFunctionId == _mappingFunctionId
            && step.Constant.Equals(_constant)
            && step.SourceMinimum.Equals(_sourceMinimum)
            && step.SourceMaximum.Equals(_sourceMaximum)
            && step.TargetMinimum.Equals(_targetMinimum)
            && step.TargetMaximum.Equals(_targetMaximum)
            && step.InputOverflow == _inputOverflow
            && step.DivideByZero == _divideByZero;
    }
}
