using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class TimelineObjectListCandidateTests
{
    [Fact]
    public void LogicalPrefixStreamMatchesFormalValuesAcrossPagesAndReplacementOverlay()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project);
        for (int i = 0; i < 2300; i++)
            segment.Notes.Add(new(project) { StartTick = i % 719, LengthTicks = 1 + i % 91, Note = i % 128, Velocity = 90 });
        var original = segment.Notes.CreateQuerySnapshot();
        var originalValues = original.EnumerateAll().ToArray();
        for (int i = 0; i < 2300; i += 17)
        { segment.Notes[i].StartTick += 10000; segment.Notes[i].Velocity = 40; }
        for (int i = 2299; i >= 0; i -= 29) segment.Notes.RemoveAt(i);
        segment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 7, Note = 61, Velocity = 120 });
        var edited = segment.Notes.CreateQuerySnapshot();
        foreach (long end in new long[] { 0, 1, 16, 256, 719, 10000, long.MaxValue })
        {
            Assert.Equal(originalValues.Where(value => value.StartTick < end).OrderBy(value => value.Id),
                original.EnumerateListCandidates(end).OrderBy(value => value.Id));
            Assert.Equal(edited.EnumerateAll().Where(value => value.StartTick < end).OrderBy(value => value.Id),
                edited.EnumerateListCandidates(end).OrderBy(value => value.Id));
        }
    }

    [Fact]
    public void TemplatePrefixIncludesNotesAndZeroLengthEventsWithoutDuplicates()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        for (int i = 0; i < 1800; i++)
            voice.Events.Add(new(project) { Tick = i % 149, Kind = i % 2 == 0 ? TemplateEventKind.Note : TemplateEventKind.ControlChange,
                LengthTicks = i % 2 == 0 ? 9000 : 0, Number = i % 2 == 0 ? i % 128 : 11, Value = i % 127 + 1 });
        _ = voice.Events.CreateQuerySnapshot();
        for (int i = 0; i < 1800; i += 31) { voice.Events[i].Tick += 2000; voice.Events[i].Value = 3; }
        for (int i = 1799; i >= 0; i -= 47) voice.Events.RemoveAt(i);
        var snapshot = voice.Events.CreateQuerySnapshot();
        foreach (long end in new long[] { 0, 1, 16, 149, 2000, long.MaxValue })
            Assert.Equal(snapshot.EnumerateAll().Where(value => value.Tick < end).OrderBy(value => value.Id),
                snapshot.EnumerateListCandidates(end).OrderBy(value => value.Id));
    }

    [Fact]
    public void PointPrefixPreservesFrozenValuesAndSupportsCancellation()
    {
        using MidoraProject project = new(192);
        LogicalParameterLane lane = new(project) { ParameterId = new(12345) };
        for (int i = 0; i < 2000; i++) lane.Points.Add(new(project, i % 127, i, CurveInterpolation.Step));
        var original = lane.Points.CreateQuerySnapshot();
        var values = original.EnumerateAll().ToArray();
        for (int i = 0; i < 2000; i += 17) lane.Points[i] = lane.Points[i] with { Tick = lane.Points[i].Tick + 1000, Value = -9 };
        var edited = lane.Points.CreateQuerySnapshot();
        foreach (long end in new long[] { 0, 1, 16, 127, 1000, long.MaxValue })
        {
            Assert.Equal(values.Where(value => value.Tick < end).OrderBy(value => value.Id),
                original.EnumerateListCandidates(end).OrderBy(value => value.Id));
            Assert.Equal(edited.EnumerateAll().Where(value => value.Tick < end).OrderBy(value => value.Id),
                edited.EnumerateListCandidates(end).OrderBy(value => value.Id));
        }
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => edited.EnumerateListCandidates(long.MaxValue, cancel.Token).ToArray());
        Assert.Equal(edited.Count, edited.EnumerateListCandidates(long.MaxValue).Count());
    }
}
