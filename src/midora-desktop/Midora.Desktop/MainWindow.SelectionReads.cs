using Midora.Application;
using Midora.Desktop.Presentation.Rendering;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using System.Runtime.CompilerServices;

namespace Midora.Desktop;

public partial class MainWindow
{
    private static readonly AsyncLocal<IProgress<TimelineEditPreparationProgress>?> SelectionReadProgress = new();
    private (WeakReference<ProjectDocumentSession> Document, long Revision,
        TimelineSelectionOperationContext Context, long Span)? _selectionSpanCache;

    private bool TryGetSelectionSpan(ProjectDocumentSession document, WorkspaceViewModel workspace,
        TimelineSelectionOperationContext context, out long span)
    {
        span = 0;
        if (_selectionSpanCache is { } cached
            && cached.Document.TryGetTarget(out var cachedDocument)
            && ReferenceEquals(cachedDocument, document)
            && cached.Revision == document.PublicationRevision
            && ReferenceEquals(cached.Context.Ids, context.Ids)
            && cached.Context == context)
        {
            span = cached.Span;
            return true;
        }
        // Point render items may have a synthetic 1-tick width. Never use that
        // width for Scale's exact max(point.Tick)-min(point.Tick) contract.
        TimelineItemKind? kind = context.Kind switch
        {
            TimelineSelectionObjectKind.LogicalNotes => TimelineItemKind.LogicalNote,
            TimelineSelectionObjectKind.DirectMidiNotes => TimelineItemKind.DirectMidiNote,
            TimelineSelectionObjectKind.TemplateNotes => TimelineItemKind.TemplateNote,
            TimelineSelectionObjectKind.Segments or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments => TimelineItemKind.Segment,
            _ => null
        };
        bool ownerMatches = kind == TimelineItemKind.Segment
            ? workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement }
            : workspace.ObjectId == context.OwnerId
                && (context.Kind != TimelineSelectionObjectKind.TemplateNotes
                    || workspace is InstrumentWorkspaceViewModel instrument
                        && instrument.ActiveSubVoiceId == context.SecondaryId);
        return kind is { } itemKind && ownerMatches
            && TryGetCurrentSelectionSpan(workspace, document.PublicationRevision, context.Ids, itemKind, out span);
    }

    internal static bool TryGetCurrentSelectionSpan(WorkspaceViewModel workspace, long publicationRevision,
        CompressedMidoraIdSet ids, TimelineItemKind kind, out long span)
    {
        span = 0;
        var snapshot = workspace.SelectionSnapshot;
        if (kind is not (TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote or TimelineItemKind.Segment)
            || workspace.PresentationDocumentRevision != publicationRevision
            || snapshot.Revision != workspace.Selection.Revision
            || !ReferenceEquals(snapshot.IdSet, ids)
            || !snapshot.MetricsAreComplete
            || !snapshot.TryGetMetrics(kind, out var metrics)
            || metrics.Count != ids.Count || metrics.Count == 0) return false;
        span = checked(metrics.MaximumEndTick - metrics.MinimumStartTick);
        return true;
    }

    private static T WithSelectionReadProgress<T>(IProgress<TimelineEditPreparationProgress> progress, Func<T> read)
    {
        var previous = SelectionReadProgress.Value;
        SelectionReadProgress.Value = progress;
        try { return read(); }
        finally { SelectionReadProgress.Value = previous; }
    }

    internal static long MeasurePointSelectionSpan(IEnumerable<long> ticks, CancellationToken token = default)
    {
        long minimum = long.MaxValue, maximum = long.MinValue;
        bool any = false;
        foreach (long tick in ticks)
        {
            token.ThrowIfCancellationRequested();
            minimum = Math.Min(minimum, tick);
            maximum = Math.Max(maximum, tick);
            any = true;
        }
        return any ? checked(maximum - minimum) : 0;
    }

    private static IEnumerable<T> ReadMetricValues<T>(MidoraProject project, ITimelineObjectSource<T> source,
        IReadOnlyCollection<MidoraId> ids, CancellationToken token)
    {
        if (typeof(T) == typeof(LogicalNoteSnapshotValue)) return ReadScalars<LogicalNoteSnapshotValue>();
        if (typeof(T) == typeof(DirectMidiNoteValue)) return ReadScalars<DirectMidiNoteValue>();
        if (typeof(T) == typeof(TemplateEventSnapshotValue)) return ReadScalars<TemplateEventSnapshotValue>();
        if (typeof(T) == typeof(CurvePointSnapshotValue)) return ReadScalars<CurvePointSnapshotValue>();
        if (typeof(T) == typeof(DirectMidiChannelEventValue)) return ReadScalars<DirectMidiChannelEventValue>();
        if (typeof(T) == typeof(MidoraId)) return ReadScalars<MidoraId>();
        if (typeof(T) == typeof(OpaqueMidiEventValue)) return ReadOpaque();
        throw new NotSupportedException("The timeline selection has no bounded scalar reader.");

        IEnumerable<T> ReadScalars<TScalar>() where TScalar : unmanaged
        {
            foreach (var value in ProjectTimelineReadPreparation.ReadSelectedValues(project,
                (ITimelineObjectSource<TScalar>)(object)source, ids, token, SelectionReadProgress.Value,
                requireAll: false, preserveFormalOrder: false))
            {
                TScalar scalar = value;
                yield return Unsafe.As<TScalar, T>(ref scalar);
            }
        }
        IEnumerable<T> ReadOpaque()
        {
            foreach (var value in ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(project,
                (OpaqueMidiEventObjectSource)(object)source, ids, token, SelectionReadProgress.Value, requireAll: false))
            {
                OpaqueMidiEventValue scalar = value;
                yield return Unsafe.As<OpaqueMidiEventValue, T>(ref scalar);
            }
        }
    }

    private static MidoraId MetricValueId<T>(T value)
    {
        if (typeof(T) == typeof(LogicalNoteSnapshotValue)) return Unsafe.As<T, LogicalNoteSnapshotValue>(ref value).Id;
        if (typeof(T) == typeof(DirectMidiNoteValue)) return Unsafe.As<T, DirectMidiNoteValue>(ref value).Id;
        if (typeof(T) == typeof(TemplateEventSnapshotValue)) return Unsafe.As<T, TemplateEventSnapshotValue>(ref value).Id;
        if (typeof(T) == typeof(CurvePointSnapshotValue)) return Unsafe.As<T, CurvePointSnapshotValue>(ref value).Id;
        if (typeof(T) == typeof(DirectMidiChannelEventValue)) return Unsafe.As<T, DirectMidiChannelEventValue>(ref value).Id;
        if (typeof(T) == typeof(OpaqueMidiEventValue)) return Unsafe.As<T, OpaqueMidiEventValue>(ref value).Id;
        if (typeof(T) == typeof(MidoraId)) return Unsafe.As<T, MidoraId>(ref value);
        throw new NotSupportedException("The timeline selection has no scalar identity reader.");
    }
}
