using Midora.Domain;

namespace Midora.Application;

public static class ProjectTrackColorPolicy
{
    private static readonly MidoraColor[] PureMidiColors =
    [
        new(0x6d, 0x7f, 0xa8),
        new(0x9b, 0x6a, 0x6a),
        new(0x6f, 0x93, 0x6f),
        new(0x9a, 0x81, 0x5f),
        new(0x80, 0x6f, 0xa3),
        new(0x60, 0x91, 0x8c),
        new(0x9a, 0x6f, 0x8a),
        new(0x84, 0x90, 0x64)
    ];

    internal static IReadOnlyList<MidoraColor> PureMidiTrackPalette { get; } =
        Array.AsReadOnly(PureMidiColors);

    internal static MidoraColor ColorForPureMidiTrackInsertion(
        MidoraProject project,
        int arrangementInsertionIndex)
    {
        ArgumentNullException.ThrowIfNull(project);
        if ((uint)arrangementInsertionIndex > (uint)project.ArrangementTracks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrangementInsertionIndex));
        }

        int pureMidiOrdinal = 0;
        for (int index = 0; index < arrangementInsertionIndex; index++)
        {
            if (project.ArrangementTracks[index].Kind == ArrangementTrackKind.PureMidiTrack)
            {
                pureMidiOrdinal++;
            }
        }
        return ColorForPureMidiTrackOrdinal(pureMidiOrdinal);
    }

    internal static MidoraColor ColorForPureMidiTrackOrdinal(int pureMidiOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pureMidiOrdinal);
        return PureMidiColors[pureMidiOrdinal % PureMidiColors.Length];
    }

    public static MidoraColor ResolveDisplayColor(
        MidoraProject project,
        LogicalTrack track)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        return track.ColorOverride
            ?? project.FindEventInstrumentDefinition(track)?.Color
            ?? MidoraColor.DefaultInstrument;
    }

    public static MidoraColor ResolveDisplayColor(PureMidiTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Color ?? MidoraColor.DefaultInstrument;
    }
}
