using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Admission accounting for non-paged catalog metadata in a private Project
/// mirror. Timeline scalar pages and immutable strings are shared, not copied.
/// This is conservative metadata accounting, not a process Working Set meter.
/// </summary>
internal static class BoundedProjectDirectoryBudget
{
    internal static IDisposable Reserve(MidoraProject project, BulkEditPreparationContext scope)
    {
        long bytes = 4096;
        // Four immutable Conductor roots are shared with the private mirror;
        // neither their record directories nor their text/value pages are copied.
        Add(4, 256);
        State(project.GlobalInitialState); State(project.GlobalResetDefaults);
        Add((long)project.Tracks.Count + project.PureMidiTracks.Count + project.MidiChannelRoots.Count
            + project.EventInstrumentUsages.Count, 1024);
        Add(project.ArrangementTracks.Count, 64);
        foreach (var track in project.Tracks)
        {
            Add(track.Segments.Count, 2048);
            foreach (var segment in track.Segments) Add(segment.ParameterLanes.Count, 1024);
        }
        foreach (var track in project.PureMidiTracks) Add(track.Segments.Count, 4096);
        foreach (var instrument in project.EventInstruments)
        {
            Add(1, 4096); State(instrument.InitialState);
            Add((long)instrument.LogicalParameters.Count + instrument.MappingFunctions.Count + instrument.Envelopes.Count, 1024);
            foreach (var parameter in instrument.LogicalParameters) Add(parameter.EnumItems.Count, 128);
            foreach (var mapping in instrument.ParameterMappings) { Add(1, 1024); Add(mapping.Steps.Count, 512); }
            foreach (var voice in instrument.SubVoices)
            {
                Add(1, 4096); State(voice.InitialState); Add(voice.Curves.Count, 1024);
                foreach (var mapping in voice.EventMappings) { Add(1, 1024); Add(mapping.Steps.Count, 512); }
            }
        }
        Add((long)project.DamagedEventInstruments.Count + project.DamagedLogicalTracks.Count
            + project.DamagedEventInstrumentUsages.Count + project.DamagedMidiChannelRoots.Count + project.DamagedPureMidiTracks.Count, 64);
        return scope.Resources.ReserveWorking(bytes);

        void Add(long count, int perRecord)
        {
            scope.Token.ThrowIfCancellationRequested();
            bytes = checked(bytes + count * perRecord);
            if (bytes > scope.Resources.Budget.MaximumWorkingBytes)
                throw new InvalidOperationException("The edit's Project catalog snapshot exceeds its working-memory budget. No changes were applied.");
        }
        void State(MidiInitialState state) => Add((long)state.Controllers.Count + state.RegisteredParameters.Count + state.NonRegisteredParameters.Count, 128);
    }
}
