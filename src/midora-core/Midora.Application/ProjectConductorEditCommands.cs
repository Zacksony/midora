using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateTempo(MidoraId tempoId, long tick, decimal beatsPerMinute) =>
        SetConductorValue("Change tempo", ConductorKind.Tempo, tempoId, tick, bpm: beatsPerMinute);

    public static IProjectEditCommand DeleteTempo(MidoraId tempoId) =>
        DeleteConductorValue(ConductorKind.Tempo, tempoId);

    public static IProjectEditCommand UpdateTimeSignature(MidoraId timeSignatureId, long tick, int numerator, int denominator) =>
        SetConductorValue("Change time signature", ConductorKind.TimeSignature, timeSignatureId, tick,
            primary: numerator, secondary: denominator);

    public static IProjectEditCommand DeleteTimeSignature(MidoraId timeSignatureId) =>
        DeleteConductorValue(ConductorKind.TimeSignature, timeSignatureId);

    public static IProjectEditCommand UpdateKeySignature(MidoraId keySignatureId, long tick, int sharpsFlats, bool isMinor) =>
        SetConductorValue("Change key signature", ConductorKind.KeySignature, keySignatureId, tick,
            primary: sharpsFlats, flag: isMinor);

    public static IProjectEditCommand DeleteKeySignature(MidoraId keySignatureId) =>
        DeleteConductorValue(ConductorKind.KeySignature, keySignatureId);

    public static IProjectEditCommand UpdateProjectMarker(MidoraId markerId, long tick, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SetConductorValue("Change project marker", ConductorKind.Marker, markerId, tick, text: name);
    }

    public static IProjectEditCommand DeleteProjectMarker(MidoraId markerId) =>
        DeleteConductorValue(ConductorKind.Marker, markerId);

    public static IProjectEditCommand UpdateProjectEndMarker(long tick) =>
        new BoundedProjectEditCommand("Move project end marker", (project, token, progress) =>
        {
            ProjectEndMarker marker = project.Conductor.EndMarker
                ?? throw new InvalidOperationException("The Project End Marker does not exist.");
            return ((IProgressReportingProjectEditCommand)SetConductorValue(
                "Move project end marker", ConductorKind.End, marker.Id, tick)).Prepare(project, token, progress);
        });

    public static IProjectEditCommand DeleteProjectEndMarker() =>
        new BoundedProjectEditCommand("Delete project end marker", (project, token, progress) =>
        {
            ProjectEndMarker marker = project.Conductor.EndMarker
                ?? throw new InvalidOperationException("The Project End Marker does not exist.");
            return ((IProgressReportingProjectEditCommand)DeleteConductorSelection([marker.Id])).Prepare(project, token, progress);
        });

    private static int FindIndex<T>(
        IList<T> values,
        MidoraId id,
        Func<T, MidoraId> getId,
        string parameterName)
    {
        int result = -1;
        for (int index = 0; index < values.Count; index++)
        {
            if (getId(values[index]) != id)
            {
                continue;
            }
            if (result >= 0)
            {
                throw new InvalidOperationException("The Project stable ID is duplicated.");
            }
            result = index;
        }
        return result >= 0 ? result : throw new ArgumentOutOfRangeException(parameterName);
    }


    private static void ValidateConductorTick(long tick, string parameterName)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateTempo(decimal beatsPerMinute)
    {
        if (beatsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute));
        }
        decimal exact;
        try
        {
            exact = 60_000_000m / beatsPerMinute;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute),
                beatsPerMinute,
                "Tempo cannot be represented by the MIDI 1.0 Set Tempo field.");
        }
        decimal rounded = decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
        if (rounded is < 1m or > 16_777_215m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute),
                beatsPerMinute,
                "Tempo cannot be represented by the MIDI 1.0 Set Tempo field.");
        }
    }

    private static void ReplaceRequired<T>(
        List<T> values,
        T expected,
        T replacement,
        string objectName)
        where T : class
    {
        int index = values.IndexOf(expected);
        if (index < 0)
        {
            throw new InvalidOperationException($"The {objectName} is no longer present.");
        }
        values[index] = replacement;
    }
}
