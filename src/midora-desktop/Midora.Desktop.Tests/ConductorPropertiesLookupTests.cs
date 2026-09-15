using System.Collections;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class ConductorPropertiesLookupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleEventPropertiesAndCommandCreationUseOwnerIdIndexWithoutScanning(bool marker)
    {
        using MidoraProject project = new(480);
        var tempos = new CountingSource<TempoChange>(Enumerable.Range(0, 20_000)
            .Select(index => new TempoChange(project, index * 10L, 120m)).ToArray(), static value => value.Id);
        var markers = new CountingSource<ProjectMarker>(Enumerable.Range(0, 20_000)
            .Select(index => new ProjectMarker(project, index * 10L, $"Marker {index}")).ToArray(), static value => value.Id);
        project.Conductor.Tempos.AdoptSource(tempos);
        project.Conductor.Markers.AdoptSource(markers);
        _ = project.Conductor.Tempos.CaptureQuerySnapshot();
        _ = project.Conductor.Markers.CaptureQuerySnapshot();
        MidoraId selected = marker ? markers.LastId : tempos.LastId;
        TimelineWorkspaceViewModel workspace = new(WorkspaceKey.ForType(WorkspaceKind.ConductorTrack),
            "Conductor", TimelineWorkspaceMode.Conductor);
        workspace.Selection.Replace(selected);
        InstrumentCatalogResolver resolver = new(new InstrumentCatalogState(true, [], []),
            Array.Empty<InstrumentCatalogSoundFontEntry>());
        ObjectPropertiesViewModel properties = new();
        tempos.ReadValues = 0;
        markers.ReadValues = 0;

        ObjectPropertiesProjection.Rebuild(properties, project, workspace, resolver);
        Assert.Equal(marker ? "Project Marker" : "Tempo", properties.Title);
        Assert.Equal("199990", Assert.Single(properties.Fields, field => field.Key == "conductor.tick").Value);
        Assert.InRange(tempos.ReadValues + markers.ReadValues, 1, 2);

        tempos.ReadValues = 0;
        markers.ReadValues = 0;
        var command = ObjectPropertiesProjection.CreateEditCommand(project, workspace,
            marker ? "conductor.name" : "conductor.bpm", marker ? "Changed" : "90");
        Assert.NotNull(command);
        Assert.InRange(tempos.ReadValues + markers.ReadValues, 1, 2);
        workspace.CancelBackgroundPresentationWork();
    }

    [Theory]
    [InlineData(0, "Tempo", "conductor.bpm", "120")]
    [InlineData(1, "Time Signature", "conductor.numerator", "4")]
    [InlineData(2, "Key Signature", "conductor.sharpsFlats", "-2")]
    [InlineData(3, "Project Marker", "conductor.name", "Frozen marker")]
    [InlineData(4, "Project End Marker", "conductor.tick", "960")]
    public void DetachedSelectionReadsFrozenOwnerWithoutMutableWorkspace(
        int kind, string title, string fieldKey, string expectedValue)
    {
        using MidoraProject project = new(480);
        KeySignatureChange key = new(project, 480, -2, true);
        ProjectMarker marker = new(project, 480, "Frozen marker");
        ProjectEndMarker end = new(project, 960);
        project.Conductor.KeySignatures.Add(key);
        project.Conductor.Markers.Add(marker);
        project.Conductor.EndMarker = end;
        MidoraId[] ids = [project.Conductor.Tempos[0].Id, project.Conductor.TimeSignatures[0].Id,
            key.Id, marker.Id, end.Id];
        ConductorTrack frozen = project.Conductor.CloneFrozen();
        project.Conductor.Tempos.Clear();
        project.Conductor.TimeSignatures.Clear();
        project.Conductor.KeySignatures.Clear();
        project.Conductor.Markers.Clear();
        end.Tick = 1920;

        ObjectPropertiesViewModel result = ObjectPropertiesProjection.ReadConductorSelection(
            frozen, ids[kind], CancellationToken.None);
        Assert.Equal(title, result.Title);
        Assert.Equal(expectedValue, Assert.Single(result.Fields, field => field.Key == fieldKey).Value);
        Assert.NotSame(result, ObjectPropertiesProjection.ReadConductorSelection(
            frozen, ids[kind], CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedSelectionHonorsCancellationBeforeAndDuringIndexedRead(bool cancelDuringRead)
    {
        using MidoraProject project = new(480);
        var source = new CountingSource<TempoChange>(Enumerable.Range(0, 256)
            .Select(index => new TempoChange(project, index * 10L, 120m)).ToArray(), static value => value.Id);
        project.Conductor.Tempos.AdoptSource(source);
        ConductorTrack frozen = project.Conductor.CloneFrozen();
        using CancellationTokenSource cancellation = new();
        source.ReadValues = 0;
        if (cancelDuringRead) source.OnRead = cancellation.Cancel;
        else cancellation.Cancel();

        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            ObjectPropertiesProjection.ReadConductorSelection(frozen, source.LastId, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (cancelDuringRead) Assert.InRange(source.ReadValues, 1, 2);
        else Assert.Equal(0, source.ReadValues);
    }

    [Fact]
    public void DetachedMissingSelectionHasNoEditableFields()
    {
        using MidoraProject project = new(480);
        ProjectMarker absent = new(project, 0, "Not added");
        ObjectPropertiesViewModel result = ObjectPropertiesProjection.ReadConductorSelection(
            project.Conductor.CloneFrozen(), absent.Id, CancellationToken.None);
        Assert.Equal("Missing Conductor Event", result.Title);
        Assert.Empty(result.Fields);
    }

    private sealed class CountingSource<T>(T[] values, Func<T, MidoraId> id) : IIndexedImmutableTimelineValueSource<T>
    {
        private readonly long _firstId = id(values[0]).Value;
        public MidoraId LastId => id(values[^1]);
        public int ReadValues { get; set; }
        public Action? OnRead { get; set; }
        public int Count => values.Length;
        public int PageCapacity => 128;
        public T this[int index] { get { ReadValues++; OnRead?.Invoke(); return values[index]; } }
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            int first = checked(pageIndex * PageCapacity);
            int count = Math.Min(PageCapacity, Count - first);
            ReadValues += count;
            OnRead?.Invoke();
            return values.AsMemory(first, count);
        }
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> page) { page = ReadPage(pageIndex); return true; }
        public bool TryFindOrdinalById(MidoraId target, out int ordinal)
        {
            long candidate = target.Value - _firstId;
            ordinal = candidate >= 0 && candidate < Count ? (int)candidate : -1;
            return ordinal >= 0;
        }
        public IEnumerator<T> GetEnumerator() { for (int index = 0; index < Count; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
