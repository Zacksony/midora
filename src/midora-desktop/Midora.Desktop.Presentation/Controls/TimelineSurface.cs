using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Typography;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Controls;

public sealed class TimelineItemEventArgs(
    TimelineRenderItem item,
    long tick,
    int lane,
    ModifierKeys modifiers,
    bool isDoubleClick,
    bool isCopyDragStart = false,
    bool preserveSelectionForPotentialCopyDrag = false,
    bool preserveExistingSelection = false) : RoutedEventArgs
{
    public TimelineRenderItem Item { get; } = item;
    public long Tick { get; } = tick;
    public int Lane { get; } = lane;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool IsDoubleClick { get; } = isDoubleClick;
    public bool IsCopyDragStart { get; } = isCopyDragStart;
    public bool PreserveSelectionForPotentialCopyDrag { get; } =
        preserveSelectionForPotentialCopyDrag;
    public bool PreserveExistingSelection { get; } = preserveExistingSelection;
}

public sealed class TimelineMarqueeEventArgs(
    TimelineMaterializedSelection materialization,
    ModifierKeys modifiers) : RoutedEventArgs
{
    public TimelineMaterializedSelection Materialization { get; } = materialization;
    public IReadOnlyCollection<MidoraId> ItemIds { get; } = materialization.Ids;
    public ModifierKeys Modifiers { get; } = modifiers;
}

public sealed class TimelineSelectionReplacementEventArgs : RoutedEventArgs
{
    public TimelineSelectionSnapshot? BaseSelection { get; set; }
}

public sealed class TimelineRulerEventArgs(long tick) : RoutedEventArgs
{
    public long Tick { get; } = tick;
}

public sealed class TimelineTimeRangeEventArgs(long startTick, long endTick) : RoutedEventArgs
{
    public long StartTick { get; } = startTick;
    public long EndTick { get; } = endTick;
}

public sealed class TimelinePointEventArgs(
    long tick,
    int lane,
    double normalizedValue,
    ModifierKeys modifiers,
    bool isDoubleClick,
    bool isEmptyBackground = false) : RoutedEventArgs
{
    public long Tick { get; } = tick;
    public int Lane { get; } = lane;
    public double NormalizedValue { get; } = normalizedValue;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool IsDoubleClick { get; } = isDoubleClick;
    public bool IsEmptyBackground { get; } = isEmptyBackground;
}

public sealed class TimelineLanePreviewEventArgs(int lane, int pitch, int velocity) : RoutedEventArgs
{
    public int Lane { get; } = lane;
    public int Pitch { get; } = pitch;
    public int Velocity { get; } = velocity;
}

public sealed class TimelinePitchPreviewEventArgs(int pitch, int velocity) : RoutedEventArgs
{
    public int Pitch { get; } = pitch;
    public int Velocity { get; } = velocity;
}

public sealed class TimelineNotePlacementEventArgs(
    long startTick,
    long endTick,
    int pitch,
    int velocity) : RoutedEventArgs
{
    public long StartTick { get; } = startTick;
    public long EndTick { get; } = endTick;
    public long LengthTicks => Math.Max(1, checked(EndTick - StartTick));
    public int Pitch { get; } = pitch;
    public int Velocity { get; } = velocity;
}

public sealed class TimelineSegmentPlacementEventArgs(
    long startTick,
    long endTick,
    int lane) : RoutedEventArgs
{
    public long StartTick { get; } = startTick;
    public long EndTick { get; } = endTick;
    public long LengthTicks => Math.Max(1, checked(EndTick - StartTick));
    public int Lane { get; } = lane;
}

public enum TimelineSurfaceMode
{
    General,
    Arrangement,
    PianoRoll,
    EventLanes,
    Conductor,
    Velocity
}

internal readonly record struct TimelineSurfaceRenderPhaseTiming(
    TimeSpan Background,
    TimeSpan Content,
    TimeSpan Overlay,
    TimeSpan Total)
{
    public static TimelineSurfaceRenderPhaseTiming FromTimestamps(
        long start,
        long backgroundEnd,
        long contentEnd,
        long end) => new(
            System.Diagnostics.Stopwatch.GetElapsedTime(start, backgroundEnd),
            System.Diagnostics.Stopwatch.GetElapsedTime(backgroundEnd, contentEnd),
            System.Diagnostics.Stopwatch.GetElapsedTime(contentEnd, end),
            System.Diagnostics.Stopwatch.GetElapsedTime(start, end));
}

public sealed class TimelineVelocityEditEventArgs(
    IReadOnlyDictionary<MidoraId, int> velocities) : RoutedEventArgs
{
    public IReadOnlyDictionary<MidoraId, int> Velocities { get; } = velocities;
}

public sealed class TimelineEventPointEditEventArgs(
    MidoraId? directItemId,
    IReadOnlyDictionary<long, double> points) : RoutedEventArgs
{
    public MidoraId? DirectItemId { get; } = directItemId;
    public IReadOnlyDictionary<long, double> Points { get; } = points;
}

public enum TimelineToolMode
{
    Select,
    Draw,
    Erase,
    Split
}

public enum TimelineLaneHeaderCommand
{
    ToggleExpanded,
    ToggleMute,
    ToggleSolo
}

public sealed class TimelineLaneHeaderCommandEventArgs(
    int lane,
    TimelineLaneHeaderCommand command) : RoutedEventArgs
{
    public int Lane { get; } = lane;
    public TimelineLaneHeaderCommand Command { get; } = command;
}

public sealed class TimelineLaneHeaderEventArgs(
    int lane,
    bool isSharedGroupTarget = false) : RoutedEventArgs
{
    public int Lane { get; } = lane;
    public bool IsSharedGroupTarget { get; } = isSharedGroupTarget;
}

public sealed class TimelineArrangementInstrumentEventArgs(MidoraId instrumentId) : RoutedEventArgs
{
    public MidoraId InstrumentId { get; } = instrumentId;
}

public sealed class TimelineArrangementMidiRouteEventArgs(
    MidoraId trackId,
    MidoraId rootId) : RoutedEventArgs
{
    public MidoraId TrackId { get; } = trackId;
    public MidoraId RootId { get; } = rootId;
}

public sealed class TimelineLaneHeaderReorderEventArgs(
    int sourceLane,
    int targetLane,
    bool joinsTargetGroup,
    bool movesWholeGroup,
    bool insertsAfterTarget,
    bool detachesFromSourceGroup) : RoutedEventArgs
{
    public int SourceLane { get; } = sourceLane;
    public int TargetLane { get; } = targetLane;
    public bool JoinsTargetGroup { get; } = joinsTargetGroup;
    public bool MovesWholeGroup { get; } = movesWholeGroup;
    public bool InsertsAfterTarget { get; } = insertsAfterTarget;
    public bool DetachesFromSourceGroup { get; } = detachesFromSourceGroup;
}

public enum TimelineItemEditKind
{
    Move,
    ResizeStart,
    ResizeEnd
}

public sealed class TimelineItemEditEventArgs(
    TimelineRenderItem item,
    TimelineItemEditKind editKind,
    long tickDelta,
    int laneDelta,
    double valueDelta,
    ModifierKeys modifiers,
    bool copyRequested = false) : RoutedEventArgs
{
    public TimelineRenderItem Item { get; } = item;
    public TimelineItemEditKind EditKind { get; } = editKind;
    public long TickDelta { get; } = tickDelta;
    public int LaneDelta { get; } = laneDelta;
    public double ValueDelta { get; } = valueDelta;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool CopyRequested { get; } = copyRequested;
}

public sealed partial class TimelineSurface : Control
{
    private const int MaximumFormattedTextCacheEntries = 2048;
    public const double MinimumPianoLaneHeight = 3;
    public const double MaximumPianoLaneHeight = 128;
    public const double ArrangementLaneHeaderWidth = 232;
    public const double ConductorEditorLaneHeaderWidth = 150;
    private const double MinimumArrangementLaneHeight = 28;
    private const double MaximumArrangementLaneHeight = 112;
    private const double ArrangementParentLaneHeightRatio = 0.62;
    private const double MinimumArrangementParentLaneHeight = 22;
    private const int MaximumImmediateVectorNotePreviewCount = 512;

    public static readonly RoutedEvent AltGestureConsumedEvent = EventManager.RegisterRoutedEvent(
        nameof(AltGestureConsumed),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TimelineSurface));

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot),
        typeof(TimelineRenderSnapshot),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnViewportMetricsChanged));

    public static readonly DependencyProperty SelectionSnapshotProperty = DependencyProperty.Register(
        nameof(SelectionSnapshot),
        typeof(TimelineSelectionSnapshot),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnRasterProjectionChanged));

    public static readonly DependencyProperty SelectedArrangementTrackIdProperty = DependencyProperty.Register(
        nameof(SelectedArrangementTrackId),
        typeof(MidoraId?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsConductorTrackSelectedProperty = DependencyProperty.Register(
        nameof(IsConductorTrackSelected),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RulerSnapshotProperty = DependencyProperty.Register(
        nameof(RulerSnapshot),
        typeof(TimelineRenderSnapshot),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            static (sender, _) => ((TimelineSurface)sender).CancelConductorRuler()));

    public static readonly DependencyProperty StartTickProperty = DependencyProperty.Register(
        nameof(StartTick),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            0L,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnRasterProjectionChanged));

    public static readonly DependencyProperty TickSpanProperty = DependencyProperty.Register(
        nameof(TickSpan),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            3072L,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnRasterProjectionChanged));

    public static readonly DependencyProperty FirstLaneProperty = DependencyProperty.Register(
        nameof(FirstLane),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnViewportMetricsChanged,
            CoerceFirstLane));

    public static readonly DependencyProperty LaneHeightProperty = DependencyProperty.Register(
        nameof(LaneHeight),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            24d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnViewportMetricsChanged,
            CoerceLaneHeight));

    public static readonly DependencyProperty LaneHeaderWidthOverrideProperty = DependencyProperty.Register(
        nameof(LaneHeaderWidthOverride),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender,
            OnViewportMetricsChanged),
        value => double.IsNaN((double)value) || (double.IsFinite((double)value) && (double)value >= 0));

    public static readonly DependencyProperty GridStepTicksProperty = DependencyProperty.Register(
        nameof(GridStepTicks),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(192L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OperationStepTicksProperty = DependencyProperty.Register(
        nameof(OperationStepTicks),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(48L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DisplayGridUsesBarsProperty = DependencyProperty.Register(
        nameof(DisplayGridUsesBars),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OperationUsesBarsProperty = DependencyProperty.Register(
        nameof(OperationUsesBars),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty TimeSignatureMapProperty = DependencyProperty.Register(
        nameof(TimeSignatureMap),
        typeof(ProjectTimeSignatureMap),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProjectTickOffsetProperty = DependencyProperty.Register(
        nameof(ProjectTickOffset),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultCreationLengthTicksProperty = DependencyProperty.Register(
        nameof(DefaultCreationLengthTicks),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(192L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewTicksPerQuarterNoteProperty = DependencyProperty.Register(
        nameof(PreviewTicksPerQuarterNote),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            768,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnPreviewScaleChanged));

    public static readonly DependencyProperty DefaultVelocityProperty = DependencyProperty.Register(
        nameof(DefaultVelocity),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(100));

    public static readonly DependencyProperty ValueAxisMinimumProperty = DependencyProperty.Register(
        nameof(ValueAxisMinimum),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueAxisMaximumProperty = DependencyProperty.Register(
        nameof(ValueAxisMaximum),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(127d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueAxisIntegralProperty = DependencyProperty.Register(
        nameof(ValueAxisIntegral),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaybackCursorTickProperty = DependencyProperty.Register(
        nameof(PlaybackCursorTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty EditCursorTickProperty = DependencyProperty.Register(
        nameof(EditCursorTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeStartTickProperty = DependencyProperty.Register(
        nameof(RangeStartTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeEndTickProperty = DependencyProperty.Register(
        nameof(RangeEndTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeRangeStartTickProperty = DependencyProperty.Register(
        nameof(TimeRangeStartTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeRangeEndTickProperty = DependencyProperty.Register(
        nameof(TimeRangeEndTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CanExtendEventCreationRangeProperty = DependencyProperty.Register(
        nameof(CanExtendEventCreationRange), typeof(bool), typeof(TimelineSurface), new PropertyMetadata(false));

    public static readonly DependencyProperty CanDragEventValueProperty = DependencyProperty.Register(
        nameof(CanDragEventValue), typeof(bool), typeof(TimelineSurface), new PropertyMetadata(true));

    public static readonly DependencyProperty IsTimeRangeSelectionEnabledProperty =
        DependencyProperty.Register(
            nameof(IsTimeRangeSelectionEnabled),
            typeof(bool),
            typeof(TimelineSurface),
            new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty SurfaceModeProperty = DependencyProperty.Register(
        nameof(SurfaceMode),
        typeof(TimelineSurfaceMode),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            TimelineSurfaceMode.General,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnViewportMetricsChanged));

    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(
        nameof(CanEdit),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty IsSelectionFloatingToolEnabledProperty =
        DependencyProperty.Register(
            nameof(IsSelectionFloatingToolEnabled),
            typeof(bool),
            typeof(TimelineSurface),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToolModeProperty = DependencyProperty.Register(
        nameof(ToolMode),
        typeof(TimelineToolMode),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            TimelineToolMode.Select,
            FrameworkPropertyMetadataOptions.AffectsRender
                | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnToolModeChanged));

    public static readonly DependencyProperty GridVisibleProperty = DependencyProperty.Register(
        nameof(GridVisible),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightedPitchProperty = DependencyProperty.Register(
        nameof(HighlightedPitch),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyPropertyKey MaximumFirstLanePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(MaximumFirstLane),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty MaximumFirstLaneProperty = MaximumFirstLanePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey CanScrollLanesPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanScrollLanes),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty CanScrollLanesProperty = CanScrollLanesPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey VisibleLaneCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(VisibleLaneCount),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(1));

    public static readonly DependencyProperty VisibleLaneCountProperty = VisibleLaneCountPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ValueScrollOffsetProperty = DependencyProperty.Register(
        nameof(ValueScrollOffset),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueScrollOffsetChanged,
            CoerceValueScrollOffset));

    private static readonly DependencyPropertyKey ValueScrollMaximumPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ValueScrollMaximum),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0d));

    public static readonly DependencyProperty ValueScrollMaximumProperty = ValueScrollMaximumPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey CanScrollValuesPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanScrollValues),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty CanScrollValuesProperty = CanScrollValuesPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey ValueScrollViewportSizePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ValueScrollViewportSize),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(1d));

    public static readonly DependencyProperty ValueScrollViewportSizeProperty = ValueScrollViewportSizePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey PointerPositionTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PointerPositionText),
        typeof(string),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty PointerPositionTextProperty = PointerPositionTextPropertyKey.DependencyProperty;

    private readonly List<TimelineRenderItem> _visibleItems = new(capacity: 512);
    private readonly List<TimelineRenderItem> _genericRenderItems = new(capacity: 512);
    private readonly List<TimelineRenderItem> _lastCompleteGenericRenderItems = new(capacity: 512);
    private string? _lastCompleteGenericProjectionKey;
    private readonly List<TimelineRenderItem> _rulerItems = new(capacity: 64);
    private readonly List<TimelineRenderItem> _rulerQueryItems = new(capacity: 64);
    private readonly List<TimelineRenderItem> _hitItems = new(capacity: 16);
    private readonly List<TimelineGridLine> _gridLines = new(capacity: 256);
    private Brush? _penBorderBrush;
    private Brush? _penInfoBrush;
    private Brush? _penTextBrush;
    private Brush? _penRedBrush;
    private Brush? _penRedSubtleBrush;
    private Brush? _penSuccessBrush;
    private Brush? _penSuccessSubtleBrush;
    private Brush? _penSegmentSelectionBrush;
    private Pen? _borderPen;
    private Pen? _infoPen;
    private Pen? _textPen;
    private Pen? _redPen;
    private Pen? _successPen;
    private Pen? _selectionPen;
    private Pen? _segmentSelectionPen;
    private Pen? _trackSelectionPen;
    private Pen? _beatGridPen;
    private Pen? _editCursorPen;
    private Pen? _marqueePen;
    private Pen? _dragPreviewPen;
    private Pen? _warningDashPen;
    private Pen? _groupDetachBoundaryPen;
    private readonly Dictionary<(string Value, double Size, int Weight, Brush Brush), FormattedText>
        _textCache = [];
    private readonly Queue<(string Value, double Size, int Weight, Brush Brush)>
        _textCacheOrder = [];
    private readonly Dictionary<string, SolidColorBrush> _fallbackBrushes =
        new(StringComparer.Ordinal);
    private PianoKeyboardDrawingKey? _pianoKeyboardDrawingKey;
    private DrawingGroup? _pianoKeyboardDrawing;
    internal int PianoKeyboardDrawingBuildCount { get; private set; }
    private readonly Dictionary<uint, SegmentAccentResources> _segmentAccentResources = [];
    private readonly Dictionary<uint, SolidColorBrush> _rawAccentBrushes = [];
    private double _cachedPixelsPerDip;
    private TimelineRenderSnapshot? _arrangementRowLayoutSnapshot;
    private double _arrangementRowLayoutLaneHeight = double.NaN;
    private double[] _arrangementRowOffsets = [];
    private Point? _panOrigin;
    private long _panStartTick;
    private int _panFirstLane;
    private double _panValueScrollOffset;
    private Point? _marqueeOrigin;
    private Point? _marqueeCurrent;
    private long _marqueeAnchorTick;
    private int _marqueeAnchorLane;
    private double _marqueeAnchorNormalizedValue;
    private TimelineRenderItem? _dragItem;
    private TimelineItemEditKind _dragKind;
    private Point _dragOrigin;
    private long _dragOriginTick;
    private int _dragOriginLane;
    private long _dragCurrentTick;
    private int _dragCurrentLane;
    private bool _dragActivated;
    private bool _dragCopyRequested;
    private bool _deferredControlClickToggle;
    private bool _deferredPlainDrawSegmentSelection;
    private bool _dragTimeLocked;
    private ModifierKeys _dragModifiers;
    private TimelineSelectionSnapshot? _dragPreviewSelection;
    private long _dragPreviewSelectionRevision = -1;
    private bool _dragPreviewSelectionPrepared;
    private long _dragPreviewMinimumStartTick;
    private int _dragPreviewMinimumLane;
    private int _dragPreviewMaximumLane;
    private double _dragPreviewMinimumValue;
    private double _dragPreviewMaximumValue;
    private readonly List<TimelineRenderItem> _dragPreviewItems = new(capacity: 512);
    private readonly List<TimelineRenderItem> _dragPreviewQueryScratch = new(capacity: 512);
    private StreamGeometry? _dragPreviewGeometry;
    private DragPreviewGeometryKey? _dragPreviewGeometryKey;
    private DragPreviewIdPrefetchSignature? _dragPreviewIdPrefetchSignature;
    private CancellationTokenSource _resizePreviewCancellation = new();
    private long _resizePreviewGeneration;
    private ResizePreviewSignature? _resizePreviewSignature;
    private readonly HashSet<TimelineRasterCacheKey> _requestedResizePreviewKeys = [];
    private readonly List<PianoTileDrawEntry> _resizePreviewTiles = new(capacity: 32);
    private ResizeLaneLayoutKey? _resizeLaneLayoutKey;
    private double[] _resizeLaneTopsDevice = [];
    private double[] _resizeLaneHeightsDevice = [];
    private int _dragPitchPreviewAnchorLane;
    private int _dragPitchPreviewLastPitch = -1;
    private int _dragPitchPreviewVelocity;
    private bool _dragPitchPreviewActive;
    private TimelineLanePreviewEventArgs? _activeLanePreview;
    private long? _notePlacementStartTick;
    private long _notePlacementCurrentTick;
    private long _notePlacementInitialLengthTicks;
    private long _notePlacementPointerAnchorTick;
    private Point _notePlacementOrigin;
    private bool _notePlacementActivated;
    private bool _notePlacementTimeLocked;
    private int _notePlacementPitch;
    private int _notePlacementVelocity;
    private long? _segmentPlacementStartTick;
    private long _segmentPlacementCurrentTick;
    private int _segmentPlacementLane;
    private Point _segmentPlacementOrigin;
    private bool _segmentPlacementActivated;
    private Point? _rulerDragOrigin;
    private long _rulerDragStartTick;
    private long _rulerDragCurrentTick;
    private Point? _hoverPoint;
    private Point? _velocityOrigin;
    private MouseButton _velocityButton;
    private MidoraId? _velocityDirectItemId;
    private bool _velocitySelectionRestricted;
    private readonly Dictionary<MidoraId, int> _velocityEdits = [];
    private readonly List<Point> _velocityTracePoints = new(capacity: 128);
    private Point? _eventPointOrigin;
    private MouseButton _eventPointButton;
    private bool _eventPointHorizontalTrace;
    private bool _eventPointTimeLocked;
    private MidoraId? _eventPointDirectItemId;
    private readonly Dictionary<long, double> _eventPointEdits = [];
    private readonly List<Point> _eventPointTracePoints = new(capacity: 128);
    private Point? _pendingRightGestureOrigin;
    private ModifierKeys _pendingRightGestureModifiers;
    private bool _pendingRightGestureCanTrace;
    private bool _pendingRightGestureDragThresholdReached;
    private long? _rightDoubleClickCandidateTimestampMilliseconds;
    private Point _rightDoubleClickCandidatePoint;
    private bool _suppressRightButtonUpAfterDoubleClick;
    private bool _suppressAutomaticContextMenuOpening;
    private DispatcherTimer? _delayedContextMenuTimer;
    private CancellationTokenSource? _delayedContextMenuQueryCancellation;
    private long _delayedContextMenuGeneration;
    private bool _delayedContextMenuDelayElapsed;
    private bool _delayedContextMenuQueryReady;
    private bool _delayedContextMenuAwaitingMouseUp;
    private bool _delayedContextMenuTargetApplied;
    private ModifierKeys _delayedContextMenuModifiers;
    private TimelineRenderItem? _delayedContextMenuHit;
    private TimelineSelectionSnapshot? _delayedContextMenuSelectionSnapshot;
    private long _delayedContextMenuTick;
    private int _delayedContextMenuLane;
    private long _selectionToolRevision = -1;
    private TimelineItemKind _selectionToolKind;
    private bool _selectionToolPinned;
    private long _selectionToolPinnedTick;
    private double _selectionToolPinnedVertical;
    private Vector _selectionToolFollowOffset;
    private Rect _selectionToolBounds = Rect.Empty;
    private Rect _selectionToolGripBounds = Rect.Empty;
    private Rect _selectionToolPinBounds = Rect.Empty;
    private Rect _selectionToolResizeStartBounds = Rect.Empty;
    private Rect _selectionToolResizeEndBounds = Rect.Empty;
    private Rect _selectionToolMoveBounds = Rect.Empty;
    private Point? _selectionToolGripOrigin;
    private long _selectionToolGripStartTick;
    private double _selectionToolGripStartVertical;
    private Vector _selectionToolGripStartFollowOffset;
    private int? _hoverLaneHeader;
    private MidoraId? _hoverSharedGroupId;
    private MidoraId? _contextSharedGroupId;
    private int? _pressedLaneHeader;
    private bool _pressedLaneHeaderTargetsSharedGroup;
    private MidoraId? _pressedSharedGroupId;
    private int _laneHeaderDragTarget;
    private Point _laneHeaderDragOrigin;
    private bool _laneHeaderDragActivated;
    private bool _laneHeaderDragJoinsTargetGroup;
    private bool _laneHeaderDragMovesWholeGroup;
    private bool _laneHeaderDragInsertsAfterTarget;
    private bool _laneHeaderDragDetachesFromSourceGroup;
    private MidoraId? _laneHeaderDragExteriorBoundaryGroupId;
    private ArrangementSharedGroupDropZone _laneHeaderDragExteriorBoundaryZone;
    private bool _laneHeaderDragJoinUsesChip;
    private bool _laneHeaderDragRequiresRebind;
    private int? _externalArrangementInsertionIndex;
    private readonly HashSet<TimelineRasterCacheKey> _requestedRasterKeys = [];
    private readonly long _rasterConsumerId = TimelineRasterCache.CreateConsumerId();
    // Fingerprint work is isolated per surface.  A slow or non-cooperatively
    // cancellable source owned by a closing tab must not head-of-line block
    // every other timeline in the process.
    private readonly SemaphoreSlim _tileFingerprintWorker = new(1, 1);
    private const int MaximumPreparedTileFingerprints = 4096;
    private readonly Dictionary<TileFingerprintRequestKey, ulong> _preparedTileFingerprints = [];
    private readonly HashSet<TileFingerprintRequestKey> _pendingTileFingerprints = [];
    private readonly Queue<TileFingerprintRequestKey> _preparedTileFingerprintOrder = [];
    private readonly Queue<SegmentPreviewWarmupRequest> _segmentPreviewWarmupQueue = [];
    private readonly Dictionary<MidoraId, SegmentPreviewFallbackFrame>
        _visibleSegmentPreviewFallbackFrames = [];
    private readonly List<SegmentPreviewDetailTile> _segmentPreviewDetailTiles =
        new(capacity: 8);
    private readonly List<Rect> _segmentPreviewDetailedBounds = new(capacity: 8);
    private readonly List<Rect> _segmentPreviewFallbackGaps = new(capacity: 8);
    private long _segmentPreviewWarmupGeneration;
    private int _segmentPreviewWarmupInFlight;
    private bool _segmentPreviewWarmupRetryScheduled;
    private CancellationTokenSource? _segmentPreviewWarmupPlanCancellation;
    private CancellationTokenSource _rasterRequestCancellation = new();
    private CancellationTokenSource _selectionRasterRequestCancellation = new();
    private ExactRasterProjectionSignature? _exactRasterProjectionSignature;
    private CancellationTokenSource _exactPrefetchCancellation = new();
    private long _exactPrefetchGeneration;
    private ExactPrefetchSignature? _exactPrefetchSignature;
    private CancellationTokenSource _rulerPrefetchCancellation = new();
    private long _rulerPrefetchGeneration;
    private ExactPrefetchSignature? _rulerPrefetchSignature;
    private bool _exactQueryPending;
    private bool _marqueeQueryPending;
    private CancellationTokenSource _pendingGestureCancellation = new();
    private long _gestureToken;
    private bool _viewportPrefetchQueued;
    private bool _rasterInvalidationQueued;
    private readonly List<PianoTileDrawEntry> _pianoNoteTileDrawEntries = new(capacity: 64);
    private readonly List<PianoTileDrawEntry> _pianoSelectionTileDrawEntries = new(capacity: 64);
    private readonly List<PianoTileDrawEntry> _pianoTileFallbackEntries = new(capacity: 64);
    private PianoRasterProjectionSignature? _preparedPianoProjection;
    private bool _preparedPianoNoteFrameComplete;
    private PianoCompositeRasterFrame? _committedPianoFrame;
    private readonly List<VelocityTileDrawEntry> _velocityTileDrawEntries = new(capacity: 32);
    private VelocityCompositeRasterFrame? _committedVelocityFrame;
    private readonly List<EventPointTileDrawEntry> _eventPointTileDrawEntries = new(capacity: 32);
    private EventPointCompositeRasterFrame? _committedEventPointFrame;
    private readonly List<EventPointTileDrawEntry> _dragPreviewEventPointTiles = new(capacity: 32);
    private double _valueViewMinimum;
    private double _valueViewMaximum = 1;
    private bool _backgroundWorkSuspended;

    internal bool CaptureRenderPhaseTimings { get; set; }
    internal TimelineSurfaceRenderPhaseTiming LastRenderPhaseTiming { get; private set; }

    public TimelineSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Arrow;
        FocusVisualStyle = null;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Loaded += OnSurfaceLoaded;
        Unloaded += OnSurfaceUnloaded;
    }

    private void OnSurfaceLoaded(object sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        bool wasSuspended = _backgroundWorkSuspended;
        _backgroundWorkSuspended = false;
        if (wasSuspended)
        {
            ResetRasterRequests(scheduleArrangementWarmup: SurfaceMode == TimelineSurfaceMode.Arrangement);
        }
        else
        {
            ScheduleVisibleExactPrefetch();
            if (SurfaceMode == TimelineSurfaceMode.Arrangement)
            {
                ScheduleSegmentPreviewWarmup();
            }
        }
    }

    private void OnSurfaceUnloaded(object sender, RoutedEventArgs args)
    {
        CancelOnionRequests();
        _onionCancellation.Cancel();
        _onionSourceSnapshot = null;
        _onionDrawSnapshot = null;
        _ = sender;
        _ = args;
        if (_backgroundWorkSuspended) return;
        _backgroundWorkSuspended = true;
        CancelConductorHit();
        _conductorLabelRequests.Clear();
        CancelPendingRightGesture(cancelDelayedMenu: true);

        _rasterRequestCancellation.Cancel();
        _selectionRasterRequestCancellation.Cancel();
        _exactPrefetchCancellation.Cancel();
        _rulerPrefetchCancellation.Cancel();
        _pendingGestureCancellation.Cancel();
        _resizePreviewCancellation.Cancel();
        _segmentPreviewWarmupPlanCancellation?.Cancel();
        _segmentPreviewWarmupPlanCancellation?.Dispose();
        _segmentPreviewWarmupPlanCancellation = null;
        _segmentPreviewWarmupQueue.Clear();
        _segmentPreviewWarmupInFlight = 0;
        _segmentPreviewWarmupRetryScheduled = false;
        _requestedRasterKeys.Clear();
        _requestedResizePreviewKeys.Clear();
        _exactPrefetchSignature = null;
        _rulerPrefetchSignature = null;
        _exactQueryPending = false;
        _marqueeQueryPending = false;
        Cursor = Cursors.Arrow;
        _rasterInvalidationQueued = false;
        _gestureToken = checked(_gestureToken + 1);
        _exactPrefetchGeneration = checked(_exactPrefetchGeneration + 1);
        _rulerPrefetchGeneration = checked(_rulerPrefetchGeneration + 1);
        _resizePreviewGeneration = checked(_resizePreviewGeneration + 1);
        _segmentPreviewWarmupGeneration = checked(_segmentPreviewWarmupGeneration + 1);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RenderedSurfaceAutomationPeer(this, "TimelineSurface");

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _ = UIElementAutomationPeer.CreatePeerForElement(this);
    }

    public TimelineRenderSnapshot? Snapshot
    {
        get => (TimelineRenderSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public TimelineSelectionSnapshot? SelectionSnapshot
    {
        get => (TimelineSelectionSnapshot?)GetValue(SelectionSnapshotProperty);
        set => SetValue(SelectionSnapshotProperty, value);
    }

    public MidoraId? SelectedArrangementTrackId
    {
        get => (MidoraId?)GetValue(SelectedArrangementTrackIdProperty);
        set => SetValue(SelectedArrangementTrackIdProperty, value);
    }

    public bool IsConductorTrackSelected
    {
        get => (bool)GetValue(IsConductorTrackSelectedProperty);
        set => SetValue(IsConductorTrackSelectedProperty, value);
    }

    public TimelineRenderSnapshot? RulerSnapshot
    {
        get => (TimelineRenderSnapshot?)GetValue(RulerSnapshotProperty);
        set => SetValue(RulerSnapshotProperty, value);
    }

    public long StartTick
    {
        get => (long)GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, value);
    }

    public long TickSpan
    {
        get => (long)GetValue(TickSpanProperty);
        set => SetValue(TickSpanProperty, value);
    }

    public int FirstLane
    {
        get => (int)GetValue(FirstLaneProperty);
        set => SetValue(FirstLaneProperty, value);
    }

    public int MaximumFirstLane => (int)GetValue(MaximumFirstLaneProperty);

    public bool CanScrollLanes => (bool)GetValue(CanScrollLanesProperty);

    public int VisibleLaneCount => (int)GetValue(VisibleLaneCountProperty);

    public double ValueScrollOffset
    {
        get => (double)GetValue(ValueScrollOffsetProperty);
        set => SetValue(ValueScrollOffsetProperty, value);
    }

    public double ValueScrollMaximum => (double)GetValue(ValueScrollMaximumProperty);

    public bool CanScrollValues => (bool)GetValue(CanScrollValuesProperty);

    public double ValueScrollViewportSize => (double)GetValue(ValueScrollViewportSizeProperty);

    public string PointerPositionText => (string)GetValue(PointerPositionTextProperty);

    public double LaneHeight
    {
        get => (double)GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public long GridStepTicks
    {
        get => (long)GetValue(GridStepTicksProperty);
        set => SetValue(GridStepTicksProperty, value);
    }

    public long OperationStepTicks
    {
        get => (long)GetValue(OperationStepTicksProperty);
        set => SetValue(OperationStepTicksProperty, Math.Max(1, value));
    }

    public bool DisplayGridUsesBars
    {
        get => (bool)GetValue(DisplayGridUsesBarsProperty);
        set => SetValue(DisplayGridUsesBarsProperty, value);
    }

    public bool OperationUsesBars
    {
        get => (bool)GetValue(OperationUsesBarsProperty);
        set => SetValue(OperationUsesBarsProperty, value);
    }

    public ProjectTimeSignatureMap? TimeSignatureMap
    {
        get => (ProjectTimeSignatureMap?)GetValue(TimeSignatureMapProperty);
        set => SetValue(TimeSignatureMapProperty, value);
    }

    public long ProjectTickOffset
    {
        get => (long)GetValue(ProjectTickOffsetProperty);
        set => SetValue(ProjectTickOffsetProperty, value);
    }

    public long DefaultCreationLengthTicks
    {
        get => (long)GetValue(DefaultCreationLengthTicksProperty);
        set => SetValue(DefaultCreationLengthTicksProperty, Math.Max(1, value));
    }

    public int PreviewTicksPerQuarterNote
    {
        get => (int)GetValue(PreviewTicksPerQuarterNoteProperty);
        set => SetValue(PreviewTicksPerQuarterNoteProperty, Math.Max(1, value));
    }

    public int DefaultVelocity
    {
        get => (int)GetValue(DefaultVelocityProperty);
        set => SetValue(DefaultVelocityProperty, Math.Clamp(value, 1, 127));
    }

    public double ValueAxisMinimum
    {
        get => (double)GetValue(ValueAxisMinimumProperty);
        set => SetValue(ValueAxisMinimumProperty, value);
    }

    public double ValueAxisMaximum
    {
        get => (double)GetValue(ValueAxisMaximumProperty);
        set => SetValue(ValueAxisMaximumProperty, value);
    }

    public bool ValueAxisIntegral
    {
        get => (bool)GetValue(ValueAxisIntegralProperty);
        set => SetValue(ValueAxisIntegralProperty, value);
    }

    public long? PlaybackCursorTick
    {
        get => (long?)GetValue(PlaybackCursorTickProperty);
        set => SetValue(PlaybackCursorTickProperty, value);
    }

    public long? EditCursorTick
    {
        get => (long?)GetValue(EditCursorTickProperty);
        set => SetValue(EditCursorTickProperty, value);
    }

    public long? RangeStartTick
    {
        get => (long?)GetValue(RangeStartTickProperty);
        set => SetValue(RangeStartTickProperty, value);
    }

    public long? RangeEndTick
    {
        get => (long?)GetValue(RangeEndTickProperty);
        set => SetValue(RangeEndTickProperty, value);
    }

    // Template creation extends the owner atomically; the visual crop remains
    // unchanged until that command has succeeded.
    public bool CanExtendEventCreationRange
    {
        get => (bool)GetValue(CanExtendEventCreationRangeProperty);
        set => SetValue(CanExtendEventCreationRangeProperty, value);
    }

    private long? EventCreationRangeEndTick => CanExtendEventCreationRange ? null : RangeEndTick;

    public bool CanDragEventValue
    {
        get => (bool)GetValue(CanDragEventValueProperty);
        set => SetValue(CanDragEventValueProperty, value);
    }

    public long? TimeRangeStartTick
    {
        get => (long?)GetValue(TimeRangeStartTickProperty);
        set => SetValue(TimeRangeStartTickProperty, value);
    }

    public long? TimeRangeEndTick
    {
        get => (long?)GetValue(TimeRangeEndTickProperty);
        set => SetValue(TimeRangeEndTickProperty, value);
    }

    public bool IsTimeRangeSelectionEnabled
    {
        get => (bool)GetValue(IsTimeRangeSelectionEnabledProperty);
        set => SetValue(IsTimeRangeSelectionEnabledProperty, value);
    }

    public void AdjustVerticalZoom(bool zoomIn)
    {
        if (SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            double factor = zoomIn ? 1.15 : 1 / 1.15;
            LaneHeight = Math.Clamp(
                LaneHeight * factor,
                MinimumArrangementLaneHeight,
                MaximumArrangementLaneHeight);
            UpdateVerticalViewportMetrics();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (SurfaceMode != TimelineSurfaceMode.PianoRoll)
        {
            return;
        }

        double rulerHeight = GetRulerHeight();
        double contentHeight = Math.Max(0, ActualHeight - rulerHeight);
        double oldHeight = Math.Max(MinimumPianoLaneHeight, LaneHeight);
        double centerLane = FirstLane + contentHeight / (2 * oldHeight);
        double dpiScaleY = Math.Max(0.01, VisualTreeHelper.GetDpi(this).DpiScaleY);
        int oldDevicePixels = Math.Clamp(
            (int)Math.Round(oldHeight * dpiScaleY, MidpointRounding.AwayFromZero),
            (int)MinimumPianoLaneHeight,
            (int)MaximumPianoLaneHeight);
        int newDevicePixels = Math.Clamp(
            oldDevicePixels + (zoomIn ? 1 : -1),
            (int)MinimumPianoLaneHeight,
            (int)MaximumPianoLaneHeight);
        double newHeight = newDevicePixels / dpiScaleY;
        LaneHeight = newHeight;
        double visibleLaneCount = contentHeight / Math.Max(MinimumPianoLaneHeight, LaneHeight);
        FirstLane = Math.Clamp(
            (int)Math.Round(centerLane - visibleLaneCount / 2, MidpointRounding.AwayFromZero),
            0,
            127);
        UpdateVerticalViewportMetrics();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public TimelineSurfaceMode SurfaceMode
    {
        get => (TimelineSurfaceMode)GetValue(SurfaceModeProperty);
        set => SetValue(SurfaceModeProperty, value);
    }

    public bool CanEdit
    {
        get => (bool)GetValue(CanEditProperty);
        set => SetValue(CanEditProperty, value);
    }

    public bool IsSelectionFloatingToolEnabled
    {
        get => (bool)GetValue(IsSelectionFloatingToolEnabledProperty);
        set => SetValue(IsSelectionFloatingToolEnabledProperty, value);
    }

    public TimelineToolMode ToolMode
    {
        get => (TimelineToolMode)GetValue(ToolModeProperty);
        set => SetValue(ToolModeProperty, value);
    }
    public bool GridVisible
    {
        get => (bool)GetValue(GridVisibleProperty);
        set => SetValue(GridVisibleProperty, value);
    }
    public int HighlightedPitch
    {
        get => (int)GetValue(HighlightedPitchProperty);
        set => SetValue(HighlightedPitchProperty, value);
    }

    public event EventHandler<TimelineItemEventArgs>? ItemInvoked;
    public event EventHandler<TimelinePointEventArgs>? BackgroundInvoked;
    public event EventHandler<TimelineItemEditEventArgs>? ItemEditCompleted;
    public event EventHandler<TimelineSelectionReplacementEventArgs>? SelectionReplacementStarted;
    public event EventHandler<TimelineMarqueeEventArgs>? MarqueeCompleted;
    public event EventHandler<TimelineRulerEventArgs>? RulerClicked;
    public event EventHandler<TimelineTimeRangeEventArgs>? TimeRangeSelected;
    public event EventHandler<TimelineItemEventArgs>? SegmentSplitRequested;
    public event EventHandler<TimelineLanePreviewEventArgs>? LanePreviewPressed;
    public event EventHandler<TimelineLanePreviewEventArgs>? LanePreviewReleased;
    public event EventHandler<TimelinePitchPreviewEventArgs>? PitchPreviewRequested;
    public event EventHandler? PitchPreviewReleased;
    public event EventHandler<TimelineNotePlacementEventArgs>? NotePlacementStarted;
    public event EventHandler<TimelineNotePlacementEventArgs>? NotePlacementCompleted;
    public event EventHandler? NotePlacementCancelled;
    public event EventHandler<TimelineSegmentPlacementEventArgs>? SegmentPlacementCompleted;
    public event EventHandler? SegmentPlacementCancelled;
    public event EventHandler<TimelineLaneHeaderCommandEventArgs>? LaneHeaderCommandInvoked;
    public event EventHandler<TimelineLaneHeaderEventArgs>? LaneHeaderInvoked;
    public event EventHandler<TimelineLaneHeaderEventArgs>? LaneHeaderDoubleInvoked;
    public event EventHandler<TimelineLaneHeaderEventArgs>? LaneHeaderContextRequested;
    public event EventHandler<TimelineLaneHeaderReorderEventArgs>? LaneHeaderReorderCompleted;
    public event EventHandler<TimelineArrangementInstrumentEventArgs>? ArrangementInstrumentInvoked;
    public event EventHandler<TimelineArrangementMidiRouteEventArgs>? ArrangementMidiRouteInvoked;
    public event EventHandler<TimelineVelocityEditEventArgs>? VelocityEditCompleted;
    public event EventHandler<TimelineEventPointEditEventArgs>? EventPointEditCompleted;
    public event EventHandler<TimelineEventPointTraceEventArgs>? EventPointTraceCompleted;
    public event EventHandler? ViewportChanged;
    public event RoutedEventHandler AltGestureConsumed
    {
        add => AddHandler(AltGestureConsumedEvent, value);
        remove => RemoveHandler(AltGestureConsumedEvent, value);
    }

    public double LaneHeaderWidth => GetLaneHeaderWidth();

    public double LaneHeaderWidthOverride
    {
        get => (double)GetValue(LaneHeaderWidthOverrideProperty);
        set => SetValue(LaneHeaderWidthOverrideProperty, value);
    }

    public bool TryGetArrangementLaneHeader(Point point, out int lane)
    {
        lane = -1;
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || point.X < 0
            || point.X >= GetLaneHeaderWidth()
            || point.Y < GetRulerHeight()
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            return false;
        }
        double relativeY = point.Y - GetRulerHeight();
        if (relativeY < 0 || relativeY >= GetLaneContentHeight(viewport)) return false;
        int candidate = YToLane(viewport, relativeY);
        if ((uint)candidate >= (uint)(Snapshot?.LaneLabels.Count ?? 0)) return false;
        lane = candidate;
        return true;
    }

    public bool TryGetArrangementLaneAt(Point point, out int lane)
    {
        lane = -1;
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || point.X < 0
            || point.X >= ActualWidth
            || point.Y < GetRulerHeight()
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            return false;
        }
        double relativeY = point.Y - GetRulerHeight();
        if (relativeY < 0 || relativeY >= GetLaneContentHeight(viewport)) return false;
        int candidate = YToLane(viewport, relativeY);
        if ((uint)candidate >= (uint)(Snapshot?.LaneLabels.Count ?? 0)) return false;
        lane = candidate;
        return true;
    }

    public bool TryGetArrangementTrackInsertionIndex(Point point, out int insertionIndex)
    {
        insertionIndex = -1;
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || point.X < 0
            || point.X >= ActualWidth
            || point.Y < GetRulerHeight()
            || point.Y >= ActualHeight
            || Snapshot is not TimelineRenderSnapshot snapshot
            || snapshot.ArrangementLanes.Count == 0
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            return false;
        }

        int laneCount = snapshot.ArrangementLanes.Count;
        double rulerHeight = GetRulerHeight();
        double contentBottom = rulerHeight + GetLaneContentHeight(viewport);
        if (point.Y >= contentBottom)
        {
            insertionIndex = Math.Max(0, laneCount - 1);
            return true;
        }

        int lane = YToLane(viewport, point.Y - rulerHeight);
        if ((uint)lane >= (uint)laneCount) return false;
        double laneTop = GetLaneTop(viewport, lane, rulerHeight);
        double laneBottom = laneTop + GetLaneVisualHeight(lane);
        const double gapHitHeight = 8;
        double topDistance = Math.Abs(point.Y - laneTop);
        double bottomDistance = Math.Abs(laneBottom - point.Y);
        bool nearTop = topDistance <= gapHitHeight;
        bool nearBottom = bottomDistance <= gapHitHeight;
        if (!nearTop && !nearBottom) return false;

        int boundaryLane = nearTop && (!nearBottom || topDistance <= bottomDistance)
            ? lane
            : lane + 1;
        if (boundaryLane <= 0 || boundaryLane > laneCount) return false;
        if (boundaryLane < laneCount)
        {
            ArrangementLaneDescriptor before = snapshot.ArrangementLanes[boundaryLane - 1];
            ArrangementLaneDescriptor after = snapshot.ArrangementLanes[boundaryLane];
            if (before.IsSharedGroup
                && after.IsSharedGroup
                && before.SharedGroupId.HasValue
                && before.SharedGroupId == after.SharedGroupId)
            {
                return false;
            }
        }

        // Arrangement lane zero is Conductor. Every later lane maps one-to-one
        // to the global mixed Track order, so the visual boundary immediately
        // after Conductor is insertion index zero.
        insertionIndex = boundaryLane - 1;
        return true;
    }

    public void SetExternalArrangementInsertionPreview(int? insertionIndex)
    {
        int? normalized = insertionIndex is >= 0 ? insertionIndex : null;
        if (_externalArrangementInsertionIndex == normalized) return;
        _externalArrangementInsertionIndex = normalized;
        InvalidateVisual();
    }

    public void SetArrangementSharedGroupContextHighlight(MidoraId? sharedGroupId)
    {
        if (_contextSharedGroupId == sharedGroupId) return;
        _contextSharedGroupId = sharedGroupId;
        InvalidateVisual();
    }

    public bool TryGetArrangementSharedGroupHeaderTarget(
        Point point,
        out MidoraId sharedGroupId)
    {
        sharedGroupId = default;
        if (!TryGetArrangementLaneHeader(point, out int lane)
            || Snapshot is not TimelineRenderSnapshot snapshot
            || (uint)lane >= (uint)snapshot.ArrangementLanes.Count
            || snapshot.ArrangementLanes[lane].SharedGroupId is not MidoraId groupId
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            return false;
        }
        ArrangementLaneDescriptor descriptor = snapshot.ArrangementLanes[lane];
        bool hit = descriptor.IsSharedGroup
            ? point.X < 13
            : TryGetArrangementJoinChipBounds(viewport, lane, out Rect bounds)
                && bounds.Contains(point);
        if (!hit) return false;
        sharedGroupId = groupId;
        return true;
    }

    public bool TryGetArrangementSharedGroupBraceTarget(
        Point point,
        out MidoraId sharedGroupId)
    {
        sharedGroupId = default;
        if (!TryGetArrangementLaneHeader(point, out int lane)
            || Snapshot is not TimelineRenderSnapshot snapshot
            || (uint)lane >= (uint)snapshot.ArrangementLanes.Count
            || snapshot.ArrangementLanes[lane] is not
            { IsSharedGroup: true, SharedGroupId: MidoraId groupId }
            || point.X >= 13)
        {
            return false;
        }
        sharedGroupId = groupId;
        return true;
    }

    public bool IsArrangementEmptyBackground(Point point)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || point.Y < GetRulerHeight()
            || point.X < 0
            || point.X >= ActualWidth)
        {
            return false;
        }
        if (TryGetArrangementLaneHeader(point, out _)) return false;
        if (!TryGetArrangementLaneAt(point, out _)) return true;
        if (!TryCreateViewport(out TimelineViewport viewport)) return false;
        bool hit = TryHitTimelineItem(point, viewport, out _);
        return !_exactQueryPending && !hit;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateVerticalViewportMetrics();
        ScheduleVisibleExactPrefetch();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        CoerceValue(LaneHeightProperty);
        UpdateVerticalViewportMetrics();
    }

    private static void OnViewportMetricsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is TimelineSurface surface)
        {
            if (args.Property == SnapshotProperty)
            {
                surface.CancelDelayedContextMenu();
                surface.CancelConductorHit();
            }
            if (args.Property == LaneHeaderWidthOverrideProperty || args.Property == SurfaceModeProperty)
            {
                surface.CancelConductorHit();
            }
            if (args.Property == SurfaceModeProperty)
            {
                surface.CoerceValue(LaneHeightProperty);
            }
            surface.UpdateVerticalViewportMetrics();
            surface.RefreshPointerPositionText();
            bool hardReset = args.Property == SnapshotProperty
                || args.Property == SurfaceModeProperty;
            if (hardReset)
            {
                bool keepPianoFrame = args.Property == SnapshotProperty
                    && args.OldValue is TimelineRenderSnapshot oldSnapshot
                    && args.NewValue is TimelineRenderSnapshot newSnapshot
                    && string.Equals(
                        oldSnapshot.ProjectionKey,
                        newSnapshot.ProjectionKey,
                        StringComparison.Ordinal);
                if (!keepPianoFrame)
                {
                    surface._committedPianoFrame = null;
                    surface._committedVelocityFrame = null;
                    surface._committedEventPointFrame = null;
                }
                surface.ResetRasterRequests(scheduleArrangementWarmup: true);
            }
            else
            {
                surface.QueueVisibleExactPrefetch();
            }
        }
    }

    private static void OnRasterProjectionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        if (args.Property == SelectionSnapshotProperty)
        {
            surface.TryPromotePreparedDragPreviewSelection(
                args.NewValue as TimelineSelectionSnapshot);
            // Selection is an independent raster family. Cancel only layers
            // whose pixels depend on it; invalidating PianoNotes here would
            // force a dense source fingerprint/raster pass when the user
            // merely selected a handful of objects.
            surface.RestartPendingSelectionRasterWork();
        }
        // Repeated viewport changes are coalesced below so one physical input
        // frame cannot enqueue an unbounded chain of source-page prefetches.
        if (args.Property != SelectionSnapshotProperty)
        {
            surface.QueueVisibleExactPrefetch();
        }
    }

    private void QueueVisibleExactPrefetch()
    {
        if (_backgroundWorkSuspended || _viewportPrefetchQueued) return;
        _viewportPrefetchQueued = true;
        QueueWeakSurfaceSignal(
            static surface =>
            {
                surface._viewportPrefetchQueued = false;
                if (!surface._backgroundWorkSuspended)
                {
                    surface.ScheduleVisibleExactPrefetch();
                }
            },
            DispatcherPriority.Background);
    }

    private void ResetRasterRequests(bool scheduleArrangementWarmup)
    {
        _conductorLabelRequests.Clear();
        _rasterRequestCancellation.Cancel();
        _rasterRequestCancellation.Dispose();
        _rasterRequestCancellation = new();
        _selectionRasterRequestCancellation.Cancel();
        _selectionRasterRequestCancellation.Dispose();
        _selectionRasterRequestCancellation = new();
        _requestedRasterKeys.Clear();
        _preparedTileFingerprints.Clear();
        _pendingTileFingerprints.Clear();
        _preparedTileFingerprintOrder.Clear();
        _exactRasterProjectionSignature = null;
        _exactPrefetchCancellation.Cancel();
        _exactPrefetchCancellation.Dispose();
        _exactPrefetchCancellation = new();
        _exactPrefetchSignature = null;
        _rulerPrefetchCancellation.Cancel();
        _rulerPrefetchCancellation.Dispose();
        _rulerPrefetchCancellation = new();
        _rulerPrefetchSignature = null;
        _exactQueryPending = false;
        _marqueeQueryPending = false;
        _pendingGestureCancellation.Cancel();
        _pendingGestureCancellation.Dispose();
        _pendingGestureCancellation = new();
        _dragPreviewIdPrefetchSignature = null;
        _gestureToken = checked(_gestureToken + 1);
        if (_backgroundWorkSuspended) return;
        QueueVisibleExactPrefetch();
        if (scheduleArrangementWarmup)
        {
            ScheduleSegmentPreviewWarmup();
        }
    }

    private void BeginExactRasterProjection(long horizontalScaleKey, long verticalScaleKey)
    {
        if (SurfaceMode is not (
                TimelineSurfaceMode.PianoRoll
                or TimelineSurfaceMode.EventLanes
                or TimelineSurfaceMode.Velocity))
        {
            return;
        }

        ExactRasterProjectionSignature signature = new(
            SurfaceMode,
            horizontalScaleKey,
            verticalScaleKey);
        if (_exactRasterProjectionSignature == signature) return;

        // A zoom projection is an all-or-nothing pixel family. Cancel queued
        // work for the previous scale so it cannot consume the bounded visible
        // queue while the current scale is waiting to appear.
        RestartPendingRasterWork();
        _exactRasterProjectionSignature = signature;
    }

    private void RestartPendingRasterWork()
    {
        _conductorLabelRequests.Clear();
        _rasterRequestCancellation.Cancel();
        _rasterRequestCancellation.Dispose();
        _rasterRequestCancellation = new();
        _selectionRasterRequestCancellation.Cancel();
        _selectionRasterRequestCancellation.Dispose();
        _selectionRasterRequestCancellation = new();
        _requestedRasterKeys.Clear();
        _preparedTileFingerprints.Clear();
        _pendingTileFingerprints.Clear();
        _preparedTileFingerprintOrder.Clear();
    }

    private void RestartPendingSelectionRasterWork()
    {
        _selectionRasterRequestCancellation.Cancel();
        _selectionRasterRequestCancellation.Dispose();
        _selectionRasterRequestCancellation = new();
        _requestedRasterKeys.RemoveWhere(static key =>
            IsSelectionDependentRasterLayer(key.Layer));
        foreach (TileFingerprintRequestKey key in _preparedTileFingerprints.Keys
            .Where(static key => IsSelectionDependentRasterLayer(key.Layer))
            .ToArray())
        {
            _preparedTileFingerprints.Remove(key);
        }
        _pendingTileFingerprints.RemoveWhere(static key =>
            IsSelectionDependentRasterLayer(key.Layer));
    }

    private static bool IsSelectionDependentRasterLayer(TimelineRasterLayer layer) =>
        layer is TimelineRasterLayer.PianoSelection
            or TimelineRasterLayer.VelocityBars
            or TimelineRasterLayer.EventPoints
            or TimelineRasterLayer.ConductorMeta
            or TimelineRasterLayer.EventPointSelection;

    private void ScheduleVisibleExactPrefetch()
    {
        if (_backgroundWorkSuspended
            || Snapshot is not TimelineRenderSnapshot snapshot
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }
        long guard = Math.Max(1, viewport.TickLength / 2);
        long start = viewport.StartTick >= guard ? viewport.StartTick - guard : 0;
        long end = viewport.EndTick > long.MaxValue - guard
            ? long.MaxValue
            : viewport.EndTick + guard;
        end = Math.Min(end, snapshot.MaximumEndTick);
        if (end <= start) return;
        int laneGuard = Math.Max(1, viewport.LaneCount / 2);
        ScheduleExactPrefetch(
            snapshot,
            start,
            end,
            Math.Max(0, viewport.FirstLane - laneGuard),
            checked(viewport.LastLaneExclusive + laneGuard));
    }

    private void ScheduleExactPrefetch(
        TimelineRenderSnapshot snapshot,
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (_backgroundWorkSuspended
            || endTick <= startTick
            || lastLaneExclusive <= firstLane)
        {
            return;
        }
        ExactPrefetchSignature signature = new(
            snapshot.SemanticRevision,
            snapshot.ProjectionKey,
            startTick,
            endTick,
            firstLane,
            lastLaneExclusive);
        if (_exactPrefetchSignature == signature) return;

        _exactPrefetchCancellation.Cancel();
        _exactPrefetchCancellation.Dispose();
        _exactPrefetchCancellation = new();
        CancellationToken cancellationToken = _exactPrefetchCancellation.Token;
        long generation = checked(++_exactPrefetchGeneration);
        _exactPrefetchSignature = signature;
        _exactQueryPending = true;
        _ = Task.Run(
            () => snapshot.PrefetchRange(
                startTick,
                endTick,
                firstLane,
                lastLaneExclusive,
                cancellationToken),
            cancellationToken).ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                        () =>
                        {
                            if (generation != _exactPrefetchGeneration)
                            {
                                return;
                            }
                            // A completed signature is not a residency proof:
                            // decoded Content-Pack pages can be evicted later,
                            // and a failed/cancelled prefetch must be retryable.
                            // Only the current generation may clear its marker.
                            if (_exactPrefetchSignature == signature)
                                _exactPrefetchSignature = null;
                            _exactQueryPending = false;
                            if (_backgroundWorkSuspended
                                || !ReferenceEquals(Snapshot, snapshot)
                                || cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }
                            if (task.IsFaulted)
                            {
                                System.Diagnostics.Trace.TraceError(
                                    $"Timeline exact prefetch failed: {task.Exception}");
                            }
                            InvalidateVisual();
                            RefreshHoverIntent();
                        },
                        DispatcherPriority.Render);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void ScheduleRulerExactPrefetch(
        TimelineRenderSnapshot snapshot,
        long startTick,
        long endTick)
    {
        if (_backgroundWorkSuspended || endTick <= startTick) return;
        ExactPrefetchSignature signature = new(
            snapshot.SemanticRevision,
            snapshot.ProjectionKey,
            startTick,
            endTick,
            0,
            1);
        if (_rulerPrefetchSignature == signature) return;

        _rulerPrefetchCancellation.Cancel();
        _rulerPrefetchCancellation.Dispose();
        _rulerPrefetchCancellation = new();
        CancellationToken cancellationToken = _rulerPrefetchCancellation.Token;
        long generation = checked(++_rulerPrefetchGeneration);
        _rulerPrefetchSignature = signature;
        _ = Task.Run(
            () => snapshot.PrefetchRange(startTick, endTick, 0, 1, cancellationToken),
            cancellationToken).ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                    () =>
                    {
                        if (generation != _rulerPrefetchGeneration) return;
                        if (_rulerPrefetchSignature == signature)
                            _rulerPrefetchSignature = null;
                        if (_backgroundWorkSuspended
                            || cancellationToken.IsCancellationRequested
                            || !ReferenceEquals(RulerSnapshot, snapshot))
                        {
                            return;
                        }
                        if (task.IsFaulted)
                        {
                            System.Diagnostics.Trace.TraceError(
                                $"Timeline ruler prefetch failed: {task.Exception}");
                        }
                        InvalidateVisual();
                    },
                    DispatcherPriority.Render);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private static void OnToolModeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        if (surface._pendingConductorPress is not null) surface.CancelConductorHit();
        surface.RefreshHoverIntent();
    }

    private static void OnPreviewScaleChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        if (surface.SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            surface.ScheduleSegmentPreviewWarmup();
        }
    }

    private static object CoerceFirstLane(DependencyObject dependencyObject, object baseValue)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        return Math.Clamp((int)baseValue, 0, surface.ComputeMaximumFirstLane());
    }

    private static object CoerceLaneHeight(DependencyObject dependencyObject, object baseValue)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        double value = (double)baseValue;
        if (!double.IsFinite(value)) value = 24;
        if (surface.SurfaceMode != TimelineSurfaceMode.PianoRoll)
        {
            return value;
        }

        double dpiScaleY = Math.Max(0.01, VisualTreeHelper.GetDpi(surface).DpiScaleY);
        int devicePixels = Math.Clamp(
            (int)Math.Round(value * dpiScaleY, MidpointRounding.AwayFromZero),
            (int)MinimumPianoLaneHeight,
            (int)MaximumPianoLaneHeight);
        return devicePixels / dpiScaleY;
    }

    private static object CoerceValueScrollOffset(DependencyObject dependencyObject, object baseValue)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        double value = (double)baseValue;
        return double.IsFinite(value)
            ? Math.Clamp(value, 0, surface.ValueScrollMaximum)
            : 0d;
    }

    private static void OnValueScrollOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        surface.SetValueViewRange(surface._valueViewMinimum, surface._valueViewMaximum);
        double range = Math.Clamp(surface._valueViewMaximum - surface._valueViewMinimum, 1d / 64, 1);
        double offset = Math.Clamp((double)args.NewValue, 0, Math.Max(0, 1 - range));
        double maximum = Math.Min(1, 1 - offset);
        surface.SetValueViewRange(
            Math.Max(0, maximum - range),
            maximum);
        surface.ResetRasterRequests(scheduleArrangementWarmup: false);
        surface.InvalidateVisual();
        surface.RefreshPointerPositionText();
        surface.ViewportChanged?.Invoke(surface, EventArgs.Empty);
    }

    private void UpdateVerticalViewportMetrics()
    {
        int visibleLaneCount = ComputeFullyVisibleLaneCount();
        int maximum = ComputeMaximumFirstLane();
        SetValue(VisibleLaneCountPropertyKey, visibleLaneCount);
        SetValue(MaximumFirstLanePropertyKey, maximum);
        SetValue(CanScrollLanesPropertyKey, maximum > 0);
        CoerceValue(FirstLaneProperty);
        UpdateValueScrollMetrics();
    }

    private int ComputeMaximumFirstLane() =>
        SurfaceMode == TimelineSurfaceMode.Arrangement
            ? ComputeArrangementMaximumFirstLane()
            : Math.Max(0, ComputeTotalLaneCount() - ComputeFullyVisibleLaneCount());

    private int ComputeFullyVisibleLaneCount()
    {
        double contentHeight = Math.Max(0, ActualHeight - GetRulerHeight());
        if (SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            return CountArrangementRowsThatFit(FirstLane, contentHeight, includePartial: false);
        }
        int visibleLaneCount = LaneHeight > 0 && double.IsFinite(LaneHeight)
            ? Math.Max(1, (int)Math.Floor(contentHeight / LaneHeight))
            : 1;
        return SurfaceMode == TimelineSurfaceMode.PianoRoll
            ? Math.Min(128, visibleLaneCount)
            : visibleLaneCount;
    }

    private int ComputeTotalLaneCount()
    {
        return SurfaceMode == TimelineSurfaceMode.PianoRoll
            ? 128
            : SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
                ? 1
                : Math.Max(
                    Snapshot?.LaneLabels.Count ?? 0,
                    (Snapshot?.MaterializedMaximumLane ?? -1) + 1);
    }

    private int ComputeVisibleLaneCount()
    {
        double contentHeight = Math.Max(0, ActualHeight - GetRulerHeight());
        if (SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            return CountArrangementRowsThatFit(FirstLane, contentHeight, includePartial: true);
        }
        int visibleLaneCount = LaneHeight > 0 && double.IsFinite(LaneHeight)
            ? Math.Max(1, (int)Math.Ceiling(contentHeight / LaneHeight))
            : 1;
        return SurfaceMode == TimelineSurfaceMode.PianoRoll
            ? Math.Min(128, visibleLaneCount)
            : visibleLaneCount;
    }

    private void UpdateValueScrollMetrics()
    {
        SetValueViewRange(_valueViewMinimum, _valueViewMaximum);
        double range = Math.Clamp(_valueViewMaximum - _valueViewMinimum, 1d / 64, 1);
        double maximum = Math.Max(0, 1 - range);
        SetValue(ValueScrollMaximumPropertyKey, maximum);
        SetValue(ValueScrollViewportSizePropertyKey, range);
        SetValue(CanScrollValuesPropertyKey, maximum > 0);
        CoerceValue(ValueScrollOffsetProperty);
    }

    private void SetValueViewRange(double desiredMinimum, double desiredMaximum)
    {
        if (!double.IsFinite(desiredMinimum)
            || !double.IsFinite(desiredMaximum)
            || desiredMaximum <= desiredMinimum)
        {
            _valueViewMinimum = 0;
            _valueViewMaximum = 1;
            return;
        }

        double range = Math.Clamp(desiredMaximum - desiredMinimum, 1d / 64, 1);
        double minimum = Math.Clamp(desiredMinimum, 0, Math.Max(0, 1 - range));
        double maximum = Math.Min(1, minimum + range);
        if (maximum <= minimum)
        {
            _valueViewMinimum = 0;
            _valueViewMaximum = 1;
            return;
        }

        _valueViewMinimum = minimum;
        _valueViewMaximum = maximum;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        bool captureTimings = CaptureRenderPhaseTimings;
        long frameStart = captureTimings
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
        base.OnRender(drawingContext);
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            if (captureTimings) LastRenderPhaseTiming = default;
            return;
        }
        Brush surface = Brush("Brush.Surface.0", Color.FromRgb(9, 11, 14));
        Brush alternate = Brush("Brush.Surface.1", Color.FromRgb(14, 17, 21));
        Brush border = Brush("Brush.Border", Color.FromRgb(42, 48, 58));
        Brush red = Brush("Brush.Red", Color.FromRgb(229, 72, 77));
        Brush redDark = Brush("Brush.Red.Dark", Color.FromRgb(143, 36, 41));
        Brush redSubtle = Brush("Brush.Red.Subtle", Color.FromRgb(44, 17, 20));
        Brush info = Brush("Brush.Info", Color.FromRgb(98, 166, 246));
        Brush success = Brush("Brush.Success", Color.FromRgb(88, 196, 135));
        Brush successSubtle = Brush("Brush.Success.Subtle", Color.FromRgb(16, 37, 27));
        Brush warning = Brush("Brush.Warning", Color.FromRgb(232, 179, 75));
        Brush text = Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245));
        Brush segment = Brush("Brush.Segment", Color.FromRgb(66, 78, 88));
        Brush selectedSegment = Brush("Brush.Segment.Selected", Color.FromRgb(48, 59, 69));
        Brush segmentSelection = Brush("Brush.Segment.Selection", Color.FromRgb(145, 166, 184));
        Brush segmentNotePreview = Brush("Brush.Segment.NotePreview", Color.FromRgb(189, 199, 207));
        Brush segmentPianoNote = Brush("Brush.Segment.PianoNote", Color.FromRgb(163, 178, 190));
        Brush segmentPianoOutside = Brush("Brush.Segment.PianoOutside", Color.FromRgb(2, 3, 4));
        Brush pianoWhiteKey = Brush("Brush.PianoKey.White", Color.FromRgb(212, 216, 221));
        Brush pianoBlackKey = Brush("Brush.PianoKey.Black", Color.FromRgb(21, 24, 29));
        Brush pianoKeyLabel = Brush("Brush.PianoKey.Label", Color.FromRgb(37, 43, 51));
        EnsurePens(border, info, text, red, redSubtle, success, successSubtle, segmentSelection);

        drawingContext.DrawRectangle(surface, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        if (SurfaceMode is not (TimelineSurfaceMode.Velocity or TimelineSurfaceMode.EventLanes))
        {
            for (int relativeLane = 0; relativeLane < viewport.LaneCount; relativeLane++)
            {
                int absoluteLane = viewport.FirstLane + relativeLane;
                double y = GetLaneTop(viewport, absoluteLane, rulerHeight);
                double rowHeight = GetLaneVisualHeight(absoluteLane);
                bool shaded = SurfaceMode == TimelineSurfaceMode.PianoRoll
                    ? absoluteLane is >= 0 and < 128
                      && !PianoKeyPresentation.IsBlackKey(127 - absoluteLane)
                    : (relativeLane & 1) != 0;
                if (shaded)
                {
                    drawingContext.DrawRectangle(
                        alternate,
                        null,
                        new Rect(
                            laneHeaderWidth,
                            y,
                            Math.Max(0, ActualWidth - laneHeaderWidth),
                            Math.Min(rowHeight, Math.Max(0, rulerHeight + GetLaneContentHeight(viewport) - y))));
                }
                drawingContext.DrawLine(_borderPen, new Point(0, y), new Point(ActualWidth, y));
            }
        }
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && Snapshot is TimelineRenderSnapshot arrangementRows
            && arrangementRows.ArrangementLanes.Count > 0)
        {
            int finalLane = arrangementRows.ArrangementLanes.Count - 1;
            double finalBottom = GetLaneTop(viewport, finalLane, rulerHeight)
                + GetLaneVisualHeight(finalLane);
            if (finalBottom >= rulerHeight && finalBottom <= ActualHeight)
            {
                drawingContext.DrawLine(
                    _borderPen,
                    new Point(0, finalBottom),
                    new Point(ActualWidth, finalBottom));
            }
        }

        DrawPianoOutsideActiveRange(
            drawingContext,
            viewport,
            segmentPianoOutside,
            laneHeaderWidth,
            rulerHeight);
        if (GridVisible) DrawGrid(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        if (SurfaceMode is TimelineSurfaceMode.Velocity or TimelineSurfaceMode.EventLanes)
        {
            DrawValueGrid(drawingContext, text, laneHeaderWidth, rulerHeight, drawLabels: false);
        }
        DrawActiveRange(drawingContext, viewport, segmentSelection, laneHeaderWidth, rulerHeight);
        DrawTimeRangeSelection(drawingContext, viewport, info, laneHeaderWidth, rulerHeight);
        if (SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            DrawArrangementParentRowBackgrounds(
                drawingContext,
                viewport,
                Brushes.Black,
                laneHeaderWidth,
                rulerHeight);
        }
        long backgroundEnd = captureTimings
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
        TimelineRenderSnapshot? snapshot = Snapshot;
        if (snapshot is not null)
        {
            if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
            {
                DrawOnion(drawingContext, viewport, laneHeaderWidth, rulerHeight);
                DrawPianoNoteTiles(
                    drawingContext,
                    viewport,
                    red,
                    warning,
                    segmentPianoNote,
                    laneHeaderWidth,
                    rulerHeight);
                DrawPianoSelectionOverlay(
                    drawingContext,
                    viewport,
                    redDark,
                    warning,
                    red,
                    laneHeaderWidth,
                    rulerHeight);
            }
            else if (SurfaceMode == TimelineSurfaceMode.Velocity)
            {
                DrawVelocityTiles(
                    drawingContext,
                    viewport,
                    segmentPianoNote,
                    red,
                    border,
                    laneHeaderWidth,
                    rulerHeight);
                DrawVelocityEditOverlay(
                    drawingContext,
                    viewport,
                    segmentPianoNote,
                    red,
                    laneHeaderWidth,
                    rulerHeight);
            }
            else if (SurfaceMode == TimelineSurfaceMode.EventLanes)
            {
                DrawEventPointTiles(
                    drawingContext,
                    viewport,
                    info,
                    text,
                    border,
                    laneHeaderWidth,
                    rulerHeight);
            }
            else if (SurfaceMode == TimelineSurfaceMode.Conductor && snapshot.ConductorSource is not null)
            {
                DrawConductorMetaTiles(drawingContext, viewport, info, red, text, border, laneHeaderWidth, rulerHeight);
            }
            else
            {
                if (SurfaceMode == TimelineSurfaceMode.Arrangement)
                {
                    DrawArrangementConductorTiles(
                        drawingContext,
                        viewport,
                        info,
                        border,
                        laneHeaderWidth,
                        rulerHeight);
                }
                _genericRenderItems.Clear();
                bool genericReady = snapshot.TryQueryIntoCached(
                    viewport.StartTick,
                    viewport.EndTick,
                    viewport.FirstLane,
                    viewport.LastLaneExclusive,
                    _genericRenderItems);
                IReadOnlyList<TimelineRenderItem> renderItems;
                if (genericReady)
                {
                    _lastCompleteGenericRenderItems.Clear();
                    _lastCompleteGenericRenderItems.AddRange(_genericRenderItems);
                    _lastCompleteGenericProjectionKey = snapshot.ProjectionKey;
                    renderItems = _genericRenderItems;
                }
                else
                {
                    ScheduleExactPrefetch(
                        snapshot,
                        viewport.StartTick,
                        viewport.EndTick,
                        viewport.FirstLane,
                        viewport.LastLaneExclusive);
                    renderItems = string.Equals(
                        _lastCompleteGenericProjectionKey,
                        snapshot.ProjectionKey,
                        StringComparison.Ordinal)
                            ? _lastCompleteGenericRenderItems
                            : Array.Empty<TimelineRenderItem>();
                }
                bool allowDetailedSegmentPreviewRequests = true;
                _visibleSegmentPreviewFallbackFrames.Clear();
                if (SurfaceMode == TimelineSurfaceMode.Arrangement)
                {
                    allowDetailedSegmentPreviewRequests = PrepareVisibleSegmentPreviewFallbacks(
                        snapshot,
                        renderItems,
                        segmentNotePreview,
                        red);
                }
                foreach (TimelineRenderItem item in renderItems)
                {
                    if (SurfaceMode == TimelineSurfaceMode.Arrangement
                        && item.Kind is TimelineItemKind.ConductorEvent
                            or TimelineItemKind.Marker)
                    {
                        continue;
                    }
                    DrawItem(
                        drawingContext,
                        viewport,
                        item,
                        red,
                        redDark,
                        info,
                        warning,
                        segment,
                        selectedSegment,
                        segmentNotePreview,
                        segmentPianoNote,
                        laneHeaderWidth,
                        rulerHeight,
                        allowDetailedSegmentPreviewRequests);
                }
            }
        }

        long contentEnd = captureTimings
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;

        DrawDragPreview(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        DrawDirectManipulationHover(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        DrawCreationHoverPreview(drawingContext, viewport, red, info, laneHeaderWidth, rulerHeight);
        DrawSegmentPlacementPreview(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        DrawNotePlacementPreview(drawingContext, viewport, red, laneHeaderWidth, rulerHeight);
        DrawEventPointTrace(drawingContext, laneHeaderWidth, rulerHeight);
        DrawCursor(drawingContext, viewport, EditCursorTick, _editCursorPen!, laneHeaderWidth, rulerHeight);
        DrawTimelineChrome(
            drawingContext,
            viewport,
            alternate,
            border,
            text,
            pianoWhiteKey,
            pianoBlackKey,
            pianoKeyLabel,
            laneHeaderWidth,
            rulerHeight);
        if (SurfaceMode is TimelineSurfaceMode.Velocity or TimelineSurfaceMode.EventLanes)
        {
            DrawValueGrid(drawingContext, text, laneHeaderWidth, rulerHeight, drawLabels: true);
        }
        DrawRulerOverview(drawingContext, viewport, text, warning, laneHeaderWidth, rulerHeight);
        DrawMarquee(drawingContext, viewport, info);
        DrawSelectionFloatingTool(
            drawingContext,
            viewport,
            laneHeaderWidth,
            rulerHeight);
        if (captureTimings)
        {
            LastRenderPhaseTiming = TimelineSurfaceRenderPhaseTiming.FromTimestamps(
                frameStart,
                backgroundEnd,
                contentEnd,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        try { HandleTimelineMouseDown(e); }
        catch (OverflowException) { RejectOutOfRangeGesture(e); }
    }

    private void HandleTimelineMouseDown(MouseButtonEventArgs e)
    {
        // A new physical press supersedes a previous cold Conductor request.
        // Hover cannot replace the pending hit, but a later deliberate gesture can.
        // Replay clears its pending record before re-entering this handler.
        if (_pendingConductorPress is not null) CancelConductorHit();
        _pendingGestureCancellation.Cancel();
        _pendingGestureCancellation.Dispose();
        _pendingGestureCancellation = new();
        if (_marqueeQueryPending)
        {
            _marqueeQueryPending = false;
            Cursor = Cursors.Arrow;
        }
        _gestureToken = checked(_gestureToken + 1);
        Point point = InteractionPosition(e);
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && e.ChangedButton == MouseButton.Right
            && point.X >= 0
            && point.X < GetLaneHeaderWidth()
            && point.Y >= GetRulerHeight())
        {
            e.Handled = true;
            return;
        }
        Focus();
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panOrigin = point;
            _panStartTick = StartTick;
            _panFirstLane = FirstLane;
            _panValueScrollOffset = ValueScrollOffset;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }
        UpdatePointerPositionText(point, viewport);

        if (e.ChangedButton == MouseButton.Left
            && TryBeginSelectionFloatingToolGesture(point, viewport))
        {
            e.Handled = true;
            return;
        }

        if (TryDeferConductorPress(e, point, viewport))
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right
            && IsEditableTimelineContent(point, viewport))
        {
            if (TryConsumeRightDoubleClickCandidate(
                    point,
                    Environment.TickCount64))
            {
                CancelPendingRightGesture(cancelDelayedMenu: true);
                _suppressRightButtonUpAfterDoubleClick = true;
                ToggleDrawSelectToolMode();
                e.Handled = true;
                return;
            }

            BeginPendingRightGesture(point, ConductorGestureModifiers);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (SurfaceMode == TimelineSurfaceMode.Velocity
            && CanEdit
            && e.ChangedButton is MouseButton.Left or MouseButton.Right
            && point.X >= GetLaneHeaderWidth()
            && point.Y >= GetRulerHeight())
        {
            bool forceTrace = TimelineToolPolicy.ForcesValueTrace(
                ToolMode,
                SurfaceMode,
                e.ChangedButton,
                ConductorGestureModifiers);
            _velocityOrigin = point;
            _velocityButton = e.ChangedButton;
            _velocityEdits.Clear();
            _velocitySelectionRestricted = SelectionSnapshot is { Count: > 0 };
            TimelineRenderItem directItem = default;
            bool attemptedVelocityHit = e.ChangedButton == MouseButton.Left && !forceTrace;
            bool hasVelocityHit = attemptedVelocityHit
                && TryHitVelocityBar(point, viewport, out directItem);
            if (attemptedVelocityHit && !hasVelocityHit && _exactQueryPending)
            {
                Cursor = Cursors.Wait;
                e.Handled = true;
                return;
            }
            _velocityDirectItemId = hasVelocityHit ? directItem.Id : null;
            if (forceTrace)
            {
                RaiseEvent(new RoutedEventArgs(AltGestureConsumedEvent, this));
            }
            _velocityTracePoints.Clear();
            if (_velocityDirectItemId is MidoraId directId)
            {
                UpdateSingleVelocity(directId, point.Y, GetRulerHeight());
            }
            else
            {
                _velocityTracePoints.Add(ClampVelocityTracePoint(point));
            }
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.EventLanes
            && ToolMode == TimelineToolMode.Draw
            && CanEdit
            && (EventPointEditCompleted is not null || EventPointTraceCompleted is not null)
            && e.ChangedButton is MouseButton.Left or MouseButton.Right
            && point.X >= GetLaneHeaderWidth()
            && point.Y >= GetRulerHeight())
        {
            TimelineRenderItem directItem = default;
            bool forceTrace = TimelineToolPolicy.ForcesValueTrace(
                ToolMode,
                SurfaceMode,
                e.ChangedButton,
                ConductorGestureModifiers);
            bool hasDirectItem = e.ChangedButton == MouseButton.Left
                && !forceTrace
                && TryHitEventPoint(point, viewport, out directItem);
            if (e.ChangedButton == MouseButton.Left
                && !forceTrace
                && !hasDirectItem
                && _exactQueryPending)
            {
                Cursor = Cursors.Wait;
                e.Handled = true;
                return;
            }
            if (hasDirectItem)
            {
                goto ContinueDirectTimelineInteraction;
            }
            _eventPointOrigin = point;
            _eventPointButton = e.ChangedButton;
            _eventPointHorizontalTrace = TimelineToolPolicy.RequestsHorizontalValueTrace(
                ToolMode,
                SurfaceMode,
                e.ChangedButton,
                ConductorGestureModifiers);
            _eventPointTimeLocked = TimelineToolPolicy.RequestsTimeLockedPointCreation(
                ToolMode,
                SurfaceMode,
                e.ChangedButton,
                ConductorGestureModifiers);
            _eventPointEdits.Clear();
            _eventPointDirectItemId = null;
            if (forceTrace)
            {
                RaiseEvent(new RoutedEventArgs(AltGestureConsumedEvent, this));
            }
            _eventPointTracePoints.Clear();
            _eventPointTracePoints.Add(ClampEventPointTracePoint(point));
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
    ContinueDirectTimelineInteraction:
        if (e.ChangedButton == MouseButton.Right
            && SurfaceMode == TimelineSurfaceMode.PianoRoll
            && point.X < GetLaneHeaderWidth()
            && point.Y >= GetRulerHeight())
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        ModifierKeys modifiers = ConductorGestureModifiers;
        if (point.X >= laneHeaderWidth && point.Y >= 0 && point.Y < rulerHeight)
        {
            long rulerTick = viewport.XToTick(point.X - laneHeaderWidth);
            if (!IsTimeRangeSelectionEnabled)
            {
                RulerClicked?.Invoke(this, new(SnapAbsolute(rulerTick)));
                e.Handled = true;
                return;
            }
            _rulerDragOrigin = point;
            _rulerDragStartTick = rulerTick;
            _rulerDragCurrentTick = rulerTick;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && !IsInsideLaneContent(viewport, point.Y, rulerHeight))
        {
            Cursor = Cursors.Arrow;
            e.Handled = true;
            return;
        }
        int lane = YToLane(viewport, point.Y - rulerHeight);
        if (e.ChangedButton == MouseButton.Left
            && SurfaceMode == TimelineSurfaceMode.Arrangement
            && TryGetArrangementLaneHeader(point, out int commandLane)
            && (lane = commandLane) >= 0
            && TryGetArrangementLaneCommand(point.X, lane, out TimelineLaneHeaderCommand laneCommand))
        {
            LaneHeaderCommandInvoked?.Invoke(this, new(lane, laneCommand));
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && TryGetArrangementSecondaryLink(
                point,
                viewport,
                out ArrangementLaneDescriptor linkDescriptor,
                out _))
        {
            if (linkDescriptor is
                { Kind: ArrangementLaneKind.LogicalTrack, ParentId: MidoraId instrumentId })
            {
                ArrangementInstrumentInvoked?.Invoke(
                    this,
                    new TimelineArrangementInstrumentEventArgs(instrumentId));
            }
            else if (linkDescriptor is
            {
                Kind: ArrangementLaneKind.PureMidiTrack,
                ObjectId: MidoraId trackId,
                ParentId: MidoraId rootId
            })
            {
                ArrangementMidiRouteInvoked?.Invoke(
                    this,
                    new TimelineArrangementMidiRouteEventArgs(trackId, rootId));
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && SurfaceMode == TimelineSurfaceMode.Arrangement
            && TryGetArrangementLaneHeader(point, out int pressedHeaderLane))
        {
            lane = pressedHeaderLane;
            bool targetsSharedGroup = TryGetArrangementSharedGroupBraceTarget(
                point,
                out MidoraId pressedGroupId);
            if (e.ClickCount >= 2)
            {
                LaneHeaderDoubleInvoked?.Invoke(this, new(lane, targetsSharedGroup));
                e.Handled = true;
                return;
            }
            _pressedLaneHeader = lane;
            _pressedLaneHeaderTargetsSharedGroup = targetsSharedGroup;
            _pressedSharedGroupId = targetsSharedGroup ? pressedGroupId : null;
            _laneHeaderDragTarget = lane;
            _laneHeaderDragOrigin = point;
            _laneHeaderDragActivated = false;
            _laneHeaderDragJoinsTargetGroup = false;
            _laneHeaderDragInsertsAfterTarget = false;
            _laneHeaderDragDetachesFromSourceGroup = false;
            _laneHeaderDragExteriorBoundaryGroupId = null;
            _laneHeaderDragJoinUsesChip = false;
            _laneHeaderDragRequiresRebind = false;
            _laneHeaderDragMovesWholeGroup = targetsSharedGroup;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (point.X < laneHeaderWidth
            && point.Y >= rulerHeight
            && SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Conductor)
        {
            LaneHeaderInvoked?.Invoke(this, new(lane));
            e.Handled = true;
            return;
        }
        long tick = viewport.XToTick(point.X - laneHeaderWidth);
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && point.X < laneHeaderWidth
            && point.Y >= rulerHeight)
        {
            int pitch = Math.Clamp(127 - lane, 0, 127);
            int velocity = Math.Clamp(
                (int)Math.Round(32 + point.X / Math.Max(1, laneHeaderWidth) * 95),
                1,
                127);
            _activeLanePreview = new(lane, pitch, velocity);
            CaptureMouse();
            LanePreviewPressed?.Invoke(this, _activeLanePreview);
            e.Handled = true;
            return;
        }
        bool pointIsInContent = point.X >= laneHeaderWidth
            && point.Y >= rulerHeight
            && IsInsideLaneContent(viewport, point.Y, rulerHeight);
        if (pointIsInContent
            && SurfaceMode == TimelineSurfaceMode.Arrangement
            && Snapshot is TimelineRenderSnapshot arrangementSnapshot
            && (uint)lane < (uint)arrangementSnapshot.ArrangementLanes.Count
            && arrangementSnapshot.ArrangementLanes[lane].Kind == ArrangementLaneKind.Conductor)
        {
            RaiseBackgroundInvoked(point, viewport, isDoubleClick: false);
            Cursor = Cursors.Arrow;
            e.Handled = true;
            return;
        }
        if (pointIsInContent
            && SurfaceMode == TimelineSurfaceMode.Arrangement
            && Snapshot is TimelineRenderSnapshot parentLaneSnapshot
            && (uint)lane < (uint)parentLaneSnapshot.ArrangementLanes.Count
            && IsArrangementParentLane(parentLaneSnapshot.ArrangementLanes[lane].Kind))
        {
            Cursor = Cursors.Arrow;
            e.Handled = true;
            return;
        }
        double laneHeight = GetLaneVisualHeight(lane);
        double laneOffset = Math.Clamp(
            point.Y - GetLaneTop(viewport, lane, rulerHeight),
            0,
            laneHeight);
        double normalizedValue = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? ValueYToNormalized(point.Y, rulerHeight)
            : 1 - laneOffset / Math.Max(1, laneHeight);
        if (pointIsInContent
            && TimelineToolPolicy.StartsMarqueeBeforeItemHit(ToolMode, SurfaceMode, e.ClickCount))
        {
            _marqueeOrigin = point;
            _marqueeCurrent = point;
            _marqueeAnchorTick = Math.Max(0, tick);
            _marqueeAnchorLane = lane;
            _marqueeAnchorNormalizedValue = normalizedValue;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (pointIsInContent)
        {
            if (!PopulateTimelineHitItems(
                point,
                viewport,
                preferDirectEditEdges: CanEdit
                    && ToolMode == TimelineToolMode.Draw
                    && TimelineToolPolicy.IsDirectEditingSurface(SurfaceMode)))
            {
                Cursor = Cursors.Wait;
                e.Handled = true;
                return;
            }
            if (_hitItems.Count == 0 && Snapshot is { ConductorSource: null })
            {
                long pointTolerance = TimelineTickMath.CeilingDistance(4 / viewport.PixelsPerTick);
                if (!Snapshot.TryHitTestCached(tick, pointTolerance, lane, _hitItems))
                {
                    ScheduleExactPrefetch(
                        Snapshot,
                        Math.Max(0, tick - pointTolerance),
                        tick > long.MaxValue - pointTolerance - 1
                            ? long.MaxValue
                            : tick + pointTolerance + 1,
                        lane,
                        checked(lane + 1));
                    Cursor = Cursors.Wait;
                    e.Handled = true;
                    return;
                }
                _hitItems.RemoveAll(item => !IsEventPointKind(item.Kind));
            }
            _hitItems.RemoveAll(item => IsEventPointKind(item.Kind)
                && Math.Abs(
                    SurfaceMode == TimelineSurfaceMode.EventLanes
                        ? NormalizedToValueY(item.Value, rulerHeight) - point.Y
                        : GetLaneTop(viewport, item.Lane, rulerHeight)
                          + 4 + (1 - Math.Clamp(item.Value, 0, 1)) * Math.Max(1, GetLaneVisualHeight(item.Lane) - 8)
                          - point.Y) > 8);
        }
        else
        {
            _hitItems.Clear();
        }
        if (_hitItems.Count != 0)
        {
            if (ToolMode == TimelineToolMode.Select && e.ClickCount == 1)
            {
                RaiseBackgroundInvoked(point, viewport, isDoubleClick: false);
            }
            int hitIndex = 0;
            if ((modifiers & ModifierKeys.Alt) != 0 && _hitItems.Count > 1)
            {
                int primaryIndex = _hitItems.FindIndex(item =>
                    IsPrimary(item));
                hitIndex = primaryIndex < 0 ? 0 : (primaryIndex + 1) % _hitItems.Count;
            }
            TimelineRenderItem hit = _hitItems[hitIndex];
            bool canBeginItemEdit = e.ClickCount == 1
                && CanEdit
                && TimelineToolPolicy.CanBeginItemEdit(ToolMode, SurfaceMode, hit.Kind);
            bool preserveSelectionForPotentialCopyDrag =
                TimelineToolPolicy.DefersControlSelectionToggleForPotentialDrag(
                    ToolMode,
                    SurfaceMode,
                    hit.Kind,
                    modifiers,
                    IsSelected(hit));
            bool deferPlainDrawSegmentSelection = ToolMode == TimelineToolMode.Draw
                && SurfaceMode == TimelineSurfaceMode.Arrangement
                && hit.Kind == TimelineItemKind.Segment
                && modifiers == ModifierKeys.None
                && IsSelected(hit)
                && (SelectionSnapshot?.Count ?? 0) > 1;
            ItemInvoked?.Invoke(
                this,
                new TimelineItemEventArgs(
                    hit,
                    tick,
                    lane,
                    modifiers,
                    e.ClickCount == 2,
                    preserveSelectionForPotentialCopyDrag:
                        preserveSelectionForPotentialCopyDrag
                            || deferPlainDrawSegmentSelection));
            if (CanEdit
                && ToolMode == TimelineToolMode.Split
                && SurfaceMode == TimelineSurfaceMode.Arrangement
                && hit.Kind == TimelineItemKind.Segment)
            {
                SegmentSplitRequested?.Invoke(
                    this,
                    new TimelineItemEventArgs(hit, tick, lane, modifiers, isDoubleClick: false));
                e.Handled = true;
                return;
            }
            if (canBeginItemEdit)
            {
                double left = laneHeaderWidth + viewport.TickToX(hit.StartTick);
                double right = laneHeaderWidth + viewport.TickToX(hit.EndTick);
                _dragKind = TimelineToolPolicy.ResolveItemEditKind(
                    ToolMode,
                    SurfaceMode,
                    hit.Kind,
                    modifiers,
                    isNearStart: Math.Abs(point.X - left)
                        <= TimelineToolPolicy.DirectEditEdgeTolerancePixels,
                    isNearEnd: Math.Abs(point.X - right)
                        <= TimelineToolPolicy.DirectEditEdgeTolerancePixels);
                if (TimelineToolPolicy.ForcesItemMove(
                        ToolMode,
                        SurfaceMode,
                        hit.Kind,
                        modifiers))
                {
                    RaiseEvent(new RoutedEventArgs(AltGestureConsumedEvent, this));
                }
                _dragItem = hit;
                _dragModifiers = modifiers;
                _dragOrigin = point;
                _dragOriginTick = tick;
                _dragOriginLane = lane;
                _dragCurrentTick = tick;
                _dragCurrentLane = lane;
                _dragActivated = false;
                _dragCopyRequested = (modifiers & ModifierKeys.Control) != 0
                    && TimelineToolPolicy.SupportsCopyDrag(
                        ToolMode,
                        SurfaceMode,
                        hit.Kind,
                    _dragKind);
                _deferredControlClickToggle = preserveSelectionForPotentialCopyDrag;
                _deferredPlainDrawSegmentSelection = deferPlainDrawSegmentSelection;
                _dragTimeLocked = TimelineToolPolicy.RequestsTimeLockedItemMove(
                    ToolMode,
                    SurfaceMode,
                    hit.Kind,
                    _dragKind,
                    modifiers);
                _dragPreviewSelectionPrepared = false;
                _dragPreviewSelection = null;
                InvalidateDragPreviewGeometry();
                PrepareDragPitchPreview(hit);
                CaptureMouse();
            }
        }
        else
        {
            if (CanEdit
                && ToolMode == TimelineToolMode.Draw
                && SurfaceMode == TimelineSurfaceMode.Arrangement
                && pointIsInContent)
            {
                long snappedStart = SnapAbsolute(tick);
                _segmentPlacementStartTick = snappedStart;
                _segmentPlacementCurrentTick = checked(snappedStart + Math.Max(1, DefaultCreationLengthTicks));
                _segmentPlacementLane = lane;
                _segmentPlacementOrigin = point;
                _segmentPlacementActivated = false;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (CanEdit
                && ToolMode == TimelineToolMode.Draw
                && SurfaceMode == TimelineSurfaceMode.PianoRoll
                && pointIsInContent)
            {
                long snappedStart = SnapAbsolute(tick);
                _notePlacementStartTick = snappedStart;
                _notePlacementInitialLengthTicks = Math.Max(1, DefaultCreationLengthTicks);
                _notePlacementCurrentTick = checked(
                    snappedStart + _notePlacementInitialLengthTicks);
                _notePlacementPointerAnchorTick = tick;
                _notePlacementPitch = Math.Clamp(127 - lane, 0, 127);
                _notePlacementVelocity = Math.Clamp(DefaultVelocity, 1, 127);
                _notePlacementOrigin = point;
                _notePlacementActivated = false;
                _notePlacementTimeLocked = TimelineToolPolicy.RequestsTimeLockedNotePlacement(
                    ToolMode,
                    SurfaceMode,
                    e.ChangedButton,
                    modifiers);
                CaptureMouse();
                NotePlacementStarted?.Invoke(this, new(
                    snappedStart,
                    _notePlacementCurrentTick,
                    _notePlacementPitch,
                    _notePlacementVelocity));
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            bool requestsCreation = CanEdit
                && TimelineToolPolicy.RequestsBackgroundCreation(
                    ToolMode,
                    SurfaceMode,
                    e.ClickCount)
                && pointIsInContent;
            BackgroundInvoked?.Invoke(
                this,
                new TimelinePointEventArgs(
                    tick,
                    lane,
                    normalizedValue,
                    Keyboard.Modifiers,
                    requestsCreation,
                    isEmptyBackground: SurfaceMode == TimelineSurfaceMode.Arrangement));
            if (requestsCreation)
            {
                e.Handled = true;
                return;
            }
            if (!pointIsInContent)
            {
                e.Handled = true;
                return;
            }
            _marqueeOrigin = point;
            _marqueeCurrent = point;
            _marqueeAnchorTick = Math.Max(0, tick);
            _marqueeAnchorLane = lane;
            _marqueeAnchorNormalizedValue = normalizedValue;
            CaptureMouse();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    private bool IsEditableTimelineContent(Point point, TimelineViewport viewport)
    {
        if (SurfaceMode is not (TimelineSurfaceMode.Arrangement
            or TimelineSurfaceMode.PianoRoll
            or TimelineSurfaceMode.EventLanes
            or TimelineSurfaceMode.Conductor))
        {
            return false;
        }
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        return point.X >= laneHeaderWidth
            && point.X <= ActualWidth
            && point.Y >= rulerHeight
            && point.Y <= ActualHeight
            && IsInsideLaneContent(viewport, point.Y, rulerHeight);
    }

    private void BeginPendingRightGesture(Point point, ModifierKeys modifiers)
    {
        CancelPendingRightGesture(cancelDelayedMenu: true);
        _pendingRightGestureOrigin = point;
        _pendingRightGestureDragThresholdReached = false;
        _pendingRightGestureModifiers = modifiers;
        _pendingRightGestureCanTrace = CanEdit
            && SurfaceMode == TimelineSurfaceMode.EventLanes
            && ToolMode == TimelineToolMode.Draw
            && (EventPointEditCompleted is not null || EventPointTraceCompleted is not null);
        _suppressAutomaticContextMenuOpening = true;
        PrepareDelayedContextMenuQuery(
            point,
            modifiers,
            startTimer: false,
            awaitingMouseUp: true);
    }

    private bool TryConsumeRightDoubleClickCandidate(
        Point point,
        long timestampMilliseconds)
    {
        if (_rightDoubleClickCandidateTimestampMilliseconds is not long firstTimestamp)
        {
            return false;
        }

        Point firstPoint = _rightDoubleClickCandidatePoint;
        ClearRightDoubleClickCandidate();
        return TimelineToolPolicy.IsRightDoubleClick(
            timestampMilliseconds - firstTimestamp,
            point.X - firstPoint.X,
            point.Y - firstPoint.Y);
    }

    private void RegisterRightDoubleClickCandidate(
        Point point,
        long timestampMilliseconds)
    {
        _rightDoubleClickCandidatePoint = point;
        _rightDoubleClickCandidateTimestampMilliseconds = timestampMilliseconds;
    }

    private void ClearRightDoubleClickCandidate()
    {
        _rightDoubleClickCandidateTimestampMilliseconds = null;
        _rightDoubleClickCandidatePoint = default;
    }

    private void BeginRightButtonEventTrace(Point origin, Point current)
    {
        ModifierKeys modifiers = _pendingRightGestureModifiers;
        _pendingRightGestureOrigin = null;
        _pendingRightGestureCanTrace = false;
        _eventPointOrigin = origin;
        _eventPointButton = MouseButton.Right;
        _eventPointHorizontalTrace = TimelineToolPolicy.RequestsHorizontalValueTrace(
            ToolMode,
            SurfaceMode,
            MouseButton.Right,
            modifiers);
        _eventPointTimeLocked = TimelineToolPolicy.RequestsTimeLockedPointCreation(
            ToolMode,
            SurfaceMode,
            MouseButton.Right,
            modifiers);
        _eventPointEdits.Clear();
        _eventPointDirectItemId = null;
        _eventPointTracePoints.Clear();
        _eventPointTracePoints.Add(ClampEventPointTracePoint(origin));
        UpdateEventPointTrace(origin, current);
    }

    private void PrepareDelayedContextMenuQuery(
        Point point,
        ModifierKeys modifiers,
        bool startTimer,
        bool awaitingMouseUp)
    {
        CancelDelayedContextMenu();
        if (ContextMenu is null)
        {
            _delayedContextMenuQueryReady = true;
            _delayedContextMenuAwaitingMouseUp = awaitingMouseUp;
            _suppressAutomaticContextMenuOpening = false;
            return;
        }

        if (!TryCreateViewport(out TimelineViewport viewport)
            || !IsEditableTimelineContent(point, viewport))
        {
            _suppressAutomaticContextMenuOpening = false;
            return;
        }

        TimelineRenderSnapshot? snapshot = Snapshot;
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        long tick = viewport.XToContainingTick(point.X - laneHeaderWidth);
        bool isValuePointLane = SurfaceMode == TimelineSurfaceMode.EventLanes;
        int lane = isValuePointLane ? 0 : YToLane(viewport, point.Y - rulerHeight);
        // Freeze the value-axis projection with the target, not just tick/lane.
        // An event lane is one two-dimensional lane, not a stack of LaneHeight rows.
        RightClickValuePointProjection? pointProjection = isValuePointLane
            ? new(viewport, point, laneHeaderWidth, rulerHeight,
                Math.Max(1, ActualHeight - rulerHeight), _valueViewMinimum, _valueViewMaximum)
            : null;
        long generation = checked(++_delayedContextMenuGeneration);
        CancellationTokenSource cancellation = new();
        _delayedContextMenuQueryCancellation = cancellation;
        _delayedContextMenuDelayElapsed = false;
        _delayedContextMenuQueryReady = snapshot is null;
        _delayedContextMenuAwaitingMouseUp = awaitingMouseUp;
        _delayedContextMenuTargetApplied = false;
        _delayedContextMenuHit = null;
        _delayedContextMenuSelectionSnapshot = SelectionSnapshot;
        _delayedContextMenuTick = tick;
        _delayedContextMenuLane = lane;
        _delayedContextMenuModifiers = modifiers;
        _suppressAutomaticContextMenuOpening = true;
        if (startTimer) StartDelayedContextMenuTimer();

        if (snapshot is null)
        {
            TryOpenDelayedContextMenu();
            return;
        }

        CancellationToken cancellationToken = cancellation.Token;
        _ = Task.Run(
                () => QueryRightClickContextTarget(
                    snapshot,
                    tick,
                    lane,
                    pointProjection,
                    cancellationToken),
                cancellationToken)
            .ContinueWith(
                task =>
                {
                    if (task.IsCanceled) return;
                    if (task.IsFaulted)
                    {
                        _ = task.Exception;
                        CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                            () =>
                            {
                                if (generation == _delayedContextMenuGeneration)
                                {
                                    CancelDelayedContextMenu();
                                }
                            }, DispatcherPriority.Input);
                        return;
                    }
                    CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                        () => CompleteDelayedContextMenuQuery(
                            generation,
                            snapshot,
                            tick,
                            lane,
                            task.Result), DispatcherPriority.Input);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void StartDelayedContextMenuTimer()
    {
        if (_delayedContextMenuTimer is not null) return;
        _delayedContextMenuTimer = new(
            TimeSpan.FromMilliseconds(
                TimelineToolPolicy.RightDoubleClickIntervalMilliseconds),
            DispatcherPriority.Input,
            OnDelayedContextMenuTick,
            Dispatcher);
        _delayedContextMenuTimer.Start();
    }

    private static TimelineRenderItem? QueryRightClickContextTarget(
        TimelineRenderSnapshot snapshot,
        long tick,
        int lane,
        RightClickValuePointProjection? pointProjection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pointProjection is { } projection)
        {
            long tolerance = Math.Max(1, CeilingToLong(8 / projection.Viewport.PixelsPerTick));
            long start = tick - Math.Min(tick, tolerance);
            long end = tick >= long.MaxValue - tolerance ? long.MaxValue : tick + tolerance + 1;
            TimelineRenderItem? hit = null;
            // Stream the exact source in the worker. A zoomed-out eight-DIP range
            // can contain millions of points; do not materialize/sort that range.
            snapshot.VisitInto(start, end, 0, 1, candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.State.HasFlag(TimelineItemState.HitTestDisabled)
                    || !IsEventPointKind(candidate.Kind)
                    || !projection.Contains(candidate)) return;
                if (hit is not { } current
                    || candidate.ZIndex > current.ZIndex
                    || candidate.ZIndex == current.ZIndex
                        && (candidate.Length < current.Length
                            || candidate.Length == current.Length && candidate.Id.CompareTo(current.Id) < 0))
                    hit = candidate;
            });
            cancellationToken.ThrowIfCancellationRequested();
            return hit;
        }
        List<TimelineRenderItem> items = [];
        snapshot.HitTestInto(tick, 0, lane, items);
        cancellationToken.ThrowIfCancellationRequested();
        return items.Count == 0 ? null : items[0];
    }

    private readonly record struct RightClickValuePointProjection(
        TimelineViewport Viewport, Point Pointer, double HeaderWidth, double RulerHeight,
        double ContentHeight, double ValueMinimum, double ValueMaximum)
    {
        public bool Contains(TimelineRenderItem item)
        {
            double x = HeaderWidth + Viewport.TickToX(item.StartTick);
            double y = RulerHeight
                + (ValueMaximum - Math.Clamp(item.Value, ValueMinimum, ValueMaximum))
                / Math.Max(1d / 256, ValueMaximum - ValueMinimum) * ContentHeight;
            return Math.Abs(Pointer.X - x) <= 8 && Math.Abs(Pointer.Y - y) <= 8;
        }
    }

    private void CompleteDelayedContextMenuQuery(
        long generation,
        TimelineRenderSnapshot snapshot,
        long tick,
        int lane,
        TimelineRenderItem? hit)
    {
        if (generation != _delayedContextMenuGeneration
            || _delayedContextMenuQueryCancellation?.IsCancellationRequested != false)
        {
            return;
        }
        if (!ReferenceEquals(Snapshot, snapshot))
        {
            CancelDelayedContextMenu();
            return;
        }

        _delayedContextMenuHit = hit;
        _delayedContextMenuQueryReady = true;
        if (_delayedContextMenuAwaitingMouseUp) return;
        TryOpenDelayedContextMenu();
    }

    private void ApplyDelayedContextMenuTarget()
    {
        if (_delayedContextMenuTargetApplied) return;
        _delayedContextMenuTargetApplied = true;
        if (_delayedContextMenuHit is not TimelineRenderItem item) return;
        bool wasSelectedAtRightDown =
            _delayedContextMenuSelectionSnapshot?.Contains(item.Id) == true;
        ItemInvoked?.Invoke(
            this,
            new TimelineItemEventArgs(
                item,
                _delayedContextMenuTick,
                _delayedContextMenuLane,
                _delayedContextMenuModifiers,
                isDoubleClick: false,
                preserveExistingSelection: wasSelectedAtRightDown));
    }

    private void OnDelayedContextMenuTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_delayedContextMenuTimer is DispatcherTimer timer)
        {
            timer.Stop();
            timer.Tick -= OnDelayedContextMenuTick;
            _delayedContextMenuTimer = null;
        }
        _delayedContextMenuDelayElapsed = true;
        TryOpenDelayedContextMenu();
    }

    private void TryOpenDelayedContextMenu()
    {
        if (!_delayedContextMenuDelayElapsed || !_delayedContextMenuQueryReady) return;
        ContextMenu? menu = ContextMenu;
        bool canOpen = IsLoaded && menu is not null;
        ApplyDelayedContextMenuTarget();
        CompleteDelayedContextMenuRequest(cancelQuery: false);
        _suppressAutomaticContextMenuOpening = false;
        if (!canOpen || menu is null) return;
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void CancelPendingRightGesture(bool cancelDelayedMenu)
    {
        _pendingRightGestureOrigin = null;
        _pendingRightGestureCanTrace = false;
        _pendingRightGestureDragThresholdReached = false;
        _suppressRightButtonUpAfterDoubleClick = false;
        if (cancelDelayedMenu)
        {
            CancelDelayedContextMenu();
        }
        else if (_delayedContextMenuQueryCancellation is null)
        {
            _suppressAutomaticContextMenuOpening = false;
        }
    }

    private void CancelDelayedContextMenu()
    {
        CompleteDelayedContextMenuRequest(cancelQuery: true);
        _suppressAutomaticContextMenuOpening = false;
    }

    private void CompleteDelayedContextMenuRequest(bool cancelQuery)
    {
        ClearRightDoubleClickCandidate();
        checked { _delayedContextMenuGeneration++; }
        if (_delayedContextMenuTimer is DispatcherTimer timer)
        {
            timer.Stop();
            timer.Tick -= OnDelayedContextMenuTick;
            _delayedContextMenuTimer = null;
        }
        if (_delayedContextMenuQueryCancellation is CancellationTokenSource cancellation)
        {
            if (cancelQuery) cancellation.Cancel();
            cancellation.Dispose();
            _delayedContextMenuQueryCancellation = null;
        }
        _delayedContextMenuDelayElapsed = false;
        _delayedContextMenuQueryReady = false;
        _delayedContextMenuAwaitingMouseUp = false;
        _delayedContextMenuTargetApplied = false;
        _delayedContextMenuHit = null;
        _delayedContextMenuSelectionSnapshot = null;
    }

    internal void ToggleDrawSelectToolMode()
    {
        TimelineToolMode next = TimelineToolPolicy.ResolveDrawSelectToggle(ToolMode);
        SetCurrentValue(ToolModeProperty, next);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_pendingRightGestureOrigin is not null
            || _suppressRightButtonUpAfterDoubleClick)
        {
            e.Handled = true;
            return;
        }
        Point point = e.GetPosition(this);
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && point.X < GetLaneHeaderWidth())
        {
            // The piano keyboard ruler is an audition surface only. Swallow the
            // right-click before WPF can briefly open the Timeline ContextMenu.
            e.Handled = true;
            return;
        }
        base.OnMouseRightButtonDown(e);
        if (SurfaceMode == TimelineSurfaceMode.Velocity || _eventPointOrigin is not null) return;
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && TryGetArrangementLaneHeader(point, out int headerLane))
        {
            LaneHeaderContextRequested?.Invoke(
                this,
                new(
                    headerLane,
                    TryGetArrangementSharedGroupBraceTarget(point, out _)));
            return;
        }
        // Content-target selection is deliberately deferred to the frozen
        // right-button gesture created by OnMouseDown. Running an exact hit
        // query here would put a cold page decode back on the WPF thread and
        // could also select a different object from the one whose menu opens.
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        if (_suppressAutomaticContextMenuOpening)
        {
            e.Handled = true;
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && Mouse.GetPosition(this).X < GetLaneHeaderWidth())
        {
            // The ContextMenuOpening event follows the mouse event. Handling
            // both prevents the shared menu from flashing for one frame.
            e.Handled = true;
            return;
        }
        base.OnContextMenuOpening(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        try { HandleTimelineMouseMove(e); }
        catch (OverflowException) { RejectOutOfRangeGesture(e); }
    }

    private void HandleTimelineMouseMove(MouseEventArgs e)
    {
        Point point = InteractionPosition(e);
        if (_pendingConductorPress is { } pendingConductor)
        {
            pendingConductor.LastPosition = point;
            pendingConductor.LastMove = e;
            if (pendingConductor.MovePositions.Count == 0 || pendingConductor.MovePositions[^1] != point)
                pendingConductor.MovePositions.Add(point);
            e.Handled = true;
            return;
        }
        _hoverPoint = point;
        if (TryCreateViewport(out TimelineViewport pointerViewport))
        {
            UpdatePointerPositionText(point, pointerViewport);
        }
        int? previousHoverLaneHeader = _hoverLaneHeader;
        MidoraId? previousHoverSharedGroupId = _hoverSharedGroupId;
        if (_selectionToolGripOrigin is Point selectionToolGripOrigin
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport selectionToolViewport))
        {
            UpdateSelectionFloatingToolGrip(
                point,
                selectionToolGripOrigin,
                selectionToolViewport);
            InvalidateVisual();
            return;
        }
        bool hoversSharedGroupBrace = TryGetArrangementSharedGroupBraceTarget(
            point,
            out MidoraId hoverSharedGroupId);
        _hoverSharedGroupId = hoversSharedGroupBrace ? hoverSharedGroupId : null;
        _hoverLaneHeader = !hoversSharedGroupBrace
            && TryGetArrangementLaneHeader(point, out int hoverLane)
                ? hoverLane
                : null;
        if (_pressedLaneHeader is int pressedLane
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport headerViewport))
        {
            bool wasJoiningTargetGroup = _laneHeaderDragJoinsTargetGroup;
            MidoraId? previousTargetGroupId = Snapshot is TimelineRenderSnapshot previousSnapshot
                && (uint)_laneHeaderDragTarget < (uint)previousSnapshot.ArrangementLanes.Count
                    ? previousSnapshot.ArrangementLanes[_laneHeaderDragTarget].SharedGroupId
                    : null;
            int nextTarget = YToLane(headerViewport, point.Y - GetRulerHeight());
            int laneCount = Snapshot?.LaneLabels.Count ?? 0;
            _laneHeaderDragTarget = laneCount == 0 ? 0 : Math.Clamp(nextTarget, 0, laneCount - 1);
            _laneHeaderDragJoinsTargetGroup = false;
            _laneHeaderDragInsertsAfterTarget = _laneHeaderDragTarget > pressedLane;
            _laneHeaderDragDetachesFromSourceGroup = false;
            _laneHeaderDragExteriorBoundaryGroupId = null;
            _laneHeaderDragJoinUsesChip = false;
            _laneHeaderDragRequiresRebind = false;
            if (Snapshot is TimelineRenderSnapshot arrangementSnapshot
                && (uint)pressedLane < (uint)arrangementSnapshot.ArrangementLanes.Count
                && (uint)_laneHeaderDragTarget < (uint)arrangementSnapshot.ArrangementLanes.Count)
            {
                ArrangementLaneDescriptor sourceDescriptor = arrangementSnapshot.ArrangementLanes[pressedLane];
                ArrangementLaneDescriptor targetDescriptor = arrangementSnapshot.ArrangementLanes[_laneHeaderDragTarget];
                bool sameTrackKind = sourceDescriptor.Kind == targetDescriptor.Kind
                    && sourceDescriptor.Kind is ArrangementLaneKind.LogicalTrack
                        or ArrangementLaneKind.PureMidiTrack;
                bool differentGroup = sourceDescriptor.SharedGroupId != targetDescriptor.SharedGroupId;
                if (!_laneHeaderDragMovesWholeGroup
                    && sameTrackKind
                    && point.X < GetLaneHeaderWidth()
                    && targetDescriptor is { IsSharedGroup: true, SharedGroupId: MidoraId targetGroupId })
                {
                    int firstGroupLane = arrangementSnapshot.ArrangementLanes
                        .First(value => value.SharedGroupId == targetGroupId).Lane;
                    int lastGroupLane = arrangementSnapshot.ArrangementLanes
                        .Last(value => value.SharedGroupId == targetGroupId).Lane;
                    double groupTop = GetLaneTop(headerViewport, firstGroupLane, GetRulerHeight());
                    double groupBottom = GetLaneTop(
                        headerViewport,
                        lastGroupLane,
                        GetRulerHeight()) + GetLaneVisualHeight(lastGroupLane);
                    bool retainedJoin = differentGroup
                        && wasJoiningTargetGroup
                        && previousTargetGroupId == targetGroupId;
                    ArrangementSharedGroupDropZone dropZone =
                        TimelineToolPolicy.ResolveArrangementSharedGroupDropZone(
                            point.Y,
                            groupTop,
                            groupBottom,
                            differentGroup,
                            retainedJoin);
                    if (dropZone == ArrangementSharedGroupDropZone.Before)
                    {
                        _laneHeaderDragTarget = firstGroupLane;
                        _laneHeaderDragInsertsAfterTarget = false;
                        _laneHeaderDragDetachesFromSourceGroup = !differentGroup;
                        _laneHeaderDragExteriorBoundaryGroupId = targetGroupId;
                        _laneHeaderDragExteriorBoundaryZone = dropZone;
                    }
                    else if (dropZone == ArrangementSharedGroupDropZone.After)
                    {
                        _laneHeaderDragTarget = lastGroupLane;
                        _laneHeaderDragInsertsAfterTarget = true;
                        _laneHeaderDragDetachesFromSourceGroup = !differentGroup;
                        _laneHeaderDragExteriorBoundaryGroupId = targetGroupId;
                        _laneHeaderDragExteriorBoundaryZone = dropZone;
                    }
                    else if (differentGroup)
                    {
                        _laneHeaderDragJoinsTargetGroup = true;
                        _laneHeaderDragRequiresRebind = sourceDescriptor.Kind == ArrangementLaneKind.LogicalTrack
                            && sourceDescriptor.ParentId != targetDescriptor.ParentId;
                    }
                }
                else if (!_laneHeaderDragMovesWholeGroup
                    && sameTrackKind
                    && differentGroup
                    && targetDescriptor.SharedGroupId.HasValue
                    && point.X < GetLaneHeaderWidth()
                    && TryGetArrangementJoinChipBounds(
                        headerViewport,
                        _laneHeaderDragTarget,
                        out Rect chipBounds)
                    && chipBounds.Contains(point))
                {
                    _laneHeaderDragJoinsTargetGroup = true;
                    _laneHeaderDragJoinUsesChip = true;
                    _laneHeaderDragRequiresRebind = sourceDescriptor.Kind == ArrangementLaneKind.LogicalTrack
                        && sourceDescriptor.ParentId != targetDescriptor.ParentId;
                }
            }
            double reorderDeltaX = point.X - _laneHeaderDragOrigin.X;
            double reorderDeltaY = point.Y - _laneHeaderDragOrigin.Y;
            _laneHeaderDragActivated |= reorderDeltaX * reorderDeltaX
                + reorderDeltaY * reorderDeltaY >= 100;
            if (_laneHeaderDragJoinsTargetGroup
                || _laneHeaderDragExteriorBoundaryGroupId.HasValue)
            {
                // A group body or exterior-boundary drop does not target the
                // member Track currently under the pointer. Keep the semantic
                // group preview as the only hover target during this gesture.
                _hoverLaneHeader = null;
                _hoverSharedGroupId = null;
            }
            Cursor = _laneHeaderDragActivated ? Cursors.SizeNS : Cursors.Arrow;
            InvalidateVisual();
            return;
        }
        bool hoverChangesVisual = ToolMode == TimelineToolMode.Draw
            && SurfaceMode is TimelineSurfaceMode.Arrangement
                or TimelineSurfaceMode.PianoRoll
                or TimelineSurfaceMode.EventLanes;
        hoverChangesVisual |= SurfaceMode == TimelineSurfaceMode.Arrangement
            && point.X < GetLaneHeaderWidth();
        if (_pendingRightGestureOrigin is Point rightOrigin
            && e.RightButton == MouseButtonState.Pressed)
        {
            double deltaX = Math.Abs(point.X - rightOrigin.X);
            double deltaY = Math.Abs(point.Y - rightOrigin.Y);
            if (deltaX >= SystemParameters.MinimumHorizontalDragDistance
                || deltaY >= SystemParameters.MinimumVerticalDragDistance)
            {
                _pendingRightGestureDragThresholdReached = true;
                if (_pendingRightGestureCanTrace)
                {
                    // Crossing the drag threshold resolves the gesture as a
                    // trace. The exact point query frozen at Right Down is no
                    // longer relevant and must never delay drawing.
                    CancelDelayedContextMenu();
                    BeginRightButtonEventTrace(rightOrigin, point);
                }
                else
                {
                    CancelPendingRightGesture(cancelDelayedMenu: true);
                    ReleaseMouseCapture();
                }
                InvalidateVisual();
                return;
            }
        }
        if (_velocityOrigin is Point velocityOrigin
            && (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed))
        {
            if (_velocityDirectItemId is MidoraId directId)
            {
                UpdateSingleVelocity(directId, point.Y, GetRulerHeight());
            }
            else
            {
                UpdateVelocityTrace(velocityOrigin, point);
            }
            InvalidateVisual();
            return;
        }
        if (_eventPointOrigin is Point eventPointOrigin
            && (_replayingConductorMove || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed))
        {
            if (_eventPointDirectItemId is MidoraId directId
                && Snapshot?.TryGetItem(directId, out TimelineRenderItem directItem) == true)
            {
                UpdateSingleEventPoint(directItem.StartTick, point.Y, GetRulerHeight());
            }
            else
            {
                UpdateEventPointTrace(eventPointOrigin, point);
            }
            InvalidateVisual();
            return;
        }
        if ((_replayingConductorMove || e.LeftButton == MouseButtonState.Pressed)
            && (_notePlacementStartTick is not null
                || _segmentPlacementStartTick is not null
                || _dragItem is not null))
        {
            AutoScrollEditGesture(
                point,
                allowHorizontal: !(_notePlacementStartTick is not null && _notePlacementTimeLocked)
                    && !(_dragItem is not null && _dragTimeLocked));
        }
        if (_segmentPlacementStartTick is long segmentStart
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport segmentPlacementViewport))
        {
            _segmentPlacementActivated |= Math.Abs(point.X - _segmentPlacementOrigin.X) >= 3;
            if (_segmentPlacementActivated)
            {
                long rawEnd = segmentPlacementViewport.XToTick(point.X - GetLaneHeaderWidth());
                long rawDelta = Math.Max(1, checked(rawEnd - segmentStart));
                long snappedDelta = SnapOperationDelta(rawDelta, checked(segmentStart + rawDelta));
                _segmentPlacementCurrentTick = checked(segmentStart + Math.Max(1, snappedDelta));
            }
            InvalidateVisual();
            return;
        }
        if (_notePlacementStartTick is not null
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport placementViewport))
        {
            _notePlacementActivated |= Math.Abs(point.X - _notePlacementOrigin.X) >= 3;
            if (_notePlacementActivated && !_notePlacementTimeLocked)
            {
                long rawEnd = placementViewport.XToTick(point.X - GetLaneHeaderWidth());
                long startTick = _notePlacementStartTick.Value;
                long pointerDelta = checked(rawEnd - _notePlacementPointerAnchorTick);
                long operationDelta = pointerDelta <= 0
                    ? 0
                    : GetPositiveCreationOperationDelta(pointerDelta, rawEnd);
                _notePlacementCurrentTick = checked(
                    startTick + _notePlacementInitialLengthTicks + operationDelta);
            }
            int placementLane = YToLane(placementViewport, point.Y - GetRulerHeight());
            int placementPitch = Math.Clamp(127 - placementLane, 0, 127);
            if (placementPitch != _notePlacementPitch)
            {
                _notePlacementPitch = placementPitch;
                PitchPreviewRequested?.Invoke(
                    this,
                    new TimelinePitchPreviewEventArgs(
                        _notePlacementPitch,
                        _notePlacementVelocity));
            }
            InvalidateVisual();
            return;
        }
        if (_dragItem is TimelineRenderItem dragItem && (_replayingConductorMove || e.LeftButton == MouseButtonState.Pressed)
            && TryCreateViewport(out TimelineViewport dragViewport))
        {
            _dragCurrentTick = _dragTimeLocked
                ? _dragOriginTick
                : dragViewport.XToTick(point.X - GetLaneHeaderWidth());
            _dragCurrentLane = SurfaceMode == TimelineSurfaceMode.Conductor && Snapshot?.ConductorSource is not null
                ? _dragOriginLane : YToLane(dragViewport, point.Y - GetRulerHeight());
            bool wasActivated = _dragActivated;
            _dragActivated |= Math.Abs(point.X - _dragOrigin.X) >= 3
                || Math.Abs(point.Y - _dragOrigin.Y) >= 3;
            if (!wasActivated && _dragActivated)
            {
                _deferredControlClickToggle = false;
            }
            if (_dragActivated)
            {
                PrepareDragPreviewSelection(dragItem);
                // Validate before scheduling OnRender. An impossible edit must
                // never leave a throwing preview attached to the WPF render loop.
                _ = GetDragPreviewTransform(dragItem);
                UpdateDragPitchPreview();
                bool invalidArrangementParentTarget = SurfaceMode == TimelineSurfaceMode.Arrangement
                    && Snapshot is TimelineRenderSnapshot arrangementSnapshot
                    && (uint)_dragCurrentLane < (uint)arrangementSnapshot.ArrangementLanes.Count
                    && IsArrangementParentLane(
                        arrangementSnapshot.ArrangementLanes[_dragCurrentLane].Kind);
                Cursor = invalidArrangementParentTarget
                    ? Cursors.Arrow
                    : IsValueEditableEventPointKind(dragItem.Kind)
                        ? Cursors.SizeNS
                        : _dragKind == TimelineItemEditKind.Move
                            ? Cursors.SizeAll
                            : Cursors.SizeWE;
            }
            UpdatePointerPositionText(point, dragViewport);
            InvalidateVisual();
            return;
        }
        if (_panOrigin is Point pan && e.MiddleButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport viewport))
        {
            double deltaX = point.X - pan.X;
            double deltaY = point.Y - pan.Y;
            long tickDelta = TimelineTickMath.RoundSignedDistance(deltaX / viewport.PixelsPerTick);
            StartTick = Math.Min(long.MaxValue - 1, TimelineTickMath.Clamp((Int128)_panStartTick - tickDelta));
            if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
            {
                double contentHeight = Math.Max(1, ActualHeight - GetRulerHeight());
                ValueScrollOffset = _panValueScrollOffset
                    - deltaY / contentHeight * ValueScrollViewportSize;
            }
            else
            {
                FirstLane = _panFirstLane - (int)Math.Round(deltaY / LaneHeight);
            }
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            RefreshPointerPositionText();
            return;
        }
        if (_marqueeOrigin is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            _marqueeCurrent = point;
            InvalidateVisual();
        }
        if (_rulerDragOrigin is not null && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport rulerViewport))
        {
            _rulerDragCurrentTick = rulerViewport.XToTick(point.X - GetLaneHeaderWidth());
            InvalidateVisual();
            return;
        }
        if (TryCreateViewport(out TimelineViewport hoverViewport))
        {
            UpdateHoverCursor(point, hoverViewport);
        }
        if (hoverChangesVisual
            || previousHoverLaneHeader != _hoverLaneHeader
            || previousHoverSharedGroupId != _hoverSharedGroupId)
        {
            InvalidateVisual();
        }
    }

    private void AutoScrollEditGesture(Point point, bool allowHorizontal)
    {
        const double edge = 24;
        long horizontalStep = Math.Max(1, TickSpan / 48);
        bool changed = false;
        if (allowHorizontal && point.X < GetLaneHeaderWidth() + edge && StartTick > 0)
        {
            StartTick = Math.Max(0, StartTick - horizontalStep);
            changed = true;
        }
        else if (allowHorizontal && point.X > ActualWidth - edge)
        {
            long maximumStart = Math.Max(0, long.MaxValue - Math.Max(1, TickSpan));
            long next = StartTick >= maximumStart - Math.Min(horizontalStep, maximumStart)
                ? maximumStart
                : StartTick + horizontalStep;
            if (next != StartTick)
            {
                StartTick = next;
                changed = true;
            }
        }
        if (point.Y < GetRulerHeight() + edge && FirstLane > 0)
        {
            FirstLane--;
            changed = true;
        }
        else if (point.Y > ActualHeight - edge)
        {
            if (FirstLane < MaximumFirstLane)
            {
                FirstLane++;
                changed = true;
            }
        }
        if (!changed) return;
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        RefreshPointerPositionText();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        try { HandleTimelineMouseUp(e); }
        catch (OverflowException) { RejectOutOfRangeGesture(e); }
    }

    private void HandleTimelineMouseUp(MouseButtonEventArgs e)
    {
        if (_pendingConductorPress is { } pendingConductor && e.ChangedButton == MouseButton.Left)
        {
            pendingConductor.LastPosition = InteractionPosition(e);
            pendingConductor.Up = e;
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _selectionToolGripOrigin is not null)
        {
            _selectionToolGripOrigin = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right
            && _suppressRightButtonUpAfterDoubleClick)
        {
            _suppressRightButtonUpAfterDoubleClick = false;
            _suppressAutomaticContextMenuOpening = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right
            && _pendingRightGestureOrigin is not null)
        {
            bool wasDrag = _pendingRightGestureDragThresholdReached;
            Point releasePoint = e.GetPosition(this);
            _pendingRightGestureOrigin = null;
            _pendingRightGestureCanTrace = false;
            _pendingRightGestureDragThresholdReached = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            if (wasDrag)
            {
                CancelDelayedContextMenu();
            }
            else
            {
                RegisterRightDoubleClickCandidate(
                    releasePoint,
                    Environment.TickCount64);
                _delayedContextMenuAwaitingMouseUp = false;
                StartDelayedContextMenuTimer();
                TryOpenDelayedContextMenu();
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _pressedLaneHeader is int pressedLane)
        {
            bool reordered = _laneHeaderDragActivated
                && (_laneHeaderDragTarget != pressedLane
                    || _laneHeaderDragJoinsTargetGroup
                    || _laneHeaderDragDetachesFromSourceGroup);
            int targetLane = _laneHeaderDragTarget;
            bool joinsTargetGroup = _laneHeaderDragJoinsTargetGroup;
            bool movesWholeGroup = _laneHeaderDragMovesWholeGroup;
            bool insertsAfterTarget = _laneHeaderDragInsertsAfterTarget;
            bool detachesFromSourceGroup = _laneHeaderDragDetachesFromSourceGroup;
            bool targetsSharedGroup = _pressedLaneHeaderTargetsSharedGroup;
            _pressedLaneHeader = null;
            _pressedLaneHeaderTargetsSharedGroup = false;
            _pressedSharedGroupId = null;
            _laneHeaderDragActivated = false;
            _laneHeaderDragJoinsTargetGroup = false;
            _laneHeaderDragMovesWholeGroup = false;
            _laneHeaderDragInsertsAfterTarget = false;
            _laneHeaderDragDetachesFromSourceGroup = false;
            _laneHeaderDragExteriorBoundaryGroupId = null;
            _laneHeaderDragJoinUsesChip = false;
            _laneHeaderDragRequiresRebind = false;
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
            if (reordered)
            {
                LaneHeaderReorderCompleted?.Invoke(
                    this,
                    new(
                        pressedLane,
                        targetLane,
                        joinsTargetGroup,
                        movesWholeGroup,
                        insertsAfterTarget,
                        detachesFromSourceGroup));
            }
            else
            {
                LaneHeaderInvoked?.Invoke(this, new(pressedLane, targetsSharedGroup));
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_velocityOrigin is not null && e.ChangedButton == _velocityButton)
        {
            TimelineViewport velocityViewport = default;
            bool completesTrace = _velocityDirectItemId is null
                && TryCreateViewport(out velocityViewport);
            Point[] trace = [];
            TimelineRenderSnapshot? traceSnapshot = null;
            TimelineSelectionSnapshot? traceSelection = null;
            long traceGestureToken = 0;
            TimelineToolMode traceTool = ToolMode;
            if (completesTrace)
            {
                UpdateVelocityTrace(_velocityOrigin.Value, e.GetPosition(this));
                trace = _velocityTracePoints.ToArray();
                traceSnapshot = Snapshot;
                traceSelection = SelectionSnapshot;
                traceGestureToken = _gestureToken;
            }
            IReadOnlyDictionary<MidoraId, int> result = new Dictionary<MidoraId, int>(_velocityEdits);
            bool restrictTraceToSelection = _velocitySelectionRestricted;
            _velocityOrigin = null;
            _velocityDirectItemId = null;
            _velocitySelectionRestricted = false;
            _velocityEdits.Clear();
            _velocityTracePoints.Clear();
            ReleaseMouseCapture();
            if (completesTrace && traceSnapshot is not null && trace.Length != 0)
            {
                CompleteVelocityTraceAsync(
                    traceSnapshot,
                    velocityViewport,
                    trace,
                    restrictTraceToSelection,
                    traceSelection,
                    traceGestureToken,
                    traceTool);
            }
            else if (result.Count > 0)
            {
                VelocityEditCompleted?.Invoke(this, new(result));
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_eventPointOrigin is not null && e.ChangedButton == _eventPointButton)
        {
            TimelineEventPointTraceEventArgs? traceResult = null;
            if (_eventPointDirectItemId is null && TryCreateViewport(out TimelineViewport eventViewport))
            {
                UpdateEventPointTrace(_eventPointOrigin.Value, InteractionPosition(e));
                if (EventPointTraceCompleted is not null)
                    traceResult = new(CreateEventPointTrace(eventViewport), Math.Max(1, OperationStepTicks),
                        OperationUsesBars, TimeSignatureMap, RangeStartTick, EventCreationRangeEndTick);
                else BuildEventPointEditsFromTrace(eventViewport);
            }
            IReadOnlyDictionary<long, double> result = new Dictionary<long, double>(_eventPointEdits);
            MidoraId? directItemId = _eventPointDirectItemId;
            _eventPointOrigin = null;
            _eventPointHorizontalTrace = false;
            _eventPointTimeLocked = false;
            _eventPointDirectItemId = null;
            _eventPointEdits.Clear();
            _eventPointTracePoints.Clear();
            ReleaseMouseCapture();
            if (traceResult is not null) EventPointTraceCompleted?.Invoke(this, traceResult);
            else if (result.Count > 0) EventPointEditCompleted?.Invoke(this, new(directItemId, result));
            RefreshPointerPositionText();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _notePlacementStartTick is long placementStart)
        {
            long placementEnd = Math.Max(checked(placementStart + 1), _notePlacementCurrentTick);
            _notePlacementStartTick = null;
            _notePlacementTimeLocked = false;
            ReleaseMouseCapture();
            NotePlacementCompleted?.Invoke(this, new(
                placementStart,
                placementEnd,
                _notePlacementPitch,
                _notePlacementVelocity));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _segmentPlacementStartTick is long segmentPlacementStart)
        {
            long segmentPlacementEnd = Math.Max(
                checked(segmentPlacementStart + 1),
                _segmentPlacementCurrentTick);
            int segmentPlacementLane = _segmentPlacementLane;
            _segmentPlacementStartTick = null;
            ReleaseMouseCapture();
            SegmentPlacementCompleted?.Invoke(this, new(
                segmentPlacementStart,
                segmentPlacementEnd,
                segmentPlacementLane));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _rulerDragOrigin is Point rulerOrigin)
        {
            if (Math.Abs(e.GetPosition(this).X - rulerOrigin.X) < 3)
            {
                long clickedTick = SnapAbsolute(_rulerDragStartTick);
                _rulerDragOrigin = null;
                ReleaseMouseCapture();
                RulerClicked?.Invoke(this, new(clickedTick));
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            long rawStart = Math.Min(_rulerDragStartTick, _rulerDragCurrentTick);
            long rawEnd = Math.Max(_rulerDragStartTick, _rulerDragCurrentTick);
            long start = SnapAbsolute(rawStart);
            long length = SnapOperationDelta(Math.Max(1, checked(rawEnd - rawStart)), rawEnd);
            long end = checked(start + Math.Max(1, length));
            bool isDrag = Math.Abs(e.GetPosition(this).X - rulerOrigin.X) >= 3 && end > start;
            _rulerDragOrigin = null;
            ReleaseMouseCapture();
            if (isDrag)
            {
                TimeRangeSelected?.Invoke(this, new(start, end));
            }
            else
            {
                RulerClicked?.Invoke(this, new(SnapAbsolute(_rulerDragStartTick)));
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _activeLanePreview is TimelineLanePreviewEventArgs preview)
        {
            _activeLanePreview = null;
            ReleaseMouseCapture();
            LanePreviewReleased?.Invoke(this, preview);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _dragItem is TimelineRenderItem item)
        {
            EndDragPitchPreview();
            if (_dragActivated)
            {
                _hoverPoint = InteractionPosition(e);
                int laneDelta = checked(_dragCurrentLane - _dragOriginLane);
                ItemEditCompleted?.Invoke(
                    this,
                    new TimelineItemEditEventArgs(
                        item,
                        _dragKind,
                        checked(_dragCurrentTick - _dragOriginTick),
                        laneDelta,
                        IsValueEditableEventPointKind(item.Kind)
                            ? GetDragPreviewTransform(item).ValueDelta
                            : -(InteractionPosition(e).Y - _dragOrigin.Y) / Math.Max(1, LaneHeight),
                        _dragModifiers,
                        _dragCopyRequested));
            }
            else if (_deferredControlClickToggle || _deferredPlainDrawSegmentSelection)
            {
                ItemInvoked?.Invoke(
                    this,
                    new TimelineItemEventArgs(
                        item,
                        _dragOriginTick,
                        _dragOriginLane,
                        _dragModifiers,
                        isDoubleClick: false));
            }
            ClearItemDrag();
            ReleaseMouseCapture();
            RefreshPointerPositionText();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle && _panOrigin is not null)
        {
            _panOrigin = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _marqueeOrigin is Point origin)
        {
            Point current = _marqueeCurrent ?? origin;
            CompleteMarquee(origin, current);
            _marqueeOrigin = null;
            _marqueeCurrent = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        ClearCapturedInteraction();
        base.OnLostMouseCapture(e);
    }

    private void ClearCapturedInteraction()
    {
        if (_pendingConductorPress is not null) CancelConductorHit();
        if (_notePlacementStartTick is not null)
        {
            _notePlacementStartTick = null;
            _notePlacementTimeLocked = false;
            NotePlacementCancelled?.Invoke(this, EventArgs.Empty);
        }
        if (_segmentPlacementStartTick is not null)
        {
            _segmentPlacementStartTick = null;
            SegmentPlacementCancelled?.Invoke(this, EventArgs.Empty);
        }
        if (_activeLanePreview is TimelineLanePreviewEventArgs preview)
        {
            _activeLanePreview = null;
            LanePreviewReleased?.Invoke(this, preview);
        }
        ClearItemDrag();
        _panOrigin = null;
        _marqueeOrigin = null;
        _marqueeCurrent = null;
        _rulerDragOrigin = null;
        _velocityOrigin = null;
        _velocityDirectItemId = null;
        _velocitySelectionRestricted = false;
        _velocityEdits.Clear();
        _velocityTracePoints.Clear();
        _eventPointOrigin = null;
        _eventPointHorizontalTrace = false;
        _eventPointTimeLocked = false;
        _eventPointDirectItemId = null;
        _eventPointEdits.Clear();
        _eventPointTracePoints.Clear();
        bool lostPendingRightGesture = _pendingRightGestureOrigin is not null;
        CancelPendingRightGesture(cancelDelayedMenu: lostPendingRightGesture);
        _selectionToolGripOrigin = null;
        _pressedLaneHeader = null;
        _pressedLaneHeaderTargetsSharedGroup = false;
        _pressedSharedGroupId = null;
        _laneHeaderDragActivated = false;
        _laneHeaderDragJoinsTargetGroup = false;
        _laneHeaderDragMovesWholeGroup = false;
        _laneHeaderDragInsertsAfterTarget = false;
        _laneHeaderDragDetachesFromSourceGroup = false;
        _laneHeaderDragExteriorBoundaryGroupId = null;
        _laneHeaderDragJoinUsesChip = false;
        _laneHeaderDragRequiresRebind = false;
        if (IsMouseOver)
        {
            RefreshPointerPositionText();
        }
        else
        {
            ResetPointerPositionText();
        }
        InvalidateVisual();
    }

    public static readonly RoutedEvent TickRangeExceededEvent = EventManager.RegisterRoutedEvent(
        nameof(TickRangeExceeded), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TimelineSurface));

    public event RoutedEventHandler TickRangeExceeded
    {
        add => AddHandler(TickRangeExceededEvent, value);
        remove => RemoveHandler(TickRangeExceededEvent, value);
    }

    private void RejectOutOfRangeGesture(MouseEventArgs e)
    {
        // This handles arithmetic failure in the input/draft path only. No
        // completed Project command is fabricated and no shorter edit is sent.
        ClearCapturedInteraction();
        ReleaseMouseCapture();
        e.Handled = true;
        Cursor = Cursors.Arrow;
        RaiseEvent(new RoutedEventArgs(TickRangeExceededEvent, this));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Point pointer = e.GetPosition(this);
            if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
                && pointer.X < GetLaneHeaderWidth())
            {
                ZoomValueAxis(pointer.Y, e.Delta, GetRulerHeight());
                ViewportChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (SurfaceMode == TimelineSurfaceMode.Arrangement
                && pointer.X < GetLaneHeaderWidth())
            {
                double arrangementFactor = e.Delta > 0 ? 1.15 : 1 / 1.15;
                LaneHeight = Math.Clamp(
                    LaneHeight * arrangementFactor,
                    MinimumArrangementLaneHeight,
                    MaximumArrangementLaneHeight);
                UpdateVerticalViewportMetrics();
                ViewportChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (SurfaceMode == TimelineSurfaceMode.PianoRoll
                && pointer.X < GetLaneHeaderWidth())
            {
                if (!IsInsideLaneContent(viewport, pointer.Y, GetRulerHeight()))
                {
                    e.Handled = true;
                    return;
                }
                int anchorLane = YToLane(viewport, pointer.Y - GetRulerHeight());
                double oldHeight = LaneHeight;
                double dpiScaleY = Math.Max(0.01, VisualTreeHelper.GetDpi(this).DpiScaleY);
                int oldDevicePixels = Math.Clamp(
                    (int)Math.Round(oldHeight * dpiScaleY, MidpointRounding.AwayFromZero),
                    (int)MinimumPianoLaneHeight,
                    (int)MaximumPianoLaneHeight);
                int devicePixelDelta = Math.Max(1, Math.Abs(e.Delta) / Mouse.MouseWheelDeltaForOneLine);
                int newDevicePixels = Math.Clamp(
                    oldDevicePixels + (e.Delta > 0 ? devicePixelDelta : -devicePixelDelta),
                    (int)MinimumPianoLaneHeight,
                    (int)MaximumPianoLaneHeight);
                double newHeight = newDevicePixels / dpiScaleY;
                double relative = (pointer.Y - GetRulerHeight()) / Math.Max(1, oldHeight);
                LaneHeight = newHeight;
                FirstLane = Math.Max(0, anchorLane - (int)Math.Floor(relative));
                ViewportChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            long anchor = viewport.XToTick(e.GetPosition(this).X - GetLaneHeaderWidth());
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            long newSpan = TimelineTickMath.ScaleSpan(TickSpan, factor);
            double anchorRatio = (anchor - viewport.StartTick) / (double)viewport.TickLength;
            long offset = TimelineTickMath.RoundSignedDistance(newSpan * anchorRatio);
            StartTick = TimelineTickMath.Pan(anchor, -offset);
            TickSpan = newSpan;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            long delta = Math.Max(1, TickSpan / 10);
            StartTick = TimelineTickMath.Pan(StartTick, e.Delta > 0 ? -delta : delta);
        }
        else
        {
            if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
            {
                double delta = Math.Max(1d / 256, ValueScrollViewportSize / 8);
                ValueScrollOffset += e.Delta > 0 ? -delta : delta;
            }
            else
            {
                int lanes = Math.Max(1, (int)Math.Ceiling(ActualHeight / LaneHeight) / 4);
                FirstLane += e.Delta > 0 ? -lanes : lanes;
            }
        }
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        RefreshPointerPositionText();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        bool retainsEventPointPosition = IsMouseCaptured
            && HasActiveEventPointGesture();
        bool hoverChangedVisual = _hoverPoint is not null
            && ToolMode == TimelineToolMode.Draw
            && SurfaceMode is TimelineSurfaceMode.Arrangement
                or TimelineSurfaceMode.PianoRoll
                or TimelineSurfaceMode.EventLanes;
        hoverChangedVisual |= _hoverLaneHeader is not null;
        hoverChangedVisual |= _hoverSharedGroupId is not null;
        if (!retainsEventPointPosition)
        {
            _hoverPoint = null;
        }
        _hoverLaneHeader = null;
        _hoverSharedGroupId = null;
        if (!IsMouseCaptured
            || SurfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll)
        {
            ResetPointerPositionText();
        }
        if (_dragItem is null)
        {
            bool directEditing = TimelineToolPolicy.IsDirectEditingSurface(SurfaceMode);
            Cursor = directEditing
                ? ToolMode == TimelineToolMode.Select ? Cursors.Cross : Cursors.Arrow
                : ToolMode == TimelineToolMode.Draw ? Cursors.Cross : Cursors.Arrow;
        }
        if (hoverChangedVisual)
        {
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _pendingConductorPress is not null)
        {
            CancelConductorHit();
            _exactQueryPending = false;
            Cursor = Cursors.Arrow;
            e.Handled = true;
            return;
        }
        if (IsAltKey(e))
        {
            RefreshHoverIntent();
        }
        if (e.Key == Key.Escape && _pressedLaneHeader is not null)
        {
            _pressedLaneHeader = null;
            _pressedLaneHeaderTargetsSharedGroup = false;
            _pressedSharedGroupId = null;
            _laneHeaderDragActivated = false;
            _laneHeaderDragJoinsTargetGroup = false;
            _laneHeaderDragMovesWholeGroup = false;
            _laneHeaderDragInsertsAfterTarget = false;
            _laneHeaderDragDetachesFromSourceGroup = false;
            _laneHeaderDragExteriorBoundaryGroupId = null;
            _laneHeaderDragJoinUsesChip = false;
            _laneHeaderDragRequiresRebind = false;
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (_panOrigin is not null || _marqueeOrigin is not null || _rulerDragOrigin is not null))
        {
            _panOrigin = null;
            _marqueeOrigin = null;
            _marqueeCurrent = null;
            _rulerDragOrigin = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _dragItem is not null)
        {
            ClearItemDrag();
            ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _notePlacementStartTick is not null)
        {
            _notePlacementStartTick = null;
            _notePlacementTimeLocked = false;
            ReleaseMouseCapture();
            NotePlacementCancelled?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _segmentPlacementStartTick is not null)
        {
            _segmentPlacementStartTick = null;
            ReleaseMouseCapture();
            SegmentPlacementCancelled?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape
            && (_pendingRightGestureOrigin is not null
                || _delayedContextMenuTimer is not null
                || _delayedContextMenuQueryCancellation is not null))
        {
            CancelPendingRightGesture(cancelDelayedMenu: true);
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            RefreshHoverIntent();
        }
        base.OnKeyUp(e);
    }

    private bool TryCreateViewport(out TimelineViewport viewport)
    {
        double contentWidth = Math.Max(0, ActualWidth - GetLaneHeaderWidth());
        double contentHeight = Math.Max(0, ActualHeight - GetRulerHeight());
        int firstLane = SurfaceMode == TimelineSurfaceMode.PianoRoll
            ? Math.Clamp(FirstLane, 0, 127)
            : Math.Max(0, FirstLane);
        int laneCount = ComputeVisibleLaneCount();
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            laneCount = Math.Min(laneCount, 128 - firstLane);
        }
        long span = TickSpan > 0 ? TickSpan : 1;
        long start = Math.Clamp(StartTick, 0, long.MaxValue - 1);
        long end = start <= long.MaxValue - span ? start + span : long.MaxValue;
        viewport = new(
            start,
            end,
            firstLane,
            laneCount,
            contentWidth,
            contentHeight,
            LaneHeight);
        if (contentWidth <= 0 || contentHeight <= 0 || LaneHeight <= 0 || end <= start)
        {
            return false;
        }
        viewport.Validate();
        return true;
    }

    private double GetLaneContentHeight(TimelineViewport viewport)
    {
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
            return Math.Min(viewport.Height, viewport.LaneCount * viewport.LaneHeight);
        if (SurfaceMode != TimelineSurfaceMode.Arrangement) return viewport.Height;
        EnsureArrangementRowLayout();
        int maximum = Math.Max(0, _arrangementRowOffsets.Length - 1);
        int first = Math.Clamp(viewport.FirstLane, 0, maximum);
        int last = Math.Clamp(viewport.LastLaneExclusive, first, maximum);
        return Math.Min(viewport.Height, _arrangementRowOffsets[last] - _arrangementRowOffsets[first]);
    }

    private bool IsInsideLaneContent(
        TimelineViewport viewport,
        double pointY,
        double rulerHeight)
    {
        double relativeY = pointY - rulerHeight;
        return relativeY >= 0 && relativeY < GetLaneContentHeight(viewport);
    }

    private void CompleteMarquee(Point origin, Point current)
    {
        if (Snapshot is null || !TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }
        if (!TryGetMarqueeBounds(
                origin,
                current,
                viewport,
                out _,
                out long start,
                out long end,
                out int firstLane,
                out int lastLaneExclusive,
                out double minimumNormalizedValue,
                out double maximumNormalizedValue))
        {
            RaiseMarqueeAnchorBackgroundInvoked();
            return;
        }
        TimelineRenderSnapshot snapshot = Snapshot;
        ModifierKeys modifiers = ConductorGestureModifiers;
        WorkspaceSelectionRangeMode mode =
            TimelineToolPolicy.ResolveMarqueeSelectionMode(modifiers);
        TimelineSelectionSnapshot baseSelection;
        if (mode == WorkspaceSelectionRangeMode.Replace)
        {
            TimelineSelectionReplacementEventArgs replacement = new();
            SelectionReplacementStarted?.Invoke(this, replacement);
            baseSelection = replacement.BaseSelection
                ?? SelectionSnapshot
                ?? new TimelineSelectionSnapshot(0, [], null);
        }
        else
        {
            baseSelection = SelectionSnapshot
                ?? new TimelineSelectionSnapshot(0, [], null);
        }
        long gestureToken = _gestureToken;
        TimelineToolMode tool = ToolMode;
        bool filterByValue = SurfaceMode is TimelineSurfaceMode.EventLanes
            or TimelineSurfaceMode.Velocity;
        Dispatcher dispatcher = Dispatcher;
        CancellationToken cancellationToken = _pendingGestureCancellation.Token;
        _marqueeQueryPending = true;
        _ = Task.Run(
            () => snapshot.MaterializeRangeSelection(
                    start,
                    end,
                    firstLane,
                    lastLaneExclusive,
                    minimumNormalizedValue,
                    maximumNormalizedValue,
                    filterByValue,
                    baseSelection,
                    mode,
                    cancellationToken),
            cancellationToken).ContinueWith(
                task =>
                {
                    if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                    _ = task.Exception;
                    CancelablePresentationDispatch.Post(dispatcher, cancellationToken,
                        () =>
                        {
                            bool currentRequest = gestureToken == _gestureToken;
                            if (currentRequest) _marqueeQueryPending = false;
                            if (task.IsFaulted)
                            {
                                System.Diagnostics.Trace.TraceError(
                                    $"Timeline marquee materialization failed: {task.Exception}");
                            }
                            if (task.IsCanceled
                                || task.IsFaulted
                                || cancellationToken.IsCancellationRequested
                                || !currentRequest
                                || !ReferenceEquals(Snapshot, snapshot)
                                || snapshot.SemanticRevision != Snapshot?.SemanticRevision
                                || !string.Equals(
                                    snapshot.ProjectionKey,
                                    Snapshot?.ProjectionKey,
                                    StringComparison.Ordinal)
                                || ToolMode != tool)
                            {
                                RefreshHoverIntent();
                                return;
                            }
                            if (!task.Result.IsUnchanged
                                || !baseSelection.MetricsAreComplete
                                    && task.Result.MetricsAreComplete)
                            {
                                MarqueeCompleted?.Invoke(
                                    this,
                                    new TimelineMarqueeEventArgs(task.Result, modifiers));
                            }
                            RefreshHoverIntent();
                        },
                        DispatcherPriority.Render);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        Cursor = Cursors.Wait;
    }

    private void DrawGrid(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double contentBottom = rulerHeight + GetLaneContentHeight(viewport);
        if (DisplayGridUsesBars
            && TimeSignatureMap is ProjectTimeSignatureMap timeSignatureMap)
        {
            double dpiScaleX = Math.Max(0.01, VisualTreeHelper.GetDpi(this).DpiScaleX);
            double ticksPerPixel = 1 / (viewport.PixelsPerTick * dpiScaleX);
            long minimumTickSpacing = TimelineTickMath.CeilingDistance(ticksPerPixel * 4);
            TimelineGridPresentation.BuildBarGridLines(
                viewport.StartTick,
                viewport.EndTick,
                timeSignatureMap,
                _gridLines,
                minimumTickSpacing,
                ProjectTickOffset);
            double previousX = double.NaN;
            foreach (TimelineGridLine line in _gridLines)
            {
                double x = laneHeaderWidth + Math.Round(viewport.TickToX(line.Tick)) + 0.5;
                if (line.Kind == TimelineGridLineKind.Beat && x == previousX)
                {
                    continue;
                }
                context.DrawLine(
                    line.Kind == TimelineGridLineKind.Bar ? _borderPen : _beatGridPen,
                    new Point(x, rulerHeight),
                    new Point(x, contentBottom));
                previousX = x;
            }
            return;
        }

        long grid = Math.Max(1, GridStepTicks);
        TimelineGridPresentation.BuildFixedGridLines(viewport.StartTick, viewport.EndTick,
            grid, _gridLines, TimelineTickMath.CeilingDistance(4 / viewport.PixelsPerTick));
        foreach (TimelineGridLine line in _gridLines)
        {
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(line.Tick)) + 0.5;
            context.DrawLine(_borderPen, new Point(x, rulerHeight), new Point(x, contentBottom));
        }
    }

    private void DrawActiveRange(
        DrawingContext context,
        TimelineViewport viewport,
        Brush brush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            return;
        }
        if (RangeStartTick is not long start || RangeEndTick is not long end || end <= start)
        {
            return;
        }
        double left = laneHeaderWidth + viewport.TickToX(Math.Max(viewport.StartTick, start));
        double right = laneHeaderWidth + viewport.TickToX(Math.Min(viewport.EndTick, end));
        if (right > left)
        {
            context.PushOpacity(0.16);
            context.DrawRectangle(brush, null, new Rect(left, rulerHeight, right - left, Math.Max(0, ActualHeight - rulerHeight)));
            context.Pop();
        }
    }

    private void DrawPianoOutsideActiveRange(
        DrawingContext context,
        TimelineViewport viewport,
        Brush outsideBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (SurfaceMode != TimelineSurfaceMode.PianoRoll
            || RangeStartTick is not long start
            || RangeEndTick is not long end
            || end <= start)
        {
            return;
        }

        double contentLeft = laneHeaderWidth;
        double contentRight = ActualWidth;
        double rangeLeft = Math.Clamp(
            laneHeaderWidth + viewport.TickToX(start),
            contentLeft,
            contentRight);
        double rangeRight = Math.Clamp(
            laneHeaderWidth + viewport.TickToX(end),
            contentLeft,
            contentRight);
        double height = GetLaneContentHeight(viewport);
        context.PushOpacity(0.72);
        if (rangeLeft > contentLeft)
        {
            context.DrawRectangle(
                outsideBrush,
                null,
                new Rect(contentLeft, rulerHeight, rangeLeft - contentLeft, height));
        }
        if (rangeRight < contentRight)
        {
            context.DrawRectangle(
                outsideBrush,
                null,
                new Rect(rangeRight, rulerHeight, contentRight - rangeRight, height));
        }
        context.Pop();
    }

    private void DrawTimeRangeSelection(
        DrawingContext drawingContext,
        TimelineViewport viewport,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        long? startValue = TimeRangeStartTick;
        long? endValue = TimeRangeEndTick;
        if (_rulerDragOrigin is not null)
        {
            startValue = Math.Min(_rulerDragStartTick, _rulerDragCurrentTick);
            endValue = Math.Max(_rulerDragStartTick, _rulerDragCurrentTick);
        }
        if (startValue is not long start || endValue is not long end || end <= start)
        {
            return;
        }
        long visibleStart = Math.Max(viewport.StartTick, start);
        long visibleEnd = Math.Min(viewport.EndTick, end);
        if (visibleEnd <= visibleStart) return;
        double left = laneHeaderWidth + viewport.TickToX(visibleStart);
        double right = laneHeaderWidth + viewport.TickToX(visibleEnd);
        Rect selection = new(
            left,
            rulerHeight,
            Math.Max(1, right - left),
            GetLaneContentHeight(viewport));
        drawingContext.PushOpacity(0.14);
        drawingContext.DrawRectangle(info, null, selection);
        drawingContext.Pop();
        drawingContext.DrawRectangle(null, _infoPen, selection);
        drawingContext.DrawRectangle(info, null, new Rect(left, 0, Math.Max(1, right - left), 3));
    }

    private void DrawPianoNoteTiles(
        DrawingContext context,
        TimelineViewport viewport,
        Brush subVoiceNoteBrush,
        Brush warningBrush,
        Brush segmentNoteBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }
        _ = context;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        // Piano note pixels are generated at the exact current projection.
        // Reprojecting a quantized LOD changes rounded note boundaries and can
        // drop a one-device-pixel outline while zooming.
        double actualPixelsPerTickDevice = viewport.PixelsPerTick * dpi.DpiScaleX;
        double actualPixelsPerLaneDevice = LaneHeight * dpi.DpiScaleY;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerTickDevice);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerLaneDevice);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        _preparedPianoProjection = new(
            snapshot.ProjectionKey,
            horizontalScaleKey,
            verticalScaleKey,
            dpiX,
            dpiY);
        _preparedPianoNoteFrameComplete = true;
        _pianoNoteTileDrawEntries.Clear();
        if (!TryGetPianoContentBounds(
                snapshot,
                actualPixelsPerTickDevice,
                out long contentStartTick,
                out long contentEndTick,
                out long firstContentTileX,
                out long lastContentTileX)
            || viewport.EndTick <= contentStartTick
            || viewport.StartTick >= contentEndTick)
        {
            return;
        }
        BeginExactRasterProjection(horizontalScaleKey, verticalScaleKey);
        long firstVisibleTileX = FloorToLong(
            viewport.StartTick * actualPixelsPerTickDevice / TimelinePianoTileRasterizer.TileSize);
        long lastVisibleTileX = FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * actualPixelsPerTickDevice
            / TimelinePianoTileRasterizer.TileSize);
        long firstVisibleTileY = FloorToLong(
            viewport.FirstLane * actualPixelsPerLaneDevice / TimelinePianoTileRasterizer.TileSize);
        long lastVisibleTileY = FloorToLong(
            Math.Max(viewport.FirstLane, viewport.LastLaneExclusive - 1) * actualPixelsPerLaneDevice
            / TimelinePianoTileRasterizer.TileSize);
        Color normalOutlineColor = GetSolidColor(
            RangeStartTick is not null && RangeEndTick is not null
                ? segmentNoteBrush
                : subVoiceNoteBrush,
            Color.FromRgb(163, 178, 190));
        Color normalColor = Color.FromArgb(
            normalOutlineColor.A,
            (byte)(normalOutlineColor.R * 0.42),
            (byte)(normalOutlineColor.G * 0.42),
            (byte)(normalOutlineColor.B * 0.42));
        Color warningColor = GetSolidColor(warningBrush, Color.FromRgb(232, 179, 75));
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(firstContentTileX, firstVisibleTileX - ring);
            long lastX = Math.Min(lastContentTileX, lastVisibleTileX + ring);
            if (lastX < firstX) continue;
            long firstY = Math.Max(0, firstVisibleTileY - ring);
            long lastY = Math.Max(firstY, lastVisibleTileY + ring);
            foreach (long tileY in TimelineTickMath.InclusiveIndices(firstY, lastY))
            {
                foreach (long tileX in TimelineTickMath.InclusiveIndices(firstX, lastX))
                {
                    bool visible = tileX >= firstVisibleTileX && tileX <= lastVisibleTileX
                        && tileY >= firstVisibleTileY && tileY <= lastVisibleTileY;
                    if (ring == 1 && visible)
                    {
                        continue;
                    }
                    TimelinePianoTileRasterRequest request = new(
                        snapshot,
                        actualPixelsPerTickDevice,
                        actualPixelsPerLaneDevice,
                        tileX,
                        tileY,
                        normalColor,
                        warningColor,
                        normalOutlineColor);
                    TileFingerprintRequestKey fingerprintKey = new(
                        TimelineRasterLayer.PianoNotes,
                        snapshot.SemanticRevision,
                        snapshot.ProjectionKey,
                        -1,
                        horizontalScaleKey,
                        verticalScaleKey,
                        request.TileX,
                        request.TileY,
                        dpiX,
                        dpiY);
                    if (!TryGetPreparedTileFingerprint(
                            fingerprintKey,
                            snapshot,
                            () => snapshot.GetPianoTileContentFingerprint(
                                actualPixelsPerTickDevice,
                                actualPixelsPerLaneDevice,
                                request.TileX,
                                request.TileY),
                            visible && snapshot.CanComputeTileFingerprintSynchronously,
                            out ulong noteFingerprint))
                    {
                        if (visible) _preparedPianoNoteFrameComplete = false;
                        continue;
                    }
                    TimelineRasterCacheKey key = new(
                        TimelineRasterLayer.PianoNotes,
                        snapshot.ProjectionKey,
                        noteFingerprint,
                        horizontalScaleKey,
                        verticalScaleKey,
                        request.TileX,
                        request.TileY,
                        ColorToArgb(normalColor),
                        ColorToArgb(warningColor),
                        ColorToArgb(normalOutlineColor),
                        dpiX,
                        dpiY);
                    if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                    {
                        if (visible && bitmap is not null)
                        {
                            _pianoNoteTileDrawEntries.Add(new(key, bitmap));
                        }
                        else if (visible)
                        {
                            _preparedPianoNoteFrameComplete = false;
                        }
                        continue;
                    }
                    if (visible) _preparedPianoNoteFrameComplete = false;
                    RequestRaster(
                        key,
                        cancellationToken => request.Rasterize(cancellationToken),
                        visible
                            ? TimelineRasterRequestPriority.Visible
                            : TimelineRasterRequestPriority.Normal);
                }
            }
        }
    }

    private void DrawPianoSelectionOverlay(
        DrawingContext context,
        TimelineViewport viewport,
        Brush selectedBrush,
        Brush warningBrush,
        Brush outlineBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double actualPixelsPerTickDevice = viewport.PixelsPerTick * dpi.DpiScaleX;
        double actualPixelsPerLaneDevice = LaneHeight * dpi.DpiScaleY;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerTickDevice);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerLaneDevice);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        PianoRasterProjectionSignature projection = new(
            snapshot.ProjectionKey,
            horizontalScaleKey,
            verticalScaleKey,
            dpiX,
            dpiY);
        if (_preparedPianoProjection != projection)
        {
            _preparedPianoProjection = projection;
            _preparedPianoNoteFrameComplete = false;
            _pianoNoteTileDrawEntries.Clear();
        }
        _pianoSelectionTileDrawEntries.Clear();
        bool selectionFrameComplete = true;
        TimelineSelectionSnapshot? selection = SelectionSnapshot is { Count: > 0 } selected
            ? selected
            : null;
        if (selection is null)
        {
            _ = PresentPianoRasterFrame(
                context,
                viewport,
                projection,
                actualPixelsPerTickDevice,
                actualPixelsPerLaneDevice,
                laneHeaderWidth,
                rulerHeight,
                selectionFrameComplete);
            return;
        }
        if (!TryGetPianoContentBounds(
                snapshot,
                actualPixelsPerTickDevice,
                out long contentStartTick,
                out long contentEndTick,
                out long firstContentTileX,
                out long lastContentTileX)
            || viewport.EndTick <= contentStartTick
            || viewport.StartTick >= contentEndTick)
        {
            _ = PresentPianoRasterFrame(
                context,
                viewport,
                projection,
                actualPixelsPerTickDevice,
                actualPixelsPerLaneDevice,
                laneHeaderWidth,
                rulerHeight,
                selectionFrameComplete);
            return;
        }
        BeginExactRasterProjection(horizontalScaleKey, verticalScaleKey);
        long firstTileX = Math.Max(0, FloorToLong(
            viewport.StartTick * actualPixelsPerTickDevice / TimelinePianoTileRasterizer.TileSize));
        long lastTileX = Math.Max(firstTileX, FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * actualPixelsPerTickDevice
            / TimelinePianoTileRasterizer.TileSize));
        long firstTileY = Math.Max(0, FloorToLong(
            viewport.FirstLane * actualPixelsPerLaneDevice / TimelinePianoTileRasterizer.TileSize));
        long lastTileY = Math.Max(firstTileY, FloorToLong(
            Math.Max(viewport.FirstLane, viewport.LastLaneExclusive - 1) * actualPixelsPerLaneDevice
            / TimelinePianoTileRasterizer.TileSize));
        Color selectedColor = GetSolidColor(selectedBrush, Color.FromRgb(143, 36, 41));
        Color warningColor = GetSolidColor(warningBrush, Color.FromRgb(232, 179, 75));
        Color outlineColor = GetSolidColor(outlineBrush, Color.FromRgb(229, 61, 68));
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(firstContentTileX, firstTileX - ring);
            long lastX = Math.Min(lastContentTileX, lastTileX + ring);
            if (lastX < firstX) continue;
            long firstY = Math.Max(0, firstTileY - ring);
            long lastY = Math.Max(firstY, lastTileY + ring);
            foreach (long tileY in TimelineTickMath.InclusiveIndices(firstY, lastY))
            {
                foreach (long tileX in TimelineTickMath.InclusiveIndices(firstX, lastX))
                {
                    bool visible = tileX >= firstTileX && tileX <= lastTileX
                        && tileY >= firstTileY && tileY <= lastTileY;
                    if (ring == 1 && visible)
                    {
                        continue;
                    }
                    // Large selections prepare their tile fingerprints on a
                    // background worker. Capture immutable coordinates for
                    // both factories: closing over the for-loop variables
                    // makes every deferred factory observe the coordinates
                    // after the loops have advanced, which can incorrectly
                    // classify every already-visible selection tile as empty.
                    long requestTileX = tileX;
                    long requestTileY = tileY;
                    TileFingerprintRequestKey fingerprintKey = new(
                        TimelineRasterLayer.PianoSelection,
                        snapshot.SemanticRevision,
                        snapshot.ProjectionKey,
                        selection.Revision,
                        horizontalScaleKey,
                        verticalScaleKey,
                        tileX,
                        tileY,
                        dpiX,
                        dpiY);
                    if (!TryGetPreparedTileFingerprint(
                            fingerprintKey,
                            snapshot,
                            () => TimelinePianoTileRasterizer.ComputeSelectionFingerprint(
                                snapshot,
                                selection,
                                actualPixelsPerTickDevice,
                                actualPixelsPerLaneDevice,
                                requestTileX,
                                requestTileY),
                            visible && selection.Count <= 4096,
                            out ulong contentFingerprint))
                    {
                        if (visible) selectionFrameComplete = false;
                        continue;
                    }
                    if (contentFingerprint == 0)
                    {
                        // The immutable selection index proves that this tile
                        // contains no selected geometry. It is already a
                        // complete transparent tile and needs neither a cache
                        // entry nor a background raster request.
                        continue;
                    }
                    TimelineRasterCacheKey key = new(
                        TimelineRasterLayer.PianoSelection,
                        snapshot.ProjectionKey,
                        contentFingerprint,
                        horizontalScaleKey,
                        verticalScaleKey,
                        tileX,
                        tileY,
                        ColorToArgb(selectedColor),
                        ColorToArgb(warningColor),
                        ColorToArgb(outlineColor),
                        dpiX,
                        dpiY);
                    if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                    {
                        if (visible && bitmap is not null)
                        {
                            _pianoSelectionTileDrawEntries.Add(new(key, bitmap));
                        }
                        else if (visible)
                        {
                            selectionFrameComplete = false;
                        }
                        continue;
                    }
                    if (visible) selectionFrameComplete = false;
                    RequestRaster(
                        key,
                        cancellationToken => TimelinePianoTileRasterizer.Rasterize(
                            snapshot,
                            actualPixelsPerTickDevice,
                            actualPixelsPerLaneDevice,
                            requestTileX,
                            requestTileY,
                            selectedColor,
                            warningColor,
                            selection,
                            selectionOnly: true,
                            outlineColor: outlineColor,
                            cancellationToken: cancellationToken),
                        visible
                            ? TimelineRasterRequestPriority.Visible
                            : TimelineRasterRequestPriority.Normal);
                }
            }
        }
        bool currentFramePresented = PresentPianoRasterFrame(
            context,
            viewport,
            projection,
            actualPixelsPerTickDevice,
            actualPixelsPerLaneDevice,
            laneHeaderWidth,
            rulerHeight,
            selectionFrameComplete);

        PrefetchPianoDragPreviewTiles(
            snapshot,
            selection,
            actualPixelsPerTickDevice,
            actualPixelsPerLaneDevice,
            firstTileX,
            Math.Min(lastTileX, lastContentTileX),
            firstTileY,
            lastTileY,
            dpiX,
            dpiY);

        if (currentFramePresented
            && selection.Primary is MidoraId primaryId
            && snapshot.TryGetItemCached(primaryId, out TimelineRenderItem primaryItem, out _)
            && primaryItem.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote
            && primaryItem.EndTick > viewport.StartTick
            && primaryItem.StartTick < viewport.EndTick
            && primaryItem.Lane >= viewport.FirstLane
            && primaryItem.Lane < viewport.LastLaneExclusive)
        {
            Rect bounds = TimelineRasterPlacement.GetUnclippedItemBounds(
                viewport, primaryItem, laneHeaderWidth, rulerHeight, LaneHeight);
            if (bounds.Width > 4 && bounds.Height > 4)
            {
                context.DrawRectangle(
                    null,
                    _selectionPen,
                    new Rect(bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, bounds.Height - 2));
            }
        }
    }

    private bool PresentPianoRasterFrame(
        DrawingContext context,
        TimelineViewport viewport,
        PianoRasterProjectionSignature projection,
        double currentPixelsPerTickDevice,
        double currentPixelsPerLaneDevice,
        double laneHeaderWidth,
        double rulerHeight,
        bool selectionFrameComplete)
    {
        bool currentFrameComplete = _preparedPianoNoteFrameComplete
            && selectionFrameComplete;
        PianoCompositeRasterFrame? frameToDraw;
        if (currentFrameComplete)
        {
            frameToDraw = new(
                projection,
                _pianoNoteTileDrawEntries.ToArray(),
                _pianoSelectionTileDrawEntries.ToArray(),
                SelectionSnapshot is { Count: > 0 } completedSelection
                    ? completedSelection.Revision
                    : -1);
            _committedPianoFrame = frameToDraw;
        }
        else if (_committedPianoFrame is PianoCompositeRasterFrame committed
            && committed.Projection == projection)
        {
            // Keep the previous complete semantic/selection pair visible
            // while the replacement pair is prepared.  The two layers are
            // committed together, so an edit can neither expose a blank tile
            // nor combine an old selection overlay with new note pixels.
            TimelineSelectionSnapshot? replacementSelection =
                SelectionSnapshot is { Count: > 0 } selected
                    ? selected
                    : null;
            frameToDraw = new(
                projection,
                committed.NoteTiles,
                replacementSelection is not null
                    && committed.SelectionRevision == replacementSelection.Revision
                        ? committed.SelectionTiles
                        : _pianoSelectionTileDrawEntries.ToArray(),
                replacementSelection?.Revision ?? -1);
        }
        else
        {
            // A genuinely new zoom/DPI projection has no geometrically valid
            // fallback.  Show only pixels already prepared for that exact
            // projection; never rescale another pixel family.
            frameToDraw = new(
                projection,
                _pianoNoteTileDrawEntries.ToArray(),
                _pianoSelectionTileDrawEntries.ToArray(),
                SelectionSnapshot is { Count: > 0 } partialSelection
                    ? partialSelection.Revision
                    : -1);
        }

        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            GetLaneContentHeight(viewport));
        context.PushClip(new RectangleGeometry(contentBounds));
        DrawPianoTileEntries(
            context,
            viewport,
            frameToDraw.NoteTiles,
            currentPixelsPerTickDevice,
            currentPixelsPerLaneDevice,
            laneHeaderWidth,
            rulerHeight);
        DrawPianoTileEntries(
            context,
            viewport,
            // Selection clearing is authoritative even while an old note frame
            // is retained to avoid flashing. Replaying the committed selection
            // pixels here would make a formally empty selection appear to come
            // back after another workspace refresh. Array.Empty also keeps this
            // render-time guard allocation-free.
            SelectionSnapshot is { Count: > 0 } currentSelection
                && frameToDraw.SelectionRevision == currentSelection.Revision
                ? frameToDraw.SelectionTiles
                : Array.Empty<PianoTileDrawEntry>(),
            currentPixelsPerTickDevice,
            currentPixelsPerLaneDevice,
            laneHeaderWidth,
            rulerHeight);
        context.Pop();
        _pianoNoteTileDrawEntries.Clear();
        _pianoSelectionTileDrawEntries.Clear();
        return currentFrameComplete;
    }

    private void DrawPianoTileEntries(
        DrawingContext context,
        TimelineViewport viewport,
        IReadOnlyList<PianoTileDrawEntry> entries,
        double pixelsPerTickDevice,
        double pixelsPerLaneDevice,
        double laneHeaderWidth,
        double rulerHeight)
    {
        foreach (PianoTileDrawEntry entry in entries)
        {
            DrawPianoTile(
                context,
                viewport,
                entry,
                pixelsPerTickDevice,
                pixelsPerLaneDevice,
                laneHeaderWidth,
                rulerHeight);
        }
    }

    private void DrawPianoTile(
        DrawingContext context,
        TimelineViewport viewport,
        PianoTileDrawEntry entry,
        double pixelsPerTickDevice,
        double pixelsPerLaneDevice,
        double laneHeaderWidth,
        double rulerHeight)
    {
        Rect destination = TimelineRasterPlacement.GetPianoTileDestination(
            viewport,
            pixelsPerTickDevice,
            pixelsPerLaneDevice,
            entry.Key.TileX,
            entry.Key.TileY,
            laneHeaderWidth,
            rulerHeight,
            LaneHeight);
        Rect coreDestination = TimelineRasterPlacement.GetPianoTileCoreDestination(
            viewport,
            pixelsPerTickDevice,
            pixelsPerLaneDevice,
            entry.Key.TileX,
            entry.Key.TileY,
            laneHeaderWidth,
            rulerHeight,
            LaneHeight);
        context.PushClip(new RectangleGeometry(coreDestination));
        context.DrawImage(entry.Bitmap, destination);
        context.Pop();
    }

    private void RequestRaster(
        TimelineRasterCacheKey key,
        Func<TimelineRasterBuffer> factory,
        TimelineRasterRequestPriority priority = TimelineRasterRequestPriority.Normal)
        => RequestRaster(key, _ => factory(), priority);

    private void RequestRaster(
        TimelineRasterCacheKey key,
        Func<CancellationToken, TimelineRasterBuffer> factory,
        TimelineRasterRequestPriority priority = TimelineRasterRequestPriority.Normal)
    {
        if (_backgroundWorkSuspended) return;
        if (!_requestedRasterKeys.Add(key))
        {
            return;
        }
        CancellationToken cancellationToken = IsSelectionDependentRasterLayer(key.Layer)
            ? _selectionRasterRequestCancellation.Token
            : _rasterRequestCancellation.Token;
        bool accepted = TimelineRasterCache.Shared.Request(
            key,
            factory,
            Dispatcher,
            () =>
            {
                _requestedRasterKeys.Remove(key);
                if (!_backgroundWorkSuspended
                    && Snapshot is TimelineRenderSnapshot snapshot
                    && string.Equals(snapshot.ProjectionKey, key.ProjectionKey, StringComparison.Ordinal))
                {
                    QueueRasterInvalidation();
                }
            },
            cancellationToken,
            priority,
            consumerId: _rasterConsumerId);
        if (!accepted)
        {
            _requestedRasterKeys.Remove(key);
        }
    }

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Floor(value);

    private static bool TryGetPianoContentBounds(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        out long contentStartTick,
        out long contentEndTick,
        out long firstTileX,
        out long lastTileX)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        contentStartTick = 0;
        contentEndTick = snapshot.MaximumEndTick;
        if (contentEndTick <= contentStartTick)
        {
            firstTileX = 0;
            lastTileX = -1;
            return false;
        }
        firstTileX = Math.Max(0, FloorToLong(
            contentStartTick * devicePixelsPerTick
            / TimelinePianoTileRasterizer.TileSize));
        lastTileX = Math.Max(firstTileX, FloorToLong(
            (contentEndTick - 1d)
            * devicePixelsPerTick
            / TimelinePianoTileRasterizer.TileSize));
        return true;
    }

    private static bool TryGetHorizontalContentTileBounds(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        int tileSize,
        out long firstTileX,
        out long lastTileX)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long contentStartTick = 0;
        long contentEndTick = snapshot.MaximumEndTick;
        if (contentEndTick <= contentStartTick)
        {
            firstTileX = 0;
            lastTileX = -1;
            return false;
        }
        firstTileX = Math.Max(0, FloorToLong(
            contentStartTick * devicePixelsPerTick / tileSize));
        lastTileX = Math.Max(firstTileX, FloorToLong(
            (contentEndTick - 1d) * devicePixelsPerTick / tileSize));
        return true;
    }

    private static Color GetSolidColor(Brush brush, Color fallback) =>
        brush is SolidColorBrush solid ? solid.Color : fallback;

    private static uint ColorToArgb(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private bool IsSelected(TimelineRenderItem item) =>
        SelectionSnapshot?.Contains(item.Id)
        ?? item.State.HasFlag(TimelineItemState.Selected);

    private bool IsPrimary(TimelineRenderItem item) =>
        SelectionSnapshot is TimelineSelectionSnapshot selection
            ? selection.Primary == item.Id
            : item.State.HasFlag(TimelineItemState.Primary);

    private void DrawItem(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush red,
        Brush redDark,
        Brush info,
        Brush warning,
        Brush segment,
        Brush selectedSegment,
        Brush segmentNotePreview,
        Brush segmentPianoNote,
        double laneHeaderWidth,
        double rulerHeight,
        bool allowDetailedSegmentPreviewRequests)
    {
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && item.Kind == TimelineItemKind.ProjectEndMarker)
        {
            DrawArrangementProjectEndMarker(
                context,
                viewport,
                item,
                warning,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.Conductor
            && item.Kind is TimelineItemKind.ConductorEvent
                or TimelineItemKind.Marker
                or TimelineItemKind.ProjectEndMarker)
        {
            DrawConductorPoint(
                context,
                viewport,
                item,
                item.Kind == TimelineItemKind.ProjectEndMarker ? warning : info,
                redDark,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        if (item.Kind == TimelineItemKind.Velocity)
        {
            DrawVelocityBar(
                context,
                viewport,
                item,
                segmentPianoNote,
                red,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        if (item.Kind == TimelineItemKind.LogicalParameterCurve)
        {
            DrawCurve(context, viewport, item, info, laneHeaderWidth, rulerHeight);
            return;
        }
        if (IsEventPointKind(item.Kind))
        {
            DrawCurvePoint(context, viewport, item, info, laneHeaderWidth, rulerHeight);
            return;
        }
        SegmentAccentResources? accent = item.Kind == TimelineItemKind.Segment
            && item.AccentColor != 0
                ? GetSegmentAccentResources(item.AccentColor)
                : null;
        double left = laneHeaderWidth + Math.Max(-1, viewport.TickToX(item.StartTick));
        double right = laneHeaderWidth + Math.Min(viewport.Width + 1, viewport.TickToX(item.EndTick));
        double itemLaneHeight = GetLaneVisualHeight(item.Lane);
        bool fillsArrangementLane = SurfaceMode == TimelineSurfaceMode.Arrangement
            && item.Kind == TimelineItemKind.Segment;
        double laneTop = GetLaneTop(viewport, item.Lane, rulerHeight);
        double top = laneTop + (fillsArrangementLane ? 0 : 3);
        double height = fillsArrangementLane
            ? itemLaneHeight
            : Math.Max(3, itemLaneHeight - 6);
        if (right <= left || top >= ActualHeight || top + height <= 0)
        {
            return;
        }

        bool selected = IsSelected(item);
        bool isSegmentPianoRoll = SurfaceMode == TimelineSurfaceMode.PianoRoll
            && RangeStartTick is not null
            && RangeEndTick is not null;
        Brush fill = item.State.HasFlag(TimelineItemState.Invalid)
            || item.State.HasFlag(TimelineItemState.Broken)
            ? warning
            : item.Kind == TimelineItemKind.Segment
                ? selected
                    ? accent?.SelectedSegment ?? selectedSegment
                    : accent?.Segment ?? segment
            : item.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote
                ? selected ? redDark : isSegmentPianoRoll ? segmentPianoNote : red
                : info;
        double opacity = item.State.HasFlag(TimelineItemState.OutsideActiveRange)
            ? 0.35
            : item.Kind == TimelineItemKind.Segment ? 0.88 : 0.78;
        context.PushOpacity(opacity);
        Rect rectangle = new(left, top, Math.Max(1, right - left), height);
        context.DrawRoundedRectangle(fill, _borderPen, rectangle, 2, 2);
        context.Pop();

        if (item.Kind == TimelineItemKind.Segment
            && Snapshot?.SegmentPreviews.TryGetValue(item.Id, out TimelineSegmentPreview? preview) == true)
        {
            Rect fullSegmentBounds = SurfaceMode == TimelineSurfaceMode.Arrangement
                ? new Rect(
                    laneHeaderWidth + viewport.TickToX(item.StartTick),
                    laneTop,
                    Math.Max(1, viewport.TickToX(item.EndTick) - viewport.TickToX(item.StartTick)),
                    itemLaneHeight)
                : TimelineRasterPlacement.GetUnclippedItemBounds(
                    viewport,
                    item,
                    laneHeaderWidth,
                    rulerHeight,
                    LaneHeight);
            DrawSegmentPreview(
                context,
                rectangle,
                fullSegmentBounds,
                preview,
                checked(item.EndTick - item.StartTick),
                viewport.PixelsPerTick,
                accent?.NotePreview ?? segmentNotePreview,
                Brush("Brush.Red", Color.FromRgb(229, 61, 68)),
                allowDetailedSegmentPreviewRequests);
        }

        if (selected)
        {
            Pen selectionPen = item.Kind == TimelineItemKind.Segment
                ? accent?.SelectionPen ?? _segmentSelectionPen!
                : _selectionPen!;
            Rect selectionBounds = item.Kind == TimelineItemKind.Segment
                ? InsetRectangle(rectangle, selectionPen.Thickness / 2)
                : rectangle;
            context.DrawRoundedRectangle(null, selectionPen, selectionBounds, 2, 2);
            if (IsPrimary(item)
                && selectionBounds.Width > 4
                && selectionBounds.Height > 4)
            {
                Rect primary = InsetRectangle(selectionBounds, 1);
                context.DrawRoundedRectangle(null, selectionPen, primary, 1, 1);
            }
        }
    }

    private static Rect InsetRectangle(Rect rectangle, double inset)
    {
        double effective = Math.Max(0, Math.Min(
            inset,
            Math.Min(rectangle.Width, rectangle.Height) / 2));
        return new Rect(
            rectangle.Left + effective,
            rectangle.Top + effective,
            Math.Max(0, rectangle.Width - effective * 2),
            Math.Max(0, rectangle.Height - effective * 2));
    }

    private SegmentAccentResources GetSegmentAccentResources(uint color)
    {
        if (_segmentAccentResources.TryGetValue(color, out SegmentAccentResources? existing))
        {
            return existing;
        }
        if (_segmentAccentResources.Count >= 256) _segmentAccentResources.Clear();
        TimelineAccentPalette palette = TimelineAccentPalette.FromArgb(color);
        SolidColorBrush segment = FreezeBrush(palette.Segment);
        SolidColorBrush selected = FreezeBrush(palette.SelectedSegment);
        SolidColorBrush preview = FreezeBrush(palette.NotePreview);
        SolidColorBrush selectionBrush = new(palette.SelectionBorder)
        {
            Opacity = 0.6
        };
        selectionBrush.Freeze();
        Pen selectionPen = new(selectionBrush, 1.25)
        {
            DashStyle = DashStyles.Solid
        };
        selectionPen.Freeze();
        SegmentAccentResources created = new(segment, selected, preview, selectionPen);
        _segmentAccentResources.Add(color, created);
        return created;
    }

    private SolidColorBrush GetRawAccentBrush(uint color)
    {
        if (_rawAccentBrushes.TryGetValue(color, out SolidColorBrush? existing)) return existing;
        if (_rawAccentBrushes.Count >= 256) _rawAccentBrushes.Clear();
        SolidColorBrush created = FreezeBrush(Color.FromArgb(
            (byte)(color >> 24),
            (byte)(color >> 16),
            (byte)(color >> 8),
            (byte)color));
        _rawAccentBrushes.Add(color, created);
        return created;
    }

    private static SolidColorBrush FreezeBrush(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }

    private void DrawArrangementConductorTiles(
        DrawingContext context,
        TimelineViewport viewport,
        Brush fallbackBrush,
        Brush borderBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot
            || viewport.FirstLane != 0
            || !snapshot.HasConductorPreviewItems)
        {
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        double rasterDpiScaleX = dpiX / 1024d;
        double rasterDpiScaleY = dpiY / 1024d;
        double devicePixelsPerTick = viewport.PixelsPerTick * rasterDpiScaleX;
        double deviceLaneHeight = LaneHeight * rasterDpiScaleY;
        long firstTile = Math.Max(0, FloorToLong(
            viewport.StartTick * devicePixelsPerTick / TimelineConductorTileRasterizer.TileSize));
        long lastTile = Math.Max(firstTile, FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * devicePixelsPerTick
            / TimelineConductorTileRasterizer.TileSize));
        Color fallback = GetSolidColor(fallbackBrush, Color.FromRgb(98, 166, 246));
        Color border = GetSolidColor(borderBrush, Color.FromRgb(42, 48, 58));
        int gutterX = TimelineConductorTileRasterizer.GetGutter(rasterDpiScaleX);
        int gutterY = checked((int)Math.Ceiling(
            (TimelineConductorTileRasterizer.PointRadius + TimelineConductorTileRasterizer.OutlineThickness)
            * rasterDpiScaleY) + 1);
        double coreTop = rulerHeight;
        Rect clip = new(
            laneHeaderWidth,
            coreTop,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Min(LaneHeight, Math.Max(0, ActualHeight - coreTop)));
        context.PushClip(new RectangleGeometry(clip));
        foreach (long tile in TimelineTickMath.InclusiveIndices(firstTile, lastTile))
        {
            TimelineRasterCacheKey key = new(
                TimelineRasterLayer.ArrangementConductorPreview,
                snapshot.ProjectionKey,
                snapshot.GetConductorTileContentFingerprint(
                    devicePixelsPerTick,
                    tile,
                    rasterDpiScaleX),
                BitConverter.DoubleToInt64Bits(devicePixelsPerTick),
                BitConverter.DoubleToInt64Bits(deviceLaneHeight),
                tile,
                0,
                ColorToArgb(fallback),
                ColorToArgb(border),
                0,
                dpiX,
                dpiY);
            if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap) && bitmap is not null)
            {
                double worldLeft = tile * (double)TimelineConductorTileRasterizer.TileSize - gutterX;
                double x = laneHeaderWidth
                    + (worldLeft - viewport.StartTick * devicePixelsPerTick) / rasterDpiScaleX;
                double y = coreTop - gutterY / rasterDpiScaleY;
                context.DrawImage(bitmap, new Rect(
                    x,
                    y,
                    bitmap.PixelWidth / rasterDpiScaleX,
                    bitmap.PixelHeight / rasterDpiScaleY));
                continue;
            }
            long requestTile = tile;
            RequestRaster(key, token => TimelineConductorTileRasterizer.Rasterize(
                snapshot,
                devicePixelsPerTick,
                deviceLaneHeight,
                requestTile,
                rasterDpiScaleX,
                rasterDpiScaleY,
                fallback,
                border,
                token));
        }
        context.Pop();
    }

    private void DrawArrangementProjectEndMarker(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush warning,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double x = laneHeaderWidth + viewport.TickToX(item.StartTick);
        if (x < laneHeaderWidth - 1 || x > ActualWidth + 1)
        {
            return;
        }
        double top = rulerHeight + 3;
        double height = Math.Max(3, Math.Min(LaneHeight - 6, ActualHeight - top));
        context.DrawRectangle(warning, null, new Rect(x - 0.5, top, 1, height));
    }

    private void DrawConductorPoint(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush normalBrush,
        Brush selectedBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double x = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double y = rulerHeight
            + (item.Lane - viewport.FirstLane + 0.5) * LaneHeight;
        const double radius = 4.5;
        if (x + radius < laneHeaderWidth
            || x - radius > ActualWidth
            || y + radius < rulerHeight
            || y - radius > ActualHeight)
        {
            return;
        }

        bool selected = IsSelected(item);
        context.DrawEllipse(
            selected ? selectedBrush : normalBrush,
            _borderPen,
            new Point(x, y),
            radius,
            radius);
        if (selected)
        {
            context.DrawEllipse(
                null,
                _selectionPen,
                new Point(x, y),
                radius + 2,
                radius + 2);
        }
    }

    private bool PrepareVisibleSegmentPreviewFallbacks(
        TimelineRenderSnapshot snapshot,
        IReadOnlyList<TimelineRenderItem> visibleItems,
        Brush defaultNoteBrush,
        Brush eventBrush)
    {
        Color defaultNoteColor = GetSolidColor(
            defaultNoteBrush,
            Color.FromRgb(189, 199, 207));
        Color eventColor = GetSolidColor(
            eventBrush,
            Color.FromRgb(229, 61, 68));
        bool allFallbacksReady = true;
        foreach (TimelineRenderItem item in visibleItems)
        {
            if (item.Kind != TimelineItemKind.Segment
                || !snapshot.SegmentPreviews.TryGetValue(
                    item.Id,
                    out TimelineSegmentPreview? preview)
                || !preview.HasNoteContent && !preview.HasEventContent)
            {
                continue;
            }
            Color noteColor = item.AccentColor == 0
                ? defaultNoteColor
                : TimelineAccentPalette.FromArgb(item.AccentColor).NotePreview;
            long segmentLengthTicks = checked(item.EndTick - item.StartTick);
            // The first layer is one fixed, segment-scoped coarse image. It is
            // identical at every viewport zoom and is also what the global
            // background warmup produces. Only the second/detail layer varies
            // by display LOD.
            int fallbackLod = TimelineSegmentPreviewRasterizer.SelectWarmupLod(
                segmentLengthTicks,
                PreviewTicksPerQuarterNote);
            if (TryGetCompleteSegmentPreviewFallback(
                preview,
                segmentLengthTicks,
                fallbackLod,
                noteColor,
                eventColor,
                out SegmentPreviewFallbackFrame frame))
            {
                _visibleSegmentPreviewFallbackFrames[item.Id] = frame;
            }
            else
            {
                allFallbacksReady = false;
            }
        }
        return allFallbacksReady;
    }

    private bool TryGetCompleteSegmentPreviewFallback(
        TimelineSegmentPreview preview,
        long segmentLengthTicks,
        int lod,
        Color noteColor,
        Color eventColor,
        out SegmentPreviewFallbackFrame frame)
    {
        long contentWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
            segmentLengthTicks,
            PreviewTicksPerQuarterNote,
            lod);
        int tileCount = checked((int)(1 + ((contentWidth - 1)
            / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize)));
        if (tileCount > TimelineSegmentPreviewRasterizer.MaximumFallbackTilesPerSegment)
        {
            throw new InvalidOperationException(
                "A Segment preview fallback exceeded its bounded tile budget.");
        }

        BitmapSource? tile0 = null;
        BitmapSource? tile1 = null;
        BitmapSource? tile2 = null;
        BitmapSource? tile3 = null;
        bool complete = true;
        for (int tile = 0; tile < tileCount; tile++)
        {
            SegmentPreviewWarmupRequest request = CreateSegmentPreviewRequest(
                preview,
                segmentLengthTicks,
                PreviewTicksPerQuarterNote,
                lod,
                tile,
                noteColor,
                eventColor);
            if (TimelineRasterCache.Shared.TryGet(request.Key, out BitmapSource? bitmap)
                && bitmap is not null)
            {
                switch (tile)
                {
                    case 0: tile0 = bitmap; break;
                    case 1: tile1 = bitmap; break;
                    case 2: tile2 = bitmap; break;
                    case 3: tile3 = bitmap; break;
                }
                continue;
            }
            complete = false;
            RequestSegmentPreviewRaster(request, preview);
        }
        if (!complete)
        {
            frame = default;
            return false;
        }
        frame = new(
            lod,
            contentWidth,
            tileCount,
            tile0,
            tile1,
            tile2,
            tile3);
        return true;
    }

    private void DrawSegmentPreview(
        DrawingContext context,
        Rect visibleSegmentBounds,
        Rect fullSegmentBounds,
        TimelineSegmentPreview preview,
        long segmentLengthTicks,
        double currentPixelsPerTick,
        Brush noteBrush,
        Brush eventBrush,
        bool allowDetailedRequests)
    {
        if (!preview.HasNoteContent && !preview.HasEventContent
            || visibleSegmentBounds.Width <= 0
            || visibleSegmentBounds.Height <= 0
            || fullSegmentBounds.Width <= 0
            || fullSegmentBounds.Height <= 0)
        {
            return;
        }
        Color noteColor = GetSolidColor(noteBrush, Color.FromRgb(189, 199, 207));
        Color eventColor = GetSolidColor(eventBrush, Color.FromRgb(229, 61, 68));
        double normalizedVisibleLeft = Math.Clamp(
            (visibleSegmentBounds.Left - fullSegmentBounds.Left) / fullSegmentBounds.Width,
            0,
            1);
        double normalizedVisibleRight = Math.Clamp(
            (visibleSegmentBounds.Right - fullSegmentBounds.Left) / fullSegmentBounds.Width,
            0,
            1);
        if (normalizedVisibleRight <= normalizedVisibleLeft) return;
        int lod = TimelineSegmentPreviewRasterizer.SelectDisplayLod(
            currentPixelsPerTick,
            PreviewTicksPerQuarterNote);
        long fixedContentWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
            segmentLengthTicks,
            PreviewTicksPerQuarterNote,
            lod);
        long tileCount = 1 + ((fixedContentWidth - 1)
            / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize);
        double fixedVisibleLeft = normalizedVisibleLeft * fixedContentWidth;
        double fixedVisibleRight = normalizedVisibleRight * fixedContentWidth;
        long firstTile = Math.Max(
            0,
            FloorToLong(fixedVisibleLeft / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize));
        long lastTile = Math.Max(
            firstTile,
            FloorToLong(
                Math.BitDecrement(fixedVisibleRight)
                / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize));
        lastTile = Math.Min(tileCount - 1, lastTile);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        context.PushClip(new RectangleGeometry(visibleSegmentBounds));
        bool hasFallback = _visibleSegmentPreviewFallbackFrames.TryGetValue(
            preview.SegmentId,
            out SegmentPreviewFallbackFrame fallback);
        if (hasFallback && lod >= fallback.Lod)
        {
            DrawSegmentPreviewFallback(
                context,
                fullSegmentBounds,
                fallback,
                dpi);
            context.Pop();
            return;
        }
        if (!hasFallback && SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            context.Pop();
            return;
        }

        _segmentPreviewDetailTiles.Clear();
        _segmentPreviewDetailedBounds.Clear();
        foreach (long tile in TimelineTickMath.InclusiveIndices(firstTile, lastTile))
        {
            SegmentPreviewWarmupRequest request = CreateSegmentPreviewRequest(
                preview,
                segmentLengthTicks,
                PreviewTicksPerQuarterNote,
                lod,
                tile,
                noteColor,
                eventColor);
            TimelineRasterCacheKey key = request.Key;
            if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap)
                && bitmap is not null)
            {
                long tileLeft = checked(
                    tile * TimelineSegmentPreviewRasterizer.FixedPreviewTileSize);
                Rect destination = TimelineRasterPlacement.GetSegmentPreviewTileDestination(
                    fullSegmentBounds,
                    fixedContentWidth,
                    tileLeft,
                    bitmap.PixelWidth,
                    dpi);
                _segmentPreviewDetailTiles.Add(new(destination, bitmap));
                _segmentPreviewDetailedBounds.Add(destination);
                continue;
            }
            if (allowDetailedRequests)
                RequestSegmentPreviewRaster(request, preview);
        }

        if (hasFallback)
        {
            TimelineRasterPlacement.BuildUncoveredHorizontalGaps(
                visibleSegmentBounds,
                _segmentPreviewDetailedBounds,
                _segmentPreviewFallbackGaps);
            foreach (Rect gap in _segmentPreviewFallbackGaps)
            {
                context.PushClip(new RectangleGeometry(gap));
                DrawSegmentPreviewFallback(
                    context,
                    fullSegmentBounds,
                    fallback,
                    dpi);
                context.Pop();
            }
        }
        foreach (SegmentPreviewDetailTile detail in _segmentPreviewDetailTiles)
            context.DrawImage(detail.Bitmap, detail.Destination);
        context.Pop();
    }

    private static void DrawSegmentPreviewFallback(
        DrawingContext context,
        Rect fullSegmentBounds,
        SegmentPreviewFallbackFrame frame,
        DpiScale dpi)
    {
        for (int tile = 0; tile < frame.TileCount; tile++)
        {
            BitmapSource bitmap = frame.GetBitmap(tile);
            long tileLeft = checked(
                (long)tile * TimelineSegmentPreviewRasterizer.FixedPreviewTileSize);
            Rect destination = TimelineRasterPlacement.GetSegmentPreviewTileDestination(
                fullSegmentBounds,
                frame.ContentWidth,
                tileLeft,
                bitmap.PixelWidth,
                dpi);
            context.DrawImage(bitmap, destination);
        }
    }

    private bool TryGetPreparedTileFingerprint(
        TileFingerprintRequestKey key,
        TimelineRenderSnapshot snapshot,
        Func<ulong> factory,
        bool computeSynchronously,
        out ulong fingerprint)
    {
        if (_preparedTileFingerprints.TryGetValue(key, out fingerprint)) return true;
        if (computeSynchronously)
        {
            fingerprint = factory();
            PublishPreparedTileFingerprint(key, fingerprint);
            return true;
        }
        if (_backgroundWorkSuspended || !_pendingTileFingerprints.Add(key)) return false;

        CancellationToken cancellationToken = IsSelectionDependentRasterLayer(key.Layer)
            ? _selectionRasterRequestCancellation.Token
            : _rasterRequestCancellation.Token;
        _ = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                await _tileFingerprintWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                cancellationToken.ThrowIfCancellationRequested();
                ulong value = factory();
                cancellationToken.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_backgroundWorkSuspended
                        || cancellationToken.IsCancellationRequested
                        || !ReferenceEquals(Snapshot, snapshot))
                    {
                        return;
                    }
                    _pendingTileFingerprints.Remove(key);
                    PublishPreparedTileFingerprint(key, value);
                    QueueRasterInvalidation();
                }, DispatcherPriority.Background, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                        () =>
                        {
                            if (!cancellationToken.IsCancellationRequested
                                && ReferenceEquals(Snapshot, snapshot))
                                _pendingTileFingerprints.Remove(key);
                        },
                        DispatcherPriority.Background);
                }
            }
            finally
            {
                if (entered) _tileFingerprintWorker.Release();
            }
        }, CancellationToken.None);
        fingerprint = 0;
        return false;
    }

    private void PublishPreparedTileFingerprint(TileFingerprintRequestKey key, ulong value)
    {
        _preparedTileFingerprints[key] = value;
        _preparedTileFingerprintOrder.Enqueue(key);
        while (_preparedTileFingerprints.Count > MaximumPreparedTileFingerprints
            && _preparedTileFingerprintOrder.TryDequeue(out TileFingerprintRequestKey oldest))
        {
            _preparedTileFingerprints.Remove(oldest);
        }
    }

    private void RequestSegmentPreviewRaster(
        SegmentPreviewWarmupRequest request,
        TimelineSegmentPreview preview)
    {
        if (_backgroundWorkSuspended) return;
        TimelineRasterCacheKey key = request.Key;
        MidoraId segmentId = preview.SegmentId;
        if (!_requestedRasterKeys.Add(key)) return;
        bool accepted = TimelineRasterCache.Shared.Request(
            key,
            request.Factory,
            Dispatcher,
            () =>
            {
                _requestedRasterKeys.Remove(key);
                if (!_backgroundWorkSuspended
                    && Snapshot?.SegmentPreviews.TryGetValue(segmentId, out _) == true)
                {
                    QueueRasterInvalidation();
                }
            },
            _rasterRequestCancellation.Token,
            TimelineRasterRequestPriority.Visible,
            consumerId: _rasterConsumerId);
        if (!accepted)
        {
            _requestedRasterKeys.Remove(key);
        }
    }

    private void QueueRasterInvalidation()
    {
        if (_backgroundWorkSuspended || _rasterInvalidationQueued) return;
        _rasterInvalidationQueued = true;
        QueueWeakSurfaceSignal(
            static surface =>
            {
                surface._rasterInvalidationQueued = false;
                if (!surface._backgroundWorkSuspended) surface.InvalidateVisual();
            },
            DispatcherPriority.Render);
    }

    private void QueueWeakSurfaceSignal(Action<TimelineSurface> callback, DispatcherPriority priority)
    {
        WeakReference<TimelineSurface> owner = new(this);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (owner.TryGetTarget(out TimelineSurface? surface)) callback(surface);
        }, priority);
    }

    private void ScheduleSegmentPreviewWarmup()
    {
        long generation = checked(++_segmentPreviewWarmupGeneration);
        CancellationTokenSource? previousCancellation = _segmentPreviewWarmupPlanCancellation;
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        _segmentPreviewWarmupPlanCancellation = null;
        _segmentPreviewWarmupQueue.Clear();
        _segmentPreviewWarmupInFlight = 0;
        _segmentPreviewWarmupRetryScheduled = false;
        if (_backgroundWorkSuspended
            || SurfaceMode != TimelineSurfaceMode.Arrangement
            || Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }

        Color defaultNoteColor = GetSolidColor(
            Brush("Brush.Segment.NotePreview", Color.FromRgb(189, 199, 207)),
            Color.FromRgb(189, 199, 207));
        Color eventColor = GetSolidColor(
            Brush("Brush.Red", Color.FromRgb(229, 61, 68)),
            Color.FromRgb(229, 61, 68));
        CancellationTokenSource cancellation = new();
        _segmentPreviewWarmupPlanCancellation = cancellation;
        _ = BuildSegmentPreviewWarmupPlanAsync(
            snapshot,
            defaultNoteColor,
            eventColor,
            PreviewTicksPerQuarterNote,
            generation,
            cancellation.Token);
    }

    private async Task BuildSegmentPreviewWarmupPlanAsync(
        TimelineRenderSnapshot snapshot,
        Color defaultNoteColor,
        Color eventColor,
        int ticksPerQuarterNote,
        long generation,
        CancellationToken cancellationToken)
    {
        List<SegmentPreviewWarmupRequest> requests;
        try
        {
            requests = await Task.Run(() =>
            {
                List<SegmentPreviewWarmupRequest> result = [];
                foreach (TimelineRenderItem item in snapshot.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Kind != TimelineItemKind.Segment
                        || !snapshot.SegmentPreviews.TryGetValue(
                            item.Id,
                            out TimelineSegmentPreview? preview)
                        || !preview.HasNoteContent && !preview.HasEventContent)
                    {
                        continue;
                    }
                    Color noteColor = item.AccentColor == 0
                        ? defaultNoteColor
                        : TimelineAccentPalette.FromArgb(item.AccentColor).NotePreview;
                    long segmentLengthTicks = checked(item.EndTick - item.StartTick);
                    int lod = TimelineSegmentPreviewRasterizer.SelectWarmupLod(
                        segmentLengthTicks,
                        ticksPerQuarterNote);
                    long contentWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
                        segmentLengthTicks,
                        ticksPerQuarterNote,
                        lod);
                    long tileCount = 1 + ((contentWidth - 1)
                        / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize);
                    for (long tile = 0; tile < tileCount; tile++)
                    {
                        result.Add(CreateSegmentPreviewRequest(
                            preview,
                            segmentLengthTicks,
                            ticksPerQuarterNote,
                            lod,
                            tile,
                            noteColor,
                            eventColor));
                    }
                }
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (_backgroundWorkSuspended
                        || cancellationToken.IsCancellationRequested
                        || generation != _segmentPreviewWarmupGeneration
                        || SurfaceMode != TimelineSurfaceMode.Arrangement)
                    {
                        return;
                    }
                    foreach (SegmentPreviewWarmupRequest request in requests)
                    {
                        _segmentPreviewWarmupQueue.Enqueue(request);
                    }
                    PumpSegmentPreviewWarmup(generation);
                },
                DispatcherPriority.ApplicationIdle,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private void PumpSegmentPreviewWarmup(long generation)
    {
        if (_backgroundWorkSuspended
            || generation != _segmentPreviewWarmupGeneration
            || SurfaceMode != TimelineSurfaceMode.Arrangement)
        {
            return;
        }
        int maximumWarmupConcurrency = TimelineRasterCache.RecommendedBackgroundConcurrency;
        while (_segmentPreviewWarmupInFlight < maximumWarmupConcurrency
            && _segmentPreviewWarmupQueue.TryDequeue(out SegmentPreviewWarmupRequest request))
        {
            if (TimelineRasterCache.Shared.TryGet(request.Key, out _)) continue;
            _segmentPreviewWarmupInFlight++;
            bool accepted = TimelineRasterCache.Shared.Request(
                request.Key,
                request.Factory,
                Dispatcher,
                () =>
                {
                    if (_backgroundWorkSuspended
                        || generation != _segmentPreviewWarmupGeneration)
                    {
                        return;
                    }
                    _segmentPreviewWarmupInFlight = Math.Max(0, _segmentPreviewWarmupInFlight - 1);
                    PumpSegmentPreviewWarmup(generation);
                },
                _segmentPreviewWarmupPlanCancellation?.Token ?? default,
                TimelineRasterRequestPriority.Background,
                consumerId: _rasterConsumerId);
            if (accepted) continue;
            _segmentPreviewWarmupInFlight--;
            _segmentPreviewWarmupQueue.Enqueue(request);
            ScheduleSegmentPreviewWarmupRetry(generation);
            return;
        }
    }

    private void ScheduleSegmentPreviewWarmupRetry(long generation)
    {
        if (_backgroundWorkSuspended || _segmentPreviewWarmupRetryScheduled) return;
        _segmentPreviewWarmupRetryScheduled = true;
        QueueWeakSurfaceSignal(
            surface =>
            {
                if (surface._backgroundWorkSuspended
                    || generation != surface._segmentPreviewWarmupGeneration)
                {
                    return;
                }
                surface._segmentPreviewWarmupRetryScheduled = false;
                surface.PumpSegmentPreviewWarmup(generation);
            },
            DispatcherPriority.ApplicationIdle);
    }

    private static SegmentPreviewWarmupRequest CreateSegmentPreviewRequest(
        TimelineSegmentPreview preview,
        long segmentLengthTicks,
        int ticksPerQuarterNote,
        int lod,
        long tile,
        Color noteColor,
        Color eventColor)
    {
        long contentWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
            segmentLengthTicks,
            ticksPerQuarterNote,
            lod);
        ulong contentFingerprint = TimelineContentFingerprint.Combine(
            preview.GetTileContentFingerprint(
                eventLayer: false,
                contentWidth,
                tile),
            preview.GetTileContentFingerprint(
                eventLayer: true,
                contentWidth,
                tile));
        TimelineRasterCacheKey key = new(
            TimelineRasterLayer.ArrangementSegmentPreview,
            $"arrangement-segment-preview:{preview.SegmentId.Value}",
            contentFingerprint,
            contentWidth,
            TimelineSegmentPreviewRasterizer.Height,
            tile,
            lod,
            ColorToArgb(noteColor),
            ColorToArgb(eventColor),
            0,
            96,
            96);
        return new(
            key,
            token => TimelineSegmentPreviewRasterizer.RasterizeFixedPreviewTile(
                preview,
                segmentLengthTicks,
                ticksPerQuarterNote,
                lod,
                tile,
                noteColor,
                eventColor,
                token));
    }

    private void DrawVelocityTiles(
        DrawingContext context,
        TimelineViewport viewport,
        Brush normalBrush,
        Brush selectedBrush,
        Brush borderBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int horizontalLod = TimelineRasterLod.Quantize(viewport.PixelsPerTick * dpi.DpiScaleX);
        double lodPixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        BeginExactRasterProjection(horizontalLod, 0);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        VelocityRasterProjectionSignature projection = new(
            snapshot.ProjectionKey,
            horizontalLod,
            dpiX,
            dpiY);
        double valueRange = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        double valueTop = rulerHeight + (_valueViewMaximum - 1) / valueRange * contentHeight;
        double valueBottom = rulerHeight + _valueViewMaximum / valueRange * contentHeight;
        _velocityTileDrawEntries.Clear();
        if (!TryGetHorizontalContentTileBounds(
                snapshot,
                lodPixelsPerTick,
                TimelineVelocityTileRasterizer.TileSize,
                out long firstContentTileX,
                out long lastContentTileX))
        {
            PresentVelocityRasterLayer(
                context,
                viewport,
                projection,
                horizontalLod,
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom,
                currentFrameComplete: true);
            return;
        }
        long firstTileX = Math.Max(firstContentTileX, FloorToLong(
            viewport.StartTick * lodPixelsPerTick / TimelineVelocityTileRasterizer.TileSize));
        long lastTileX = Math.Min(lastContentTileX, FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * lodPixelsPerTick
            / TimelineVelocityTileRasterizer.TileSize));
        if (lastTileX < firstTileX)
        {
            PresentVelocityRasterLayer(
                context,
                viewport,
                projection,
                horizontalLod,
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom,
                currentFrameComplete: true);
            return;
        }
        TimelineSelectionSnapshot? selection = SelectionSnapshot;
        Color normalColor = GetSolidColor(normalBrush, Color.FromRgb(163, 178, 190));
        Color selectedColor = GetSolidColor(selectedBrush, Color.FromRgb(229, 61, 68));
        Color borderColor = GetSolidColor(borderBrush, Color.FromRgb(49, 58, 69));
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        bool currentFrameComplete = true;
        context.PushClip(new RectangleGeometry(contentBounds));
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(firstContentTileX, firstTileX - ring);
            long lastX = Math.Min(lastContentTileX, lastTileX + ring);
            foreach (long tileX in TimelineTickMath.InclusiveIndices(firstX, lastX))
            {
                bool visible = tileX >= firstTileX && tileX <= lastTileX;
                if (ring == 1 && visible) continue;
                long requestTileX = tileX;
                TileFingerprintRequestKey fingerprintKey = new(
                    TimelineRasterLayer.VelocityBars,
                    snapshot.SemanticRevision,
                    snapshot.ProjectionKey,
                    selection?.Revision ?? -1,
                    horizontalLod,
                    0,
                    tileX,
                    0,
                    dpiX,
                    dpiY);
                if (!TryGetPreparedTileFingerprint(
                        fingerprintKey,
                        snapshot,
                        () => TimelineVelocityTileRasterizer.ComputeContentFingerprint(
                            snapshot, selection, horizontalLod, requestTileX),
                        visible && snapshot.CanComputeTileFingerprintSynchronously,
                        out ulong fingerprint))
                {
                    if (visible) currentFrameComplete = false;
                    continue;
                }
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.VelocityBars,
                    snapshot.ProjectionKey,
                    fingerprint,
                    horizontalLod,
                    0,
                    tileX,
                    0,
                    ColorToArgb(normalColor),
                    ColorToArgb(selectedColor),
                    ColorToArgb(borderColor),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                {
                    if (visible && bitmap is not null)
                    {
                        _velocityTileDrawEntries.Add(new(key, bitmap));
                    }
                    continue;
                }
                if (visible) currentFrameComplete = false;
                RequestRaster(
                    key,
                    token => TimelineVelocityTileRasterizer.Rasterize(
                        snapshot,
                        selection,
                        horizontalLod,
                        requestTileX,
                        normalColor,
                        selectedColor,
                        borderColor,
                        token),
                    visible
                        ? TimelineRasterRequestPriority.Visible
                        : TimelineRasterRequestPriority.Normal);
            }
        }
        PresentVelocityRasterLayer(
            context,
            viewport,
            projection,
            horizontalLod,
            laneHeaderWidth,
            rulerHeight,
            valueTop,
            valueBottom,
            currentFrameComplete);
        context.Pop();
    }

    private void PresentVelocityRasterLayer(
        DrawingContext context,
        TimelineViewport viewport,
        VelocityRasterProjectionSignature projection,
        int currentHorizontalLod,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom,
        bool currentFrameComplete)
    {
        VelocityCompositeRasterFrame frameToDraw;
        if (currentFrameComplete)
        {
            frameToDraw = new(projection, _velocityTileDrawEntries.ToArray());
            _committedVelocityFrame = frameToDraw;
        }
        else if (_committedVelocityFrame is VelocityCompositeRasterFrame committed
            && committed.Projection == projection)
        {
            frameToDraw = committed;
        }
        else
        {
            // Another zoom/DPI projection is never geometrically compatible.
            frameToDraw = new(projection, _velocityTileDrawEntries.ToArray());
        }
        DrawVelocityTileEntries(
            context,
            viewport,
            frameToDraw.Tiles,
            currentHorizontalLod,
            laneHeaderWidth,
            rulerHeight,
            valueTop,
            valueBottom);
        _velocityTileDrawEntries.Clear();
    }

    private void DrawVelocityTileEntries(
        DrawingContext context,
        TimelineViewport viewport,
        IReadOnlyList<VelocityTileDrawEntry> entries,
        int horizontalLod,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        foreach (VelocityTileDrawEntry entry in entries)
        {
            DrawVelocityTile(
                context,
                viewport,
                entry,
                horizontalLod,
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom);
        }
    }

    private static void DrawVelocityTile(
        DrawingContext context,
        TimelineViewport viewport,
        VelocityTileDrawEntry entry,
        int horizontalLod,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        Rect destination = TimelineRasterPlacement.GetVelocityTileDestination(
            viewport, horizontalLod, entry.Key.TileX, laneHeaderWidth, rulerHeight,
            valueTop, valueBottom);
        Rect coreDestination = TimelineRasterPlacement.GetVelocityTileCoreDestination(
            viewport, horizontalLod, entry.Key.TileX, laneHeaderWidth, rulerHeight,
            valueTop, valueBottom);
        context.PushClip(new RectangleGeometry(coreDestination));
        context.DrawImage(entry.Bitmap, destination);
        context.Pop();
    }

    private void DrawEventPointTiles(
        DrawingContext context,
        TimelineViewport viewport,
        Brush normalBrush,
        Brush primaryBrush,
        Brush borderBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        double rasterDpiScaleX = dpiX / 1024d;
        double rasterDpiScaleY = dpiY / 1024d;
        double devicePixelsPerTick = viewport.PixelsPerTick * dpi.DpiScaleX;
        double valueRange = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        double devicePixelsPerValue = contentHeight * dpi.DpiScaleY / valueRange;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerTick);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerValue);
        BeginExactRasterProjection(horizontalScaleKey, verticalScaleKey);
        EventPointRasterProjectionSignature projection = new(
            snapshot.ProjectionKey,
            horizontalScaleKey,
            verticalScaleKey,
            dpiX,
            dpiY);
        _eventPointTileDrawEntries.Clear();
        if (snapshot.TotalItemCount == 0)
        {
            PresentEventPointRasterLayer(
                context,
                viewport,
                projection,
                devicePixelsPerTick,
                devicePixelsPerValue,
                laneHeaderWidth,
                rulerHeight,
                rasterDpiScaleX,
                rasterDpiScaleY,
                currentFrameComplete: true);
            return;
        }
        if (!TryGetHorizontalContentTileBounds(
                snapshot,
                devicePixelsPerTick,
                TimelineEventPointTileRasterizer.TileSize,
                out long firstContentTileX,
                out long lastContentTileX))
        {
            PresentEventPointRasterLayer(
                context,
                viewport,
                projection,
                devicePixelsPerTick,
                devicePixelsPerValue,
                laneHeaderWidth,
                rulerHeight,
                rasterDpiScaleX,
                rasterDpiScaleY,
                currentFrameComplete: true);
            return;
        }
        // A Tempo state continues beyond the last event. Ordinary event lanes
        // retain their existing finite point-content bounds.
        if (snapshot.IsTempoProjection)
        {
            firstContentTileX = 0;
            lastContentTileX = Math.Max(lastContentTileX, FloorToLong(
                viewport.EndTick * devicePixelsPerTick / TimelineEventPointTileRasterizer.TileSize) + 1);
        }
        long firstVisibleTileX = Math.Max(firstContentTileX, FloorToLong(
            viewport.StartTick * devicePixelsPerTick / TimelineEventPointTileRasterizer.TileSize));
        long lastVisibleTileX = Math.Min(lastContentTileX, FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * devicePixelsPerTick
            / TimelineEventPointTileRasterizer.TileSize));
        if (lastVisibleTileX < firstVisibleTileX)
        {
            PresentEventPointRasterLayer(
                context,
                viewport,
                projection,
                devicePixelsPerTick,
                devicePixelsPerValue,
                laneHeaderWidth,
                rulerHeight,
                rasterDpiScaleX,
                rasterDpiScaleY,
                currentFrameComplete: true);
            return;
        }
        double visibleWorldTop = (1 - _valueViewMaximum) * devicePixelsPerValue;
        double visibleWorldBottom = (1 - _valueViewMinimum) * devicePixelsPerValue;
        long firstVisibleTileY = Math.Max(0, FloorToLong(
            visibleWorldTop / TimelineEventPointTileRasterizer.TileSize));
        long lastVisibleTileY = Math.Max(firstVisibleTileY, FloorToLong(
            Math.BitDecrement(visibleWorldBottom) / TimelineEventPointTileRasterizer.TileSize));
        TimelineSelectionSnapshot? selection = SelectionSnapshot;
        Color normalColor = snapshot.IsTempoProjection
            ? GetSolidColor(Brush("Brush.Success", Color.FromRgb(88, 196, 135)), Color.FromRgb(88, 196, 135))
            : GetSolidColor(normalBrush, Color.FromRgb(98, 166, 246));
        Color primaryColor = snapshot.IsTempoProjection
            ? GetSolidColor(Brush("Brush.Red", Color.FromRgb(229, 72, 77)), Color.FromRgb(229, 72, 77))
            : GetSolidColor(primaryBrush, Color.FromRgb(241, 243, 245));
        Color borderColor = GetSolidColor(borderBrush, Color.FromRgb(42, 48, 58));
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        bool currentFrameComplete = true;
        context.PushClip(new RectangleGeometry(contentBounds));
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(firstContentTileX, firstVisibleTileX - ring);
            long lastX = Math.Min(lastContentTileX, lastVisibleTileX + ring);
            long firstY = Math.Max(0, firstVisibleTileY - ring);
            long lastY = Math.Max(firstY, lastVisibleTileY + ring);
            foreach (long tileY in TimelineTickMath.InclusiveIndices(firstY, lastY))
            {
                foreach (long tileX in TimelineTickMath.InclusiveIndices(firstX, lastX))
                {
                    bool visible = tileX >= firstVisibleTileX && tileX <= lastVisibleTileX
                        && tileY >= firstVisibleTileY && tileY <= lastVisibleTileY;
                    if (ring == 1 && visible) continue;
                    long requestTileX = tileX;
                    long requestTileY = tileY;
                    TileFingerprintRequestKey fingerprintKey = new(
                        TimelineRasterLayer.EventPoints,
                        snapshot.SemanticRevision,
                        snapshot.ProjectionKey,
                        selection?.Revision ?? -1,
                        horizontalScaleKey,
                        verticalScaleKey,
                        tileX,
                        tileY,
                        dpiX,
                        dpiY);
                    if (!TryGetPreparedTileFingerprint(
                            fingerprintKey,
                            snapshot,
                            () => TimelineEventPointTileRasterizer.ComputeContentFingerprint(
                                snapshot,
                                selection,
                                devicePixelsPerTick,
                                devicePixelsPerValue,
                                requestTileX,
                                requestTileY,
                                rasterDpiScaleX,
                                rasterDpiScaleY),
                            visible && snapshot.CanComputeTileFingerprintSynchronously,
                            out ulong contentFingerprint))
                    {
                        if (visible) currentFrameComplete = false;
                        continue;
                    }
                    TimelineRasterCacheKey key = new(
                        TimelineRasterLayer.EventPoints,
                        snapshot.ProjectionKey,
                        contentFingerprint,
                        horizontalScaleKey,
                        verticalScaleKey,
                        tileX,
                        tileY,
                        ColorToArgb(normalColor),
                        ColorToArgb(primaryColor),
                        ColorToArgb(borderColor),
                        dpiX,
                        dpiY);
                    if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                    {
                        if (visible && bitmap is not null)
                        {
                            _eventPointTileDrawEntries.Add(new(key, bitmap));
                        }
                        continue;
                    }
                    if (visible) currentFrameComplete = false;
                    RequestRaster(
                        key,
                        cancellationToken => TimelineEventPointTileRasterizer.Rasterize(
                            snapshot,
                            selection,
                            devicePixelsPerTick,
                            devicePixelsPerValue,
                            requestTileX,
                            requestTileY,
                            rasterDpiScaleX,
                            rasterDpiScaleY,
                            normalColor,
                            primaryColor,
                            borderColor,
                            cancellationToken: cancellationToken),
                        visible
                            ? TimelineRasterRequestPriority.Visible
                            : TimelineRasterRequestPriority.Normal);
                }
            }
        }
        PresentEventPointRasterLayer(
            context,
            viewport,
            projection,
            devicePixelsPerTick,
            devicePixelsPerValue,
            laneHeaderWidth,
            rulerHeight,
            rasterDpiScaleX,
            rasterDpiScaleY,
            currentFrameComplete);
        context.Pop();
        if (selection is { Count: > 0 } && CanEdit)
        {
            PrefetchEventPointSelectionTiles(
                snapshot,
                selection,
                devicePixelsPerTick,
                devicePixelsPerValue,
                firstVisibleTileX,
                lastVisibleTileX,
                firstVisibleTileY,
                lastVisibleTileY,
                rasterDpiScaleX,
                rasterDpiScaleY,
                normalColor,
                primaryColor,
                borderColor,
                dpiX,
                dpiY);
        }
    }

    private void PrefetchEventPointSelectionTiles(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot selection,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long firstVisibleTileX,
        long lastVisibleTileX,
        long firstVisibleTileY,
        long lastVisibleTileY,
        double dpiScaleX,
        double dpiScaleY,
        Color normalColor,
        Color primaryColor,
        Color borderColor,
        int dpiX,
        int dpiY)
    {
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerTick);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerValue);
        if (!TryGetHorizontalContentTileBounds(
                snapshot,
                devicePixelsPerTick,
                TimelineEventPointTileRasterizer.TileSize,
                out long firstContentTileX,
                out long lastContentTileX))
        {
            return;
        }
        long firstX = Math.Max(firstContentTileX, firstVisibleTileX - 1);
        long lastX = Math.Min(lastContentTileX, lastVisibleTileX + 1);
        long firstY = Math.Max(0, firstVisibleTileY - 1);
        long lastY = Math.Max(firstY, lastVisibleTileY + 1);
        foreach (long tileY in TimelineTickMath.InclusiveIndices(firstY, lastY))
        {
            foreach (long tileX in TimelineTickMath.InclusiveIndices(firstX, lastX))
            {
                bool visible = tileX >= firstVisibleTileX && tileX <= lastVisibleTileX
                    && tileY >= firstVisibleTileY && tileY <= lastVisibleTileY;
                long requestTileX = tileX;
                long requestTileY = tileY;
                TileFingerprintRequestKey fingerprintKey = new(
                    TimelineRasterLayer.EventPointSelection,
                    snapshot.SemanticRevision,
                    snapshot.ProjectionKey,
                    selection.Revision,
                    horizontalScaleKey,
                    verticalScaleKey,
                    tileX,
                    tileY,
                    dpiX,
                    dpiY);
                if (!TryGetPreparedTileFingerprint(
                        fingerprintKey,
                        snapshot,
                        () => TimelineEventPointTileRasterizer.ComputeContentFingerprint(
                            snapshot,
                            selection,
                            devicePixelsPerTick,
                            devicePixelsPerValue,
                            requestTileX,
                            requestTileY,
                            dpiScaleX,
                            dpiScaleY,
                            selectionOnly: true),
                        visible && snapshot.CanComputeTileFingerprintSynchronously,
                        out ulong contentFingerprint))
                {
                    continue;
                }
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.EventPointSelection,
                    snapshot.ProjectionKey,
                    contentFingerprint,
                    horizontalScaleKey,
                    verticalScaleKey,
                    tileX,
                    tileY,
                    ColorToArgb(normalColor),
                    ColorToArgb(primaryColor),
                    ColorToArgb(borderColor),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out _))
                {
                    continue;
                }
                RequestRaster(
                    key,
                    cancellationToken => TimelineEventPointTileRasterizer.Rasterize(
                        snapshot,
                        selection,
                        devicePixelsPerTick,
                        devicePixelsPerValue,
                        requestTileX,
                        requestTileY,
                        dpiScaleX,
                        dpiScaleY,
                        normalColor,
                        primaryColor,
                        borderColor,
                        selectionOnly: true,
                        cancellationToken: cancellationToken));
            }
        }
    }

    private void PresentEventPointRasterLayer(
        DrawingContext context,
        TimelineViewport viewport,
        EventPointRasterProjectionSignature projection,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        double laneHeaderWidth,
        double rulerHeight,
        double dpiScaleX,
        double dpiScaleY,
        bool currentFrameComplete)
    {
        EventPointCompositeRasterFrame frameToDraw;
        if (currentFrameComplete)
        {
            frameToDraw = new(projection, _eventPointTileDrawEntries.ToArray());
            _committedEventPointFrame = frameToDraw;
        }
        else if (_committedEventPointFrame is EventPointCompositeRasterFrame committed
            && committed.Projection == projection)
        {
            frameToDraw = committed;
        }
        else
        {
            // Another zoom/DPI projection is never geometrically compatible.
            frameToDraw = new(projection, _eventPointTileDrawEntries.ToArray());
        }
        DrawEventPointTileEntries(
            context,
            viewport,
            frameToDraw.Tiles,
            devicePixelsPerTick,
            devicePixelsPerValue,
            laneHeaderWidth,
            rulerHeight,
            dpiScaleX,
            dpiScaleY);
        _eventPointTileDrawEntries.Clear();
    }

    private void DrawEventPointTileEntries(
        DrawingContext context,
        TimelineViewport viewport,
        IReadOnlyList<EventPointTileDrawEntry> entries,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        double laneHeaderWidth,
        double rulerHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        foreach (EventPointTileDrawEntry entry in entries)
        {
            DrawEventPointTile(
                context,
                viewport,
                entry,
                devicePixelsPerTick,
                devicePixelsPerValue,
                laneHeaderWidth,
                rulerHeight,
                dpiScaleX,
                dpiScaleY);
        }
    }

    private void DrawEventPointTile(
        DrawingContext context,
        TimelineViewport viewport,
        EventPointTileDrawEntry entry,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        double laneHeaderWidth,
        double rulerHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        Rect destination = TimelineRasterPlacement.GetEventPointTileDestination(
            viewport,
            devicePixelsPerTick,
            devicePixelsPerValue,
            entry.Key.TileX,
            entry.Key.TileY,
            laneHeaderWidth,
            rulerHeight,
            _valueViewMinimum,
            _valueViewMaximum,
            dpiScaleX,
            dpiScaleY);
        Rect coreDestination = TimelineRasterPlacement.GetEventPointTileCoreDestination(
            viewport,
            devicePixelsPerTick,
            devicePixelsPerValue,
            entry.Key.TileX,
            entry.Key.TileY,
            laneHeaderWidth,
            rulerHeight,
            _valueViewMinimum,
            _valueViewMaximum,
            dpiScaleX,
            dpiScaleY);
        context.PushClip(new RectangleGeometry(coreDestination));
        context.DrawImage(entry.Bitmap, destination);
        context.Pop();
    }

    private void DrawVelocityEditOverlay(
        DrawingContext context,
        TimelineViewport viewport,
        Brush normalBrush,
        Brush selectedBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_velocityOrigin is null) return;
        if (_velocityDirectItemId is MidoraId directId
            && Snapshot is TimelineRenderSnapshot snapshot
            && _velocityEdits.ContainsKey(directId)
            && snapshot.TryGetItem(directId, out TimelineRenderItem item)
            && item.EndTick > viewport.StartTick
            && item.StartTick < viewport.EndTick)
        {
            DrawVelocityBar(
                context,
                viewport,
                item,
                normalBrush,
                selectedBrush,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        DrawVelocityTrace(context, laneHeaderWidth, rulerHeight);
    }

    private void DrawVelocityBar(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush normalBrush,
        Brush selectedBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double value = _velocityEdits.TryGetValue(item.Id, out int edited)
            ? edited / 127d
            : Math.Clamp(item.Value, 1d / 127d, 1);
        double centerX = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double valueY = NormalizedToValueY(value, rulerHeight);
        double zeroY = NormalizedToValueY(0, rulerHeight);
        double top = Math.Clamp(Math.Min(valueY, zeroY), rulerHeight, ActualHeight);
        double bottom = Math.Clamp(Math.Max(valueY, zeroY), rulerHeight, ActualHeight);
        const double stemWidth = TimelineVelocityTileRasterizer.StemWidth;
        const double markerSize = TimelineVelocityTileRasterizer.MarkerSize;
        Rect bar = new(centerX - stemWidth / 2, top, stemWidth, Math.Max(1, bottom - top));
        bool selected = IsSelected(item);
        Brush barBrush = selected ? selectedBrush : normalBrush;
        context.PushOpacity(selected ? 0.4 : 0.2);
        context.DrawRectangle(barBrush, null, bar);
        context.Pop();
        context.PushOpacity(selected ? 1 : 0.86);
        context.DrawRectangle(null, _borderPen, bar);
        context.Pop();

        if (centerX + markerSize / 2 >= laneHeaderWidth && centerX - markerSize / 2 < ActualWidth)
        {
            Rect onsetMarker = new(
                centerX - markerSize / 2,
                top,
                markerSize,
                markerSize);
            context.DrawRectangle(barBrush, _borderPen, onsetMarker);
        }
    }

    private void DrawVelocityTrace(
        DrawingContext context,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_velocityTracePoints.Count == 0) return;
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        context.PushClip(new RectangleGeometry(contentBounds));
        if (_velocityTracePoints.Count == 1)
        {
            Point point = _velocityTracePoints[0];
            context.DrawEllipse(_selectionPen!.Brush, null, point, 2, 2);
        }
        else
        {
            StreamGeometry geometry = new();
            using (StreamGeometryContext geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(_velocityTracePoints[0], isFilled: false, isClosed: false);
                for (int index = 1; index < _velocityTracePoints.Count; index++)
                {
                    geometryContext.LineTo(_velocityTracePoints[index], isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            context.DrawGeometry(null, _selectionPen, geometry);
        }
        context.Pop();
    }

    private void DrawEventPointTrace(
        DrawingContext context,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_eventPointTracePoints.Count == 0) return;
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        context.PushClip(new RectangleGeometry(contentBounds));
        if (_eventPointTracePoints.Count == 1)
        {
            context.DrawEllipse(_selectionPen!.Brush, null, _eventPointTracePoints[0], 2, 2);
        }
        else
        {
            StreamGeometry geometry = new();
            using (StreamGeometryContext geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(_eventPointTracePoints[0], isFilled: false, isClosed: false);
                for (int index = 1; index < _eventPointTracePoints.Count; index++)
                {
                    geometryContext.LineTo(_eventPointTracePoints[index], isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            context.DrawGeometry(null, _selectionPen, geometry);
        }
        context.Pop();
    }

    private bool TryHitEventPoint(
        Point point,
        TimelineViewport viewport,
        out TimelineRenderItem item)
    {
        item = default;
        if (Snapshot is null) return false;
        if (Snapshot.ConductorSource is not null)
            return TryHitConductorPoint(point, viewport, out item);
        long tick = viewport.XToTick(point.X - GetLaneHeaderWidth());
        long tolerance = TimelineTickMath.CeilingDistance(5 / viewport.PixelsPerTick);
        _visibleItems.Clear();
        if (!Snapshot.TryHitTestCached(tick, tolerance, 0, _visibleItems))
        {
            ScheduleExactPrefetch(
                Snapshot,
                Math.Max(0, tick - tolerance),
                tick > long.MaxValue - tolerance - 1 ? long.MaxValue : tick + tolerance + 1,
                0,
                1);
            return false;
        }
        _exactQueryPending = false;
        foreach (TimelineRenderItem candidate in _visibleItems.OrderByDescending(value => value.ZIndex))
        {
            if (!IsEventPointKind(candidate.Kind)) continue;
            double x = GetLaneHeaderWidth() + viewport.TickToX(candidate.StartTick);
            double y = NormalizedToValueY(candidate.Value, GetRulerHeight());
            if (Math.Abs(point.X - x) <= 8 && Math.Abs(point.Y - y) <= 8)
            {
                item = candidate;
                return true;
            }
        }
        return false;
    }

    private void UpdateSingleEventPoint(long tick, double y, double rulerHeight) =>
        _eventPointEdits[tick] = Math.Clamp(ValueYToNormalized(y, rulerHeight), 0, 1);

    private void UpdateEventPointTrace(Point origin, Point point)
    {
        Point clamped = ClampEventPointTracePoint(point);
        if (_eventPointTimeLocked)
        {
            Point clampedOrigin = ClampEventPointTracePoint(origin);
            _eventPointTracePoints.Clear();
            _eventPointTracePoints.Add(new Point(clampedOrigin.X, clamped.Y));
            return;
        }
        if (_eventPointButton == MouseButton.Right)
        {
            Point clampedOrigin = ClampEventPointTracePoint(origin);
            if (_eventPointHorizontalTrace)
            {
                clamped = new Point(clamped.X, clampedOrigin.Y);
            }
            _eventPointTracePoints.Clear();
            _eventPointTracePoints.Add(clampedOrigin);
            _eventPointTracePoints.Add(clamped);
            return;
        }
        if (_eventPointTracePoints.Count == 0)
        {
            _eventPointTracePoints.Add(ClampEventPointTracePoint(origin));
        }
        Point previous = _eventPointTracePoints[^1];
        double deltaX = clamped.X - previous.X;
        double deltaY = clamped.Y - previous.Y;
        if (deltaX * deltaX + deltaY * deltaY >= 1)
        {
            _eventPointTracePoints.Add(clamped);
        }
        else
        {
            _eventPointTracePoints[^1] = clamped;
        }
    }

    private Point ClampEventPointTracePoint(Point point) => new(
        Math.Clamp(point.X, GetLaneHeaderWidth(), Math.Max(GetLaneHeaderWidth(), ActualWidth)),
        Math.Clamp(point.Y, GetRulerHeight(), Math.Max(GetRulerHeight(), ActualHeight)));

    private void BuildEventPointEditsFromTrace(TimelineViewport viewport)
    {
        TimelineValueTraceSampler.SampleInto(CreateEventPointTrace(viewport), _eventPointEdits,
            Math.Max(1, OperationStepTicks), OperationUsesBars, TimeSignatureMap, RangeStartTick, EventCreationRangeEndTick);
    }

    private TimelineValueTracePoint[] CreateEventPointTrace(TimelineViewport viewport)
    {
        TimelineValueTracePoint[] trace = new TimelineValueTracePoint[_eventPointTracePoints.Count];
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        for (int index = 0; index < _eventPointTracePoints.Count; index++)
        {
            Point point = _eventPointTracePoints[index];
            double contentX = Math.Clamp(point.X - header, 0, viewport.Width);
            long tick = viewport.XToTick(contentX);
            trace[index] = new(
                tick,
                Math.Clamp(ValueYToNormalized(point.Y, ruler), 0, 1));
        }
        return trace;
    }

    private void DrawValueGrid(
        DrawingContext context,
        Brush text,
        double laneHeaderWidth,
        double rulerHeight,
        bool drawLabels)
    {
        double minimum = ValueAxisMinimum;
        double maximum = ValueAxisMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = 0;
            maximum = 127;
        }
        string? previousLabel = null;
        for (int index = 0; index <= 4; index++)
        {
            double screenRatio = index / 4d;
            var tick = TimelineValueAxisTick.At(minimum, maximum,
                _valueViewMinimum, _valueViewMaximum, screenRatio, ValueAxisIntegral);
            double normalized = tick.Normalized;
            double value = tick.Value;
            string labelText = ValueAxisIntegral
                ? Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);
            if (string.Equals(previousLabel, labelText, StringComparison.Ordinal)) continue;
            previousLabel = labelText;
            double y = NormalizedToValueY(normalized, rulerHeight);
            if (drawLabels)
            {
                FormattedText label = GetFormattedText(labelText, text, 9, FontWeights.Normal);
                double labelY = Math.Clamp(
                    y - label.Height / 2,
                    rulerHeight,
                    Math.Max(rulerHeight, ActualHeight - label.Height));
                context.DrawText(label, new Point(Math.Max(2, laneHeaderWidth - label.Width - 4), labelY));
            }
            else
            {
                context.DrawLine(_borderPen, new Point(laneHeaderWidth, y), new Point(ActualWidth, y));
            }
        }
    }

    private static void ApplyVelocityTraceSegment(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        bool restrictToSelection,
        Point from,
        Point to,
        TimelineViewport viewport,
        double header,
        double ruler,
        double valueViewMinimum,
        double valueViewMaximum,
        double actualHeight,
        IDictionary<MidoraId, int> edits,
        CancellationToken cancellationToken)
    {
        long fromTick = viewport.XToTick(from.X - header);
        long toTick = viewport.XToTick(to.X - header);
        long minimum = Math.Min(fromTick, toTick);
        long maximum = Math.Max(fromTick, toTick);
        double span = toTick - fromTick;
        List<TimelineRenderItem> items = [];
        snapshot.QueryInto(
            minimum,
            maximum == long.MaxValue ? long.MaxValue : Math.Max(minimum + 1, maximum + 1),
            0,
            1,
            items);
        int visited = 0;
        foreach (TimelineRenderItem item in items)
        {
            if ((visited++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind != TimelineItemKind.Velocity
                || (restrictToSelection && selection?.Contains(item.Id) != true)
                || item.EndTick <= minimum
                || item.StartTick > maximum)
            {
                continue;
            }
            long sampleTick = Math.Clamp(item.StartTick, minimum, maximum);
            double ratio = span == 0 ? 1 : (sampleTick - fromTick) / span;
            double y = from.Y + (to.Y - from.Y) * Math.Clamp(ratio, 0, 1);
            int velocity = Math.Clamp(
                (int)Math.Round(
                    ValueYToNormalized(
                        y,
                        ruler,
                        valueViewMinimum,
                        valueViewMaximum,
                        actualHeight) * 127,
                    MidpointRounding.AwayFromZero),
                1,
                127);
            edits[item.Id] = velocity;
        }
    }

    private void UpdateVelocityTrace(Point origin, Point point)
    {
        Point clamped = ClampVelocityTracePoint(point);
        if (_velocityButton == MouseButton.Right)
        {
            _velocityTracePoints.Clear();
            _velocityTracePoints.Add(ClampVelocityTracePoint(origin));
            _velocityTracePoints.Add(clamped);
            return;
        }

        if (_velocityTracePoints.Count == 0)
        {
            _velocityTracePoints.Add(ClampVelocityTracePoint(origin));
        }
        Point previous = _velocityTracePoints[^1];
        double deltaX = clamped.X - previous.X;
        double deltaY = clamped.Y - previous.Y;
        if (deltaX * deltaX + deltaY * deltaY >= 1)
        {
            _velocityTracePoints.Add(clamped);
        }
        else
        {
            _velocityTracePoints[^1] = clamped;
        }
    }

    private Point ClampVelocityTracePoint(Point point) => new(
        Math.Clamp(point.X, GetLaneHeaderWidth(), Math.Max(GetLaneHeaderWidth(), ActualWidth)),
        Math.Clamp(point.Y, GetRulerHeight(), Math.Max(GetRulerHeight(), ActualHeight)));

    private void CompleteVelocityTraceAsync(
        TimelineRenderSnapshot snapshot,
        TimelineViewport viewport,
        Point[] trace,
        bool restrictToSelection,
        TimelineSelectionSnapshot? selection,
        long gestureToken,
        TimelineToolMode tool)
    {
        CancellationToken cancellationToken = _pendingGestureCancellation.Token;
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        double valueViewMinimum = _valueViewMinimum;
        double valueViewMaximum = _valueViewMaximum;
        double actualHeight = ActualHeight;
        long selectionRevision = selection?.Revision ?? -1;
        _exactQueryPending = true;
        Cursor = Cursors.Wait;
        _ = Task.Run(
            () =>
            {
                Dictionary<MidoraId, int> edits = [];
                if (trace.Length == 1)
                {
                    ApplyVelocityTraceSegment(
                        snapshot,
                        selection,
                        restrictToSelection,
                        trace[0],
                        trace[0],
                        viewport,
                        header,
                        ruler,
                        valueViewMinimum,
                        valueViewMaximum,
                        actualHeight,
                        edits,
                        cancellationToken);
                }
                else
                {
                    for (int index = 1; index < trace.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ApplyVelocityTraceSegment(
                            snapshot,
                            selection,
                            restrictToSelection,
                            trace[index - 1],
                            trace[index],
                            viewport,
                            header,
                            ruler,
                            valueViewMinimum,
                            valueViewMaximum,
                            actualHeight,
                            edits,
                            cancellationToken);
                    }
                }
                return edits;
            },
            cancellationToken).ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                    () =>
                    {
                        if (task.IsFaulted)
                        {
                            System.Diagnostics.Trace.TraceError(
                                $"Timeline velocity trace query failed: {task.Exception}");
                        }
                        if (task.IsCanceled
                            || task.IsFaulted
                            || cancellationToken.IsCancellationRequested
                            || gestureToken != _gestureToken
                            || !ReferenceEquals(Snapshot, snapshot)
                            || Snapshot?.SemanticRevision != snapshot.SemanticRevision
                            || !string.Equals(
                                Snapshot?.ProjectionKey,
                                snapshot.ProjectionKey,
                                StringComparison.Ordinal)
                            || ToolMode != tool
                            || (SelectionSnapshot?.Revision ?? -1) != selectionRevision)
                        {
                            return;
                        }
                        _exactQueryPending = false;
                        Cursor = Cursors.Arrow;
                        if (task.Result.Count != 0)
                            VelocityEditCompleted?.Invoke(this, new(task.Result));
                        RefreshHoverIntent();
                        InvalidateVisual();
                    },
                    DispatcherPriority.Render);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private static double ValueYToNormalized(
        double y,
        double rulerHeight,
        double valueViewMinimum,
        double valueViewMaximum,
        double actualHeight)
    {
        double range = Math.Max(1d / 256, valueViewMaximum - valueViewMinimum);
        double contentHeight = Math.Max(1, actualHeight - rulerHeight);
        return Math.Clamp(
            valueViewMaximum - Math.Clamp((y - rulerHeight) / contentHeight, 0, 1) * range,
            0,
            1);
    }

    private void UpdateSingleVelocity(MidoraId id, double y, double rulerHeight)
    {
        int velocity = Math.Clamp(
            (int)Math.Round(ValueYToNormalized(y, rulerHeight) * 127, MidpointRounding.AwayFromZero),
            1,
            127);
        _velocityEdits[id] = velocity;
    }

    private bool TryHitVelocityBar(
        Point point,
        TimelineViewport viewport,
        out TimelineRenderItem item)
    {
        item = default;
        if (Snapshot is null) return false;
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        long tick = viewport.XToTick(point.X - header);
        double horizontalTolerance = TimelineVelocityTileRasterizer.MarkerSize / 2 + 2;
        long tolerance = TimelineTickMath.CeilingDistance(horizontalTolerance / viewport.PixelsPerTick);
        _visibleItems.Clear();
        if (!Snapshot.TryHitTestCached(tick, tolerance, 0, _visibleItems))
        {
            ScheduleExactPrefetch(
                Snapshot,
                Math.Max(0, tick - tolerance),
                tick > long.MaxValue - tolerance - 1 ? long.MaxValue : tick + tolerance + 1,
                0,
                1);
            return false;
        }
        _exactQueryPending = false;
        for (int index = 0; index < _visibleItems.Count; index++)
        {
            TimelineRenderItem candidate = _visibleItems[index];
            if (candidate.Kind != TimelineItemKind.Velocity)
            {
                continue;
            }
            double centerX = header + viewport.TickToX(candidate.StartTick);
            double value = _velocityEdits.TryGetValue(candidate.Id, out int edited)
                ? edited / 127d
                : Math.Clamp(candidate.Value, 1d / 127d, 1);
            double top = NormalizedToValueY(value, ruler);
            double bottom = NormalizedToValueY(0, ruler);
            if (Math.Abs(point.X - centerX) <= horizontalTolerance
                && point.Y >= top - 2
                && point.Y <= Math.Max(top + TimelineVelocityTileRasterizer.MarkerSize, bottom + 2))
            {
                item = candidate;
                return true;
            }
        }
        return false;
    }

    private void DrawCurve(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double left = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double right = laneHeaderWidth + viewport.TickToX(item.EndTick);
        double y1 = SurfaceMode == TimelineSurfaceMode.EventLanes
            ? NormalizedToValueY(item.Value, rulerHeight)
            : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
              + 4 + (1 - Math.Clamp(item.Value, 0, 1)) * Math.Max(1, LaneHeight - 8);
        double y2 = SurfaceMode == TimelineSurfaceMode.EventLanes
            ? NormalizedToValueY(item.SecondaryValue, rulerHeight)
            : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
              + 4 + (1 - Math.Clamp(item.SecondaryValue, 0, 1)) * Math.Max(1, LaneHeight - 8);
        if (item.Interpolation == CurveInterpolation.Step)
        {
            context.DrawLine(_infoPen, new Point(left, y1), new Point(right, y1));
            context.DrawLine(_infoPen, new Point(right, y1), new Point(right, y2));
        }
        else
        {
            context.DrawLine(_infoPen, new Point(left, y1), new Point(right, y2));
        }
    }

    private void DrawCurvePoint(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double x = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double y = SurfaceMode == TimelineSurfaceMode.EventLanes
            ? NormalizedToValueY(item.Value, rulerHeight)
            : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
              + 4 + (1 - Math.Clamp(item.Value, 0, 1)) * Math.Max(1, LaneHeight - 8);
        context.DrawEllipse(info, _borderPen, new Point(x, y), 4, 4);
        if (IsSelected(item))
        {
            context.DrawEllipse(null, IsPrimary(item) ? _textPen : _infoPen, new Point(x, y), 6, 6);
        }
    }

    private void DrawCursor(
        DrawingContext context,
        TimelineViewport viewport,
        long? tick,
        Pen pen,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (tick is not long value || value < viewport.StartTick || value >= viewport.EndTick)
        {
            return;
        }
        double x = laneHeaderWidth + Math.Round(viewport.TickToX(value)) + 0.5;
        context.DrawLine(
            pen,
            new Point(x, rulerHeight),
            new Point(x, rulerHeight + GetLaneContentHeight(viewport)));
    }

    private void DrawDragPreview(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (!_dragActivated || _dragItem is not TimelineRenderItem item)
        {
            return;
        }
        PrepareDragPreviewSelection(item);
        DragPreviewTransform transform = GetDragPreviewTransform(item);
        try
        {
            if (SurfaceMode == TimelineSurfaceMode.Arrangement
                && Snapshot is TimelineRenderSnapshot arrangementSnapshot
                && (uint)_dragCurrentLane < (uint)arrangementSnapshot.ArrangementLanes.Count
                && IsArrangementParentLane(arrangementSnapshot.ArrangementLanes[_dragCurrentLane].Kind))
            {
                return;
            }

            bool immediateVectorNotePreview = ShouldDrawImmediateVectorNotePreview(item);
            if (_dragKind is TimelineItemEditKind.ResizeStart or TimelineItemEditKind.ResizeEnd
                && SupportsFullSelectionDragPreview(item.Kind)
                && item.Kind != TimelineItemKind.Segment
                && !immediateVectorNotePreview)
            {
                DrawResizePreview(
                    context,
                    viewport,
                    item,
                    transform,
                    laneHeaderWidth,
                    rulerHeight);
                return;
            }
            if (SupportsFullSelectionDragPreview(item.Kind))
            {
                bool rasterPreviewDrawn = !immediateVectorNotePreview
                    && _dragKind == TimelineItemEditKind.Move
                    && (item.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote
                        ? TryDrawTranslatedPianoSelectionPreview(
                            context,
                            viewport,
                            transform,
                            laneHeaderWidth,
                            rulerHeight)
                        : IsEventPointKind(item.Kind)
                            && TryDrawTranslatedEventPointSelectionPreview(
                                context,
                                viewport,
                                transform,
                                laneHeaderWidth,
                                rulerHeight));
                if (rasterPreviewDrawn)
                {
                    DrawDragCopyMarker(
                        context,
                        viewport,
                        item,
                        transform,
                        laneHeaderWidth,
                        rulerHeight);
                    return;
                }
                StreamGeometry geometry = GetDragPreviewGeometry(
                    viewport,
                    item,
                    transform,
                    laneHeaderWidth,
                    rulerHeight);
                Rect contentBounds = new(
                    laneHeaderWidth,
                    rulerHeight,
                    Math.Max(0, ActualWidth - laneHeaderWidth),
                    Math.Max(0, ActualHeight - rulerHeight));
                context.PushClip(new RectangleGeometry(contentBounds));
                context.DrawGeometry(null, _dragPreviewPen ?? _infoPen, geometry);
                context.Pop();
                DrawDragCopyMarker(
                    context,
                    viewport,
                    item,
                    transform,
                    laneHeaderWidth,
                    rulerHeight);
                return;
            }

            if (!TryGetDragPreviewBounds(
                    item,
                    transform,
                    viewport,
                    laneHeaderWidth,
                    rulerHeight,
                    out Rect rectangle))
            {
                return;
            }
            context.DrawRoundedRectangle(null, _marqueePen, rectangle, 2, 2);
            DrawDragCopyMarker(
                context,
                viewport,
                item,
                transform,
                laneHeaderWidth,
                rulerHeight);
        }
        finally
        {
            DrawDragDeltaLabel(context, transform);
        }
    }

    private StreamGeometry GetDragPreviewGeometry(
        TimelineViewport viewport,
        TimelineRenderItem anchor,
        DragPreviewTransform transform,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            StreamGeometry empty = new();
            empty.Freeze();
            return empty;
        }

        DragPreviewGeometryKey key = new(
            snapshot.ContentFingerprint,
            _dragPreviewSelection?.Revision ?? -1,
            anchor.Id,
            anchor.Kind,
            _dragKind,
            transform.TickDelta,
            transform.LaneDelta,
            BitConverter.DoubleToInt64Bits(transform.ValueDelta),
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            BitConverter.DoubleToInt64Bits(viewport.PixelsPerTick),
            BitConverter.DoubleToInt64Bits(LaneHeight),
            BitConverter.DoubleToInt64Bits(_valueViewMinimum),
            BitConverter.DoubleToInt64Bits(_valueViewMaximum),
            BitConverter.DoubleToInt64Bits(laneHeaderWidth),
            BitConverter.DoubleToInt64Bits(rulerHeight),
            BitConverter.DoubleToInt64Bits(ActualWidth),
            BitConverter.DoubleToInt64Bits(ActualHeight));
        if (_dragPreviewGeometry is not null && _dragPreviewGeometryKey == key)
        {
            return _dragPreviewGeometry;
        }

        bool itemsReady = QueryDragPreviewItems(snapshot, viewport, anchor, transform);
        if (!_dragPreviewItems.Any(candidate => candidate.Id == anchor.Id))
        {
            _dragPreviewItems.Add(anchor);
        }
        StreamGeometry geometry = new();
        using (StreamGeometryContext geometryContext = geometry.Open())
        {
            foreach (TimelineRenderItem candidate in _dragPreviewItems)
            {
                if (!IsDragPreviewSelectionMember(candidate, anchor)
                    || !TryGetDragPreviewBounds(
                        candidate,
                        transform,
                        viewport,
                        laneHeaderWidth,
                        rulerHeight,
                        out Rect bounds))
                {
                    continue;
                }
                AppendRectangle(geometryContext, bounds);
            }
        }
        geometry.Freeze();
        if (itemsReady)
        {
            _dragPreviewGeometry = geometry;
            _dragPreviewGeometryKey = key;
        }
        return geometry;
    }

    private void DrawResizePreview(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem anchor,
        DragPreviewTransform transform,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double devicePixelsPerTick = viewport.PixelsPerTick * dpi.DpiScaleX;
        double contentWidthDevice = Math.Max(1, (ActualWidth - laneHeaderWidth) * dpi.DpiScaleX);
        double contentHeightDevice = Math.Max(1, (ActualHeight - rulerHeight) * dpi.DpiScaleY);
        int laneCount = SurfaceMode == TimelineSurfaceMode.PianoRoll
            ? 128
            : snapshot.LaneLabels.Count;
        ResizeLaneLayoutKey laneLayoutKey = new(
            snapshot.ProjectionKey,
            SurfaceMode,
            viewport.FirstLane,
            laneCount,
            BitConverter.DoubleToInt64Bits(LaneHeight),
            BitConverter.DoubleToInt64Bits(rulerHeight),
            checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero)));
        if (_resizeLaneLayoutKey != laneLayoutKey)
        {
            _resizeLaneTopsDevice = new double[laneCount];
            _resizeLaneHeightsDevice = new double[laneCount];
            for (int lane = 0; lane < laneCount; lane++)
            {
                if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
                {
                    _resizeLaneTopsDevice[lane] =
                        (lane - viewport.FirstLane) * LaneHeight * dpi.DpiScaleY;
                    _resizeLaneHeightsDevice[lane] = LaneHeight * dpi.DpiScaleY;
                }
                else
                {
                    _resizeLaneTopsDevice[lane] =
                        (GetLaneTop(viewport, lane, rulerHeight) - rulerHeight + 2)
                        * dpi.DpiScaleY;
                    _resizeLaneHeightsDevice[lane] =
                        Math.Max(1, (GetLaneVisualHeight(lane) - 4) * dpi.DpiScaleY);
                }
            }
            _resizeLaneLayoutKey = laneLayoutKey;
        }
        double[] laneTops = _resizeLaneTopsDevice;
        double[] laneHeights = _resizeLaneHeightsDevice;

        TimelineResizeEdge edge = _dragKind == TimelineItemEditKind.ResizeStart
            ? TimelineResizeEdge.Start
            : TimelineResizeEdge.End;
        TimelineSelectionSnapshot? previewSelection = _dragPreviewSelection;
        long selectionRevision = previewSelection?.Revision ?? -1;
        long minimumLength = Math.Max(1, OperationStepTicks);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        ResizePreviewSignature signature = new(
            snapshot.SemanticRevision,
            snapshot.ProjectionKey,
            snapshot.ContentFingerprint,
            selectionRevision,
            anchor.Id,
            edge,
            transform.TickDelta,
            minimumLength,
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            BitConverter.DoubleToInt64Bits(devicePixelsPerTick),
            BitConverter.DoubleToInt64Bits(LaneHeight),
            checked((int)Math.Ceiling(contentWidthDevice / TimelineResizePreviewRasterizer.TileSize)),
            checked((int)Math.Ceiling(contentHeightDevice / TimelineResizePreviewRasterizer.TileSize)),
            dpiX,
            dpiY);
        if (_resizePreviewSignature != signature)
        {
            _resizePreviewCancellation.Cancel();
            _resizePreviewCancellation.Dispose();
            _resizePreviewCancellation = new();
            _requestedResizePreviewKeys.Clear();
            _resizePreviewSignature = signature;
            _resizePreviewGeneration = checked(_resizePreviewGeneration + 1);
        }

        Color color = GetDragPreviewRasterColor();
        ulong contentFingerprint = TimelineContentFingerprint.Combine(
            snapshot.ContentFingerprint,
            unchecked((ulong)selectionRevision));
        contentFingerprint = TimelineContentFingerprint.Combine(
            contentFingerprint,
            unchecked((ulong)transform.TickDelta));
        contentFingerprint = TimelineContentFingerprint.Combine(
            contentFingerprint,
            unchecked((ulong)viewport.StartTick));

        _resizePreviewTiles.Clear();
        bool complete = true;
        for (int tileY = 0; tileY < signature.TileRows; tileY++)
        {
            for (int tileX = 0; tileX < signature.TileColumns; tileX++)
            {
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.ResizePreview,
                    snapshot.ProjectionKey,
                    contentFingerprint,
                    BitConverter.DoubleToInt64Bits(devicePixelsPerTick),
                    BitConverter.DoubleToInt64Bits(LaneHeight * dpi.DpiScaleY),
                    tileX,
                    tileY,
                    0,
                    0,
                    ColorToArgb(color),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap)
                    && bitmap is not null)
                {
                    _resizePreviewTiles.Add(new(key, bitmap));
                    continue;
                }
                complete = false;
                int requestTileX = tileX;
                int requestTileY = tileY;
                RequestResizePreviewRaster(
                    key,
                    cancellationToken => TimelineResizePreviewRasterizer.Rasterize(
                        snapshot,
                        previewSelection,
                        anchor.Id,
                        anchor.Kind,
                        edge,
                        transform.TickDelta,
                        minimumLength,
                        viewport.StartTick,
                        devicePixelsPerTick,
                        laneTops,
                        laneHeights,
                        requestTileX,
                        requestTileY,
                        color,
                        cancellationToken));
            }
        }

        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        context.PushClip(new RectangleGeometry(contentBounds));
        DrawResizeTiles(context, _resizePreviewTiles, laneHeaderWidth, rulerHeight, dpi);
        if (!complete)
        {
            if (TryGetDragPreviewBounds(
                    anchor,
                    transform,
                    viewport,
                    laneHeaderWidth,
                    rulerHeight,
                    out Rect bounds))
            {
                context.DrawRectangle(null, _dragPreviewPen ?? _infoPen, bounds);
            }
        }
        context.Pop();
        _resizePreviewTiles.Clear();
    }

    private void RequestResizePreviewRaster(
        TimelineRasterCacheKey key,
        Func<CancellationToken, TimelineRasterBuffer> factory)
    {
        if (_backgroundWorkSuspended) return;
        if (!_requestedResizePreviewKeys.Add(key)) return;
        long generation = _resizePreviewGeneration;
        bool accepted = TimelineRasterCache.Shared.Request(
            key,
            factory,
            Dispatcher,
            () =>
            {
                _requestedResizePreviewKeys.Remove(key);
                if (!_backgroundWorkSuspended
                    && generation == _resizePreviewGeneration)
                    QueueRasterInvalidation();
            },
            _resizePreviewCancellation.Token,
            TimelineRasterRequestPriority.Visible,
            consumerId: _rasterConsumerId);
        if (!accepted) _requestedResizePreviewKeys.Remove(key);
    }

    private static void DrawResizeTiles(
        DrawingContext context,
        IReadOnlyList<PianoTileDrawEntry> entries,
        double laneHeaderWidth,
        double rulerHeight,
        DpiScale dpi)
    {
        foreach (PianoTileDrawEntry entry in entries)
        {
            double left = laneHeaderWidth
                + entry.Key.TileX * TimelineResizePreviewRasterizer.TileSize / dpi.DpiScaleX;
            double top = rulerHeight
                + entry.Key.TileY * TimelineResizePreviewRasterizer.TileSize / dpi.DpiScaleY;
            Rect core = new(
                left,
                top,
                TimelineResizePreviewRasterizer.TileSize / dpi.DpiScaleX,
                TimelineResizePreviewRasterizer.TileSize / dpi.DpiScaleY);
            Rect destination = new(
                left - TimelineResizePreviewRasterizer.Gutter / dpi.DpiScaleX,
                top - TimelineResizePreviewRasterizer.Gutter / dpi.DpiScaleY,
                TimelineResizePreviewRasterizer.RasterSize / dpi.DpiScaleX,
                TimelineResizePreviewRasterizer.RasterSize / dpi.DpiScaleY);
            context.PushClip(new RectangleGeometry(core));
            context.DrawImage(entry.Bitmap, destination);
            context.Pop();
        }
    }

    private void PrefetchPianoDragPreviewTiles(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot selection,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long firstVisibleTileX,
        long lastVisibleTileX,
        long firstVisibleTileY,
        long lastVisibleTileY,
        int dpiX,
        int dpiY)
    {
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerTick);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerLane);
        Color fillColor = Colors.Transparent;
        Color outlineColor = GetDragPreviewRasterColor();
        if (!TryGetPianoContentBounds(
                snapshot,
                devicePixelsPerTick,
                out _,
                out _,
                out long firstContentTileX,
                out long lastContentTileX))
        {
            return;
        }
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(firstContentTileX, firstVisibleTileX - ring);
            long lastX = Math.Min(lastContentTileX, lastVisibleTileX + ring);
            if (lastX < firstX) continue;
            long firstY = Math.Max(0, firstVisibleTileY - ring);
            long lastY = Math.Max(firstY, lastVisibleTileY + ring);
            foreach (long tileY in TimelineTickMath.InclusiveIndices(firstY, lastY))
            {
                foreach (long tileX in TimelineTickMath.InclusiveIndices(firstX, lastX))
                {
                    bool visible = tileX >= firstVisibleTileX && tileX <= lastVisibleTileX
                        && tileY >= firstVisibleTileY && tileY <= lastVisibleTileY;
                    if (ring == 1 && visible)
                    {
                        continue;
                    }
                    long requestTileX = tileX;
                    long requestTileY = tileY;
                    TileFingerprintRequestKey fingerprintKey = new(
                        TimelineRasterLayer.PianoDragPreview,
                        snapshot.SemanticRevision,
                        snapshot.ProjectionKey,
                        selection.Revision,
                        horizontalScaleKey,
                        verticalScaleKey,
                        tileX,
                        tileY,
                        dpiX,
                        dpiY);
                    if (!TryGetPreparedTileFingerprint(
                            fingerprintKey,
                            snapshot,
                            () => TimelineContentFingerprint.WithSelection(
                                snapshot.GetPianoTileContentFingerprint(
                                    devicePixelsPerTick,
                                    devicePixelsPerLane,
                                    requestTileX,
                                    requestTileY),
                                selection.Revision),
                            visible && snapshot.CanComputeTileFingerprintSynchronously,
                            out ulong contentFingerprint))
                    {
                        continue;
                    }
                    TimelineRasterCacheKey key = new(
                        TimelineRasterLayer.PianoDragPreview,
                        snapshot.ProjectionKey,
                        contentFingerprint,
                        horizontalScaleKey,
                        verticalScaleKey,
                        tileX,
                        tileY,
                        ColorToArgb(fillColor),
                        ColorToArgb(fillColor),
                        ColorToArgb(outlineColor),
                        dpiX,
                        dpiY);
                    if (TimelineRasterCache.Shared.TryGet(key, out _))
                    {
                        continue;
                    }
                    RequestRaster(
                        key,
                        cancellationToken => TimelinePianoTileRasterizer.Rasterize(
                            snapshot,
                            devicePixelsPerTick,
                            devicePixelsPerLane,
                            requestTileX,
                            requestTileY,
                            fillColor,
                            fillColor,
                            selection,
                            selectionOnly: true,
                            outlineColor: outlineColor,
                            cancellationToken: cancellationToken));
                }
            }
        }
    }

    private Color GetDragPreviewRasterColor()
    {
        Brush brush = Brush("Brush.Info", Color.FromRgb(98, 166, 246));
        Color color = GetSolidColor(brush, Color.FromRgb(98, 166, 246));
        return Color.FromArgb(
            checked((byte)Math.Round(
                color.A * brush.Opacity * 0.88,
                MidpointRounding.AwayFromZero)),
            color.R,
            color.G,
            color.B);
    }

    private bool TryDrawTranslatedPianoSelectionPreview(
        DrawingContext context,
        TimelineViewport viewport,
        DragPreviewTransform transform,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_dragPreviewSelection is not TimelineSelectionSnapshot selection
            || SelectionSnapshot?.Revision != selection.Revision
            || Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return false;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double devicePixelsPerTick = viewport.PixelsPerTick * dpi.DpiScaleX;
        double devicePixelsPerLane = LaneHeight * dpi.DpiScaleY;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerTick);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerLane);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        Color fillColor = Colors.Transparent;
        Color outlineColor = GetDragPreviewRasterColor();

        long sourceStartTick = SaturatingSubtractTick(viewport.StartTick, transform.TickDelta);
        long sourceEndTick = SaturatingSubtractTick(viewport.EndTick, transform.TickDelta);
        int sourceFirstLane = checked((int)Math.Clamp(
            (long)viewport.FirstLane - transform.LaneDelta,
            0,
            128));
        int sourceLastLaneExclusive = checked((int)Math.Clamp(
            (long)viewport.LastLaneExclusive - transform.LaneDelta,
            0,
            128));
        if (sourceStartTick >= long.MaxValue
            || sourceEndTick <= sourceStartTick
            || sourceLastLaneExclusive <= sourceFirstLane)
        {
            return false;
        }

        long firstTileX = Math.Max(0, FloorToLong(
            sourceStartTick * devicePixelsPerTick / TimelinePianoTileRasterizer.TileSize));
        long lastTileX = Math.Max(firstTileX, FloorToLong(
            Math.Max(sourceStartTick, sourceEndTick - 1) * devicePixelsPerTick
            / TimelinePianoTileRasterizer.TileSize));
        if (!TryGetPianoContentBounds(
                snapshot,
                devicePixelsPerTick,
                out long contentStartTick,
                out long contentEndTick,
                out long firstContentTileX,
                out long lastContentTileX)
            || sourceEndTick <= contentStartTick
            || sourceStartTick >= contentEndTick)
        {
            return false;
        }
        firstTileX = Math.Max(firstTileX, firstContentTileX);
        lastTileX = Math.Min(lastTileX, lastContentTileX);
        if (lastTileX < firstTileX)
        {
            return false;
        }
        long firstTileY = Math.Max(0, FloorToLong(
            sourceFirstLane * devicePixelsPerLane / TimelinePianoTileRasterizer.TileSize));
        long lastTileY = Math.Max(firstTileY, FloorToLong(
            Math.Max(sourceFirstLane, sourceLastLaneExclusive - 1) * devicePixelsPerLane
            / TimelinePianoTileRasterizer.TileSize));

        _pianoTileFallbackEntries.Clear();
        foreach (long tileY in TimelineTickMath.InclusiveIndices(firstTileY, lastTileY))
        {
            foreach (long tileX in TimelineTickMath.InclusiveIndices(firstTileX, lastTileX))
            {
                long requestTileX = tileX;
                long requestTileY = tileY;
                TileFingerprintRequestKey fingerprintKey = new(
                    TimelineRasterLayer.PianoDragPreview,
                    snapshot.SemanticRevision,
                    snapshot.ProjectionKey,
                    selection.Revision,
                    horizontalScaleKey,
                    verticalScaleKey,
                    tileX,
                    tileY,
                    dpiX,
                    dpiY);
                if (!TryGetPreparedTileFingerprint(
                        fingerprintKey,
                        snapshot,
                        () => TimelineContentFingerprint.WithSelection(
                            snapshot.GetPianoTileContentFingerprint(
                                devicePixelsPerTick,
                                devicePixelsPerLane,
                                requestTileX,
                                requestTileY),
                            selection.Revision),
                        snapshot.CanComputeTileFingerprintSynchronously,
                        out ulong contentFingerprint))
                {
                    continue;
                }
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.PianoDragPreview,
                    snapshot.ProjectionKey,
                    contentFingerprint,
                    horizontalScaleKey,
                    verticalScaleKey,
                    tileX,
                    tileY,
                    ColorToArgb(fillColor),
                    ColorToArgb(fillColor),
                    ColorToArgb(outlineColor),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                {
                    if (bitmap is not null)
                    {
                        _pianoTileFallbackEntries.Add(new(key, bitmap));
                    }
                    continue;
                }
                RequestRaster(
                    key,
                    cancellationToken => TimelinePianoTileRasterizer.Rasterize(
                        snapshot,
                        devicePixelsPerTick,
                        devicePixelsPerLane,
                        requestTileX,
                        requestTileY,
                        fillColor,
                        fillColor,
                        selection,
                        selectionOnly: true,
                        outlineColor: outlineColor,
                        cancellationToken: cancellationToken));
            }
        }
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            GetLaneContentHeight(viewport));
        context.PushClip(new RectangleGeometry(contentBounds));
        context.PushTransform(new TranslateTransform(
            transform.TickDelta * viewport.PixelsPerTick,
            transform.LaneDelta * LaneHeight));
        foreach (PianoTileDrawEntry entry in _pianoTileFallbackEntries)
        {
            DrawPianoTile(
                context,
                viewport,
                entry,
                devicePixelsPerTick,
                devicePixelsPerLane,
                laneHeaderWidth,
                rulerHeight);
        }
        context.Pop();
        context.Pop();
        _pianoTileFallbackEntries.Clear();
        return true;
    }

    private bool TryDrawTranslatedEventPointSelectionPreview(
        DrawingContext context,
        TimelineViewport viewport,
        DragPreviewTransform transform,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_dragPreviewSelection is not TimelineSelectionSnapshot selection
            || SelectionSnapshot?.Revision != selection.Revision
            || Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return false;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        double rasterDpiScaleX = dpiX / 1024d;
        double rasterDpiScaleY = dpiY / 1024d;
        double devicePixelsPerTick = viewport.PixelsPerTick * dpi.DpiScaleX;
        double valueRange = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        double devicePixelsPerValue = contentHeight * dpi.DpiScaleY / valueRange;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerTick);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(devicePixelsPerValue);

        long sourceStartTick = SaturatingSubtractTick(viewport.StartTick, transform.TickDelta);
        long sourceEndTick = SaturatingSubtractTick(viewport.EndTick, transform.TickDelta);
        if (sourceStartTick >= long.MaxValue || sourceEndTick <= sourceStartTick)
        {
            return false;
        }
        double sourceMinimum = Math.Clamp(_valueViewMinimum - transform.ValueDelta, 0, 1);
        double sourceMaximum = Math.Clamp(_valueViewMaximum - transform.ValueDelta, 0, 1);
        if (sourceMaximum <= sourceMinimum)
        {
            return false;
        }

        long firstTileX = Math.Max(0, FloorToLong(
            sourceStartTick * devicePixelsPerTick / TimelineEventPointTileRasterizer.TileSize));
        long lastTileX = Math.Max(firstTileX, FloorToLong(
            Math.Max(sourceStartTick, sourceEndTick - 1) * devicePixelsPerTick
            / TimelineEventPointTileRasterizer.TileSize));
        double sourceWorldTop = (1 - sourceMaximum) * devicePixelsPerValue;
        double sourceWorldBottom = (1 - sourceMinimum) * devicePixelsPerValue;
        long firstTileY = Math.Max(0, FloorToLong(
            sourceWorldTop / TimelineEventPointTileRasterizer.TileSize));
        long lastTileY = Math.Max(firstTileY, FloorToLong(
            Math.BitDecrement(sourceWorldBottom) / TimelineEventPointTileRasterizer.TileSize));
        ulong contentFingerprint = TimelineContentFingerprint.WithSelection(
            snapshot.ContentFingerprint,
            selection.Revision);
        Color normalColor = GetSolidColor(
            Brush("Brush.Info", Color.FromRgb(98, 166, 246)),
            Color.FromRgb(98, 166, 246));
        Color primaryColor = GetSolidColor(
            Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245)),
            Color.FromRgb(241, 243, 245));
        Color borderColor = GetSolidColor(
            Brush("Brush.Border", Color.FromRgb(42, 48, 58)),
            Color.FromRgb(42, 48, 58));

        _dragPreviewEventPointTiles.Clear();
        foreach (long tileY in TimelineTickMath.InclusiveIndices(firstTileY, lastTileY))
        {
            foreach (long tileX in TimelineTickMath.InclusiveIndices(firstTileX, lastTileX))
            {
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.EventPointSelection,
                    snapshot.ProjectionKey,
                    contentFingerprint,
                    horizontalScaleKey,
                    verticalScaleKey,
                    tileX,
                    tileY,
                    ColorToArgb(normalColor),
                    ColorToArgb(primaryColor),
                    ColorToArgb(borderColor),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                {
                    if (bitmap is not null)
                    {
                        _dragPreviewEventPointTiles.Add(new(key, bitmap));
                    }
                    continue;
                }
                long requestTileX = tileX;
                long requestTileY = tileY;
                RequestRaster(
                    key,
                    cancellationToken => TimelineEventPointTileRasterizer.Rasterize(
                        snapshot,
                        selection,
                        devicePixelsPerTick,
                        devicePixelsPerValue,
                        requestTileX,
                        requestTileY,
                        rasterDpiScaleX,
                        rasterDpiScaleY,
                        normalColor,
                        primaryColor,
                        borderColor,
                        selectionOnly: true,
                        cancellationToken: cancellationToken));
            }
        }
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        context.PushClip(new RectangleGeometry(contentBounds));
        context.PushTransform(new TranslateTransform(
            transform.TickDelta * viewport.PixelsPerTick,
            -transform.ValueDelta / valueRange * contentHeight));
        foreach (EventPointTileDrawEntry entry in _dragPreviewEventPointTiles)
        {
            DrawEventPointTile(
                context,
                viewport,
                entry,
                devicePixelsPerTick,
                devicePixelsPerValue,
                laneHeaderWidth,
                rulerHeight,
                rasterDpiScaleX,
                rasterDpiScaleY);
        }
        context.Pop();
        context.Pop();
        _dragPreviewEventPointTiles.Clear();
        return true;
    }

    private bool QueryDragPreviewItems(
        TimelineRenderSnapshot snapshot,
        TimelineViewport viewport,
        TimelineRenderItem anchor,
        DragPreviewTransform transform)
    {
        if (ShouldDrawImmediateVectorNotePreview(anchor))
        {
            _dragPreviewItems.Clear();
            if (_dragPreviewSelection is null)
            {
                return true;
            }
            _dragPreviewQueryScratch.Clear();
            if (!snapshot.TryQueryByIdsCached(
                    _dragPreviewSelection.IdSet,
                    _dragPreviewQueryScratch))
            {
                ScheduleDragPreviewIdPrefetch(snapshot, _dragPreviewSelection);
                return false;
            }
            _dragPreviewItems.AddRange(_dragPreviewQueryScratch);
            return true;
        }

        long queryStart = viewport.StartTick;
        long queryEnd = viewport.EndTick;
        if (_dragKind == TimelineItemEditKind.Move)
        {
            queryStart = SaturatingSubtractTick(viewport.StartTick, transform.TickDelta);
            queryEnd = SaturatingSubtractTick(viewport.EndTick, transform.TickDelta);
        }
        else if (_dragKind == TimelineItemEditKind.ResizeEnd && transform.TickDelta > 0)
        {
            queryStart = SaturatingSubtractTick(viewport.StartTick, transform.TickDelta);
        }
        else if (_dragKind == TimelineItemEditKind.ResizeStart && transform.TickDelta < 0)
        {
            queryEnd = SaturatingSubtractTick(viewport.EndTick, transform.TickDelta);
        }

        int queryFirstLane;
        int queryLastLane;
        if (IsEventPointKind(anchor.Kind))
        {
            queryFirstLane = 0;
            queryLastLane = 1;
        }
        else if (_dragKind == TimelineItemEditKind.Move)
        {
            queryFirstLane = Math.Clamp(
                checked(viewport.FirstLane - transform.LaneDelta),
                0,
                128);
            queryLastLane = Math.Clamp(
                checked(viewport.LastLaneExclusive - transform.LaneDelta),
                0,
                128);
        }
        else
        {
            queryFirstLane = Math.Clamp(viewport.FirstLane, 0, 128);
            queryLastLane = Math.Clamp(viewport.LastLaneExclusive, 0, 128);
        }

        if (queryStart >= long.MaxValue
            || queryEnd <= queryStart
            || queryLastLane <= queryFirstLane)
        {
            _dragPreviewItems.Clear();
            return true;
        }
        _dragPreviewQueryScratch.Clear();
        if (!snapshot.TryQueryIntoCached(
            queryStart,
            queryEnd,
            queryFirstLane,
            queryLastLane,
            _dragPreviewQueryScratch))
        {
            _dragPreviewItems.Clear();
            ScheduleExactPrefetch(
                snapshot,
                queryStart,
                queryEnd,
                queryFirstLane,
                queryLastLane);
            return false;
        }
        _dragPreviewItems.Clear();
        _dragPreviewItems.AddRange(_dragPreviewQueryScratch);
        return true;
    }

    private void ScheduleDragPreviewIdPrefetch(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot selection)
    {
        DragPreviewIdPrefetchSignature signature = new(snapshot, selection.Revision);
        if (_backgroundWorkSuspended || _dragPreviewIdPrefetchSignature == signature)
        {
            return;
        }

        _dragPreviewIdPrefetchSignature = signature;
        CancellationToken cancellationToken = _pendingGestureCancellation.Token;
        IReadOnlySet<MidoraId> ids = selection.IdSet;
        _ = Task.Run(
            () => snapshot.PrefetchIds(ids, cancellationToken),
            cancellationToken).ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        System.Diagnostics.Trace.TraceError(
                            $"Timeline drag-preview ID prefetch failed: {task.Exception}");
                    }
                    if (task.IsCanceled
                        || task.IsFaulted
                        || Dispatcher.HasShutdownStarted)
                    {
                        return;
                    }
                    CancelablePresentationDispatch.Post(Dispatcher, cancellationToken,
                        () =>
                        {
                            if (_backgroundWorkSuspended
                                || cancellationToken.IsCancellationRequested
                                || !_dragActivated
                                || !ReferenceEquals(Snapshot, snapshot)
                                || SelectionSnapshot?.Revision != selection.Revision
                                || _dragPreviewIdPrefetchSignature != signature)
                            {
                                return;
                            }
                            InvalidateDragPreviewGeometry();
                            InvalidateVisual();
                        },
                        DispatcherPriority.Render);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private bool TryGetDragPreviewBounds(
        TimelineRenderItem item,
        DragPreviewTransform transform,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight,
        out Rect bounds)
    {
        if (IsEventPointKind(item.Kind))
        {
            long pointTick = SaturatingAddTick(item.StartTick, transform.TickDelta);
            double normalized = Math.Clamp(item.Value + transform.ValueDelta, 0, 1);
            Point center = new(
                laneHeaderWidth + viewport.TickToX(pointTick),
                NormalizedToValueY(normalized, rulerHeight));
            bounds = new Rect(center.X - 5, center.Y - 5, 10, 10);
            return bounds.Right >= laneHeaderWidth
                && bounds.Left <= ActualWidth
                && bounds.Bottom >= rulerHeight
                && bounds.Top <= ActualHeight;
        }

        long start = item.StartTick;
        long end = item.EndTick;
        int lane = item.Lane;
        switch (_dragKind)
        {
            case TimelineItemEditKind.Move:
                start = SaturatingAddTick(start, transform.TickDelta);
                end = SaturatingAddTick(start, item.Length);
                lane = checked(lane + transform.LaneDelta);
                int laneCount = SurfaceMode == TimelineSurfaceMode.PianoRoll
                    ? 128
                    : Snapshot?.LaneLabels.Count ?? int.MaxValue;
                if (lane < 0 || lane >= laneCount)
                {
                    bounds = Rect.Empty;
                    return false;
                }
                break;
            case TimelineItemEditKind.ResizeStart:
                long startMinimumLength = TimelineToolPolicy.ResolveResizeMinimumLength(
                    Math.Max(1, item.Length),
                    Math.Max(1, OperationStepTicks));
                start = Math.Clamp(
                    SaturatingAddTick(start, transform.TickDelta),
                    0,
                    end - startMinimumLength);
                break;
            case TimelineItemEditKind.ResizeEnd:
                long endMinimumLength = TimelineToolPolicy.ResolveResizeMinimumLength(
                    Math.Max(1, item.Length),
                    Math.Max(1, OperationStepTicks));
                end = Math.Max(
                    start + endMinimumLength,
                    SaturatingAddTick(end, transform.TickDelta));
                break;
        }

        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double laneHeight = GetLaneVisualHeight(lane);
        double top = GetLaneTop(viewport, lane, rulerHeight) + 2;
        bounds = new(left, top, Math.Max(1, right - left), Math.Max(3, laneHeight - 4));
        return bounds.Right >= laneHeaderWidth
            && bounds.Left <= ActualWidth
            && bounds.Bottom >= rulerHeight
            && bounds.Top <= ActualHeight;
    }

    private DragPreviewTransform GetDragPreviewTransform(TimelineRenderItem anchor)
    {
        long rawTickDelta = _dragTimeLocked
            ? 0
            : checked(_dragCurrentTick - _dragOriginTick);
        // The raw pointer can pass the domain before delta snapping chooses a
        // perfectly representable result (including zero). Only the *snapped*
        // musical result is an edit boundary, not this lookup coordinate.
        long snapTarget = TimelineTickMath.Clamp((Int128)(_dragKind == TimelineItemEditKind.ResizeEnd
            ? anchor.EndTick : anchor.StartTick) + rawTickDelta);
        long tickDelta = SnapOperationDelta(rawTickDelta, snapTarget);
        if (_dragKind is TimelineItemEditKind.Move or TimelineItemEditKind.ResizeStart)
        {
            tickDelta = Math.Max(tickDelta, -_dragPreviewMinimumStartTick);
        }
        if (_dragKind is TimelineItemEditKind.Move or TimelineItemEditKind.ResizeEnd)
            _ = checked(anchor.EndTick + Math.Max(0, tickDelta));

        int laneDelta = checked(_dragCurrentLane - _dragOriginLane);
        if (_dragCopyRequested
            && anchor.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote)
        {
            laneDelta = Math.Clamp(
                laneDelta,
                -_dragPreviewMinimumLane,
                127 - _dragPreviewMaximumLane);
        }

        double valueDelta = 0;
        if (CanDragEventValue && IsValueEditableEventPointKind(anchor.Kind))
        {
            Point current = _hoverPoint ?? _dragOrigin;
            double requested = GetDragNormalizedValueDelta(current.Y);
            double quantizedAnchor = QuantizeNormalizedValue(anchor.Value + requested);
            requested = quantizedAnchor - anchor.Value;
            valueDelta = Math.Clamp(
                requested,
                -_dragPreviewMinimumValue,
                1 - _dragPreviewMaximumValue);
        }
        return new(tickDelta, laneDelta, valueDelta);
    }

    private double QuantizeNormalizedValue(double normalized)
    {
        normalized = Math.Clamp(normalized, 0, 1);
        if (!ValueAxisIntegral
            || !double.IsFinite(ValueAxisMinimum)
            || !double.IsFinite(ValueAxisMaximum)
            || ValueAxisMaximum <= ValueAxisMinimum)
        {
            return normalized;
        }
        double value = ValueAxisMinimum
            + normalized * (ValueAxisMaximum - ValueAxisMinimum);
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Clamp(
            (rounded - ValueAxisMinimum) / (ValueAxisMaximum - ValueAxisMinimum),
            0,
            1);
    }

    private void DrawDragCopyMarker(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem anchor,
        DragPreviewTransform transform,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (!_dragCopyRequested
            || !TryGetDragPreviewBounds(
                anchor,
                transform,
                viewport,
                laneHeaderWidth,
                rulerHeight,
                out Rect bounds))
        {
            return;
        }
        FormattedText copyMarker = GetFormattedText(
            "+",
            Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245)),
            12,
            FontWeights.Bold);
        Point marker = IsEventPointKind(anchor.Kind)
            ? new Point(bounds.Right, bounds.Top - 5)
            : new Point(bounds.Left + 4, bounds.Top + 1);
        context.DrawText(copyMarker, marker);
    }

    private void PrepareDragPreviewSelection(TimelineRenderItem anchor)
    {
        if (_dragPreviewSelectionPrepared)
        {
            return;
        }
        _dragPreviewSelectionPrepared = true;
        _dragPreviewSelectionRevision = SelectionSnapshot?.Revision ?? -1;
        _dragPreviewSelection = SupportsFullSelectionDragPreview(anchor.Kind)
            && SelectionSnapshot?.Contains(anchor.Id) == true
                ? SelectionSnapshot
                : null;

        _dragPreviewMinimumStartTick = anchor.StartTick;
        _dragPreviewMinimumLane = anchor.Lane;
        _dragPreviewMaximumLane = anchor.Lane;
        _dragPreviewMinimumValue = anchor.Value;
        _dragPreviewMaximumValue = anchor.Value;
        if (_dragPreviewSelection is null)
        {
            return;
        }
        if (_dragPreviewSelection.TryGetMetrics(
                anchor.Kind,
                out TimelineSelectionMetrics metrics))
        {
            _dragPreviewMinimumStartTick = metrics.MinimumStartTick;
            _dragPreviewMinimumLane = metrics.MinimumLane;
            _dragPreviewMaximumLane = metrics.MaximumLane;
            _dragPreviewMinimumValue = metrics.MinimumValue;
            _dragPreviewMaximumValue = metrics.MaximumValue;
            return;
        }
        _dragPreviewSelection = null;
    }

    private void TryPromotePreparedDragPreviewSelection(
        TimelineSelectionSnapshot? selection)
    {
        if (!_dragPreviewSelectionPrepared
            || _dragItem is not TimelineRenderItem anchor
            || selection is null
            || selection.Revision != _dragPreviewSelectionRevision
            || !SupportsFullSelectionDragPreview(anchor.Kind)
            || !selection.Contains(anchor.Id)
            || !selection.TryGetMetrics(anchor.Kind, out TimelineSelectionMetrics metrics))
        {
            return;
        }

        _dragPreviewSelection = selection;
        _dragPreviewMinimumStartTick = metrics.MinimumStartTick;
        _dragPreviewMinimumLane = metrics.MinimumLane;
        _dragPreviewMaximumLane = metrics.MaximumLane;
        _dragPreviewMinimumValue = metrics.MinimumValue;
        _dragPreviewMaximumValue = metrics.MaximumValue;
        InvalidateDragPreviewGeometry();
        InvalidateVisual();
    }

    private bool IsDragPreviewSelectionMember(
        TimelineRenderItem candidate,
        TimelineRenderItem anchor) =>
        candidate.Kind == anchor.Kind
        && (_dragPreviewSelection?.Contains(candidate.Id) ?? candidate.Id == anchor.Id);

    private bool ShouldDrawImmediateVectorNotePreview(TimelineRenderItem anchor)
    {
        if (anchor.Kind is not (TimelineItemKind.LogicalNote
            or TimelineItemKind.DirectMidiNote
            or TimelineItemKind.TemplateNote))
        {
            return false;
        }
        return _dragPreviewSelection is null
            || _dragPreviewSelection.Count <= MaximumImmediateVectorNotePreviewCount;
    }

    private static bool SupportsFullSelectionDragPreview(TimelineItemKind kind) =>
        kind is TimelineItemKind.Segment
            or TimelineItemKind.LogicalNote
            or TimelineItemKind.DirectMidiNote
            or TimelineItemKind.TemplateNote
            or TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
            or TimelineItemKind.DirectMidiEvent
            or TimelineItemKind.OpaqueMidiEvent;

    private static void AppendRectangle(StreamGeometryContext context, Rect bounds)
    {
        context.BeginFigure(bounds.TopLeft, isFilled: false, isClosed: true);
        context.LineTo(bounds.TopRight, isStroked: true, isSmoothJoin: false);
        context.LineTo(bounds.BottomRight, isStroked: true, isSmoothJoin: false);
        context.LineTo(bounds.BottomLeft, isStroked: true, isSmoothJoin: false);
    }

    private static long SaturatingAddTick(long value, long delta)
    {
        if (delta >= 0)
        {
            return value > long.MaxValue - delta ? long.MaxValue : value + delta;
        }
        long magnitude = delta == long.MinValue ? long.MaxValue : -delta;
        return value < magnitude ? 0 : value - magnitude;
    }

    private static long SaturatingSubtractTick(long value, long delta)
    {
        if (delta >= 0)
        {
            return value < delta ? 0 : value - delta;
        }
        long magnitude = delta == long.MinValue ? long.MaxValue : -delta;
        return value > long.MaxValue - magnitude ? long.MaxValue : value + magnitude;
    }

    private void InvalidateDragPreviewGeometry()
    {
        _dragPreviewGeometry = null;
        _dragPreviewGeometryKey = null;
        _dragPreviewItems.Clear();
    }

    private void DrawDirectManipulationHover(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_dragItem is not null
            || !CanEdit
            || _hoverPoint is not Point pointer
            || TimelineToolPolicy.ForcesValueTrace(
                ToolMode,
                SurfaceMode,
                MouseButton.Left,
                Keyboard.Modifiers)
            || !TryHitTimelineItem(pointer, viewport, out TimelineRenderItem item)
            || !TimelineToolPolicy.CanBeginItemEdit(ToolMode, SurfaceMode, item.Kind))
        {
            return;
        }

        double left = laneHeaderWidth + viewport.TickToX(item.StartTick);
        if (IsEventPointKind(item.Kind))
        {
            double y = NormalizedToValueY(item.Value, rulerHeight);
            context.PushOpacity(0.65);
            context.DrawEllipse(null, _marqueePen, new Point(left, y), 5, 5);
            context.Pop();
            return;
        }
        double right = laneHeaderWidth + viewport.TickToX(item.EndTick);
        double laneHeight = GetLaneVisualHeight(item.Lane);
        double top = GetLaneTop(viewport, item.Lane, rulerHeight) + 2;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(3, laneHeight - 4));
        context.PushOpacity(0.65);
        context.DrawRoundedRectangle(null, _marqueePen, bounds, 2, 2);
        context.Pop();
    }

    private void DrawArrangementParentRowBackgrounds(
        DrawingContext context,
        TimelineViewport viewport,
        Brush background,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot) return;
        for (int lane = viewport.FirstLane; lane < viewport.LastLaneExclusive; lane++)
        {
            if ((uint)lane >= (uint)snapshot.ArrangementLanes.Count
                || !IsArrangementParentLane(snapshot.ArrangementLanes[lane].Kind))
            {
                continue;
            }
            double top = GetLaneTop(viewport, lane, rulerHeight);
            double height = Math.Min(
                GetLaneVisualHeight(lane),
                Math.Max(0, rulerHeight + GetLaneContentHeight(viewport) - top));
            if (height <= 0) continue;
            context.DrawRectangle(
                background,
                null,
                new Rect(
                    laneHeaderWidth,
                    top,
                    Math.Max(0, ActualWidth - laneHeaderWidth),
                    height));
            context.DrawLine(_borderPen, new Point(0, top), new Point(ActualWidth, top));
            context.DrawLine(_borderPen, new Point(0, top + height), new Point(ActualWidth, top + height));
        }
    }

    private void DrawTimelineChrome(
        DrawingContext context,
        TimelineViewport viewport,
        Brush chrome,
        Brush border,
        Brush text,
        Brush pianoWhiteKey,
        Brush pianoBlackKey,
        Brush pianoKeyLabel,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (rulerHeight > 0)
        {
            context.DrawRectangle(chrome, null, new Rect(0, 0, ActualWidth, rulerHeight));
            context.DrawLine(_borderPen, new Point(0, rulerHeight - 0.5), new Point(ActualWidth, rulerHeight - 0.5));
        }
        if (laneHeaderWidth > 0)
        {
            context.DrawRectangle(chrome, null, new Rect(0, rulerHeight, laneHeaderWidth, Math.Max(0, ActualHeight - rulerHeight)));
            context.DrawLine(_borderPen, new Point(laneHeaderWidth - 0.5, 0), new Point(laneHeaderWidth - 0.5, ActualHeight));
        }

        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            DrawPianoKeyboard(
                context,
                viewport,
                pianoWhiteKey,
                pianoBlackKey,
                pianoKeyLabel,
                laneHeaderWidth,
                rulerHeight);
        }
        else if (SurfaceMode is not (TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity))
        {
            IReadOnlyList<string> labels = Snapshot?.LaneLabels ?? Array.Empty<string>();
            IReadOnlyList<string> secondaryLabels = Snapshot?.LaneSecondaryLabels ?? Array.Empty<string>();
            IReadOnlyList<TimelineLaneState> states = Snapshot?.LaneStates ?? Array.Empty<TimelineLaneState>();
            IReadOnlyList<uint> laneColors = Snapshot?.LaneColors ?? Array.Empty<uint>();
            Brush secondaryText = Brush("Brush.Text.Tertiary", Color.FromRgb(103, 113, 128));
            Brush hoverBackground = Brush("Brush.Surface.2", Color.FromRgb(20, 24, 30));
            Brush pressedBackground = Brush("Brush.Surface.0", Color.FromRgb(9, 11, 14));
            Brush parentBackground = Brush("Brush.Surface.2", Color.FromRgb(20, 24, 30));
            Brush selectedTrackBackground = Brush("Brush.Red.Subtle", Color.FromRgb(44, 17, 20));
            Brush selectedTrackHoverBackground = Brush(
                "Brush.Red.Subtle.Hover",
                Color.FromRgb(58, 28, 32));
            int? hoveredSecondaryLinkLane = null;
            if (_hoverPoint is Point secondaryPointer
                && TryGetArrangementSecondaryLink(
                    secondaryPointer,
                    viewport,
                    out ArrangementLaneDescriptor hoveredLinkDescriptor,
                    out _))
            {
                hoveredSecondaryLinkLane = hoveredLinkDescriptor.Lane;
            }
            context.PushClip(new RectangleGeometry(new Rect(0, rulerHeight, laneHeaderWidth, Math.Max(0, ActualHeight - rulerHeight))));
            for (int relativeLane = 0; relativeLane < viewport.LaneCount; relativeLane++)
            {
                int lane = viewport.FirstLane + relativeLane;
                if ((uint)lane >= (uint)labels.Count || string.IsNullOrWhiteSpace(labels[lane]))
                {
                    continue;
                }
                double laneTop = GetLaneTop(viewport, lane, rulerHeight);
                ArrangementLaneDescriptor? arrangementLane = SurfaceMode == TimelineSurfaceMode.Arrangement
                    && (uint)lane < (uint)(Snapshot?.ArrangementLanes.Count ?? 0)
                        ? Snapshot!.ArrangementLanes[lane]
                        : null;
                double headerVisualHeight = GetLaneVisualHeight(lane);
                if (SurfaceMode == TimelineSurfaceMode.Arrangement
                    && arrangementLane is ArrangementLaneDescriptor parentDescriptor
                    && IsArrangementParentLane(parentDescriptor.Kind))
                {
                    context.DrawRectangle(
                        parentBackground,
                        null,
                        new Rect(0, laneTop, laneHeaderWidth, headerVisualHeight));
                }
                bool selectedArrangementTrack = SurfaceMode == TimelineSurfaceMode.Arrangement
                    && arrangementLane is ArrangementLaneDescriptor selectedDescriptor
                    && IsSelectedArrangementTrackLane(selectedDescriptor);
                Rect selectedBounds = new(
                    0.5,
                    laneTop + 0.5,
                    Math.Max(0, laneHeaderWidth - 1),
                    Math.Max(0, headerVisualHeight - 1));
                if (selectedArrangementTrack)
                {
                    context.DrawRectangle(selectedTrackBackground, null, selectedBounds);
                }
                if (SurfaceMode == TimelineSurfaceMode.Arrangement
                    && (_hoverLaneHeader == lane
                        || _pressedLaneHeader == lane && !_pressedLaneHeaderTargetsSharedGroup))
                {
                    context.DrawRectangle(
                        _pressedLaneHeader == lane
                            ? pressedBackground
                            : selectedArrangementTrack
                                ? selectedTrackHoverBackground
                                : hoverBackground,
                        null,
                        new Rect(0, laneTop, laneHeaderWidth, headerVisualHeight));
                }
                if (selectedArrangementTrack)
                {
                    context.DrawRectangle(null, _trackSelectionPen, selectedBounds);
                }
                if (SurfaceMode == TimelineSurfaceMode.Arrangement
                    && (uint)lane < (uint)laneColors.Count
                    && laneColors[lane] != 0)
                {
                    context.DrawRectangle(
                        GetRawAccentBrush(laneColors[lane]),
                        null,
                        new Rect(0, laneTop, 3, headerVisualHeight));
                }
                double contentIndent = arrangementLane?.Depth == 1 ? 23 : 0;
                if (arrangementLane is
                    { Kind: ArrangementLaneKind.EventInstrument or ArrangementLaneKind.MidiChannelRoot })
                {
                    DrawArrangementDisclosure(
                        context,
                        laneTop,
                        arrangementLane.Value.IsExpanded,
                        arrangementLane.Value.HasChildren,
                        headerVisualHeight,
                        text);
                    contentIndent = 13;
                }
                else if (arrangementLane?.Kind is ArrangementLaneKind.PureMidiTrack
                    or ArrangementLaneKind.DamagedPureMidiTrack)
                {
                    double iconLeft = arrangementLane?.IsSharedGroup == true ? 15 : 5;
                    DrawMidiTrackIcon(context, laneTop, headerVisualHeight, secondaryText, iconLeft);
                    contentIndent = arrangementLane?.IsSharedGroup == true ? 33 : 23;
                }
                else if (arrangementLane?.Kind is ArrangementLaneKind.LogicalTrack
                    or ArrangementLaneKind.DamagedLogicalTrack)
                {
                    double iconLeft = arrangementLane?.IsSharedGroup == true ? 15 : 5;
                    DrawLogicalTrackIcon(context, laneTop, headerVisualHeight, secondaryText, iconLeft);
                    contentIndent = arrangementLane?.IsSharedGroup == true ? 33 : 23;
                }
                else if (arrangementLane?.Kind == ArrangementLaneKind.Conductor)
                {
                    DrawConductorTrackIcon(context, laneTop, headerVisualHeight, secondaryText);
                    contentIndent = 23;
                }
                FormattedText formatted = GetFormattedText(labels[lane], text, 11,
                    arrangementLane is ArrangementLaneDescriptor labelDescriptor
                        && IsArrangementParentLane(labelDescriptor.Kind)
                        ? FontWeights.SemiBold
                        : FontWeights.Normal);
                string secondaryLabel = (uint)lane < (uint)secondaryLabels.Count
                    ? secondaryLabels[lane]
                    : string.Empty;
                bool secondaryLinkHovered = hoveredSecondaryLinkLane == lane;
                FormattedText? secondaryFormatted = secondaryLabel.Length == 0
                    ? null
                    : GetFormattedText(
                        secondaryLabel,
                        secondaryLinkHovered
                            ? Brush("Brush.Red.Hover", Color.FromRgb(237, 72, 84))
                            : secondaryText,
                        9,
                        FontWeights.Normal);
                double combinedHeight = formatted.Height + (secondaryFormatted?.Height ?? 0) + (secondaryFormatted is null ? 0 : 1);
                if (secondaryFormatted is not null && combinedHeight > headerVisualHeight - 2)
                {
                    secondaryFormatted = null;
                    combinedHeight = formatted.Height;
                }
                double y = laneTop + Math.Max(0, (headerVisualHeight - combinedHeight) / 2);
                if (arrangementLane is ArrangementLaneDescriptor
                    && TryGetArrangementSecondaryChipBounds(
                        viewport,
                        lane,
                        out _,
                        out Rect chipBounds))
                {
                    context.DrawRoundedRectangle(
                        secondaryLinkHovered ? hoverBackground : null,
                        secondaryLinkHovered ? _redPen : _borderPen,
                        chipBounds,
                        2,
                        2);
                }
                double labelRightInset = SurfaceMode == TimelineSurfaceMode.Conductor ? 14 : 52;
                context.PushClip(new RectangleGeometry(new Rect(6 + contentIndent, laneTop, Math.Max(0, laneHeaderWidth - labelRightInset - contentIndent), headerVisualHeight)));
                context.DrawText(formatted, new Point(8 + contentIndent, y));
                if (secondaryFormatted is not null)
                {
                    Point secondaryOrigin = new(8 + contentIndent, y + formatted.Height + 2);
                    context.DrawText(secondaryFormatted, secondaryOrigin);
                }
                context.Pop();
                if (SurfaceMode == TimelineSurfaceMode.Arrangement
                    && arrangementLane is ArrangementLaneDescriptor commandDescriptor
                    && IsMonitorableArrangementLane(commandDescriptor.Kind))
                {
                    TimelineLaneState state = (uint)lane < (uint)states.Count
                        ? states[lane]
                        : TimelineLaneState.None;
                    double commandY = laneTop + Math.Max(0, (headerVisualHeight - 16) / 2) + 1;
                    DrawArrangementLaneCommand(
                        context,
                        "M",
                        laneHeaderWidth - 39,
                        commandY,
                        state.HasFlag(TimelineLaneState.Muted),
                        text);
                    DrawArrangementLaneCommand(
                        context,
                        "S",
                        laneHeaderWidth - 20,
                        commandY,
                        state.HasFlag(TimelineLaneState.Solo),
                        text);
                }
            }
            if (SurfaceMode == TimelineSurfaceMode.Arrangement)
            {
                // Braces are a group-level affordance and must remain visible
                // above per-Track hover/pressed fills and accent strips.
                DrawArrangementSharedGroupBraces(
                    context,
                    viewport,
                    rulerHeight);
            }
            if (_pressedLaneHeader is int sourceLane && _laneHeaderDragActivated)
            {
                DrawArrangementSharedGroupDropZones(
                    context,
                    viewport,
                    secondaryText,
                    rulerHeight,
                    sourceLane);
                if (_laneHeaderDragJoinsTargetGroup
                    && Snapshot is TimelineRenderSnapshot dragSnapshot
                    && (uint)_laneHeaderDragTarget < (uint)dragSnapshot.ArrangementLanes.Count
                    && dragSnapshot.ArrangementLanes[_laneHeaderDragTarget].SharedGroupId is MidoraId groupId)
                {
                    Pen? outline = _laneHeaderDragRequiresRebind
                        ? _warningDashPen
                        : _marqueePen;
                    if (_laneHeaderDragJoinUsesChip
                        && TryGetArrangementJoinChipBounds(
                            viewport,
                            _laneHeaderDragTarget,
                            out Rect chipBounds))
                    {
                        context.DrawRoundedRectangle(
                            null,
                            outline,
                            chipBounds,
                            2,
                            2);
                    }
                    else
                    {
                        if (TryGetArrangementSharedGroupBounds(
                                dragSnapshot,
                                viewport,
                                groupId,
                                rulerHeight,
                                out double top,
                                out double bottom))
                        {
                            context.DrawRectangle(
                                null,
                                outline,
                                new Rect(0.5, top + 0.5, laneHeaderWidth - 1, Math.Max(1, bottom - top - 1)));
                        }
                    }
                }
                else if (_laneHeaderDragExteriorBoundaryGroupId is MidoraId boundaryGroupId
                    && Snapshot is TimelineRenderSnapshot boundarySnapshot
                    && TryGetArrangementSharedGroupBounds(
                        boundarySnapshot,
                        viewport,
                        boundaryGroupId,
                        rulerHeight,
                        out double groupTop,
                        out double groupBottom))
                {
                    double boundaryY = TimelineToolPolicy.ResolveArrangementSharedGroupBoundaryY(
                        groupTop,
                        groupBottom,
                        _laneHeaderDragExteriorBoundaryZone);
                    double alignedBoundaryY = _laneHeaderDragExteriorBoundaryZone
                        == ArrangementSharedGroupDropZone.After
                            ? boundaryY - 0.5
                            : boundaryY + 0.5;
                    context.DrawLine(
                        _laneHeaderDragDetachesFromSourceGroup
                            ? _groupDetachBoundaryPen
                            : _infoPen,
                        new Point(0, alignedBoundaryY),
                        new Point(laneHeaderWidth, alignedBoundaryY));
                }
                else
                {
                    double insertionY = GetArrangementReorderInsertionY(
                        viewport,
                        sourceLane,
                        _laneHeaderDragTarget,
                        rulerHeight);
                    context.DrawLine(_infoPen, new Point(0, insertionY + 0.5), new Point(laneHeaderWidth, insertionY + 0.5));
                }
            }
            context.Pop();
            if (SurfaceMode == TimelineSurfaceMode.Arrangement
                && _externalArrangementInsertionIndex is int externalInsertionIndex
                && Snapshot is TimelineRenderSnapshot externalSnapshot)
            {
                int boundaryLane = Math.Clamp(
                    externalInsertionIndex + 1,
                    1,
                    externalSnapshot.ArrangementLanes.Count);
                double insertionY = GetLaneTop(viewport, boundaryLane, rulerHeight);
                if (insertionY >= rulerHeight && insertionY <= ActualHeight)
                {
                    context.DrawLine(
                        _infoPen,
                        new Point(0, insertionY + 0.5),
                        new Point(ActualWidth, insertionY + 0.5));
                }
            }
        }

        if (rulerHeight <= 0)
        {
            return;
        }
        if (DisplayGridUsesBars
            && TimeSignatureMap is ProjectTimeSignatureMap barTimeSignatureMap)
        {
            DrawBarRuler(
                context,
                viewport,
                barTimeSignatureMap,
                text,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        if (laneHeaderWidth > 0)
        {
            context.DrawText(GetFormattedText("TICK", text, 10, FontWeights.SemiBold), new Point(8, 5));
        }

        long major = Math.Max(1, GridStepTicks);
        while (major <= long.MaxValue / 2 && major * viewport.PixelsPerTick < 88)
        {
            major *= 2;
        }
        long first = viewport.StartTick / major * major;
        if (first < viewport.StartTick && first <= long.MaxValue - major)
        {
            first += major;
        }
        for (long tick = first; tick < viewport.EndTick;)
        {
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(tick)) + 0.5;
            context.DrawLine(_textPen, new Point(x, rulerHeight - 5), new Point(x, rulerHeight));
            context.DrawText(GetFormattedText(tick.ToString(CultureInfo.InvariantCulture), text, 10, FontWeights.Normal), new Point(x + 4, 4));
            if (tick > long.MaxValue - major)
            {
                break;
            }
            tick += major;
        }
    }

    private void DrawBarRuler(
        DrawingContext context,
        TimelineViewport viewport,
        ProjectTimeSignatureMap timeSignatureMap,
        Brush text,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (laneHeaderWidth > 0
            && SurfaceMode is not (TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll))
        {
            context.DrawText(
                GetFormattedText("BAR", text, 10, FontWeights.SemiBold),
                new Point(8, 5));
        }

        TimelineGridPresentation.BuildBarGridLines(viewport.StartTick, viewport.EndTick,
            timeSignatureMap, _gridLines, TimelineTickMath.CeilingDistance(88 / viewport.PixelsPerTick),
            ProjectTickOffset, includeBeats: false);
        foreach (TimelineGridLine line in _gridLines)
        {
            long localBarTick = line.Tick;
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(localBarTick)) + 0.5;
            context.DrawLine(_textPen, new Point(x, rulerHeight - 5), new Point(x, rulerHeight));
            FormattedText label = GetFormattedText(
                timeSignatureMap.GetPosition(checked(localBarTick + ProjectTickOffset)).Bar.ToString(CultureInfo.InvariantCulture),
                text,
                10,
                FontWeights.Normal);
            double labelY = SurfaceMode == TimelineSurfaceMode.Arrangement
                ? Math.Max(1, rulerHeight - label.Height - 1)
                : 4;
            context.DrawText(label, new Point(x + 4, labelY));
        }
    }

    private void DrawPianoKeyboard(
        DrawingContext context,
        TimelineViewport viewport,
        Brush whiteKey,
        Brush blackKey,
        Brush labelBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (laneHeaderWidth <= 0 || ActualHeight <= rulerHeight)
        {
            return;
        }

        Brush highlightedWhiteKey = Brush(
            "Brush.PianoKey.Root",
            Color.FromRgb(188, 112, 116));
        Brush highlightedBlackKey = Brush(
            "Brush.PianoKey.Root.Dark",
            Color.FromRgb(116, 61, 65));
        PianoKeyboardDrawingKey key = new(
            viewport.FirstLane,
            viewport.LaneCount,
            BitConverter.DoubleToInt64Bits(LaneHeight),
            BitConverter.DoubleToInt64Bits(laneHeaderWidth),
            BitConverter.DoubleToInt64Bits(rulerHeight),
            BitConverter.DoubleToInt64Bits(ActualHeight),
            HighlightedPitch,
            whiteKey,
            blackKey,
            labelBrush,
            highlightedWhiteKey,
            highlightedBlackKey,
            _borderPen);
        if (_pianoKeyboardDrawing is null || _pianoKeyboardDrawingKey != key)
        {
            DrawingGroup drawing = new();
            using (DrawingContext recording = drawing.Open())
            {
                DrawPianoKeyboardCore(
                    recording,
                    viewport,
                    whiteKey,
                    blackKey,
                    labelBrush,
                    highlightedWhiteKey,
                    highlightedBlackKey,
                    laneHeaderWidth,
                    rulerHeight);
            }
            if (drawing.CanFreeze) drawing.Freeze();
            _pianoKeyboardDrawing = drawing;
            _pianoKeyboardDrawingKey = key;
            PianoKeyboardDrawingBuildCount++;
        }
        context.DrawDrawing(_pianoKeyboardDrawing);
    }

    private void DrawPianoKeyboardCore(
        DrawingContext context,
        TimelineViewport viewport,
        Brush whiteKey,
        Brush blackKey,
        Brush labelBrush,
        Brush highlightedWhiteKey,
        Brush highlightedBlackKey,
        double laneHeaderWidth,
        double rulerHeight)
    {

        context.PushClip(new RectangleGeometry(
            new Rect(0, rulerHeight, laneHeaderWidth, ActualHeight - rulerHeight)));
        double blackKeyWidth = Math.Max(12, Math.Round(laneHeaderWidth * 0.68));
        for (int relativeLane = 0; relativeLane < viewport.LaneCount; relativeLane++)
        {
            int lane = viewport.FirstLane + relativeLane;
            int midiNote = Math.Clamp(127 - lane, 0, 127);
            double y = rulerHeight + relativeLane * LaneHeight;
            double height = Math.Min(LaneHeight, ActualHeight - y);
            if (height <= 0)
            {
                break;
            }

            Rect whiteBounds = new(0, y, laneHeaderWidth, height);
            bool highlighted = midiNote == HighlightedPitch;
            Brush rootKey = highlighted
                ? highlightedWhiteKey
                : whiteKey;
            context.DrawRectangle(rootKey, _borderPen, whiteBounds);
            if (PianoKeyPresentation.IsBlackKey(midiNote))
            {
                Rect blackBounds = new(
                    0,
                    y + 1,
                    blackKeyWidth,
                    Math.Max(1, height - 2));
                context.DrawRoundedRectangle(
                    highlighted ? highlightedBlackKey : blackKey,
                    _borderPen,
                    blackBounds,
                    1,
                    1);
                continue;
            }

            string? label = PianoKeyPresentation.GetOctaveCLabel(midiNote);
            if (label is null)
            {
                continue;
            }
            double fontSize = Math.Clamp(LaneHeight * 0.56, 8, 11);
            FormattedText formatted = GetFormattedText(
                label,
                labelBrush,
                fontSize,
                FontWeights.SemiBold);
            double labelX = Math.Max(4, laneHeaderWidth - formatted.Width - 6);
            double labelY = y + (height - formatted.Height) / 2;
            context.DrawText(formatted, new Point(labelX, labelY));
        }
        context.Pop();
    }

    private void DrawRulerOverview(
        DrawingContext context,
        TimelineViewport viewport,
        Brush text,
        Brush warning,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (rulerHeight <= 0 || RulerSnapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }

        _rulerItems.Clear();
        if (snapshot.ConductorSource is not null)
        {
            if (!TryPrepareConductorRuler(snapshot, viewport)) return;
        }
        else if (!snapshot.TryQueryIntoCached(
                viewport.StartTick,
                viewport.EndTick,
                0,
                1,
                _rulerItems))
        {
            ScheduleRulerExactPrefetch(snapshot, viewport.StartTick, viewport.EndTick);
            return;
        }
        Rect rulerContentBounds = new(
            laneHeaderWidth,
            0,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            rulerHeight);
        if (rulerContentBounds.Width <= 0)
        {
            return;
        }
        context.PushClip(new RectangleGeometry(rulerContentBounds));
        Brush markerBorder = Brush("Brush.Text.Tertiary", Color.FromRgb(116, 126, 143));
        Brush markerText = Brush("Brush.Text.Secondary", Color.FromRgb(183, 191, 204));
        Brush markerBackground = Brush("Brush.Surface.1", Color.FromRgb(14, 17, 21));
        Pen markerChipPen = FrozenPen(markerBorder, 1);
        foreach (TimelineRenderItem item in _rulerItems)
        {
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(item.StartTick)) + 0.5;
            if (SurfaceMode == TimelineSurfaceMode.Arrangement
                && item.Kind == TimelineItemKind.Marker)
            {
                FormattedText label = GetFormattedText(
                    string.IsNullOrWhiteSpace(item.Label) ? "Marker" : item.Label,
                    markerText,
                    9,
                    FontWeights.SemiBold);
                const double horizontalPadding = 5;
                const double markerHeight = 15;
                Rect bounds = new(
                    x,
                    1.5,
                    snapshot.ConductorSource is null ? Math.Max(12, label.Width + horizontalPadding * 2)
                        : Math.Clamp(label.Width + horizontalPadding * 2, 12, 90),
                    markerHeight);
                context.DrawRoundedRectangle(
                    markerBackground,
                    markerChipPen,
                    bounds,
                    3,
                    3);
                context.PushClip(new RectangleGeometry(bounds));
                context.DrawText(
                    label,
                    new Point(
                        bounds.X + horizontalPadding,
                        bounds.Y + (bounds.Height - label.Height) / 2));
                context.Pop();
                continue;
            }
            Brush brush = item.Kind == TimelineItemKind.ProjectEndMarker ? warning : text;
            Pen pen = item.Kind == TimelineItemKind.ProjectEndMarker ? _redPen! : _infoPen!;
            context.DrawLine(pen, new Point(x, Math.Max(1, rulerHeight - 9)), new Point(x, rulerHeight));

            StreamGeometry marker = new();
            using (StreamGeometryContext geometry = marker.Open())
            {
                geometry.BeginFigure(new Point(x - 3, rulerHeight - 9), true, true);
                geometry.LineTo(new Point(x + 3, rulerHeight - 9), true, false);
                geometry.LineTo(new Point(x, rulerHeight - 5), true, false);
            }
            marker.Freeze();
            context.DrawGeometry(brush, null, marker);

            if (item.Label.Length != 0)
            {
                FormattedText label = GetFormattedText(item.Label, brush, 9, FontWeights.SemiBold);
                double labelX = Math.Min(
                    Math.Max(laneHeaderWidth + 2, x + 4),
                    Math.Max(laneHeaderWidth + 2, ActualWidth - label.Width - 2));
                context.DrawText(label, new Point(labelX, Math.Max(1, rulerHeight - label.Height - 1)));
            }
        }
        context.Pop();
    }

    private void DrawArrangementLaneCommand(
        DrawingContext context,
        string label,
        double x,
        double textY,
        bool active,
        Brush text)
    {
        Rect bounds = new(x, Math.Floor(textY - 1), 16, 16);
        bool solo = label == "S";
        context.DrawRectangle(
            active ? solo ? _penSuccessSubtleBrush : _penRedSubtleBrush : null,
            active ? solo ? _successPen : _redPen : _borderPen,
            bounds);
        FormattedText formatted = GetFormattedText(label, text, 9, FontWeights.SemiBold);
        context.DrawText(
            formatted,
            new Point(
                bounds.X + (bounds.Width - formatted.Width) / 2,
                bounds.Y + (bounds.Height - formatted.Height) / 2));
    }

    private bool TryGetArrangementLaneCommand(
        double x,
        int lane,
        out TimelineLaneHeaderCommand command)
    {
        ArrangementLaneDescriptor? descriptor = Snapshot is not null
            && (uint)lane < (uint)Snapshot.ArrangementLanes.Count
                ? Snapshot.ArrangementLanes[lane]
                : null;
        if (x >= 0 && x < 20
            && descriptor is
            { Kind: ArrangementLaneKind.EventInstrument or ArrangementLaneKind.MidiChannelRoot })
        {
            command = TimelineLaneHeaderCommand.ToggleExpanded;
            return true;
        }
        if (descriptor is not null
            && IsMonitorableArrangementLane(descriptor.Value.Kind)
            && x >= GetLaneHeaderWidth() - 41 && x < GetLaneHeaderWidth() - 21)
        {
            command = TimelineLaneHeaderCommand.ToggleMute;
            return true;
        }
        if (descriptor is not null
            && IsMonitorableArrangementLane(descriptor.Value.Kind)
            && x >= GetLaneHeaderWidth() - 21 && x < GetLaneHeaderWidth())
        {
            command = TimelineLaneHeaderCommand.ToggleSolo;
            return true;
        }
        command = default;
        return false;
    }

    private bool TryGetArrangementJoinChipBounds(
        TimelineViewport viewport,
        int lane,
        out Rect bounds)
    {
        if (!TryGetArrangementSecondaryChipBounds(
                viewport,
                lane,
                out ArrangementLaneDescriptor descriptor,
                out bounds))
        {
            return false;
        }
        return !descriptor.IsSharedGroup && descriptor.SharedGroupId.HasValue;
    }

    private bool TryGetArrangementSecondaryChipBounds(
        TimelineViewport viewport,
        int lane,
        out ArrangementLaneDescriptor descriptor,
        out Rect bounds)
    {
        descriptor = default;
        bounds = Rect.Empty;
        if (Snapshot is not TimelineRenderSnapshot snapshot
            || (uint)lane >= (uint)snapshot.ArrangementLanes.Count
            || (uint)lane >= (uint)snapshot.LaneLabels.Count
            || (uint)lane >= (uint)snapshot.LaneSecondaryLabels.Count)
        {
            return false;
        }
        descriptor = snapshot.ArrangementLanes[lane];
        if (descriptor.ParentId is null
            || descriptor.Kind is not (ArrangementLaneKind.LogicalTrack
                or ArrangementLaneKind.PureMidiTrack)
            || string.IsNullOrWhiteSpace(snapshot.LaneSecondaryLabels[lane]))
        {
            return false;
        }

        double laneTop = GetLaneTop(viewport, lane, GetRulerHeight());
        double headerHeight = GetLaneVisualHeight(lane);
        FormattedText primary = GetFormattedText(
            snapshot.LaneLabels[lane],
            _penTextBrush ?? Brushes.White,
            11,
            FontWeights.Normal);
        FormattedText secondary = GetFormattedText(
            snapshot.LaneSecondaryLabels[lane],
            Brush("Brush.Text.Tertiary", Color.FromRgb(103, 113, 128)),
            9,
            FontWeights.Normal);
        double combinedHeight = primary.Height + secondary.Height + 1;
        if (combinedHeight > headerHeight - 2) return false;

        double contentIndent = descriptor.IsSharedGroup ? 33 : 23;
        double textX = 8 + contentIndent;
        double maximumRight = GetLaneHeaderWidth() - 46;
        double width = Math.Min(secondary.Width + 6, maximumRight - textX + 3);
        if (width <= 4) return false;
        double primaryY = laneTop + Math.Max(0, (headerHeight - combinedHeight) / 2);
        bounds = new(
            textX - 3,
            primaryY + primary.Height,
            width,
            secondary.Height + 3);
        return true;
    }

    private static bool IsArrangementParentLane(ArrangementLaneKind kind) =>
        kind is ArrangementLaneKind.EventInstrument
            or ArrangementLaneKind.MidiChannelRoot
            or ArrangementLaneKind.DamagedEventInstrument
            or ArrangementLaneKind.DamagedMidiChannelRoot;

    private int NormalizeArrangementReorderTarget(int sourceLane, int targetLane)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || Snapshot is not TimelineRenderSnapshot snapshot
            || (uint)sourceLane >= (uint)snapshot.ArrangementLanes.Count
            || (uint)targetLane >= (uint)snapshot.ArrangementLanes.Count
            || snapshot.ArrangementLanes[sourceLane].Kind == ArrangementLaneKind.Conductor)
        {
            return targetLane;
        }
        return snapshot.ArrangementLanes[targetLane].Kind == ArrangementLaneKind.Conductor
            ? Math.Min(1, snapshot.ArrangementLanes.Count - 1)
            : targetLane;
    }

    private static bool IsMonitorableArrangementLane(ArrangementLaneKind kind) =>
        kind is ArrangementLaneKind.EventInstrument
            or ArrangementLaneKind.MidiChannelRoot
            or ArrangementLaneKind.LogicalTrack
            or ArrangementLaneKind.PureMidiTrack;

    private static bool IsSelectableArrangementTrackLane(ArrangementLaneKind kind) =>
        kind is ArrangementLaneKind.LogicalTrack
            or ArrangementLaneKind.PureMidiTrack
            or ArrangementLaneKind.DamagedLogicalTrack
            or ArrangementLaneKind.DamagedPureMidiTrack;

    private bool IsSelectedArrangementTrackLane(ArrangementLaneDescriptor descriptor) =>
        descriptor.Kind == ArrangementLaneKind.Conductor
            ? IsConductorTrackSelected
            : SelectedArrangementTrackId is MidoraId selectedTrackId
                && descriptor.ObjectId == selectedTrackId
                && IsSelectableArrangementTrackLane(descriptor.Kind);

    private void DrawArrangementDisclosure(
        DrawingContext context,
        double laneTop,
        bool expanded,
        bool hasChildren,
        double visualHeight,
        Brush brush)
    {
        if (!hasChildren) return;
        double centerY = laneTop + visualHeight / 2;
        StreamGeometry geometry = new();
        using (StreamGeometryContext value = geometry.Open())
        {
            if (expanded)
            {
                value.BeginFigure(new Point(7, centerY - 2), false, false);
                value.LineTo(new Point(11, centerY + 2), true, false);
                value.LineTo(new Point(15, centerY - 2), true, false);
            }
            else
            {
                value.BeginFigure(new Point(9, centerY - 4), false, false);
                value.LineTo(new Point(13, centerY), true, false);
                value.LineTo(new Point(9, centerY + 4), true, false);
            }
        }
        geometry.Freeze();
        Pen pen = new(brush, 1.2);
        if (pen.CanFreeze) pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawArrangementSharedGroupBraces(
        DrawingContext context,
        TimelineViewport viewport,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot) return;
        for (int index = 0; index < snapshot.ArrangementLanes.Count; index++)
        {
            ArrangementLaneDescriptor first = snapshot.ArrangementLanes[index];
            if (!first.IsSharedGroupStart || first.SharedGroupId is not MidoraId groupId)
                continue;
            ArrangementLaneDescriptor last = first;
            for (int member = index + 1; member < snapshot.ArrangementLanes.Count; member++)
            {
                ArrangementLaneDescriptor candidate = snapshot.ArrangementLanes[member];
                if (candidate.SharedGroupId != groupId) break;
                last = candidate;
                if (candidate.IsSharedGroupEnd) break;
            }
            if (last.Lane < viewport.FirstLane || first.Lane >= viewport.LastLaneExclusive)
                continue;
            double top = GetLaneTop(viewport, first.Lane, rulerHeight) + 3;
            double bottom = GetLaneTop(viewport, last.Lane, rulerHeight)
                + GetLaneVisualHeight(last.Lane) - 3;
            if (bottom <= top) continue;
            double middle = (top + bottom) / 2;
            StreamGeometry geometry = new();
            using (StreamGeometryContext value = geometry.Open())
            {
                value.BeginFigure(new Point(11, top), false, false);
                value.BezierTo(
                    new Point(7, top),
                    new Point(7, middle - 5),
                    new Point(4, middle - 3),
                    true,
                    false);
                value.BezierTo(
                    new Point(2, middle - 1),
                    new Point(2, middle + 1),
                    new Point(4, middle + 3),
                    true,
                    false);
                value.BezierTo(
                    new Point(7, middle + 5),
                    new Point(7, bottom),
                    new Point(11, bottom),
                    true,
                    false);
            }
            geometry.Freeze();
            bool highlighted = _hoverSharedGroupId == groupId
                || _pressedSharedGroupId == groupId
                || _contextSharedGroupId == groupId;
            if (highlighted)
            {
                context.DrawRectangle(
                    Brush("Brush.Red.Subtle", Color.FromRgb(44, 17, 20)),
                    null,
                    new Rect(0, top - 3, 13, bottom - top + 6));
            }
            context.DrawGeometry(null, highlighted ? _redPen : _borderPen, geometry);
        }
    }

    private void DrawArrangementSharedGroupDropZones(
        DrawingContext context,
        TimelineViewport viewport,
        Brush brush,
        double rulerHeight,
        int sourceLane)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot
            || (uint)sourceLane >= (uint)snapshot.ArrangementLanes.Count)
        {
            return;
        }
        ArrangementLaneKind sourceKind = snapshot.ArrangementLanes[sourceLane].Kind;
        for (int index = 0; index < snapshot.ArrangementLanes.Count; index++)
        {
            ArrangementLaneDescriptor first = snapshot.ArrangementLanes[index];
            if (!first.IsSharedGroupStart
                || first.Kind != sourceKind
                || first.SharedGroupId is not MidoraId groupId)
            {
                continue;
            }
            ArrangementLaneDescriptor last = first;
            for (int member = index + 1; member < snapshot.ArrangementLanes.Count; member++)
            {
                ArrangementLaneDescriptor candidate = snapshot.ArrangementLanes[member];
                if (candidate.SharedGroupId != groupId) break;
                last = candidate;
                if (candidate.IsSharedGroupEnd) break;
            }
            if (last.Lane < viewport.FirstLane || first.Lane >= viewport.LastLaneExclusive)
                continue;
            double top = GetLaneTop(viewport, first.Lane, rulerHeight);
            double bottom = GetLaneTop(viewport, last.Lane, rulerHeight)
                + GetLaneVisualHeight(last.Lane);
            double width = Math.Max(0, GetLaneHeaderWidth() - 13);
            context.DrawRectangle(brush, null, new Rect(13, top, width, 3));
            context.DrawRectangle(brush, null, new Rect(13, Math.Max(top, bottom - 3), width, 3));
        }

        if (!_laneHeaderDragDetachesFromSourceGroup
            || snapshot.ArrangementLanes[sourceLane].SharedGroupId is not MidoraId sourceGroupId)
        {
            return;
        }

        if (!TryGetArrangementSharedGroupBounds(
                snapshot,
                viewport,
                sourceGroupId,
                rulerHeight,
                out double sourceTop,
                out double sourceBottom))
        {
            return;
        }
        double activeY = _laneHeaderDragInsertsAfterTarget
            ? Math.Max(sourceTop, sourceBottom - 5)
            : sourceTop;
        double activeWidth = Math.Max(0, GetLaneHeaderWidth() - 13);
        context.DrawRectangle(
            Brush("Brush.Info.Subtle", Color.FromRgb(20, 46, 70)),
            null,
            new Rect(13, activeY, activeWidth, 5));
    }

    private bool TryGetArrangementSharedGroupBounds(
        TimelineRenderSnapshot snapshot,
        TimelineViewport viewport,
        MidoraId groupId,
        double rulerHeight,
        out double top,
        out double bottom)
    {
        int firstLane = -1;
        int lastLane = -1;
        for (int index = 0; index < snapshot.ArrangementLanes.Count; index++)
        {
            if (snapshot.ArrangementLanes[index].SharedGroupId != groupId) continue;
            firstLane = firstLane < 0 ? snapshot.ArrangementLanes[index].Lane : firstLane;
            lastLane = snapshot.ArrangementLanes[index].Lane;
        }
        if (firstLane < 0)
        {
            top = 0;
            bottom = 0;
            return false;
        }

        top = GetLaneTop(viewport, firstLane, rulerHeight);
        bottom = GetLaneTop(viewport, lastLane, rulerHeight)
            + GetLaneVisualHeight(lastLane);
        return true;
    }

    private void DrawMidiTrackIcon(
        DrawingContext context,
        double laneTop,
        double laneHeight,
        Brush brush,
        double iconLeft = 5)
    {
        DrawFluentTrackIcon(
            context,
            "Fluent.Midi20Regular",
            laneTop,
            laneHeight,
            brush,
            iconLeft);
    }

    private void DrawLogicalTrackIcon(
        DrawingContext context,
        double laneTop,
        double laneHeight,
        Brush brush,
        double iconLeft = 5)
    {
        DrawFluentTrackIcon(
            context,
            "Fluent.MusicNote220Regular",
            laneTop,
            laneHeight,
            brush,
            iconLeft);
    }

    private void DrawConductorTrackIcon(
        DrawingContext context,
        double laneTop,
        double laneHeight,
        Brush brush)
    {
        DrawFluentTrackIcon(
            context,
            "Fluent.Wrench20Regular",
            laneTop,
            laneHeight,
            brush,
            5);
    }

    private void DrawFluentTrackIcon(
        DrawingContext context,
        string resourceKey,
        double laneTop,
        double laneHeight,
        Brush brush,
        double iconLeft)
    {
        if (TryFindResource(resourceKey) is not Geometry geometry || geometry.Bounds.IsEmpty) return;
        const double iconSize = 20;
        double translateY = Math.Round(laneTop + (laneHeight - iconSize) / 2);
        context.PushTransform(new TranslateTransform(iconLeft, translateY));
        context.DrawGeometry(brush, null, geometry);
        context.Pop();
    }

    private double GetArrangementReorderInsertionY(
        TimelineViewport viewport,
        int sourceLane,
        int targetLane,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot
            || (uint)targetLane >= (uint)snapshot.ArrangementLanes.Count)
        {
            return GetLaneTop(viewport, targetLane, rulerHeight);
        }
        int normalizedTarget = NormalizeArrangementReorderTarget(sourceLane, targetLane);
        if (normalizedTarget <= sourceLane)
            return GetLaneTop(viewport, normalizedTarget, rulerHeight);
        return GetLaneTop(viewport, normalizedTarget, rulerHeight)
            + GetLaneVisualHeight(normalizedTarget);
    }

    private void DrawMarquee(
        DrawingContext context,
        TimelineViewport viewport,
        Brush info)
    {
        if (_marqueeOrigin is not Point origin || _marqueeCurrent is not Point current)
        {
            return;
        }
        if (!TryGetMarqueeBounds(
                origin,
                current,
                viewport,
                out Rect rectangle,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _))
        {
            return;
        }
        if (rectangle.IsEmpty)
        {
            return;
        }
        Rect contentClip = new(
            GetLaneHeaderWidth(),
            GetRulerHeight(),
            viewport.Width,
            GetLaneContentHeight(viewport));
        if (contentClip.IsEmpty)
        {
            return;
        }
        context.PushClip(new RectangleGeometry(contentClip));
        context.PushOpacity(0.22);
        context.DrawRectangle(info, null, rectangle);
        context.Pop();
        context.DrawRectangle(null, _marqueePen, rectangle);
        context.Pop();
    }

    private bool TryGetSelectionFloatingToolMetrics(
        out TimelineItemKind kind,
        out TimelineSelectionMetrics metrics)
    {
        kind = default;
        metrics = default;
        if (ToolMode != TimelineToolMode.Select
            || SelectionSnapshot is not { Count: > 0, MetricsAreComplete: true } selection)
        {
            return false;
        }

        ReadOnlySpan<TimelineItemKind> candidates = SurfaceMode switch
        {
            TimelineSurfaceMode.Arrangement => [TimelineItemKind.Segment],
            TimelineSurfaceMode.PianoRoll =>
            [
                TimelineItemKind.LogicalNote,
                TimelineItemKind.DirectMidiNote,
                TimelineItemKind.TemplateNote
            ],
            TimelineSurfaceMode.EventLanes =>
            [
                TimelineItemKind.TempoPoint,
                TimelineItemKind.LogicalParameterPoint,
                TimelineItemKind.DirectMidiEvent,
                TimelineItemKind.OpaqueMidiEvent,
                TimelineItemKind.TemplateEvent
            ],
            TimelineSurfaceMode.Conductor => [TimelineItemKind.ConductorEvent],
            _ => []
        };
        int matchingKinds = 0;
        foreach (TimelineItemKind candidate in candidates)
        {
            if (!selection.TryGetMetrics(candidate, out TimelineSelectionMetrics value)
                || value.Count <= 0)
            {
                continue;
            }
            matchingKinds++;
            kind = candidate;
            metrics = value;
        }
        if (matchingKinds != 1) return false;

        foreach ((TimelineItemKind selectedKind, TimelineSelectionMetrics selectedMetrics)
            in selection.Metrics)
        {
            if (selectedMetrics.Count <= 0
                || selectedKind == kind
                || IsNoteKind(kind) && selectedKind == TimelineItemKind.Velocity)
            {
                continue;
            }
            if (selectedKind is TimelineItemKind.Segment
                or TimelineItemKind.LogicalNote
                or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote
                or TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
                or TimelineItemKind.DirectMidiEvent
                or TimelineItemKind.TemplateEvent
                or TimelineItemKind.OpaqueMidiEvent
                or TimelineItemKind.ConductorEvent)
            {
                return false;
            }
        }
        return true;
    }

    private bool TryGetSelectionFloatingToolLayout(
        TimelineViewport viewport,
        out TimelineItemKind kind,
        out TimelineSelectionMetrics metrics,
        out Rect bounds)
    {
        const double height = 30;
        const double gripWidth = 12;
        const double buttonWidth = 24;
        const double gap = 6;
        if (!TryGetSelectionFloatingToolMetrics(out kind, out metrics))
        {
            bounds = Rect.Empty;
            ClearSelectionFloatingToolBounds();
            return false;
        }

        bool hasResize = TimelineToolPolicy.SupportsSelectionFloatingToolResize(kind);
        double width = gripWidth + buttonWidth * (hasResize ? 4 : 2) + 4;
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        double naturalX = header + viewport.TickToX(metrics.MaximumEndTick) + 8;
        double naturalY = IsEventPointKind(kind)
            ? NormalizedToValueY(metrics.MaximumValue, ruler) - height - gap
            : GetLaneTop(viewport, metrics.MinimumLane, ruler) - height - gap;

        if (_selectionToolRevision != SelectionSnapshot!.Revision
            || _selectionToolKind != kind)
        {
            _selectionToolRevision = SelectionSnapshot.Revision;
            _selectionToolKind = kind;
            _selectionToolPinnedTick = metrics.MaximumEndTick;
            _selectionToolPinnedVertical = IsEventPointKind(kind)
                ? metrics.MaximumValue
                : metrics.MinimumLane;
            _selectionToolFollowOffset = default;
        }

        double x;
        double y;
        if (_selectionToolPinned)
        {
            x = header + viewport.TickToX(_selectionToolPinnedTick) + 8;
            y = IsEventPointKind(kind)
                ? NormalizedToValueY(_selectionToolPinnedVertical, ruler) - height - gap
                : GetLaneTop(
                    viewport,
                    (int)Math.Round(
                        _selectionToolPinnedVertical,
                        MidpointRounding.AwayFromZero),
                    ruler) - height - gap;
        }
        else
        {
            x = naturalX + _selectionToolFollowOffset.X;
            y = naturalY + _selectionToolFollowOffset.Y;
            x = Math.Clamp(x, header + 2, Math.Max(header + 2, ActualWidth - width - 2));
            y = Math.Clamp(y, ruler + 2, Math.Max(ruler + 2, ActualHeight - height - 2));
        }

        bounds = new(x, y, width, height);
        _selectionToolBounds = bounds;
        _selectionToolGripBounds = new(bounds.X, bounds.Y, gripWidth, bounds.Height);
        double buttonX = bounds.X + gripWidth + 2;
        _selectionToolPinBounds = new(buttonX, bounds.Y + 3, buttonWidth, height - 6);
        buttonX += buttonWidth;
        _selectionToolResizeStartBounds = hasResize
            ? new(buttonX, bounds.Y + 3, buttonWidth, height - 6)
            : Rect.Empty;
        if (hasResize) buttonX += buttonWidth;
        _selectionToolResizeEndBounds = hasResize
            ? new(buttonX, bounds.Y + 3, buttonWidth, height - 6)
            : Rect.Empty;
        if (hasResize) buttonX += buttonWidth;
        _selectionToolMoveBounds = new(buttonX, bounds.Y + 3, buttonWidth, height - 6);
        return true;
    }

    private void DrawSelectionFloatingTool(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        _ = laneHeaderWidth;
        _ = rulerHeight;
        if (!IsSelectionFloatingToolEnabled)
        {
            ClearSelectionFloatingToolBounds();
            return;
        }
        if (!TryGetSelectionFloatingToolLayout(
                viewport,
                out _,
                out _,
                out Rect bounds)
            || !bounds.IntersectsWith(new(0, 0, ActualWidth, ActualHeight)))
        {
            return;
        }

        Brush background = Brush("Brush.Surface.2", Color.FromRgb(24, 29, 36));
        Brush border = Brush("Brush.Border", Color.FromRgb(42, 48, 58));
        Brush foreground = Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245));
        Brush active = Brush("Brush.Red.Subtle", Color.FromRgb(75, 29, 34));
        Pen borderPen = FrozenPen(border, 1);
        Pen iconPen = FrozenPen(foreground, 1);
        context.DrawRoundedRectangle(background, borderPen, bounds, 4, 4);
        if (_selectionToolPinned)
        {
            context.DrawRoundedRectangle(active, null, _selectionToolPinBounds, 3, 3);
        }

        double gripCenterX = _selectionToolGripBounds.X + _selectionToolGripBounds.Width / 2;
        for (int row = -1; row <= 1; row++)
        {
            context.DrawEllipse(
                foreground,
                null,
                new(gripCenterX, bounds.Y + bounds.Height / 2 + row * 5),
                1,
                1);
        }
        DrawLockIcon(context, _selectionToolPinBounds, iconPen, _selectionToolPinned);
        if (!_selectionToolResizeStartBounds.IsEmpty)
        {
            DrawBoundaryResizeIcon(
                context,
                _selectionToolResizeStartBounds,
                iconPen,
                startEdge: true);
            DrawBoundaryResizeIcon(
                context,
                _selectionToolResizeEndBounds,
                iconPen,
                startEdge: false);
        }
        DrawMoveIcon(context, _selectionToolMoveBounds, iconPen);
    }

    private static void DrawLockIcon(
        DrawingContext context,
        Rect bounds,
        Pen pen,
        bool locked)
    {
        double centerX = bounds.X + bounds.Width / 2;
        double top = bounds.Y + 5;
        Rect body = new(centerX - 4, top + 7, 8, 7);
        context.DrawRectangle(null, pen, body);
        StreamGeometry shackle = new();
        using (StreamGeometryContext geometry = shackle.Open())
        {
            geometry.BeginFigure(new(centerX - 3, top + 7), false, false);
            geometry.BezierTo(
                new(centerX - 3, top + (locked ? 1 : 3)),
                new(centerX + 3, top + 1),
                new(centerX + 3, top + 7),
                true,
                false);
        }
        shackle.Freeze();
        context.DrawGeometry(null, pen, shackle);
    }

    private static void DrawHorizontalArrowIcon(DrawingContext context, Rect bounds, Pen pen)
    {
        double centerX = bounds.X + bounds.Width / 2;
        double centerY = bounds.Y + bounds.Height / 2;
        context.DrawLine(pen, new(centerX - 6, centerY), new(centerX + 6, centerY));
        context.DrawLine(pen, new(centerX - 6, centerY), new(centerX - 3, centerY - 3));
        context.DrawLine(pen, new(centerX - 6, centerY), new(centerX - 3, centerY + 3));
        context.DrawLine(pen, new(centerX + 6, centerY), new(centerX + 3, centerY - 3));
        context.DrawLine(pen, new(centerX + 6, centerY), new(centerX + 3, centerY + 3));
    }

    private static void DrawBoundaryResizeIcon(
        DrawingContext context,
        Rect bounds,
        Pen pen,
        bool startEdge)
    {
        double centerX = bounds.X + bounds.Width / 2;
        double centerY = bounds.Y + bounds.Height / 2;
        double boundaryX = centerX + (startEdge ? 4 : -4);
        double arrowX = centerX + (startEdge ? -6 : 6);
        context.DrawLine(
            pen,
            new(boundaryX, centerY - 6),
            new(boundaryX, centerY + 6));
        context.DrawLine(pen, new(boundaryX, centerY), new(arrowX, centerY));
        double arrowDirection = startEdge ? 1 : -1;
        context.DrawLine(
            pen,
            new(arrowX, centerY),
            new(arrowX + 3 * arrowDirection, centerY - 3));
        context.DrawLine(
            pen,
            new(arrowX, centerY),
            new(arrowX + 3 * arrowDirection, centerY + 3));
    }

    private static void DrawMoveIcon(DrawingContext context, Rect bounds, Pen pen)
    {
        DrawHorizontalArrowIcon(context, bounds, pen);
        double centerX = bounds.X + bounds.Width / 2;
        double centerY = bounds.Y + bounds.Height / 2;
        context.DrawLine(pen, new(centerX, centerY - 6), new(centerX, centerY + 6));
        context.DrawLine(pen, new(centerX, centerY - 6), new(centerX - 3, centerY - 3));
        context.DrawLine(pen, new(centerX, centerY - 6), new(centerX + 3, centerY - 3));
        context.DrawLine(pen, new(centerX, centerY + 6), new(centerX - 3, centerY + 3));
        context.DrawLine(pen, new(centerX, centerY + 6), new(centerX + 3, centerY + 3));
    }

    private bool TryBeginSelectionFloatingToolGesture(
        Point point,
        TimelineViewport viewport)
    {
        if (!TryGetSelectionFloatingToolLayout(
                viewport,
                out TimelineItemKind kind,
                out TimelineSelectionMetrics metrics,
                out _)
            || !_selectionToolBounds.Contains(point))
        {
            return false;
        }

        if (_selectionToolGripBounds.Contains(point))
        {
            _selectionToolGripOrigin = point;
            _selectionToolGripStartTick = _selectionToolPinnedTick;
            _selectionToolGripStartVertical = _selectionToolPinnedVertical;
            _selectionToolGripStartFollowOffset = _selectionToolFollowOffset;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            return true;
        }
        if (_selectionToolPinBounds.Contains(point))
        {
            ToggleSelectionFloatingToolPin(viewport, metrics, kind);
            InvalidateVisual();
            return true;
        }
        TimelineItemEditKind editKind;
        if (!_selectionToolResizeStartBounds.IsEmpty
            && _selectionToolResizeStartBounds.Contains(point))
        {
            editKind = TimelineItemEditKind.ResizeStart;
        }
        else if (!_selectionToolResizeEndBounds.IsEmpty
            && _selectionToolResizeEndBounds.Contains(point))
        {
            editKind = TimelineItemEditKind.ResizeEnd;
        }
        else if (_selectionToolMoveBounds.Contains(point))
        {
            editKind = TimelineItemEditKind.Move;
        }
        else
        {
            return true;
        }

        TimelineRenderItem anchor = metrics.EarliestItem;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool controlCopyRequested = editKind == TimelineItemEditKind.Move
            && (modifiers & ModifierKeys.Control) != 0;
        bool copySupported = TimelineToolPolicy.SupportsSelectionFloatingToolCopyDrag(
            SurfaceMode,
            kind,
            editKind);
        if (controlCopyRequested && !copySupported)
        {
            Cursor = Cursors.No;
            return true;
        }
        _dragItem = anchor;
        _dragKind = editKind;
        _dragModifiers = modifiers;
        _dragOrigin = point;
        _dragOriginTick = viewport.XToTick(point.X - GetLaneHeaderWidth());
        _dragOriginLane = YToLane(viewport, point.Y - GetRulerHeight());
        _dragCurrentTick = _dragOriginTick;
        _dragCurrentLane = _dragOriginLane;
        _dragActivated = false;
        _dragCopyRequested = controlCopyRequested;
        _deferredControlClickToggle = false;
        _deferredPlainDrawSegmentSelection = false;
        _dragTimeLocked = false;
        _dragPreviewSelectionPrepared = false;
        _dragPreviewSelection = null;
        InvalidateDragPreviewGeometry();
        PrepareDragPitchPreview(anchor);
        CaptureMouse();
        return true;
    }

    private void ToggleSelectionFloatingToolPin(
        TimelineViewport viewport,
        TimelineSelectionMetrics metrics,
        TimelineItemKind kind)
    {
        const double toolHeight = 30;
        const double gap = 6;
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        if (_selectionToolPinned)
        {
            double naturalX = header + viewport.TickToX(metrics.MaximumEndTick) + 8;
            double naturalY = IsEventPointKind(kind)
                ? NormalizedToValueY(metrics.MaximumValue, ruler) - toolHeight - gap
                : GetLaneTop(viewport, metrics.MinimumLane, ruler) - toolHeight - gap;
            _selectionToolFollowOffset = new(
                _selectionToolBounds.X - naturalX,
                _selectionToolBounds.Y - naturalY);
            _selectionToolPinned = false;
            return;
        }

        _selectionToolPinnedTick = Math.Max(
            0,
            viewport.XToTick(_selectionToolBounds.X - header - 8));
        double verticalReference = _selectionToolBounds.Y + toolHeight + gap;
        _selectionToolPinnedVertical = IsEventPointKind(kind)
            ? ValueYToNormalized(verticalReference, ruler)
            : YToLane(viewport, verticalReference - ruler);
        _selectionToolPinned = true;
    }

    private void UpdateSelectionFloatingToolGrip(
        Point point,
        Point origin,
        TimelineViewport viewport)
    {
        Vector delta = point - origin;
        if (!_selectionToolPinned)
        {
            _selectionToolFollowOffset = _selectionToolGripStartFollowOffset + delta;
            return;
        }

        long tickDelta = TimelineTickMath.RoundSignedDistance(
            delta.X / Math.Max(double.Epsilon, viewport.PixelsPerTick));
        _selectionToolPinnedTick = Math.Max(
            0,
            SaturatingAddTick(_selectionToolGripStartTick, tickDelta));
        if (IsEventPointKind(_selectionToolKind))
        {
            double contentHeight = Math.Max(1, ActualHeight - GetRulerHeight());
            double range = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
            _selectionToolPinnedVertical = Math.Clamp(
                _selectionToolGripStartVertical - delta.Y / contentHeight * range,
                0,
                1);
        }
        else
        {
            _selectionToolPinnedVertical = Math.Clamp(
                _selectionToolGripStartVertical + delta.Y / Math.Max(1, LaneHeight),
                0,
                Math.Max(0, (Snapshot?.LaneLabels.Count ?? 1) - 1));
        }
    }

    private void DrawDragDeltaLabel(
        DrawingContext context,
        DragPreviewTransform transform)
    {
        string text;
        if (_dragKind is TimelineItemEditKind.ResizeStart or TimelineItemEditKind.ResizeEnd)
        {
            text = $"{FormatSigned(transform.TickDelta)} Ticks";
        }
        else if (_dragItem is TimelineRenderItem item && IsEventPointKind(item.Kind))
        {
            double valueDelta = transform.ValueDelta * (ValueAxisMaximum - ValueAxisMinimum);
            text = $"{FormatSigned(transform.TickDelta)} Ticks, {valueDelta:+0.##;-0.##;0} Value";
        }
        else if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            text = $"{FormatSigned(-transform.LaneDelta)} Keys, {FormatSigned(transform.TickDelta)} Ticks";
        }
        else
        {
            text = $"{FormatSigned(transform.LaneDelta)} Lanes, {FormatSigned(transform.TickDelta)} Ticks";
        }

        FormattedText formatted = GetFormattedText(
            text,
            Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245)),
            11,
            FontWeights.Normal);
        Point pointer = _hoverPoint ?? _dragOrigin;
        double width = formatted.Width + 12;
        double height = formatted.Height + 8;
        double x = Math.Clamp(
            pointer.X + 12,
            2,
            Math.Max(2, ActualWidth - width - 2));
        double y = Math.Clamp(
            pointer.Y + 12,
            2,
            Math.Max(2, ActualHeight - height - 2));
        Rect background = new(
            x,
            y,
            width,
            height);
        context.DrawRoundedRectangle(
            Brush("Brush.Surface.2", Color.FromRgb(24, 29, 36)),
            _borderPen,
            background,
            3,
            3);
        context.DrawText(formatted, new(background.X + 6, background.Y + 4));

        static string FormatSigned(long value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
    }

    private void ClearSelectionFloatingToolBounds()
    {
        _selectionToolBounds = Rect.Empty;
        _selectionToolGripBounds = Rect.Empty;
        _selectionToolPinBounds = Rect.Empty;
        _selectionToolResizeStartBounds = Rect.Empty;
        _selectionToolResizeEndBounds = Rect.Empty;
        _selectionToolMoveBounds = Rect.Empty;
    }

    private static bool IsNoteKind(TimelineItemKind kind) =>
        kind is TimelineItemKind.LogicalNote
            or TimelineItemKind.DirectMidiNote
            or TimelineItemKind.TemplateNote;

    private bool TryGetMarqueeBounds(
        Point origin,
        Point current,
        TimelineViewport viewport,
        out Rect displayBounds,
        out long startTick,
        out long endTick,
        out int firstLane,
        out int lastLaneExclusive,
        out double minimumNormalizedValue,
        out double maximumNormalizedValue)
    {
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        Rect pointerTravelBounds = new(origin, current);
        if (pointerTravelBounds.Width < 2 && pointerTravelBounds.Height < 2)
        {
            displayBounds = Rect.Empty;
            startTick = endTick = 0;
            firstLane = lastLaneExclusive = 0;
            minimumNormalizedValue = maximumNormalizedValue = 0;
            return false;
        }

        double contentLeft = laneHeaderWidth;
        double contentRight = laneHeaderWidth + viewport.Width;
        long rawAnchor = _marqueeAnchorTick;
        long rawMoving = viewport.XToTick(
            Math.Clamp(current.X, contentLeft, contentRight) - laneHeaderWidth);
        TimelineGridQuantization.SnappedRange snapped = TimelineGridQuantization.SnapRangeFromAnchor(
            Math.Max(0, rawAnchor),
            Math.Max(0, rawMoving),
            Math.Max(1, OperationStepTicks),
            OperationUsesBars,
            TimeSignatureMap);
        startTick = snapped.StartTick;
        endTick = snapped.EndTick;
        if (endTick <= startTick)
        {
            displayBounds = Rect.Empty;
            firstLane = lastLaneExclusive = 0;
            minimumNormalizedValue = maximumNormalizedValue = 0;
            return false;
        }

        if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
        {
            firstLane = 0;
            lastLaneExclusive = 1;
            double movingValue = ValueYToNormalized(current.Y, rulerHeight);
            (minimumNormalizedValue, maximumNormalizedValue) =
                TimelineToolPolicy.ResolveSemanticMarqueeValueRange(
                    _marqueeAnchorNormalizedValue,
                    movingValue);
        }
        else
        {
            (firstLane, lastLaneExclusive) =
                TimelineToolPolicy.ResolveSemanticMarqueeLaneRange(
                    _marqueeAnchorLane,
                    viewport,
                    current.Y - rulerHeight);
            minimumNormalizedValue = 0;
            maximumNormalizedValue = 1;
        }
        double left = laneHeaderWidth + viewport.TickToX(startTick);
        double right = laneHeaderWidth + viewport.TickToX(endTick);
        double top = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? NormalizedToValueY(maximumNormalizedValue, rulerHeight)
            : GetLaneTop(viewport, firstLane, rulerHeight);
        double bottom = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? NormalizedToValueY(minimumNormalizedValue, rulerHeight)
            : GetLaneTop(viewport, lastLaneExclusive, rulerHeight);
        displayBounds = new Rect(
            new Point(left, top),
            new Point(right, bottom));
        return true;
    }

    private void RaiseMarqueeAnchorBackgroundInvoked()
    {
        BackgroundInvoked?.Invoke(
            this,
            new TimelinePointEventArgs(
                _marqueeAnchorTick,
                _marqueeAnchorLane,
                _marqueeAnchorNormalizedValue,
                Keyboard.Modifiers,
                isDoubleClick: false));
    }

    private void DrawNotePlacementPreview(
        DrawingContext context,
        TimelineViewport viewport,
        Brush red,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_notePlacementStartTick is not long start) return;
        long end = Math.Max(checked(start + 1), _notePlacementCurrentTick);
        int lane = 127 - _notePlacementPitch;
        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double top = rulerHeight + (lane - viewport.FirstLane) * LaneHeight;
        Rect bounds = new(left, top, Math.Max(2, right - left), LaneHeight);
        context.DrawRectangle(null, _marqueePen, bounds);
    }

    private void DrawSegmentPlacementPreview(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_segmentPlacementStartTick is not long start) return;
        long end = Math.Max(checked(start + 1), _segmentPlacementCurrentTick);
        if (Snapshot is TimelineRenderSnapshot snapshot)
        {
            snapshot.GetSegmentPlacementInfo(
                _segmentPlacementLane,
                start,
                out _,
                out long nextStart);
            if (nextStart != long.MaxValue) end = Math.Min(end, nextStart);
        }
        if (end <= start) return;
        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double laneHeight = GetLaneVisualHeight(_segmentPlacementLane);
        double top = GetLaneTop(viewport, _segmentPlacementLane, rulerHeight) + 2;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(3, laneHeight - 4));
        context.DrawRectangle(null, _marqueePen, bounds);
    }

    private void DrawCreationHoverPreview(
        DrawingContext context,
        TimelineViewport viewport,
        Brush red,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if ((_notePlacementStartTick is not null || _segmentPlacementStartTick is not null)
            || _dragItem is not null
            || ToolMode != TimelineToolMode.Draw
            || _hoverPoint is not Point pointer
            || pointer.X < laneHeaderWidth
            || pointer.Y < rulerHeight
            || (SurfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll
                && !IsInsideLaneContent(viewport, pointer.Y, rulerHeight))
            || SurfaceMode is not (TimelineSurfaceMode.Arrangement
                or TimelineSurfaceMode.PianoRoll
                or TimelineSurfaceMode.EventLanes))
        {
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.EventLanes)
        {
            if (TimelineToolPolicy.ForcesValueTrace(
                    ToolMode,
                    SurfaceMode,
                    MouseButton.Left,
                    Keyboard.Modifiers)
                || TryHitTimelineItem(pointer, viewport, out _)
                || _exactQueryPending)
            {
                return;
            }
            if (!TrySnapPointerTick(viewport.XToTick(pointer.X - laneHeaderWidth), out long pointTick)) return;
            double normalized = ValueYToNormalized(pointer.Y, rulerHeight);
            Point center = new(
                laneHeaderWidth + viewport.TickToX(pointTick),
                NormalizedToValueY(normalized, rulerHeight));
            context.PushOpacity(0.72);
            context.DrawEllipse(info, _marqueePen, center, 3.5, 3.5);
            context.Pop();
            return;
        }
        if (TryHitTimelineItem(pointer, viewport, out _) || _exactQueryPending) return;
        int lane = YToLane(viewport, pointer.Y - rulerHeight);
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && Snapshot is TimelineRenderSnapshot arrangementSnapshot
            && (uint)lane < (uint)arrangementSnapshot.ArrangementLanes.Count
            && (arrangementSnapshot.ArrangementLanes[lane].Kind == ArrangementLaneKind.Conductor
                || IsArrangementParentLane(arrangementSnapshot.ArrangementLanes[lane].Kind)))
        {
            return;
        }
        if (!TrySnapPointerTick(viewport.XToTick(pointer.X - laneHeaderWidth), out long start)) return;
        long end = start > long.MaxValue - Math.Max(1, DefaultCreationLengthTicks)
            ? long.MaxValue
            : start + Math.Max(1, DefaultCreationLengthTicks);
        bool invalid = start > long.MaxValue - Math.Max(1, DefaultCreationLengthTicks);
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && Snapshot is TimelineRenderSnapshot snapshot)
        {
            snapshot.GetSegmentPlacementInfo(
                lane,
                start,
                out bool overlaps,
                out long nextStart);
            invalid |= overlaps;
            if (!invalid && nextStart != long.MaxValue) end = Math.Min(end, nextStart);
            invalid |= end <= start;
        }
        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double laneHeight = GetLaneVisualHeight(lane);
        double top = GetLaneTop(viewport, lane, rulerHeight) + 2;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(3, laneHeight - 4));
        Pen pen = invalid ? _redPen! : _marqueePen!;
        context.PushOpacity(0.9);
        context.DrawRectangle(null, pen, bounds);
        context.Pop();
    }

    private void UpdateHoverCursor(Point point, TimelineViewport viewport)
    {
        if (TryGetSelectionFloatingToolLayout(viewport, out _, out _, out _)
            && _selectionToolBounds.Contains(point))
        {
            Cursor = _selectionToolGripBounds.Contains(point)
                || _selectionToolMoveBounds.Contains(point)
                    ? Cursors.SizeAll
                    : _selectionToolResizeStartBounds.Contains(point)
                        || _selectionToolResizeEndBounds.Contains(point)
                        ? Cursors.SizeWE
                        : Cursors.Hand;
            return;
        }
        if (_marqueeQueryPending)
        {
            Cursor = Cursors.Wait;
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.Velocity)
        {
            _velocitySelectionRestricted = SelectionSnapshot is { Count: > 0 };
            if (TimelineToolPolicy.ForcesValueTrace(
                    ToolMode,
                    SurfaceMode,
                    MouseButton.Left,
                    Keyboard.Modifiers))
            {
                Cursor = Cursors.Cross;
                return;
            }
            bool velocityHit = TryHitVelocityBar(point, viewport, out _);
            Cursor = !velocityHit && _exactQueryPending
                ? Cursors.Wait
                : velocityHit
                    ? Cursors.SizeNS
                    : Cursors.Arrow;
            return;
        }
        if (TryGetArrangementSecondaryLink(point, viewport, out _, out _))
        {
            Cursor = Cursors.Hand;
            return;
        }
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        if (point.X < header
            || point.Y < ruler
            || (SurfaceMode == TimelineSurfaceMode.PianoRoll
                && !IsInsideLaneContent(viewport, point.Y, ruler)))
        {
            Cursor = Cursors.Arrow;
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && ToolMode == TimelineToolMode.Draw
            && Snapshot is TimelineRenderSnapshot arrangementSnapshot)
        {
            int lane = YToLane(viewport, point.Y - ruler);
            if ((uint)lane < (uint)arrangementSnapshot.ArrangementLanes.Count
                && IsArrangementParentLane(arrangementSnapshot.ArrangementLanes[lane].Kind))
            {
                Cursor = Cursors.Arrow;
                return;
            }
        }
        if (TimelineToolPolicy.ForcesValueTrace(
                ToolMode,
                SurfaceMode,
                MouseButton.Left,
                Keyboard.Modifiers))
        {
            Cursor = Cursors.Cross;
            return;
        }
        bool hasItem = TryHitTimelineItem(point, viewport, out TimelineRenderItem hit);
        if (!hasItem && _exactQueryPending)
        {
            Cursor = Cursors.Wait;
            return;
        }
        bool nearEdge = false;
        if (hasItem)
        {
            double left = header + viewport.TickToX(hit.StartTick);
            double right = header + viewport.TickToX(hit.EndTick);
            nearEdge = Math.Abs(point.X - left) <= TimelineToolPolicy.DirectEditEdgeTolerancePixels
                || Math.Abs(point.X - right) <= TimelineToolPolicy.DirectEditEdgeTolerancePixels;
        }
        TimelinePointerIntent intent = TimelineToolPolicy.GetPointerIntent(
            ToolMode,
            SurfaceMode,
            isInContent: true,
            hasItem ? hit.Kind : null,
            nearEdge,
            Keyboard.Modifiers);
        Cursor = intent switch
        {
            TimelinePointerIntent.Crosshair => Cursors.Cross,
            TimelinePointerIntent.Erase => Cursors.No,
            TimelinePointerIntent.Split => Cursors.IBeam,
            TimelinePointerIntent.Move => Cursors.SizeAll,
            TimelinePointerIntent.ResizeHorizontal => Cursors.SizeWE,
            TimelinePointerIntent.ResizeVertical => Cursors.SizeNS,
            _ => Cursors.Arrow
        };
    }

    private void RefreshHoverIntent()
    {
        if (_marqueeQueryPending)
        {
            Cursor = Cursors.Wait;
            InvalidateVisual();
            return;
        }
        if (_dragItem is null
            && _hoverPoint is Point point
            && TryCreateViewport(out TimelineViewport viewport))
        {
            UpdateHoverCursor(point, viewport);
        }
        else if (_dragItem is null)
        {
            Cursor = Cursors.Arrow;
        }
        InvalidateVisual();
    }

    private static bool IsAltKey(KeyEventArgs e) =>
        e.Key is Key.LeftAlt or Key.RightAlt
        || e.SystemKey is Key.LeftAlt or Key.RightAlt;

    private static bool IsEventPointKind(TimelineItemKind kind) =>
        kind is TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
            or TimelineItemKind.DirectMidiEvent
            or TimelineItemKind.OpaqueMidiEvent;

    private static bool IsValueEditableEventPointKind(TimelineItemKind kind) =>
        kind is TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
            or TimelineItemKind.DirectMidiEvent;

    private bool TryHitTimelineItem(
        Point point,
        TimelineViewport viewport,
        out TimelineRenderItem item)
    {
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        if (point.X < header
            || point.Y < ruler
            || Snapshot is null
            || (SurfaceMode == TimelineSurfaceMode.PianoRoll
                && !IsInsideLaneContent(viewport, point.Y, ruler)))
        {
            item = default!;
            return false;
        }
        if (!PopulateTimelineHitItems(
            point,
            viewport,
            preferDirectEditEdges: CanEdit
                && ToolMode == TimelineToolMode.Draw
                && TimelineToolPolicy.IsDirectEditingSurface(SurfaceMode)))
        {
            item = default!;
            return false;
        }
        if (SurfaceMode == TimelineSurfaceMode.EventLanes)
        {
            _hitItems.RemoveAll(candidate =>
                IsEventPointKind(candidate.Kind)
                && Math.Abs(NormalizedToValueY(candidate.Value, ruler) - point.Y) > 8);
        }
        if (_hitItems.Count == 0)
        {
            item = default!;
            return false;
        }
        item = _hitItems[0];
        return true;
    }

    private bool PopulateTimelineHitItems(
        Point point,
        TimelineViewport viewport,
        bool preferDirectEditEdges)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            _hitItems.Clear();
            return true;
        }

        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        long tick = viewport.XToContainingTick(point.X - header);
        int lane = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? 0
            : YToLane(viewport, point.Y - ruler);
        if (snapshot.ConductorSource is not null)
        {
            _hitItems.Clear();
            if (TryHitConductorPoint(point, viewport, out TimelineRenderItem conductorItem))
                _hitItems.Add(conductorItem);
            return !_exactQueryPending;
        }
        if (SurfaceMode == TimelineSurfaceMode.Conductor)
        {
            const double hitRadius = 8;
            long toleranceTicks = TimelineTickMath.CeilingDistance(hitRadius / viewport.PixelsPerTick);
            if (!snapshot.TryHitTestCached(tick, toleranceTicks, lane, _hitItems))
            {
                ScheduleExactPrefetch(
                    snapshot,
                    Math.Max(0, tick - toleranceTicks),
                    tick > long.MaxValue - toleranceTicks - 1
                        ? long.MaxValue
                        : tick + toleranceTicks + 1,
                    lane,
                    checked(lane + 1));
                return false;
            }
            _hitItems.RemoveAll(candidate =>
            {
                if (candidate.Kind is not (TimelineItemKind.ConductorEvent
                    or TimelineItemKind.Marker
                    or TimelineItemKind.ProjectEndMarker))
                {
                    return true;
                }
                double candidateX = header + viewport.TickToX(candidate.StartTick);
                double candidateY = ruler
                    + (candidate.Lane - viewport.FirstLane + 0.5) * LaneHeight;
                return Math.Abs(candidateX - point.X) > hitRadius
                    || Math.Abs(candidateY - point.Y) > hitRadius;
            });
            _hitItems.Sort((left, right) =>
            {
                bool leftSelected = IsSelected(left);
                bool rightSelected = IsSelected(right);
                int bySelection = rightSelected.CompareTo(leftSelected);
                if (bySelection != 0) return bySelection;
                double leftDistance = Math.Abs(
                    header + viewport.TickToX(left.StartTick) - point.X);
                double rightDistance = Math.Abs(
                    header + viewport.TickToX(right.StartTick) - point.X);
                int byDistance = leftDistance.CompareTo(rightDistance);
                return byDistance != 0 ? byDistance : right.ZIndex.CompareTo(left.ZIndex);
            });
            return true;
        }
        if (preferDirectEditEdges)
        {
            long toleranceTicks = TimelineTickMath.CeilingDistance(
                TimelineToolPolicy.DirectEditEdgeTolerancePixels / viewport.PixelsPerTick);
            if (!snapshot.TryHitTestCached(tick, toleranceTicks, lane, _hitItems))
            {
                ScheduleExactPrefetch(
                    snapshot,
                    Math.Max(0, tick - toleranceTicks),
                    tick > long.MaxValue - toleranceTicks - 1
                        ? long.MaxValue
                        : tick + toleranceTicks + 1,
                    lane,
                    checked(lane + 1));
                return false;
            }
            int edgeIndex = TimelineToolPolicy.FindPreferredDirectEditEdgeCandidate(
                _hitItems,
                viewport,
                point.X - header,
                ToolMode,
                SurfaceMode,
                SelectionSnapshot);
            if (edgeIndex >= 0)
            {
                if (edgeIndex != 0)
                {
                    (_hitItems[0], _hitItems[edgeIndex]) = (_hitItems[edgeIndex], _hitItems[0]);
                }
                return true;
            }
        }

        long pointEnd = tick == long.MaxValue ? long.MaxValue : tick + 1;
        if (!snapshot.TryHitTestCached(tick, 0, lane, _hitItems))
        {
            ScheduleExactPrefetch(snapshot, tick, pointEnd, lane, checked(lane + 1));
            return false;
        }
        return true;
    }

    private void RaiseBackgroundInvoked(
        Point point,
        TimelineViewport viewport,
        bool isDoubleClick,
        bool isEmptyBackground = false)
    {
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && !IsInsideLaneContent(viewport, point.Y, ruler))
        {
            return;
        }
        long tick = viewport.XToTick(point.X - header);
        int lane = YToLane(viewport, point.Y - ruler);
        double laneHeight = GetLaneVisualHeight(lane);
        double laneOffset = Math.Clamp(
            point.Y - GetLaneTop(viewport, lane, ruler),
            0,
            laneHeight);
        double normalizedValue = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? ValueYToNormalized(point.Y, ruler)
            : 1 - laneOffset / Math.Max(1, laneHeight);
        BackgroundInvoked?.Invoke(
            this,
            new TimelinePointEventArgs(
                tick,
                lane,
                normalizedValue,
                Keyboard.Modifiers,
                isDoubleClick,
                isEmptyBackground));
    }

    /// <summary>Restore the complete displayed value axis without changing horizontal navigation.</summary>
    public (double Minimum, double Maximum) CaptureValueViewport() => (_valueViewMinimum, _valueViewMaximum);

    public void RestoreValueViewport((double Minimum, double Maximum) range)
    {
        if (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum)
            || range.Minimum < 0 || range.Maximum > 1 || range.Maximum <= range.Minimum)
            throw new ArgumentOutOfRangeException(nameof(range));
        SetValueViewRange(range.Minimum, range.Maximum);
        UpdateValueScrollMetrics();
        SetCurrentValue(ValueScrollOffsetProperty, Math.Clamp(1 - range.Maximum, 0, ValueScrollMaximum));
    }

    public void ResetValueViewport()
    {
        SetValueViewRange(0, 1);
        UpdateValueScrollMetrics();
        SetCurrentValue(ValueScrollOffsetProperty, 0d);
        if (Snapshot?.ConductorSource is not null) CancelConductorHit();
        RefreshPointerPositionText();
        RefreshHoverIntent();
    }

    private void ZoomValueAxis(double pointerY, int wheelDelta, double rulerHeight)
    {
        double oldRange = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double factor = wheelDelta > 0 ? 0.8 : 1.25;
        double newRange = Math.Clamp(oldRange * factor, 1d / 64, 1);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        double screenRatio = Math.Clamp((pointerY - rulerHeight) / contentHeight, 0, 1);
        double anchor = _valueViewMaximum - screenRatio * oldRange;
        double newMaximum = anchor + screenRatio * newRange;
        double newMinimum = newMaximum - newRange;
        if (newMinimum < 0)
        {
            newMaximum -= newMinimum;
            newMinimum = 0;
        }
        if (newMaximum > 1)
        {
            newMinimum -= newMaximum - 1;
            newMaximum = 1;
        }
        _valueViewMinimum = Math.Clamp(newMinimum, 0, 1 - newRange);
        _valueViewMaximum = Math.Clamp(newMaximum, _valueViewMinimum + newRange, 1);
        SetValueViewRange(_valueViewMinimum, _valueViewMaximum);
        UpdateValueScrollMetrics();
        SetCurrentValue(ValueScrollOffsetProperty, Math.Clamp(1 - _valueViewMaximum, 0, ValueScrollMaximum));
        InvalidateVisual();
    }

    private double NormalizedToValueY(double normalized, double rulerHeight)
    {
        double range = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        return rulerHeight
            + (_valueViewMaximum - Math.Clamp(normalized, _valueViewMinimum, _valueViewMaximum))
            / range
            * contentHeight;
    }

    private double ValueYToNormalized(double y, double rulerHeight)
    {
        double range = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        return Math.Clamp(
            _valueViewMaximum - Math.Clamp((y - rulerHeight) / contentHeight, 0, 1) * range,
            0,
            1);
    }

    private double GetDragNormalizedValueDelta(double currentY)
    {
        double rulerHeight = GetRulerHeight();
        return ValueYToNormalized(currentY, rulerHeight)
            - ValueYToNormalized(_dragOrigin.Y, rulerHeight);
    }

    private void RefreshPointerPositionText()
    {
        if (_hoverPoint is Point point && TryCreateViewport(out TimelineViewport viewport))
        {
            UpdatePointerPositionText(point, viewport);
        }
        else if (!IsMouseCaptured)
        {
            ResetPointerPositionText();
        }
    }

    private void UpdatePointerPositionText(Point point, TimelineViewport viewport)
    {
        try { UpdatePointerPositionTextCore(point, viewport); }
        catch (OverflowException) { ResetPointerPositionText(); }
    }

    private void UpdatePointerPositionTextCore(Point point, TimelineViewport viewport)
    {
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        bool inTimelineContent = point.X >= laneHeaderWidth
            && point.X < ActualWidth
            && point.Y >= rulerHeight
            && point.Y < ActualHeight;

        if (SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            bool hasTick = inTimelineContent && TrySnapPointerTick(
                viewport.XToTick(point.X - laneHeaderWidth), out _);
            SetValue(
                PointerPositionTextPropertyKey,
                hasTick
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"({SnapAbsolute(viewport.XToTick(point.X - laneHeaderWidth))})")
                    : string.Empty);
            return;
        }

        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            if (!inTimelineContent || !IsInsideLaneContent(viewport, point.Y, rulerHeight))
            {
                SetValue(PointerPositionTextPropertyKey, string.Empty);
                return;
            }

            if (!TrySnapPointerTick(viewport.XToTick(point.X - laneHeaderWidth), out long pianoTick))
            {
                ResetPointerPositionText();
                return;
            }
            int lane = YToLane(viewport, point.Y - rulerHeight);
            int keyNumber = Math.Clamp(127 - lane, 0, 127);
            SetValue(
                PointerPositionTextPropertyKey,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"({pianoTick}, {keyNumber})"));
            return;
        }

        bool hasActiveEventPointGesture = HasActiveEventPointGesture();
        if (SurfaceMode != TimelineSurfaceMode.EventLanes
            || (!inTimelineContent && !hasActiveEventPointGesture))
        {
            ResetPointerPositionText();
            return;
        }

        if (_dragActivated
            && _dragKind == TimelineItemEditKind.Move
            && _dragItem is TimelineRenderItem draggedPoint
            && IsEventPointKind(draggedPoint.Kind))
        {
            DragPreviewTransform transform = GetDragPreviewTransform(draggedPoint);
            SetEventPointPositionText(
                Math.Max(0, SaturatingAddTick(draggedPoint.StartTick, transform.TickDelta)),
                Math.Clamp(draggedPoint.Value + transform.ValueDelta, 0, 1));
            return;
        }

        Point clamped = ClampEventPointTracePoint(point);
        double tickX = clamped.X;
        double valueY = clamped.Y;
        if (_eventPointOrigin is Point eventOrigin)
        {
            if (_eventPointTimeLocked)
            {
                tickX = ClampEventPointTracePoint(eventOrigin).X;
            }
            if (_eventPointHorizontalTrace)
            {
                valueY = ClampEventPointTracePoint(eventOrigin).Y;
            }
        }
        else if (_dragTimeLocked
            && _dragItem is TimelineRenderItem pointItem
            && IsEventPointKind(pointItem.Kind))
        {
            tickX = GetLaneHeaderWidth() + viewport.TickToX(pointItem.StartTick);
        }

        if (!TrySnapPointerTick(viewport.XToTick(tickX - GetLaneHeaderWidth()), out long tick))
        {
            ResetPointerPositionText();
            return;
        }
        double normalized = ValueYToNormalized(valueY, GetRulerHeight());
        SetEventPointPositionText(tick, normalized);
    }

    private bool HasActiveEventPointGesture() =>
        SurfaceMode == TimelineSurfaceMode.EventLanes
        && (_eventPointOrigin is not null
            || (_dragItem is TimelineRenderItem item
                && _dragKind == TimelineItemEditKind.Move
                && IsEventPointKind(item.Kind)));

    private void SetEventPointPositionText(long tick, double normalized)
    {
        double minimum = ValueAxisMinimum;
        double maximum = ValueAxisMaximum;
        double formalValue = double.IsFinite(minimum)
            && double.IsFinite(maximum)
            && maximum > minimum
                ? minimum + Math.Clamp(normalized, 0, 1) * (maximum - minimum)
                : Math.Clamp(normalized, 0, 1);
        string formattedValue = ValueAxisIntegral
            ? Math.Round(formalValue, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture)
            : formalValue.ToString("0.######", CultureInfo.InvariantCulture);
        SetValue(
            PointerPositionTextPropertyKey,
            string.Create(
                CultureInfo.InvariantCulture,
                $"({tick}, {formattedValue})"));
    }

    private void ResetPointerPositionText() => SetValue(
        PointerPositionTextPropertyKey,
        SurfaceMode == TimelineSurfaceMode.EventLanes ? "(-, -)" : string.Empty);

    private long SnapAbsolute(long tick) => TimelineGridQuantization.SnapAbsolute(
        Math.Max(0, tick),
        Math.Max(1, OperationStepTicks),
        OperationUsesBars,
        TimeSignatureMap,
        0);

    private bool TrySnapPointerTick(long tick, out long snapped)
        => ProjectTimelineGrid.TrySnapAbsolute(Math.Max(0, tick), Math.Max(1, OperationStepTicks),
            OperationUsesBars, TimeSignatureMap, 0, out snapped);

    private long SnapOperationDelta(long delta, long targetTick) =>
        TimelineGridQuantization.SnapDelta(
            delta,
            Math.Max(0, targetTick),
            Math.Max(1, OperationStepTicks),
            OperationUsesBars,
            TimeSignatureMap);

    private long GetMinimumPositiveOperationDelta(long startTick)
    {
        if (!OperationUsesBars || TimeSignatureMap is null)
        {
            return Math.Max(1, OperationStepTicks);
        }
        ProjectBarBounds bar = TimeSignatureMap.GetBarBounds(Math.Max(0, startTick));
        return Math.Max(1, checked((long)(bar.EndTick - startTick)));
    }

    private bool TryGetArrangementSecondaryLink(
        Point point,
        TimelineViewport viewport,
        out ArrangementLaneDescriptor descriptor,
        out Rect bounds)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || point.X < 0
            || point.X >= GetLaneHeaderWidth()
            || point.Y < GetRulerHeight())
        {
            descriptor = default;
            bounds = Rect.Empty;
            return false;
        }
        int lane = YToLane(viewport, point.Y - GetRulerHeight());
        return TryGetArrangementSecondaryChipBounds(viewport, lane, out descriptor, out bounds)
            && bounds.Contains(point);
    }

    private long GetPositiveCreationOperationDelta(long pointerDelta, long targetTick)
    {
        if (pointerDelta <= 0) return 0;
        if (!OperationUsesBars || TimeSignatureMap is null)
        {
            return TimelineToolPolicy.ResolvePositiveFixedStepCreationDelta(
                pointerDelta,
                Math.Max(1, OperationStepTicks));
        }

        // Bar snapping can change step at a time-signature boundary. Advance
        // through the formal grid from the pointer anchor rather than snapping
        // the final Note length, so the frozen initial length is preserved.
        long cursor = Math.Max(0, _notePlacementPointerAnchorTick);
        long desired = Math.Max(cursor, targetTick);
        if (cursor >= desired) return 0;
        // Every crossed signature starts a bar, so a direct ceil lookup is
        // equivalent to visiting each boundary, including truncated bars.
        long end = ProjectTimelineGrid.GetGridTickAtOrAfter(desired,
            Math.Max(1, OperationStepTicks), true, TimeSignatureMap);
        return checked(end - cursor);
    }

    private static long SaturatingAddSigned(long value, long increment)
    {
        if (increment > 0 && value > long.MaxValue - increment) return long.MaxValue;
        if (increment < 0 && value < long.MinValue - increment) return long.MinValue;
        return value + increment;
    }

    private Brush Brush(string key, Color fallback)
    {
        if (TryFindResource(key) is SolidColorBrush resource)
        {
            return resource;
        }
        if (_fallbackBrushes.TryGetValue(key, out SolidColorBrush? cached))
        {
            return cached;
        }
        SolidColorBrush brush = new(fallback);
        brush.Freeze();
        _fallbackBrushes.Add(key, brush);
        return brush;
    }

    private FormattedText GetFormattedText(string value, Brush brush, double size, FontWeight weight)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cachedPixelsPerDip != pixelsPerDip)
        {
            _cachedPixelsPerDip = pixelsPerDip;
            _textCache.Clear();
            _textCacheOrder.Clear();
        }
        var key = (value, size, weight.ToOpenTypeWeight(), brush);
        if (_textCache.TryGetValue(key, out FormattedText? cached))
        {
            return cached;
        }
        FormattedText created = new(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(EmbeddedFontFamilies.Ui, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip);
        _textCache.Add(key, created);
        _textCacheOrder.Enqueue(key);
        while (_textCache.Count > MaximumFormattedTextCacheEntries
            && _textCacheOrder.TryDequeue(out var oldest))
        {
            _textCache.Remove(oldest);
        }
        return created;
    }

    private void EnsureArrangementRowLayout()
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement)
        {
            return;
        }
        TimelineRenderSnapshot? snapshot = Snapshot;
        if (ReferenceEquals(snapshot, _arrangementRowLayoutSnapshot)
            && _arrangementRowLayoutLaneHeight.Equals(LaneHeight))
        {
            return;
        }

        int count = snapshot?.LaneLabels.Count ?? 0;
        double[] offsets = new double[count + 1];
        for (int lane = 0; lane < count; lane++)
        {
            double height = LaneHeight;
            if ((uint)lane < (uint)(snapshot?.ArrangementLanes.Count ?? 0)
                && IsArrangementParentLane(snapshot!.ArrangementLanes[lane].Kind))
            {
                height = Math.Clamp(
                    LaneHeight * ArrangementParentLaneHeightRatio,
                    MinimumArrangementParentLaneHeight,
                    LaneHeight);
            }
            offsets[lane + 1] = offsets[lane] + height;
        }
        _arrangementRowLayoutSnapshot = snapshot;
        _arrangementRowLayoutLaneHeight = LaneHeight;
        _arrangementRowOffsets = offsets;
    }

    private double GetLaneVisualHeight(int lane)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement) return LaneHeight;
        EnsureArrangementRowLayout();
        return (uint)lane < (uint)Math.Max(0, _arrangementRowOffsets.Length - 1)
            ? _arrangementRowOffsets[lane + 1] - _arrangementRowOffsets[lane]
            : LaneHeight;
    }

    private double GetLaneTop(TimelineViewport viewport, int lane, double rulerHeight)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement)
            return rulerHeight + (lane - viewport.FirstLane) * LaneHeight;
        EnsureArrangementRowLayout();
        int maximum = Math.Max(0, _arrangementRowOffsets.Length - 1);
        int first = Math.Clamp(viewport.FirstLane, 0, maximum);
        int target = Math.Clamp(lane, 0, maximum);
        return rulerHeight + _arrangementRowOffsets[target] - _arrangementRowOffsets[first];
    }

    private int YToLane(TimelineViewport viewport, double relativeY)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement) return viewport.YToLane(relativeY);
        EnsureArrangementRowLayout();
        int count = Math.Max(0, _arrangementRowOffsets.Length - 1);
        if (count == 0) return viewport.FirstLane;
        int first = Math.Clamp(viewport.FirstLane, 0, count - 1);
        int lastExclusive = Math.Clamp(viewport.LastLaneExclusive, first + 1, count);
        double maximumY = Math.Max(0, GetLaneContentHeight(viewport) - double.Epsilon);
        double worldY = _arrangementRowOffsets[first] + Math.Clamp(relativeY, 0, maximumY);
        int low = first;
        int high = lastExclusive;
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            if (_arrangementRowOffsets[middle] <= worldY) low = middle;
            else high = middle;
        }
        return Math.Clamp(low, first, lastExclusive - 1);
    }

    private int CountArrangementRowsThatFit(int firstLane, double contentHeight, bool includePartial)
    {
        EnsureArrangementRowLayout();
        int total = Math.Max(0, _arrangementRowOffsets.Length - 1);
        if (total == 0) return 1;
        int first = Math.Clamp(firstLane, 0, total - 1);
        double limit = _arrangementRowOffsets[first] + Math.Max(0, contentHeight);
        int count = 0;
        for (int lane = first; lane < total; lane++)
        {
            double rowBottom = _arrangementRowOffsets[lane + 1];
            if (includePartial)
            {
                if (_arrangementRowOffsets[lane] >= limit && count != 0) break;
                count++;
                if (rowBottom >= limit) break;
            }
            else
            {
                if (rowBottom > limit) break;
                count++;
            }
        }
        return Math.Max(1, count);
    }

    private int ComputeArrangementMaximumFirstLane()
    {
        EnsureArrangementRowLayout();
        int total = Math.Max(0, _arrangementRowOffsets.Length - 1);
        if (total <= 1) return 0;
        double contentHeight = Math.Max(0, ActualHeight - GetRulerHeight());
        int first = total - 1;
        while (first > 0
            && _arrangementRowOffsets[total] - _arrangementRowOffsets[first] < contentHeight)
        {
            first--;
        }
        if (first < total - 1
            && _arrangementRowOffsets[total] - _arrangementRowOffsets[first] > contentHeight)
        {
            first++;
        }
        return first;
    }

    private double GetLaneHeaderWidth() => !double.IsNaN(LaneHeaderWidthOverride) ? LaneHeaderWidthOverride : SurfaceMode switch
    {
        TimelineSurfaceMode.Arrangement => ArrangementLaneHeaderWidth,
        TimelineSurfaceMode.PianoRoll => 52,
        TimelineSurfaceMode.EventLanes => 52,
        TimelineSurfaceMode.Conductor => 130,
        TimelineSurfaceMode.Velocity => 52,
        _ => 0
    };

    private double GetRulerHeight() => SurfaceMode switch
    {
        TimelineSurfaceMode.General => 0,
        TimelineSurfaceMode.Arrangement => 32,
        _ => 24
    };

    private void EnsurePens(
        Brush border,
        Brush info,
        Brush text,
        Brush red,
        Brush redSubtle,
        Brush success,
        Brush successSubtle,
        Brush segmentSelection)
    {
        if (ReferenceEquals(_penBorderBrush, border)
            && ReferenceEquals(_penInfoBrush, info)
            && ReferenceEquals(_penTextBrush, text)
            && ReferenceEquals(_penRedBrush, red)
            && ReferenceEquals(_penRedSubtleBrush, redSubtle)
            && ReferenceEquals(_penSuccessBrush, success)
            && ReferenceEquals(_penSuccessSubtleBrush, successSubtle)
            && ReferenceEquals(_penSegmentSelectionBrush, segmentSelection))
        {
            return;
        }

        _penBorderBrush = border;
        _penInfoBrush = info;
        _penTextBrush = text;
        _penRedBrush = red;
        _penRedSubtleBrush = redSubtle;
        _penSuccessBrush = success;
        _penSuccessSubtleBrush = successSubtle;
        _penSegmentSelectionBrush = segmentSelection;
        _borderPen = FrozenPen(border, 1);
        _infoPen = FrozenPen(info, 1);
        _textPen = FrozenPen(text, 2);
        _redPen = FrozenPen(red, 1);
        _successPen = FrozenPen(success, 1);
        Brush selection = Brush("Brush.Red.Hover", Color.FromRgb(255, 96, 101));
        _selectionPen = FrozenPen(selection, 2);
        Brush trackSelectionOutline = red.Clone();
        trackSelectionOutline.Opacity *= 0.6;
        trackSelectionOutline.Freeze();
        _trackSelectionPen = FrozenPen(trackSelectionOutline, 1);
        Brush segmentSelectionOutline = segmentSelection.Clone();
        segmentSelectionOutline.Opacity *= 0.6;
        segmentSelectionOutline.Freeze();
        _segmentSelectionPen = FrozenPen(segmentSelectionOutline, 2);
        Brush beatGrid = border.Clone();
        beatGrid.Opacity = 0.32;
        beatGrid.Freeze();
        _beatGridPen = FrozenPen(beatGrid, 1);
        _editCursorPen = FrozenPen(info, 1, DashStyles.Dash);
        _marqueePen = FrozenPen(info, 1, DashStyles.Dash);
        _warningDashPen = FrozenPen(
            Brush("Brush.Warning", Color.FromRgb(232, 179, 75)),
            1,
            DashStyles.Dash);
        _groupDetachBoundaryPen = FrozenPen(info, 3);
        Brush dragPreview = info.Clone();
        dragPreview.Opacity *= 0.88;
        dragPreview.Freeze();
        _dragPreviewPen = FrozenPen(dragPreview, 1);
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dashStyle = null)
    {
        Pen pen = new(brush, thickness) { DashStyle = dashStyle ?? DashStyles.Solid };
        pen.Freeze();
        return pen;
    }

    private void ClearItemDrag()
    {
        EndDragPitchPreview();
        _dragItem = null;
        _dragActivated = false;
        _dragCopyRequested = false;
        _deferredControlClickToggle = false;
        _deferredPlainDrawSegmentSelection = false;
        _dragTimeLocked = false;
        _dragModifiers = ModifierKeys.None;
        _dragPreviewSelection = null;
        _dragPreviewSelectionPrepared = false;
        _dragPreviewIdPrefetchSignature = null;
        _resizePreviewCancellation.Cancel();
        _resizePreviewCancellation.Dispose();
        _resizePreviewCancellation = new();
        _requestedResizePreviewKeys.Clear();
        _resizePreviewSignature = null;
        _resizePreviewGeneration = checked(_resizePreviewGeneration + 1);
        InvalidateDragPreviewGeometry();
        Cursor = Cursors.Arrow;
    }

    private void PrepareDragPitchPreview(TimelineRenderItem hit)
    {
        _dragPitchPreviewActive = false;
        _dragPitchPreviewLastPitch = -1;
        if (SurfaceMode != TimelineSurfaceMode.PianoRoll
            || _dragKind != TimelineItemEditKind.Move
            || hit.Kind is not TimelineItemKind.LogicalNote
                and not TimelineItemKind.DirectMidiNote
                and not TimelineItemKind.TemplateNote)
        {
            return;
        }

        TimelineRenderItem anchor = SelectionSnapshot?.Contains(hit.Id) == true
            && SelectionSnapshot.TryGetMetrics(hit.Kind, out TimelineSelectionMetrics metrics)
                ? metrics.EarliestItem
                : hit;
        _dragPitchPreviewAnchorLane = anchor.Lane;
        _dragPitchPreviewLastPitch = Math.Clamp(127 - anchor.Lane, 0, 127);
        double velocity = anchor.Value <= 1d ? anchor.Value * 127d : anchor.Value;
        _dragPitchPreviewVelocity = Math.Clamp(
            (int)Math.Round(velocity, MidpointRounding.AwayFromZero),
            1,
            127);
    }

    private void UpdateDragPitchPreview()
    {
        if (_dragPitchPreviewLastPitch < 0)
        {
            return;
        }
        int laneDelta = checked(_dragCurrentLane - _dragOriginLane);
        int pitch = Math.Clamp(127 - checked(_dragPitchPreviewAnchorLane + laneDelta), 0, 127);
        if (pitch == _dragPitchPreviewLastPitch)
        {
            return;
        }
        _dragPitchPreviewLastPitch = pitch;
        _dragPitchPreviewActive = true;
        PitchPreviewRequested?.Invoke(
            this,
            new TimelinePitchPreviewEventArgs(pitch, _dragPitchPreviewVelocity));
    }

    private void EndDragPitchPreview()
    {
        if (_dragPitchPreviewActive)
        {
            _dragPitchPreviewActive = false;
            PitchPreviewReleased?.Invoke(this, EventArgs.Empty);
        }
        _dragPitchPreviewLastPitch = -1;
    }

    private readonly record struct DragPreviewTransform(
        long TickDelta,
        int LaneDelta,
        double ValueDelta);

    private readonly record struct DragPreviewIdPrefetchSignature(
        TimelineRenderSnapshot Snapshot,
        long SelectionRevision);

    private readonly record struct ExactRasterProjectionSignature(
        TimelineSurfaceMode SurfaceMode,
        long HorizontalScaleKey,
        long VerticalScaleKey);

    private readonly record struct PianoKeyboardDrawingKey(
        int FirstLane,
        int LaneCount,
        long LaneHeightBits,
        long LaneHeaderWidthBits,
        long RulerHeightBits,
        long ActualHeightBits,
        int HighlightedPitch,
        Brush WhiteKey,
        Brush BlackKey,
        Brush LabelBrush,
        Brush HighlightedWhiteKey,
        Brush HighlightedBlackKey,
        Pen? BorderPen);

    private readonly record struct ResizeLaneLayoutKey(
        string ProjectionKey,
        TimelineSurfaceMode SurfaceMode,
        int FirstLane,
        int LaneCount,
        long LaneHeightBits,
        long RulerHeightBits,
        int DpiY);

    private readonly record struct ResizePreviewSignature(
        long SemanticRevision,
        string ProjectionKey,
        ulong ContentFingerprint,
        long SelectionRevision,
        MidoraId AnchorId,
        TimelineResizeEdge Edge,
        long TickDelta,
        long MinimumLength,
        long ViewportStartTick,
        long ViewportEndTick,
        int ViewportFirstLane,
        long PixelsPerTickBits,
        long LaneHeightBits,
        int TileColumns,
        int TileRows,
        int DpiX,
        int DpiY);

    private readonly record struct DragPreviewGeometryKey(
        ulong ContentFingerprint,
        long SelectionRevision,
        MidoraId AnchorId,
        TimelineItemKind ItemKind,
        TimelineItemEditKind EditKind,
        long TickDelta,
        int LaneDelta,
        long ValueDeltaBits,
        long ViewportStartTick,
        long ViewportEndTick,
        int ViewportFirstLane,
        int ViewportLastLaneExclusive,
        long PixelsPerTickBits,
        long LaneHeightBits,
        long ValueViewMinimumBits,
        long ValueViewMaximumBits,
        long LaneHeaderWidthBits,
        long RulerHeightBits,
        long ActualWidthBits,
        long ActualHeightBits);

    private readonly record struct ExactPrefetchSignature(
        long SemanticRevision,
        string ProjectionKey,
        long StartTick,
        long EndTick,
        int FirstLane,
        int LastLaneExclusive);

    private readonly record struct TileFingerprintRequestKey(
        TimelineRasterLayer Layer,
        long SemanticRevision,
        string ProjectionKey,
        long SelectionRevision,
        long HorizontalScaleKey,
        long VerticalScaleKey,
        long TileX,
        long TileY,
        int DpiX,
        int DpiY);

    private readonly record struct PianoTileDrawEntry(
        TimelineRasterCacheKey Key,
        BitmapSource Bitmap);

    private readonly record struct PianoRasterProjectionSignature(
        string ProjectionKey,
        long HorizontalScaleKey,
        long VerticalScaleKey,
        int DpiX,
        int DpiY);

    private sealed record PianoCompositeRasterFrame(
        PianoRasterProjectionSignature Projection,
        PianoTileDrawEntry[] NoteTiles,
        PianoTileDrawEntry[] SelectionTiles,
        long SelectionRevision);

    private readonly record struct VelocityTileDrawEntry(
        TimelineRasterCacheKey Key,
        BitmapSource Bitmap);

    private readonly record struct VelocityRasterProjectionSignature(
        string ProjectionKey,
        int HorizontalLod,
        int DpiX,
        int DpiY);

    private sealed record VelocityCompositeRasterFrame(
        VelocityRasterProjectionSignature Projection,
        VelocityTileDrawEntry[] Tiles);

    private readonly record struct EventPointTileDrawEntry(
        TimelineRasterCacheKey Key,
        BitmapSource Bitmap);

    private readonly record struct EventPointRasterProjectionSignature(
        string ProjectionKey,
        long HorizontalScaleKey,
        long VerticalScaleKey,
        int DpiX,
        int DpiY);

    private sealed record EventPointCompositeRasterFrame(
        EventPointRasterProjectionSignature Projection,
        EventPointTileDrawEntry[] Tiles);

    private readonly record struct SegmentPreviewWarmupRequest(
        TimelineRasterCacheKey Key,
        Func<CancellationToken, TimelineRasterBuffer> Factory);

    private readonly record struct SegmentPreviewDetailTile(
        Rect Destination,
        BitmapSource Bitmap);

    private readonly record struct SegmentPreviewFallbackFrame(
        int Lod,
        long ContentWidth,
        int TileCount,
        BitmapSource? Tile0,
        BitmapSource? Tile1,
        BitmapSource? Tile2,
        BitmapSource? Tile3)
    {
        public BitmapSource GetBitmap(int tile) => tile switch
        {
            0 when Tile0 is not null => Tile0,
            1 when Tile1 is not null => Tile1,
            2 when Tile2 is not null => Tile2,
            3 when Tile3 is not null => Tile3,
            _ => throw new ArgumentOutOfRangeException(nameof(tile))
        };
    }

    private sealed record SegmentAccentResources(
        SolidColorBrush Segment,
        SolidColorBrush SelectedSegment,
        SolidColorBrush NotePreview,
        Pen SelectionPen);

}
