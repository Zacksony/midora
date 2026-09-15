using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedTemplateLaneDelete(MidoraProject project,
        EventInstrument instrument, SubVoice voice, MidiValueTarget target, bool confirmed)
    {
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
        var source = voice.Events.CreateQuerySnapshot();
        var requested = TemplateEventMidiTargets.ToMappingTarget(target);
        if (!voice.EventMappings.Any(m => m.Target == requested))
            throw new ArgumentOutOfRangeException(nameof(target), "The requested SubVoice event lane does not exist.");
        using var records = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(scope.Resources);
        using var affected = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        for (int ordinal = 0; ordinal < source.Count; ordinal++)
        {
            scope.Checkpoint(ordinal, source.Count);
            var old = source.GetByOrdinal(ordinal);
            if (!BoundedTemplateMatchesTarget(old, target)) continue;
            if (!confirmed) throw new InvalidOperationException("Deleting a non-empty SubVoice event lane requires explicit confirmation.");
            bool keep = target.Kind == MidiValueKind.BankMsb && old.HasBankLsb
                || target.Kind == MidiValueKind.BankLsb && old.HasBankMsb;
            records.Add(new(ordinal, !keep, !keep ? old : old with
            {
                HasBankMsb = target.Kind != MidiValueKind.BankMsb && old.HasBankMsb,
                HasBankLsb = target.Kind != MidiValueKind.BankLsb && old.HasBankLsb
            }), scope.Token);
            affected.Add(old.Id, scope.Token);
        }
        records.Seal(); affected.Seal();
        var ownedStore = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(scope.Resources);
        BoundedTimelineEditValueSource<TemplateEventSnapshotValue>? changesSource = null;
        try
        {
            ownedStore.AddRange(records.ReadValues(scope.Token), scope.Token); ownedStore.Seal(); ownedStore.SpillResidentPages(scope.Token);
            changesSource = new(ownedStore);
            var replacement = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, scope.Token);
            replacement.Events.Clear(); replacement.Events.AdoptEditedSnapshot(project, source, changesSource, null, scope.Token);
            bool pitchRange = target.Kind is MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents;
            var semitones = TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.PitchBendRangeSemitones);
            var cents = TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.PitchBendRangeCents);
            replacement.EventMappings.RemoveAll(m => m.Target == requested || pitchRange && (m.Target == semitones || m.Target == cents));
            var changes = EventInstrumentChange(instrument.Id);
            ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(changes, voice, replacement, affected);
            var edit = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project, instrument, voice, replacement, changes,
                null, null, stamp);
            return new OwnedBoundedPreparedEdit(edit, [changesSource]);
        }
        catch { if (changesSource is not null) changesSource.Dispose(); else ownedStore.Dispose(); throw; }
    }
}
