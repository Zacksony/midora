using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Frozen destination of a clipboard paste. Kind describes the source payload;
/// TargetIsDirectMidi determines the destination type of compatible Note pastes.
/// These facts are command metadata, not persisted Project content.
/// </summary>
public readonly record struct ProjectClipboardPasteTarget(
    ProjectObjectClipboardKind Kind,
    MidoraId? OwnerId = null,
    MidoraId? SecondaryOwnerId = null,
    bool TargetIsDirectMidi = false,
    MidiValueTarget? MidiTarget = null);

/// <summary>
/// Bounded, conservative classification of the immutable clipboard content.
/// A null event target with OnlyEventPoints means multiple formal lanes. Mixed
/// Note/Event content and unsupported object kinds set neither Only flag.
/// Collision reduction may narrow the result but must not widen this summary.
/// </summary>
public readonly record struct ProjectClipboardPasteSelectionKinds(
    bool OnlyNotes = false,
    bool OnlyEventPoints = false,
    MidiValueTarget? MidiTarget = null,
    DirectMidiChannelEventKind? DirectMidiEventKind = null,
    int DirectMidiData1 = 0);

public interface IProjectClipboardPasteCommand : IProjectEditCommand
{
    ProjectClipboardPasteTarget PasteTarget { get; }

    /// <summary>
    /// May stream paged clipboard content on its first call; call only during
    /// cancellable background preparation, never from a UI command query.
    /// Command preparation also computes and caches this small summary.
    /// </summary>
    ProjectClipboardPasteSelectionKinds GetPasteSelectionKinds(
        CancellationToken cancellationToken = default);
}

public static partial class ProjectObjectClipboard
{
    private static ProjectClipboardPasteSelectionKinds DescribePasteSelection(
        ProjectObjectClipboardData data,
        MidiValueTarget? requestedMidiTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (data)
        {
            case LogicalNoteClipboardData:
            case DirectMidiNoteClipboardData:
                return new(OnlyNotes: true);
            case LogicalParameterLaneContentClipboardData:
            case InstrumentChangeClipboardData:
                return new(OnlyEventPoints: true);
            case DirectMidiEventClipboardData direct:
                {
                    (DirectMidiChannelEventKind Kind, int Data1)? common = null;
                    int count = 0;
                    foreach (DirectMidiEventClipboardSnapshot value in direct.Events)
                    {
                        if ((count++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                        int selector = value.Kind is DirectMidiChannelEventKind.ControlChange
                            or DirectMidiChannelEventKind.PolyphonicKeyPressure
                            or DirectMidiChannelEventKind.NoteOn
                            or DirectMidiChannelEventKind.NoteOff ? value.Data1 : 0;
                        var target = (value.Kind, selector);
                        if (common is null) common = target;
                        else if (common.Value != target)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return new(OnlyEventPoints: true);
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return count == 0 ? default : new(
                        OnlyEventPoints: true,
                        DirectMidiEventKind: common!.Value.Kind,
                        DirectMidiData1: common!.Value.Data1);
                }
            case SubVoiceTimelineEventsClipboardData template:
                {
                    bool hasNotes = false;
                    bool hasEvents = false;
                    bool unsupported = false;
                    bool mixedTargets = false;
                    bool requestedTargetSupported = requestedMidiTarget is not null;
                    MidiValueTarget? common = null;
                    int count = 0;
                    foreach (TemplateEventClipboardSnapshot value in template.Events)
                    {
                        if ((count++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                        if (value.Kind == TemplateEventKind.Note)
                        {
                            hasNotes = true;
                            if (hasEvents)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                return default;
                            }
                            continue;
                        }
                        hasEvents = true;
                        if (hasNotes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return default;
                        }
                        bool foundTarget = false;
                        bool foundRequestedTarget = false;
                        TemplateEventSnapshotValue snapshot = new(
                            default, value.Kind, value.TickOffset, value.LengthTicks,
                            value.Number, value.Value, value.SecondaryValue,
                            value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta);
                        foreach (MidiValueTarget target in TemplateEventMidiTargets.Enumerate(snapshot))
                        {
                            foundTarget = true;
                            foundRequestedTarget |= target == requestedMidiTarget;
                            if (common is null) common = target;
                            else if (common != target) mixedTargets = true;
                        }
                        unsupported |= !foundTarget;
                        requestedTargetSupported &= foundRequestedTarget;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (unsupported || hasNotes && hasEvents) return default;
                    return new(
                        OnlyNotes: hasNotes,
                        OnlyEventPoints: hasEvents,
                        MidiTarget: hasEvents && requestedTargetSupported
                            ? requestedMidiTarget : hasEvents && !mixedTargets ? common : null);
                }
            default:
                return default;
        }
    }
}
