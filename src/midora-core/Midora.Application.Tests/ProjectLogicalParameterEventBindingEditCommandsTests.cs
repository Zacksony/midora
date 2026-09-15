using Midora.Compiler;
using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectLogicalParameterEventBindingEditCommandsTests
{
    [Fact]
    public void AllScopeFreezesCurrentSubVoicesAndCreatesEmptyOwnersWithoutEvents()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        IProjectEditCommand command = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            new LogicalParameterEventBindingRequest
            {
                Name = "Expression",
                Target = MidiValueTarget.ControlChange(11),
                Operation = LogicalParameterEventBindingOperation.Override,
                SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
                SourceMinimum = 0,
                SourceMaximum = 127
            });

        IPreparedProjectEdit prepared = command.Prepare(project);
        SubVoice futureVoice = new(project) { Name = "Future" };
        instrument.SubVoices.Add(futureVoice);
        int[] eventCounts = instrument.SubVoices.Select(value => value.Events.Count).ToArray();

        prepared.Apply(project);

        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        Assert.Equal(127, parameter.DefaultValue);
        Assert.Equal(2, instrument.ParameterMappings.Count);
        Assert.DoesNotContain(instrument.ParameterMappings, value => value.SubVoiceId == futureVoice.Id);
        Assert.All(instrument.ParameterMappings, mapping =>
        {
            Assert.Equal(parameter.Id, mapping.ParameterId);
            Assert.Equal(MidiValueTarget.ControlChange(11), mapping.Target);
            Assert.Equal(MappingOverflow.Clamp, mapping.TargetSettings.Overflow);
            ValueMappingStep step = Assert.Single(mapping.Steps);
            Assert.Equal(MappingSource.LogicalParameter, step.Source);
            Assert.Equal(MappingOperation.Override, step.Operation);
            Assert.Equal(parameter.Id, step.LogicalParameterId);
        });
        Assert.All(instrument.SubVoices.Take(2), voice =>
            Assert.Single(voice.EventMappings, value => value.Target
                == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11))));
        Assert.DoesNotContain(futureVoice.EventMappings, value => value.Target
            == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)));
        Assert.Equal(eventCounts, instrument.SubVoices.Select(value => value.Events.Count));

        prepared.Undo(project);

        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.ParameterMappings);
        Assert.All(instrument.SubVoices, voice => Assert.DoesNotContain(
            voice.EventMappings,
            value => value.Target
                == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11))));
        Assert.Equal(eventCounts, instrument.SubVoices.Select(value => value.Events.Count));
    }

    [Fact]
    public void AddBindingIsOneHistoryEntryAndUndoRedoReuseEveryIdentity()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            new LogicalParameterEventBindingRequest
            {
                Name = "Expression Offset",
                Target = MidiValueTarget.ControlChange(11),
                Operation = LogicalParameterEventBindingOperation.Add,
                SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.Selected,
                SubVoiceIds = instrument.SubVoices.Select(value => value.Id).ToArray(),
                SourceMinimum = -127,
                SourceMaximum = 127
            }));

        Assert.Single(document.History);
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        LogicalParameterMapping[] mappings = instrument.ParameterMappings.ToArray();
        SubVoiceEventMapping[] owners = instrument.SubVoices.Select(voice => voice.EventMappings.Single(
            value => value.Target
                == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)))).ToArray();
        ValueMappingStep[] steps = mappings.Select(value => Assert.Single(value.Steps)).ToArray();
        long highWater = project.NextStableId;
        Assert.Equal(0, parameter.DefaultValue);

        document.Undo();

        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Equal(highWater, project.NextStableId);

        document.Redo();

        Assert.Same(parameter, Assert.Single(instrument.LogicalParameters));
        Assert.Equal(mappings, instrument.ParameterMappings);
        Assert.Equal(owners, instrument.SubVoices.Select(voice => voice.EventMappings.Single(
            value => value.Target
                == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)))));
        Assert.Equal(steps, instrument.ParameterMappings.Select(value => Assert.Single(value.Steps)));
        Assert.Equal(highWater, project.NextStableId);
    }

    [Fact]
    public void ExistingEventLaneOwnerAndItsEventsRemainUntouchedAcrossApplyAndUndo()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        MidiValueTarget target = MidiValueTarget.ControlChange(11);
        SubVoiceEventMapping owner = new(
            project,
            TemplateEventMidiTargets.ToMappingTarget(target));
        voice.EventMappings.Add(owner);
        TemplateEvent existingEvent = TemplateEvent.ControlChange(project, 12, 11, 64);
        voice.Events.Add(existingEvent);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        prepared.Apply(project);

        Assert.Same(owner, Assert.Single(voice.EventMappings, value => value.Target == owner.Target));
        Assert.Same(existingEvent, Assert.Single(voice.Events, value => value.Id == existingEvent.Id));

        prepared.Undo(project);

        Assert.Same(owner, Assert.Single(voice.EventMappings, value => value.Target == owner.Target));
        Assert.Same(existingEvent, Assert.Single(voice.Events, value => value.Id == existingEvent.Id));
    }

    [Fact]
    public async Task CreatedBindingRoundTripsThroughTheCurrentProjectFormatWithoutInventingEvents()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MidoraTests",
            $"quick-binding-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "binding.midora");
        Directory.CreateDirectory(directory);
        try
        {
            (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
            using (project)
            {
                IPreparedProjectEdit prepared =
                    ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                        instrument.Id,
                        new LogicalParameterEventBindingRequest
                        {
                            Name = "Unknown CC",
                            Target = MidiValueTarget.ControlChange(2),
                            Operation = LogicalParameterEventBindingOperation.Add,
                            SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
                            SourceMinimum = -127,
                            SourceMaximum = 127
                        }).Prepare(project);
                prepared.Apply(project);
                _ = await new MidoraProjectPackageV1("1.0.0-dev")
                    .SaveProjectAsync(project, path);
            }

            await using MidoraProjectOpenResultV1 opened =
                await new MidoraProjectPackageV1("1.0.0-dev").OpenAsync(path);
            EventInstrument restored = Assert.Single(opened.Project.EventInstruments);
            LogicalParameterDefinition parameter = Assert.Single(restored.LogicalParameters);
            Assert.Equal("Unknown CC", parameter.Name);
            Assert.Equal(2, restored.ParameterMappings.Count);
            Assert.All(restored.ParameterMappings, mapping =>
            {
                Assert.Equal(MidiValueTarget.ControlChange(2), mapping.Target);
                Assert.Equal(parameter.Id, mapping.ParameterId);
                Assert.Equal(MappingOperation.Add, Assert.Single(mapping.Steps).Operation);
            });
            Assert.All(restored.SubVoices, voice => Assert.Empty(voice.Events));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SelectedSubVoiceIdsAreFrozenWhenTheCommandIsConstructed()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        List<MidoraId> selected = [instrument.SubVoices[0].Id];
        IProjectEditCommand command = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                selected,
                LogicalParameterEventBindingConflictPolicy.Append));
        selected[0] = instrument.SubVoices[1].Id;

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        LogicalParameterMapping mapping = Assert.Single(instrument.ParameterMappings);
        Assert.Equal(instrument.SubVoices[0].Id, mapping.SubVoiceId);
    }

    [Fact]
    public void RemovingAFrozenSubVoiceBeforeApplyFailsWithoutAllocatingBindingIds()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        Assert.True(instrument.SubVoices.Remove(voice));
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(project));

        Assert.Contains("selected SubVoice was removed", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Empty(voice.EventMappings);
    }

    [Fact]
    public void RemovingTheInstrumentBeforeApplyFailsWithoutAllocatingBindingIds()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                instrument.SubVoices[0].Id,
                LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        Assert.True(project.EventInstruments.Remove(instrument));
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(project));

        Assert.Contains("Event Instrument was removed or replaced", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Empty(instrument.SubVoices[0].EventMappings);
    }

    [Fact]
    public void ReplacingTheInstrumentBeforeApplyFailsWithoutAllocatingBindingIds()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                instrument.SubVoices[0].Id,
                LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        EventInstrument replacement = new(project, instrument.Id) { Name = "Replacement" };
        project.EventInstruments[project.EventInstruments.IndexOf(instrument)] = replacement;
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(project));

        Assert.Contains("Event Instrument was removed or replaced", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Empty(instrument.SubVoices[0].EventMappings);
        Assert.Empty(replacement.LogicalParameters);
        Assert.Empty(replacement.ParameterMappings);
    }

    [Fact]
    public void ApplyingThePreparedBindingToAnotherProjectFailsAndCanBeRetriedOnItsOwner()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                instrument.SubVoices[0].Id,
                LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        MidoraProject otherProject = new(project.TicksPerQuarterNote);
        long ownerHighWater = project.NextStableId;
        long otherHighWater = otherProject.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(otherProject));

        Assert.Contains("belongs to a different Project", exception.Message);
        Assert.Equal(ownerHighWater, project.NextStableId);
        Assert.Equal(otherHighWater, otherProject.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.ParameterMappings);

        prepared.Apply(project);
        Assert.Single(instrument.LogicalParameters);
        Assert.Single(instrument.ParameterMappings);
        prepared.Undo(project);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.ParameterMappings);
    }

    [Fact]
    public void RemovingAnExistingLaneOwnerBeforeApplyFailsWithoutAllocatingBindingIds()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        SubVoiceEventMapping owner = new(
            project,
            TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)));
        voice.EventMappings.Add(owner);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        Assert.True(voice.EventMappings.Remove(owner));
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(project));

        Assert.Contains("owner collection changed", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Empty(voice.EventMappings);
    }

    [Fact]
    public void AddingALaneOwnerBeforeApplyFailsWithoutAllocatingBindingIds()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        SubVoiceEventMapping addedOwner = new(
            project,
            TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)));
        voice.EventMappings.Add(addedOwner);
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(project));

        Assert.Contains("owner collection changed", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Same(addedOwner, Assert.Single(voice.EventMappings));
    }

    [Fact]
    public void StableIdExhaustionThroughDocumentSessionFailsWithoutMutationOrRollbackFailure()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        project.RestoreNextStableId(long.MaxValue - 1);
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                CreateOverrideRequest(
                    instrument.SubVoices[0].Id,
                    LogicalParameterEventBindingConflictPolicy.Append))));

        Assert.Contains("remaining stable IDs", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.Empty(instrument.SubVoices[0].EventMappings);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void ExactStableIdCapacitySucceedsAndRedoReusesTheAllocatedIdentities()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        // One Parameter + one Mapping/chain/step + one missing owner chain.
        project.RestoreNextStableId(long.MaxValue - 5);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                instrument.SubVoices[0].Id,
                LogicalParameterEventBindingConflictPolicy.Append)));

        Assert.Equal(long.MaxValue, project.NextStableId);
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        LogicalParameterMapping mapping = Assert.Single(instrument.ParameterMappings);
        SubVoiceEventMapping owner = Assert.Single(instrument.SubVoices[0].EventMappings);
        document.Undo();
        document.Redo();
        Assert.Same(parameter, Assert.Single(instrument.LogicalParameters));
        Assert.Same(mapping, Assert.Single(instrument.ParameterMappings));
        Assert.Same(owner, Assert.Single(instrument.SubVoices[0].EventMappings));
        Assert.Equal(long.MaxValue, project.NextStableId);
    }

    [Fact]
    public void AppendPlacesNewMappingAfterLastExactPeerAndRejectsNonClampPeers()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        LogicalParameterDefinition existingParameter = AddParameter(project, instrument, "Existing");
        LogicalParameterMapping first = AddMapping(
            project, instrument, existingParameter.Id, voice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping unrelated = AddMapping(
            project, instrument, existingParameter.Id, voice.Id, MidiValueTarget.ControlChange(1), clamp: true);
        LogicalParameterMapping second = AddMapping(
            project, instrument, existingParameter.Id, voice.Id, MidiValueTarget.ControlChange(11), clamp: true);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        prepared.Apply(project);

        LogicalParameterMapping created = instrument.ParameterMappings.Single(value =>
            value.ParameterId != existingParameter.Id);
        Assert.Equal([first, unrelated, second, created], instrument.ParameterMappings);

        prepared.Undo(project);
        Assert.Equal([first, unrelated, second], instrument.ParameterMappings);

        second.TargetSettings.Overflow = MappingOverflow.Fail;
        long highWater = project.NextStableId;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
                .Prepare(project));
        Assert.Contains("Append requires every existing Mapping", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Single(instrument.LogicalParameters);
        Assert.Equal([first, unrelated, second], instrument.ParameterMappings);
    }

    [Fact]
    public void AppendRejectsNonRoundPeersBeforeAllocatingAnything()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        LogicalParameterDefinition existingParameter = AddParameter(project, instrument, "Existing");
        LogicalParameterMapping existing = AddMapping(
            project,
            instrument,
            existingParameter.Id,
            voice.Id,
            MidiValueTarget.ControlChange(11),
            clamp: true);
        existing.TargetSettings.Rounding = MappingRounding.Floor;
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Append))
                .Prepare(project));

        Assert.Contains("Append requires every existing Mapping", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Equal([existingParameter], instrument.LogicalParameters);
        Assert.Equal([existing], instrument.ParameterMappings);
        Assert.Empty(voice.EventMappings);
    }

    [Fact]
    public void AppendAcrossMultipleSubVoicesFailsAtomicallyWhenAnyPeerHasIncompatibleSettings()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        SubVoice firstVoice = instrument.SubVoices[0];
        SubVoice secondVoice = instrument.SubVoices[1];
        LogicalParameterDefinition existingParameter = AddParameter(project, instrument, "Existing");
        LogicalParameterMapping compatible = AddMapping(
            project,
            instrument,
            existingParameter.Id,
            firstVoice.Id,
            MidiValueTarget.ControlChange(11),
            clamp: true);
        LogicalParameterMapping incompatible = AddMapping(
            project,
            instrument,
            existingParameter.Id,
            secondVoice.Id,
            MidiValueTarget.ControlChange(11),
            clamp: true);
        incompatible.TargetSettings.Rounding = MappingRounding.Ceiling;
        long highWater = project.NextStableId;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                CreateOverrideRequest(
                    [firstVoice.Id, secondVoice.Id],
                    LogicalParameterEventBindingConflictPolicy.Append))
                .Prepare(project));

        Assert.Contains("Append requires every existing Mapping", exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Equal([existingParameter], instrument.LogicalParameters);
        Assert.Equal([compatible, incompatible], instrument.ParameterMappings);
        Assert.All(instrument.SubVoices, voice => Assert.Empty(voice.EventMappings));
    }

    [Fact]
    public void AppendAcrossMultipleSubVoicesPlacesEachMappingAfterItsLastExactPeer()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        SubVoice firstVoice = instrument.SubVoices[0];
        SubVoice secondVoice = instrument.SubVoices[1];
        LogicalParameterDefinition existingParameter = AddParameter(project, instrument, "Existing");
        LogicalParameterMapping firstA = AddMapping(
            project, instrument, existingParameter.Id, firstVoice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping unrelatedA = AddMapping(
            project, instrument, existingParameter.Id, firstVoice.Id, MidiValueTarget.ControlChange(1), clamp: true);
        LogicalParameterMapping firstB = AddMapping(
            project, instrument, existingParameter.Id, secondVoice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping secondA = AddMapping(
            project, instrument, existingParameter.Id, firstVoice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping unrelatedB = AddMapping(
            project, instrument, existingParameter.Id, secondVoice.Id, MidiValueTarget.ControlChange(2), clamp: true);
        LogicalParameterMapping secondB = AddMapping(
            project, instrument, existingParameter.Id, secondVoice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping[] before = instrument.ParameterMappings.ToArray();

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                [firstVoice.Id, secondVoice.Id],
                LogicalParameterEventBindingConflictPolicy.Append))
            .Prepare(project);
        prepared.Apply(project);

        LogicalParameterDefinition createdParameter = instrument.LogicalParameters.Single(value =>
            value.Id != existingParameter.Id);
        LogicalParameterMapping createdA = instrument.ParameterMappings.Single(value =>
            value.ParameterId == createdParameter.Id && value.SubVoiceId == firstVoice.Id);
        LogicalParameterMapping createdB = instrument.ParameterMappings.Single(value =>
            value.ParameterId == createdParameter.Id && value.SubVoiceId == secondVoice.Id);
        Assert.Equal(
            [firstA, unrelatedA, firstB, secondA, createdA, unrelatedB, secondB, createdB],
            instrument.ParameterMappings);

        prepared.Undo(project);
        Assert.Equal(before, instrument.ParameterMappings);
    }

    [Fact]
    public void ReplaceAcrossMultipleSubVoicesUsesEachFirstExactPeerSlotAndPreservesUnrelatedOrder()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        SubVoice firstVoice = instrument.SubVoices[0];
        SubVoice secondVoice = instrument.SubVoices[1];
        LogicalParameterDefinition existingParameter = AddParameter(project, instrument, "Existing");
        LogicalParameterMapping firstA = AddMapping(
            project, instrument, existingParameter.Id, firstVoice.Id, MidiValueTarget.ControlChange(11), clamp: false);
        LogicalParameterMapping unrelatedA = AddMapping(
            project, instrument, existingParameter.Id, firstVoice.Id, MidiValueTarget.ControlChange(1), clamp: true);
        LogicalParameterMapping firstB = AddMapping(
            project, instrument, existingParameter.Id, secondVoice.Id, MidiValueTarget.ControlChange(11), clamp: false);
        _ = AddMapping(
            project, instrument, existingParameter.Id, firstVoice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping unrelatedB = AddMapping(
            project, instrument, existingParameter.Id, secondVoice.Id, MidiValueTarget.ControlChange(2), clamp: true);
        _ = AddMapping(
            project, instrument, existingParameter.Id, secondVoice.Id, MidiValueTarget.ControlChange(11), clamp: true);
        LogicalParameterMapping[] before = instrument.ParameterMappings.ToArray();

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(
                [firstVoice.Id, secondVoice.Id],
                LogicalParameterEventBindingConflictPolicy.Replace))
            .Prepare(project);
        prepared.Apply(project);

        LogicalParameterDefinition createdParameter = instrument.LogicalParameters.Single(value =>
            value.Id != existingParameter.Id);
        LogicalParameterMapping replacementA = instrument.ParameterMappings.Single(value =>
            value.ParameterId == createdParameter.Id && value.SubVoiceId == firstVoice.Id);
        LogicalParameterMapping replacementB = instrument.ParameterMappings.Single(value =>
            value.ParameterId == createdParameter.Id && value.SubVoiceId == secondVoice.Id);
        Assert.Equal(
            [replacementA, unrelatedA, replacementB, unrelatedB],
            instrument.ParameterMappings);
        Assert.DoesNotContain(firstA, instrument.ParameterMappings);
        Assert.DoesNotContain(firstB, instrument.ParameterMappings);

        prepared.Undo(project);
        Assert.Equal(before, instrument.ParameterMappings);
    }

    [Fact]
    public void ReplaceRemovesEveryExactPeerAndUndoRestoresOriginalGlobalOrder()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        LogicalParameterDefinition existingParameter = AddParameter(project, instrument, "Existing");
        LogicalParameterMapping first = AddMapping(
            project, instrument, existingParameter.Id, voice.Id, MidiValueTarget.ControlChange(11), clamp: false);
        LogicalParameterMapping unrelated = AddMapping(
            project, instrument, existingParameter.Id, voice.Id, MidiValueTarget.ControlChange(1), clamp: true);
        LogicalParameterMapping second = AddMapping(
            project, instrument, existingParameter.Id, voice.Id, MidiValueTarget.ControlChange(11), clamp: true);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            CreateOverrideRequest(voice.Id, LogicalParameterEventBindingConflictPolicy.Replace))
            .Prepare(project);
        prepared.Apply(project);

        LogicalParameterMapping replacement = instrument.ParameterMappings[0];
        Assert.NotSame(first, replacement);
        Assert.Equal([replacement, unrelated], instrument.ParameterMappings);
        Assert.DoesNotContain(first, instrument.ParameterMappings);
        Assert.DoesNotContain(second, instrument.ParameterMappings);

        prepared.Undo(project);

        Assert.Equal([first, unrelated, second], instrument.ParameterMappings);
        prepared.Apply(project);
        Assert.Equal([replacement, unrelated], instrument.ParameterMappings);
    }

    [Fact]
    public void MultiplyCreatesOneSharedSafeFunctionAndNeutralInverseMappedDefault()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(2);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
            instrument.Id,
            new LogicalParameterEventBindingRequest
            {
                Name = "Expression Factor",
                Target = MidiValueTarget.ControlChange(11),
                Operation = LogicalParameterEventBindingOperation.Multiply,
                SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
                SourceMinimum = 0,
                SourceMaximum = 100,
                FactorMinimum = 0,
                FactorMaximum = 2
            }).Prepare(project);

        prepared.Apply(project);

        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        CSharpMappingFunction function = Assert.Single(instrument.MappingFunctions);
        Assert.Equal(50, parameter.DefaultValue);
        Assert.Equal(MappingExpressionAbiV3.Version, function.AbiVersion);
        Assert.Equal([nameof(MappingContextV2.LogicalParameterValue)],
            function.DeclaredContextFields);
        Assert.Contains("context.LogicalParameterValue == (50) ? 1", function.Body);
        Assert.All(instrument.ParameterMappings, mapping =>
        {
            ValueMappingStep step = Assert.Single(mapping.Steps);
            Assert.Equal(MappingOperation.CustomCSharp, step.Operation);
            Assert.Equal(function.Id, step.MappingFunctionId);
            Assert.Equal(parameter.Id, step.LogicalParameterId);
        });

        prepared.Undo(project);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        prepared.Apply(project);
        Assert.Same(function, Assert.Single(instrument.MappingFunctions));
    }

    [Fact]
    public void MultiplyCommandCompilesToNeutralThenPreciselyRemappedHeldTargetValues()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        SubVoice voice = instrument.SubVoices[0];
        voice.Events.Add(TemplateEvent.ControlChange(project, 0, 1, 40));
        voice.Events.Add(TemplateEvent.Note(project, 0, 10, 60, 100));
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 20 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        ProjectEditExecution execution = document.Execute(
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                new LogicalParameterEventBindingRequest
                {
                    Name = "Factor",
                    Target = MidiValueTarget.ControlChange(1),
                    Operation = LogicalParameterEventBindingOperation.Multiply,
                    SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
                    SourceMinimum = 0,
                    SourceMaximum = 100,
                    FactorMinimum = 0,
                    FactorMaximum = 2
                }));

        Assert.Equal(40, GetLogicalParameterControlChange(execution.CompilationResult, 1));
        using (MidoraCompiler fullCompiler = new())
        {
            CanonicalCompiledResult full = fullCompiler.CompileFull(project);
            Assert.Equal(full.Fingerprint, execution.CompilationResult.Fingerprint);
            Assert.Equal(full.IsConsumable, execution.CompilationResult.IsConsumable);
        }
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        lane.Points.Add(new CurvePoint(project, 0, 75, CurveInterpolation.Step));
        segment.ParameterLanes.Add(lane);

        CanonicalCompiledResult multiplied = new MidoraCompiler().CompileFull(project);
        Assert.Equal(60, GetLogicalParameterControlChange(multiplied, 1));
    }

    [Theory]
    [InlineData(0, 127, 0.0, 2.0, "exact Integer source default")]
    [InlineData(0, 100, 2.0, 3.0, "must contain the neutral factor 1")]
    [InlineData(5, 5, 1.0, 1.0, "strictly increasing")]
    public void InvalidMultiplyRangesFailBeforeAllocatingAnything(
        int sourceMinimum,
        int sourceMaximum,
        double factorMinimum,
        double factorMaximum,
        string expectedMessage)
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        long highWater = project.NextStableId;

        Exception exception = Assert.ThrowsAny<ArgumentException>(() =>
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                new LogicalParameterEventBindingRequest
                {
                    Name = "Factor",
                    Target = MidiValueTarget.ControlChange(11),
                    Operation = LogicalParameterEventBindingOperation.Multiply,
                    SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
                    SourceMinimum = sourceMinimum,
                    SourceMaximum = sourceMaximum,
                    FactorMinimum = factorMinimum,
                    FactorMaximum = factorMaximum
                }).Prepare(project));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
    }

    [Fact]
    public void InvalidTargetOrPartialVoiceSelectionFailsWithoutMutation()
    {
        (MidoraProject project, EventInstrument instrument) = CreateFixture(1);
        long highWater = project.NextStableId;
        int ownerCount = instrument.SubVoices[0].EventMappings.Count;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                new LogicalParameterEventBindingRequest
                {
                    Name = "Reverb",
                    Target = MidiValueTarget.ControlChange(91),
                    Operation = LogicalParameterEventBindingOperation.Override,
                    SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.All,
                    SourceMinimum = 0,
                    SourceMaximum = 127
                }).Prepare(project));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                instrument.Id,
                CreateOverrideRequest(
                    MidoraId.FromSequence(project.NextStableId + 100),
                    LogicalParameterEventBindingConflictPolicy.Append))
                .Prepare(project));

        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(instrument.LogicalParameters);
        Assert.Empty(instrument.MappingFunctions);
        Assert.Empty(instrument.ParameterMappings);
        Assert.All(instrument.SubVoices, value => Assert.Equal(ownerCount, value.EventMappings.Count));
    }

    private static LogicalParameterEventBindingRequest CreateOverrideRequest(
        MidoraId voiceId,
        LogicalParameterEventBindingConflictPolicy policy) =>
        CreateOverrideRequest([voiceId], policy);

    private static LogicalParameterEventBindingRequest CreateOverrideRequest(
        IReadOnlyCollection<MidoraId> voiceIds,
        LogicalParameterEventBindingConflictPolicy policy) => new()
        {
            Name = "Expression",
            Target = MidiValueTarget.ControlChange(11),
            Operation = LogicalParameterEventBindingOperation.Override,
            SubVoiceScope = LogicalParameterEventBindingSubVoiceScope.Selected,
            SubVoiceIds = voiceIds,
            SourceMinimum = 0,
            SourceMaximum = 127,
            ConflictPolicy = policy
        };

    private static int GetLogicalParameterControlChange(
        CanonicalCompiledResult result,
        int controller) => Assert.Single(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.LogicalParameter
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == controller).Message.Byte2;

    private static (MidoraProject Project, EventInstrument Instrument) CreateFixture(int voiceCount)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        while (instrument.SubVoices.Count < voiceCount)
            instrument.SubVoices.Add(new SubVoice(project) { Name = $"Voice {instrument.SubVoices.Count + 1}" });
        return (project, instrument);
    }

    private static LogicalParameterDefinition AddParameter(
        MidoraProject project,
        EventInstrument instrument,
        string name)
    {
        LogicalParameterDefinition parameter = new(project)
        {
            Name = name,
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 127,
            DisplayMinimum = 0,
            DisplayMaximum = 127,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        return parameter;
    }

    private static LogicalParameterMapping AddMapping(
        MidoraProject project,
        EventInstrument instrument,
        MidoraId parameterId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        bool clamp)
    {
        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameterId,
            SubVoiceId = subVoiceId,
            Target = target
        };
        mapping.TargetSettings.Overflow = clamp ? MappingOverflow.Clamp : MappingOverflow.Fail;
        instrument.ParameterMappings.Add(mapping);
        return mapping;
    }
}
