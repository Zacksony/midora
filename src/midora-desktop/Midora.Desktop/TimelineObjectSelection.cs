using System.Collections;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>A frozen, background-resolved menu target. Never resolve it on the Dispatcher.</summary>
internal sealed record TimelineObjectSelection(
    TimelineObjectOwner Owner, CompressedMidoraIdSet Ids,
    CompressedMidoraIdSet Notes, CompressedMidoraIdSet Events, CompressedMidoraIdSet Opaque,
    WorkspaceTimelineSelectionSource NoteSource,
    WorkspaceTimelineSelectionSource? EventSource,
    WorkspaceTimelineSelectionSource? EventQuantizeScope)
{
    public CompressedMidoraIdSet InstrumentMembers { get; init; } = CompressedMidoraIdSet.Empty;
    public bool IsMixed => (Notes.Count != 0 ? 1 : 0) + (Events.Count != 0 ? 1 : 0) + (InstrumentMembers.Count != 0 ? 1 : 0) > 1;
    public bool ContainsOpaque => Opaque.Count != 0;
    public bool CanDelete => Ids.Count != 0;
    public bool CanUseNoteTools => Notes.Count != 0;
    public bool CanUseEventValueTools => Events.Count != 0 && !ContainsOpaque && EventSource is not null;
    public bool CanQuantizeEvents => Events.Count != 0 && !ContainsOpaque && EventQuantizeScope is not null;
    public bool CanCopyOrCut => !IsMixed && !ContainsOpaque && (InstrumentMembers.Count != 0 || Notes.Count != 0 || EventSource is not null
        || Events.Count != 0 && Owner.Kind is ProjectTimelineOwnerKind.DirectMidiSegment or ProjectTimelineOwnerKind.SubVoice);
    public WorkspaceTimelineSelectionSource? HomogeneousSource => IsMixed || ContainsOpaque ? null
        : Notes.Count != 0 ? NoteSource : EventSource;

    public static TimelineObjectSelection Capture(MidoraProject project, TimelineObjectOwner owner,
        CompressedMidoraIdSet frozenIds, CancellationToken token = default,
        IProgress<TimelineEditPreparationProgress>? progress = null,
        long maximumBuilderWorkingBytes = PagedEditResourceBudget.DefaultMaximumWorkingBytes)
    {
        ArgumentNullException.ThrowIfNull(frozenIds);
        long pageCount = 0, previousPage = -1;
        foreach (var id in frozenIds)
        {
            token.ThrowIfCancellationRequested();
            long page = (id.Value - 1) >> 12;
            if (page != previousPage) pageCount++;
            previousPage = page;
        }
        // Four disjoint/classification builders plus compressed outputs,
        // sorted page entries and dictionary resize overlap. Pages, not item
        // count, bound this operation (dense million-note sets stay cheap).
        const long bytesPerInputPage = 4 * 1536;
        if (maximumBuilderWorkingBytes < 4096
            || pageCount > (maximumBuilderWorkingBytes - 4096) / bytesPerInputPage)
            throw new InvalidOperationException("The timeline selection partition exceeds its temporary working-memory budget. Select fewer objects.");
        var noteBuilder = CompressedMidoraIdSet.CreateBuilder();
        var eventBuilder = CompressedMidoraIdSet.CreateBuilder();
        var opaqueBuilder = CompressedMidoraIdSet.CreateBuilder();
        var instrumentBuilder = CompressedMidoraIdSet.CreateBuilder();
        if (owner.Kind != ProjectTimelineOwnerKind.LogicalSegment)
        {
            var context = InstrumentChangeSelectionQuery.Capture(project, new(owner.OwnerId,
                owner.Kind == ProjectTimelineOwnerKind.SubVoice ? owner.EventInstrumentId : null));
            foreach (var group in InstrumentChangeSelectionQuery.EnumerateGroups(context.Groups, frozenIds, token))
                foreach (var id in group.MemberIds) instrumentBuilder.Add(id);
        }
        var instrumentMembers = instrumentBuilder.Build();
        WorkspaceTimelineSelectionSource? eventSource = null, quantize = null;
        // These descriptors are per lane, not per selected point. A million
        // points must not repeat the Project/Definition metadata search.
        Dictionary<MidoraId, WorkspaceTimelineSelectionSource?> logicalSources = [];
        bool sawEvent = false, oneSource = true, oneQuantize = true;
        foreach (var member in ProjectTimelineObjectSelection.ReadMembers(project, owner, frozenIds, token, progress))
        {
            token.ThrowIfCancellationRequested();
            if (instrumentMembers.Contains(member.Id)) continue;
            if (member.Kind == TimelineObjectSelectionKind.Note) { noteBuilder.Add(member.Id); continue; }
            eventBuilder.Add(member.Id);
            if (member.Kind == TimelineObjectSelectionKind.Opaque)
            { opaqueBuilder.Add(member.Id); oneSource = oneQuantize = false; continue; }
            WorkspaceTimelineSelectionSource? source;
            if (owner.Kind == ProjectTimelineOwnerKind.LogicalSegment && member.LaneId is { } laneId)
            {
                if (!logicalSources.TryGetValue(laneId, out source))
                {
                    if ((logicalSources.Count + 1L) * 256 + pageCount * bytesPerInputPage + 4096 > maximumBuilderWorkingBytes)
                        throw new InvalidOperationException("The timeline lane partition exceeds its temporary working-memory budget. Select fewer objects.");
                    logicalSources.Add(laneId, source = EventSourceFor(project, owner, member));
                }
            }
            else source = EventSourceFor(project, owner, member);
            WorkspaceTimelineSelectionSource? memberQuantize = source?.QuantizeScope ?? owner.Kind switch
            {
                ProjectTimelineOwnerKind.DirectMidiSegment => new(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, owner.OwnerId),
                ProjectTimelineOwnerKind.SubVoice => new(WorkspaceTimelineSelectionKind.SubVoiceEventPoint, owner.EventInstrumentId, owner.OwnerId),
                _ => null
            };
            if (!sawEvent) { eventSource = source; quantize = memberQuantize; sawEvent = true; }
            else
            {
                oneSource &= eventSource == source;
                oneQuantize &= quantize == memberQuantize;
            }
            if (source is null) oneSource = false;
            if (memberQuantize is null) oneQuantize = false;
        }
        var notes = noteBuilder.Build(); var events = eventBuilder.Build(); var opaque = opaqueBuilder.Build();
        token.ThrowIfCancellationRequested();
        return new(owner, frozenIds, notes, events, opaque, NoteSourceFor(owner),
            oneSource ? eventSource : null, oneQuantize ? quantize : null) { InstrumentMembers = instrumentMembers };
    }

    internal static WorkspaceTimelineSelectionSource NoteSourceFor(TimelineObjectOwner owner) => owner.Kind switch
    {
        ProjectTimelineOwnerKind.LogicalSegment => new(WorkspaceTimelineSelectionKind.LogicalNote, owner.OwnerId),
        ProjectTimelineOwnerKind.DirectMidiSegment => new(WorkspaceTimelineSelectionKind.DirectMidiNote, owner.OwnerId),
        ProjectTimelineOwnerKind.SubVoice => new(WorkspaceTimelineSelectionKind.TemplateNote, owner.EventInstrumentId, owner.OwnerId),
        _ => throw new ArgumentOutOfRangeException(nameof(owner))
    };

    private static WorkspaceTimelineSelectionSource? EventSourceFor(MidoraProject project,
        TimelineObjectOwner owner, TimelineObjectSelectionMember member)
    {
        if (owner.Kind == ProjectTimelineOwnerKind.DirectMidiSegment && member.DirectKind is { } kind)
        {
            int number = kind is DirectMidiChannelEventKind.ControlChange or DirectMidiChannelEventKind.PolyphonicKeyPressure
                or DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff ? member.Number : 0;
            return new(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, owner.OwnerId,
                DirectMidiEventKind: kind, DirectMidiData1: number,
                PointMaximum: kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127);
        }
        if (owner.Kind == ProjectTimelineOwnerKind.SubVoice)
        {
            MidiValueTarget? target = member.TemplateKind switch
            {
                TemplateEventKind.ControlChange => MidiValueTarget.ControlChange(member.Number),
                TemplateEventKind.Program => MidiValueTarget.Program,
                TemplateEventKind.PitchBend => MidiValueTarget.PitchBend,
                TemplateEventKind.RegisteredParameter => MidiValueTarget.Rpn(member.Number),
                TemplateEventKind.NonRegisteredParameter => MidiValueTarget.Nrpn(member.Number),
                // A list row owns the complete event, not an implicitly chosen
                // half of a composite Bank/Pitch Bend Range value. Properties
                // can edit both; scalar tools require an unambiguous target.
                TemplateEventKind.Bank when member.HasBankMsb && member.HasBankLsb => null,
                TemplateEventKind.Bank when member.HasBankMsb => MidiValueTarget.BankMsb,
                TemplateEventKind.Bank when member.HasBankLsb => MidiValueTarget.BankLsb,
                _ => null
            };
            if (target is null) return null;
            var (minimum, maximum) = InstrumentWorkspaceViewModel.MidiValueRange(target.Value);
            return new(WorkspaceTimelineSelectionKind.SubVoiceEventPoint, owner.EventInstrumentId, owner.OwnerId,
                target, PointMinimum: minimum, PointMaximum: maximum);
        }
        var location = TimelineWorkspaceViewModel.FindSegment(project, owner.OwnerId);
        if (location is null) return null;
        var lane = location.Value.Segment.ParameterLanes.FirstOrDefault(l => l.Id == member.LaneId);
        var definition = project.FindEventInstrumentDefinition(location.Value.Track)?.LogicalParameters
            .FirstOrDefault(p => p.Id == lane?.ParameterId);
        if (definition is null) return null;
        double minimumValue = definition.DisplayMinimum, maximumValue = definition.DisplayMaximum;
        if (!double.IsFinite(minimumValue) || !double.IsFinite(maximumValue) || maximumValue <= minimumValue)
            (minimumValue, maximumValue) = (definition.Minimum, definition.Maximum);
        return new(WorkspaceTimelineSelectionKind.LogicalParameterPoint, owner.OwnerId, member.LaneId,
            PointMinimum: minimumValue, PointMaximum: maximumValue);
    }

    /// <summary>One lazy pass into the existing compressed projection, not a second ID array.</summary>
    internal static PreparedWorkspaceSelectionProjection MergeResult(WorkspaceSelection selection,
        IReadOnlyList<MidoraId> result, CompressedMidoraIdSet retained, CancellationToken token)
        => selection.PrepareProjection(new JoinedIds(result, retained), null, null, token);

    private sealed class JoinedIds(IReadOnlyList<MidoraId> result, CompressedMidoraIdSet retained) : IReadOnlyList<MidoraId>
    {
        public int Count => checked(result.Count + retained.Count);
        public MidoraId this[int index] => index >= 0 && index < result.Count ? result[index]
            : index >= result.Count && index < Count ? retained.ElementAt(index - result.Count)
            : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<MidoraId> GetEnumerator()
        { foreach (var id in result) yield return id; foreach (var id in retained) yield return id; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
