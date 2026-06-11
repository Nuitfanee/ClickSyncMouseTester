using ClickSyncMouseTester.ChartGpu;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Controls;

[SupportedOSPlatform("windows")]
public class MousePerformanceChartControl : FrameworkElement
{
    private struct HeaderLayout
    {
        public FormattedText TitleText { get; }

        public Point TitleOrigin { get; }

        public FormattedText SubtitleText { get; }

        public Point SubtitleOrigin { get; }

        public FormattedText DescriptionText { get; }

        public Point DescriptionOrigin { get; }

        public double PlotAreaTop { get; }

        public HeaderLayout(FormattedText titleText, Point titleOrigin, FormattedText subtitleText, Point subtitleOrigin, FormattedText descriptionText, Point descriptionOrigin, double plotAreaTop)
        {
            TitleText = titleText;
            TitleOrigin = titleOrigin;
            SubtitleText = subtitleText;
            SubtitleOrigin = subtitleOrigin;
            DescriptionText = descriptionText;
            DescriptionOrigin = descriptionOrigin;
            PlotAreaTop = plotAreaTop;
        }
    }

    private sealed class GapMarker
    {
        public double StartX { get; }

        public double EndX { get; }

        public double DurationMs { get; }

        public bool TouchesViewportBoundary { get; }

        public GapMarker(double startX, double endX, bool touchesViewportBoundary)
        {
            StartX = Math.Min(startX, endX);
            EndX = Math.Max(startX, endX);
            DurationMs = Math.Abs(endX - startX);
            TouchesViewportBoundary = touchesViewportBoundary;
        }
    }

    private sealed class VisiblePointCacheEntry
    {
        public IReadOnlyList<MousePerformanceChartPoint> Points { get; }

        public MousePerformancePlotType PlotType { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public bool IncludeNeighbors { get; }

        public double XOffset { get; }

        public IReadOnlyList<MousePerformanceChartPoint> VisiblePoints { get; }

        public VisiblePointCacheEntry(IReadOnlyList<MousePerformanceChartPoint> points, MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, bool includeNeighbors, double xOffset, IReadOnlyList<MousePerformanceChartPoint> visiblePoints)
        {
            Points = points;
            PlotType = plotType;
            TimeBasis = timeBasis;
            IncludeNeighbors = includeNeighbors;
            XOffset = xOffset;
            VisiblePoints = visiblePoints ?? Array.Empty<MousePerformanceChartPoint>();
        }
    }

    private sealed class GapMarkerCacheEntry
    {
        public MousePerformanceChartGapSource Source { get; }

        public MousePerformancePlotType PlotType { get; }

        public MousePerformanceTimeBasis TimeBasis { get; }

        public IReadOnlyList<GapMarker> GapMarkers { get; }

        public GapMarkerCacheEntry(MousePerformanceChartGapSource source, MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, IReadOnlyList<GapMarker> gapMarkers)
        {
            Source = source;
            PlotType = plotType;
            TimeBasis = timeBasis;
            GapMarkers = gapMarkers ?? Array.Empty<GapMarker>();
        }
    }

    private sealed class PlotBitmapCacheEntry
    {
        public MousePerformanceChartRenderFrame Frame { get; }

        public ChartViewport Viewport { get; }

        public IReadOnlyList<GapMarker> GapMarkers { get; }

        public bool ShowGapOverlay { get; }

        public MousePerformanceChartDatasetSlot GapAnalysisDatasetSlot { get; }

        public double LogicalWidth { get; }

        public double LogicalHeight { get; }

        public int PixelWidth { get; }

        public int PixelHeight { get; }

        public ImageSource Bitmap { get; }

        public PlotBitmapCacheEntry(MousePerformanceChartRenderFrame frame, ChartViewport viewport, IReadOnlyList<GapMarker> gapMarkers, bool showGapOverlay, MousePerformanceChartDatasetSlot gapAnalysisDatasetSlot, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight, ImageSource bitmap)
        {
            Frame = frame;
            Viewport = viewport;
            GapMarkers = gapMarkers;
            ShowGapOverlay = showGapOverlay;
            GapAnalysisDatasetSlot = gapAnalysisDatasetSlot;
            LogicalWidth = logicalWidth;
            LogicalHeight = logicalHeight;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            Bitmap = bitmap;
        }
    }

    private readonly struct DataPointChunkCacheKey
    {
        private readonly object _points;

        private readonly double _xOffset;

        public DataPointSourceCacheKey Source => new DataPointSourceCacheKey(_points, _xOffset);

        public DataPointChunkCacheKey(object points, double xOffset)
        {
            _points = points;
            _xOffset = xOffset;
        }

        public override bool Equals(object obj)
        {
            return obj is DataPointChunkCacheKey other && ReferenceEquals(_points, other._points) && _xOffset.Equals(other._xOffset);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(RuntimeHelpers.GetHashCode(_points), _xOffset);
        }
    }

    private readonly struct DataSegmentChunkCacheKey
    {
        private readonly object _points;

        private readonly MousePerformanceChartSeriesKind _kind;

        private readonly double _xOffset;

        private readonly object _geometryKey;

        public DataPointSourceCacheKey Source => new DataPointSourceCacheKey(_points, _xOffset);

        public DataSegmentChunkCacheKey(object points, MousePerformanceChartSeriesKind kind, double xOffset, object geometryKey)
        {
            _points = points;
            _kind = kind;
            _xOffset = xOffset;
            _geometryKey = geometryKey;
        }

        public override bool Equals(object obj)
        {
            return obj is DataSegmentChunkCacheKey other && ReferenceEquals(_points, other._points) && _kind == other._kind && _xOffset.Equals(other._xOffset) && ReferenceEquals(_geometryKey, other._geometryKey);
        }

        public override int GetHashCode()
        {
            int geometryHashCode = _geometryKey != null ? RuntimeHelpers.GetHashCode(_geometryKey) : 0;
            return HashCode.Combine(RuntimeHelpers.GetHashCode(_points), _kind, _xOffset, geometryHashCode);
        }
    }

    private readonly struct HistogramBinChunkCacheKey
    {
        private readonly object _bins;

        private readonly double _xOffset;

        private readonly double _groupScale;

        public DataPointSourceCacheKey Source => new DataPointSourceCacheKey(_bins, _xOffset);

        public HistogramBinChunkCacheKey(object bins, double xOffset, double groupScale)
        {
            _bins = bins;
            _xOffset = xOffset;
            _groupScale = groupScale;
        }

        public override bool Equals(object obj)
        {
            return obj is HistogramBinChunkCacheKey other && ReferenceEquals(_bins, other._bins) && _xOffset.Equals(other._xOffset) && _groupScale.Equals(other._groupScale);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(RuntimeHelpers.GetHashCode(_bins), _xOffset, _groupScale);
        }
    }

    private readonly struct DataPointSourceCacheKey
    {
        private readonly object _points;

        private readonly double _xOffset;

        public DataPointSourceCacheKey(object points, double xOffset)
        {
            _points = points;
            _xOffset = xOffset;
        }

        public override bool Equals(object obj)
        {
            return obj is DataPointSourceCacheKey other && ReferenceEquals(_points, other._points) && _xOffset.Equals(other._xOffset);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_points != null ? RuntimeHelpers.GetHashCode(_points) : 0, _xOffset);
        }
    }

    private struct TextLayoutCacheKey
    {
        public string Text { get; }

        public double FontSize { get; }

        public bool Strong { get; }

        public int BrushRole { get; }

        public double MaxTextWidth { get; }

        public double MaxTextHeight { get; }

        public TextAlignment TextAlignment { get; }

        public double PixelsPerDip { get; }

        public TextLayoutCacheKey(string content, double fontSize, bool strong, int brushRole, double maxTextWidth, double maxTextHeight, TextAlignment textAlignment, double pixelsPerDip)
        {
            Text = content ?? string.Empty;
            FontSize = fontSize;
            Strong = strong;
            BrushRole = brushRole;
            MaxTextWidth = maxTextWidth;
            MaxTextHeight = maxTextHeight;
            TextAlignment = textAlignment;
            PixelsPerDip = pixelsPerDip;
        }
    }

    private struct ChartViewport
    {
        public double XMinimum { get; }

        public double XMaximum { get; }

        public double YMinimum { get; }

        public double YMaximum { get; }

        public ChartViewport(double xMinimum, double xMaximum, double yMinimum, double yMaximum)
        {
            XMinimum = xMinimum;
            XMaximum = xMaximum;
            YMinimum = yMinimum;
            YMaximum = yMaximum;
        }
    }

    private struct AxisRange
    {
        public double Minimum { get; }

        public double Maximum { get; }

        public AxisRange(double minimum, double maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
        }
    }


    private const double DefaultLeftMargin = 78.0;

    private const double DefaultTopMargin = 72.0;

    private const double DefaultRightMargin = 28.0;

    private const double DefaultBottomMargin = 62.0;

    private const double HeaderHorizontalPadding = 20.0;

    private const double HeaderVerticalPadding = 12.0;

    private const double HeaderBlockSpacing = 18.0;

    private const double HeaderTitleSubtitleSpacing = 4.0;

    private const double HeaderSubtitleMaxHeight = 30.0;

    private const double HeaderDescriptionStackSpacing = 12.0;

    private const double HeaderDescriptionTopOffset = 3.0;

    private const double HeaderDescriptionMaxHeight = 36.0;

    private const double HeaderBottomPadding = 10.0;

    private const double HeaderDescriptionMinimumInlineWidth = 220.0;

    private const int GridLineCount = 5;

    private const int MinorGridSubdivisionCount = 5;

    private const double AxisZeroTolerance = 1E-06;

    private const double AxisLabelSpacingPixels = 4.0;

    private const double MajorAxisLabelFontSize = 11.0;

    private const double MinorAxisLabelFontSize = 9.5;

    private const double HistogramBarValueLabelFontSize = 10.0;

    private const int HistogramAxisBoundaryLabelLimit = 24;

    private const int HistogramBarValueLabelLimit = 96;

    private const double MinorAxisLabelOpacityFactor = 0.68;

    private const float ZeroAxisThicknessPixels = 1.35f;

    private const double MinorGridOpacityFactor = 0.38;

    private const double MinorGridThickness = 0.55;

    private const double ScatterRadius = 1.55;

    private const double MinimumViewportSpan = 0.0001;

    private const double LineOpacityFactor = 0.58;

    private const double ContinuousEstimateLineOpacityFactor = 0.92;

    private const double StemOpacityFactor = 0.16;

    private const double ScatterOpacityFactor = 0.66;

    private const double LineThickness = 1.05;

    private const double ContinuousEstimateLineThickness = 1.75;

    private const double StemThickness = 0.7;

    private const double GapBandMinimumWidth = 2.0;

    private const double GapBandOpacityFactor = 0.08;

    private const double GapLineOpacityFactor = 0.2;

    private const double GapLineThickness = 0.9;

    private const double GapThresholdMultiplier = 3.0;

    private const double GapThresholdMinimumMs = 0.5;

    private const int GpuChunkSize = 8192;

    private const int GpuChunkCacheRetainedInactiveSourceCount = 8;

    private const double AutomaticViewportZeroFocusThreshold = 1E-06;

    public static readonly DependencyProperty RenderFrameProperty = DependencyProperty.Register("RenderFrame", typeof(MousePerformanceChartRenderFrame), typeof(MousePerformanceChartControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, new PropertyChangedCallback(OnRenderFrameChanged)));

    public static readonly DependencyProperty ShowGapOverlayProperty = DependencyProperty.Register("ShowGapOverlay", typeof(bool), typeof(MousePerformanceChartControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapAnalysisDatasetSlotProperty = DependencyProperty.Register("GapAnalysisDatasetSlot", typeof(MousePerformanceChartDatasetSlot), typeof(MousePerformanceChartControl), new FrameworkPropertyMetadata(MousePerformanceChartDatasetSlot.Baseline, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush _backgroundBrush;

    private Brush _panelBrush;

    private Pen _axisPen;

    private Pen _gridPen;

    private Pen _minorGridPen;

    private Brush _primaryBrush;

    private Pen _primaryPen;

    private Pen _primaryContinuousEstimatePen;

    private Pen _primaryStemPen;

    private Brush _secondaryBrush;

    private Pen _secondaryPen;

    private Pen _secondaryContinuousEstimatePen;

    private Pen _secondaryStemPen;

    private Brush _accentBrush;

    private Pen _accentPen;

    private Pen _accentContinuousEstimatePen;

    private Pen _accentStemPen;

    private Brush _legacySingleSessionXBrush;

    private Pen _legacySingleSessionXPen;

    private Pen _legacySingleSessionXContinuousEstimatePen;

    private Pen _legacySingleSessionXStemPen;

    private Brush _legacySingleSessionYBrush;

    private Pen _legacySingleSessionYPen;

    private Pen _legacySingleSessionYContinuousEstimatePen;

    private Pen _legacySingleSessionYStemPen;

    private Brush _neutralBrush;

    private Pen _neutralPen;

    private Pen _neutralStemPen;

    private Brush _gapBrush;

    private Pen _gapPen;

    private Brush _labelBrush;

    private Brush _minorAxisLabelBrush;

    private Brush _strongLabelBrush;

    private bool _isThemeSubscribed;

    private bool _isLocalizationSubscribed;

    private double _viewXMinimum;

    private double _viewXMaximum;

    private double _viewYMinimum;

    private double _viewYMaximum;

    private bool _hasCustomViewport;

    private bool _isHistogramPanning;

    private Point _histogramPanStartPoint;

    private ChartViewport _histogramPanStartViewport;

    private int _visibleGapCount;

    private double? _visibleGapAverageDurationMs;

    private bool _isGpuRendererAvailable;

    private string _gpuRendererFailureMessage;

    private bool _deferredVisualRefreshPending;

    private ChartViewport _cachedAutomaticViewport;

    private bool _hasCachedAutomaticViewport;

    private MousePerformanceChartRenderFrame _cachedAutomaticViewportFrame;

    private ChartViewport _cachedVisiblePointViewport;

    private bool _hasCachedVisiblePointViewport;

    private GapMarkerCacheEntry _cachedGapMarkerEntry;

    private PlotBitmapCacheEntry _cachedPlotBitmapEntry;

    private readonly List<VisiblePointCacheEntry> _visiblePointCacheEntries;

    private readonly Dictionary<IReadOnlyList<MousePerformanceChartPoint>, bool> _rawCaptureMonotonicityCache;

    private readonly Dictionary<TextLayoutCacheKey, FormattedText> _textLayoutCache;

    private readonly Dictionary<DataPointChunkCacheKey, GpuPointChunk[]> _gpuDataPointChunkCache;

    private readonly Dictionary<DataSegmentChunkCacheKey, GpuSegmentChunk[]> _gpuDataSegmentChunkCache;

    private readonly Dictionary<HistogramBinChunkCacheKey, GpuHistogramBinChunk[]> _gpuHistogramBinChunkCache;

    private readonly List<DataPointSourceCacheKey> _gpuDataChunkCacheRecentSources;

    private readonly MousePerformanceChartGpuHost _gpuHost;

    private MousePerformanceChartGpuOffscreenRenderer _offscreenGpuRenderer;

    protected override int VisualChildrenCount => 1;

    public MousePerformanceChartRenderFrame RenderFrame
    {
        get
        {
            return (MousePerformanceChartRenderFrame)GetValue(RenderFrameProperty);
        }
        set
        {
            SetValue(RenderFrameProperty, value);
        }
    }

    public bool ShowGapOverlay
    {
        get
        {
            return (bool)GetValue(ShowGapOverlayProperty);
        }
        set
        {
            SetValue(ShowGapOverlayProperty, value);
        }
    }

    public MousePerformanceChartDatasetSlot GapAnalysisDatasetSlot
    {
        get
        {
            return (MousePerformanceChartDatasetSlot)GetValue(GapAnalysisDatasetSlotProperty);
        }
        set
        {
            SetValue(GapAnalysisDatasetSlotProperty, value);
        }
    }

    public int VisibleGapCount => _visibleGapCount;

    public double? VisibleGapAverageDurationMs => _visibleGapAverageDurationMs;

    public bool IsGpuRendererAvailable => _isGpuRendererAvailable;

    public string GpuRendererFailureMessage => _gpuRendererFailureMessage;

    public event EventHandler VisibleGapCountChanged;

    public event EventHandler GpuRendererAvailabilityChanged;

    public MousePerformanceChartControl()
    {
        _gpuRendererFailureMessage = string.Empty;
        _visiblePointCacheEntries = new List<VisiblePointCacheEntry>();
        _rawCaptureMonotonicityCache = new Dictionary<IReadOnlyList<MousePerformanceChartPoint>, bool>(ReferenceEqualityComparer.Instance);
        _textLayoutCache = new Dictionary<TextLayoutCacheKey, FormattedText>();
        _gpuDataPointChunkCache = new Dictionary<DataPointChunkCacheKey, GpuPointChunk[]>();
        _gpuDataSegmentChunkCache = new Dictionary<DataSegmentChunkCacheKey, GpuSegmentChunk[]>();
        _gpuHistogramBinChunkCache = new Dictionary<HistogramBinChunkCacheKey, GpuHistogramBinChunk[]>();
        _gpuDataChunkCacheRecentSources = new List<DataPointSourceCacheKey>();
        base.SnapsToDevicePixels = true;
        base.UseLayoutRounding = true;
        base.Focusable = true;
        _gpuHost = new MousePerformanceChartGpuHost
        {
            Visibility = Visibility.Hidden
        };
        AddVisualChild(_gpuHost);
        AddLogicalChild(_gpuHost);
        _gpuHost.ViewportChanged += OnGpuHostViewportChanged;
        ApplyThemeResources();
        base.Loaded += OnLoaded;
        base.Unloaded += OnUnloaded;
    }

    protected override Visual GetVisualChild(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0, "index");
        return _gpuHost;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _gpuHost.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 0.0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0.0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect finalRect = CoerceArrangeRect(ResolveGpuHostArrangeRect(finalSize, RenderFrame));
        _gpuHost.Arrange(finalRect);
        return finalSize;
    }

    public void ResetViewport()
    {
        if (RenderFrame == null || !RenderFrame.IsAvailable)
        {
            ClearViewportState();
            UpdateGpuHostScene(Rect.Empty, new ChartViewport(0.0, 1.0, 0.0, 1.0), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<GapMarker>(), null);
            InvalidateVisual();
        }
        else
        {
            ClearViewportState();
            InvalidateVisual();
        }
    }

    public new bool Focus()
    {
        bool focused = base.Focus();
        if (_gpuHost != null && _gpuHost.Visibility == Visibility.Visible)
        {
            focused = _gpuHost.FocusHost() || focused;
        }
        return focused;
    }

    public bool TryHandleWindowMouseWheel(Point screenPoint, int wheelDelta, bool shiftPressed, bool controlPressed)
    {
        if (wheelDelta == 0 || !CanForwardWheelInteraction(screenPoint))
        {
            return false;
        }
        MousePerformanceChartRenderFrame frame = RenderFrame;
        if (MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            return TryHandleWpfMouseWheel(screenPoint, wheelDelta, shiftPressed, controlPressed, frame);
        }
        return _gpuHost.TryHandleMouseWheelFromScreen((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y), wheelDelta, shiftPressed, controlPressed);
    }

    private bool CanForwardWheelInteraction(Point screenPoint)
    {
        MousePerformanceChartRenderFrame frame = RenderFrame;
        if (frame == null || !frame.IsAvailable)
        {
            return false;
        }
        if (!MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType) && (_gpuHost == null || _gpuHost.Visibility != Visibility.Visible || !_gpuHost.IsRendererAvailable))
        {
            return false;
        }
        Point localPoint = PointFromScreen(screenPoint);
        Rect plotArea = GetPlotArea(new Size(base.ActualWidth, base.ActualHeight), frame);
        return plotArea.Width > 0.0 && plotArea.Height > 0.0 && plotArea.Contains(localPoint);
    }

    private bool TryHandleWpfMouseWheel(Point screenPoint, int wheelDelta, bool shiftPressed, bool controlPressed, MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || !frame.IsAvailable)
        {
            return false;
        }

        Rect plotArea = GetPlotArea(new Size(base.ActualWidth, base.ActualHeight), frame);
        Point localPoint = PointFromScreen(screenPoint);
        ChartViewport viewport = ResolveViewport(frame);
        ChartViewport zoomedViewport = BuildWheelZoomViewport(frame, viewport, localPoint.X - plotArea.Left, localPoint.Y - plotArea.Top, plotArea.Width, plotArea.Height, wheelDelta, shiftPressed, controlPressed);
        ApplyViewport(frame, zoomedViewport);
        InvalidateVisual();
        return true;
    }

    private static ChartViewport BuildWheelZoomViewport(MousePerformanceChartRenderFrame frame, ChartViewport viewport, double positionX, double positionY, double width, double height, int wheelDelta, bool shiftPressed, bool controlPressed)
    {
        bool zoomX = true;
        bool zoomY = true;
        if (shiftPressed && !controlPressed)
        {
            zoomY = false;
        }
        else if (controlPressed && !shiftPressed)
        {
            zoomX = false;
        }

        double zoomFactor = wheelDelta > 0 ? 1.0 / 1.18 : 1.18;
        double xMinimum = viewport.XMinimum;
        double xMaximum = viewport.XMaximum;
        double yMinimum = viewport.YMinimum;
        double yMaximum = viewport.YMaximum;
        if (zoomX && width > 0.0)
        {
            double anchorRatio = Math.Max(0.0, Math.Min(1.0, positionX / width));
            double anchorX = viewport.XMinimum + (viewport.XMaximum - viewport.XMinimum) * anchorRatio;
            double nextSpan = (viewport.XMaximum - viewport.XMinimum) * zoomFactor;
            xMinimum = anchorX - anchorRatio * nextSpan;
            xMaximum = xMinimum + nextSpan;
        }
        if (zoomY && height > 0.0)
        {
            double anchorRatio = Math.Max(0.0, Math.Min(1.0, (height - positionY) / height));
            double anchorY = viewport.YMinimum + (viewport.YMaximum - viewport.YMinimum) * anchorRatio;
            double nextSpan = (viewport.YMaximum - viewport.YMinimum) * zoomFactor;
            yMinimum = anchorY - anchorRatio * nextSpan;
            yMaximum = yMinimum + nextSpan;
        }

        return ClampViewport(frame, new ChartViewport(xMinimum, xMaximum, yMinimum, yMaximum));
    }

    private static ChartViewport BuildPannedViewport(MousePerformanceChartRenderFrame frame, ChartViewport panStartViewport, double startX, double startY, double currentX, double currentY, double width, double height)
    {
        if (frame == null)
        {
            return panStartViewport;
        }

        double deltaX = currentX - startX;
        double deltaY = currentY - startY;
        double viewportWidth = panStartViewport.XMaximum - panStartViewport.XMinimum;
        double viewportHeight = panStartViewport.YMaximum - panStartViewport.YMinimum;
        double xOffset = width <= 0.0 ? 0.0 : -deltaX * viewportWidth / width;
        double yDirection = IsScreenYAxisPositiveDown(frame.PlotType) ? -1.0 : 1.0;
        double yOffset = height <= 0.0 ? 0.0 : yDirection * deltaY * viewportHeight / height;

        return ClampViewport(frame, new ChartViewport(
            panStartViewport.XMinimum + xOffset,
            panStartViewport.XMaximum + xOffset,
            panStartViewport.YMinimum + yOffset,
            panStartViewport.YMaximum + yOffset));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e == null || e.Handled)
        {
            return;
        }

        Point localPoint = e.GetPosition(this);
        if (!TryResolveHistogramPlotArea(out MousePerformanceChartRenderFrame frame, out Rect plotArea) || !plotArea.Contains(localPoint))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            CancelHistogramPan();
            ResetViewport();
            Focus();
            e.Handled = true;
            return;
        }

        if (BeginHistogramPan(frame, localPoint))
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e == null || !_isHistogramPanning)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelHistogramPan();
            return;
        }

        if (UpdateHistogramPan(e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (e == null || !_isHistogramPanning)
        {
            return;
        }

        UpdateHistogramPan(e.GetPosition(this));
        CancelHistogramPan();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelHistogramPan();
    }

    private bool BeginHistogramPan(MousePerformanceChartRenderFrame frame, Point localPoint)
    {
        if (_isHistogramPanning || frame == null || !frame.IsAvailable || !MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            return false;
        }
        if (!CaptureMouse())
        {
            return false;
        }

        _isHistogramPanning = true;
        _histogramPanStartPoint = localPoint;
        _histogramPanStartViewport = ResolveViewport(frame);
        Focus();
        return true;
    }

    private bool UpdateHistogramPan(Point localPoint)
    {
        if (!_isHistogramPanning)
        {
            return false;
        }
        if (!TryResolveHistogramPlotArea(out MousePerformanceChartRenderFrame frame, out Rect plotArea))
        {
            CancelHistogramPan();
            return false;
        }

        ChartViewport pannedViewport = BuildPannedViewport(frame, _histogramPanStartViewport, _histogramPanStartPoint.X, _histogramPanStartPoint.Y, localPoint.X, localPoint.Y, plotArea.Width, plotArea.Height);
        ApplyViewport(frame, pannedViewport);
        ClearPlotBitmapCache();
        InvalidateVisual();
        return true;
    }

    private void CancelHistogramPan()
    {
        if (!_isHistogramPanning)
        {
            return;
        }

        _isHistogramPanning = false;
        _histogramPanStartPoint = default(Point);
        _histogramPanStartViewport = new ChartViewport(0.0, 0.0, 0.0, 0.0);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private bool TryResolveHistogramPlotArea(out MousePerformanceChartRenderFrame frame, out Rect plotArea)
    {
        frame = RenderFrame;
        plotArea = Rect.Empty;
        if (frame == null || !frame.IsAvailable || !MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            return false;
        }

        plotArea = GetPlotArea(new Size(base.ActualWidth, base.ActualHeight), frame);
        return plotArea.Width > 0.0 && plotArea.Height > 0.0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isThemeSubscribed)
        {
            ThemeManager.Instance.ThemeChanged += OnThemeChanged;
            _isThemeSubscribed = true;
        }
        if (!_isLocalizationSubscribed)
        {
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
            _isLocalizationSubscribed = true;
        }
        ApplyThemeResources();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelHistogramPan();
        if (_isThemeSubscribed)
        {
            ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
            _isThemeSubscribed = false;
        }
        if (_isLocalizationSubscribed)
        {
            LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
            _isLocalizationSubscribed = false;
        }
        UpdateGpuHostScene(Rect.Empty, new ChartViewport(0.0, 1.0, 0.0, 1.0), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<GapMarker>(), null);
        DisposeOffscreenGpuRenderer();
        SyncGpuRendererStatus(isAvailable: false, string.Empty);
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        ApplyThemeResources();
        ClearPlotBitmapCache();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        _textLayoutCache.Clear();
        InvalidateArrange();
        InvalidateVisual();
    }

    private static void OnRenderFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MousePerformanceChartControl mousePerformanceChartControl)
        {
            mousePerformanceChartControl.HandleRenderFrameChanged(e.OldValue as MousePerformanceChartRenderFrame, e.NewValue as MousePerformanceChartRenderFrame);
        }
    }

    private void HandleRenderFrameChanged(MousePerformanceChartRenderFrame previousFrame, MousePerformanceChartRenderFrame nextFrame)
    {
        CancelHistogramPan();
        ClearAutomaticViewportCache();
        ClearPlotBitmapCache();
        if (previousFrame == null || nextFrame == null || !ReferenceEquals(previousFrame.Series, nextFrame.Series))
        {
            ClearVisiblePointCache(clearMonotonicity: true);
            ClearGapMarkerCache();
        }
        InvalidateArrange();
        if (nextFrame == null || !nextFrame.IsAvailable)
        {
            ClearViewportState();
        }
        else if (!_hasCustomViewport || previousFrame == null || !previousFrame.IsAvailable || previousFrame.PlotType != nextFrame.PlotType || previousFrame.TimeBasis != nextFrame.TimeBasis)
        {
            ClearViewportState();
        }
        else
        {
            ApplyViewport(nextFrame, new ChartViewport(_viewXMinimum, _viewXMaximum, _viewYMinimum, _viewYMaximum));
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        RenderCore(drawingContext, new Size(base.ActualWidth, base.ActualHeight), RenderFrame, reportVisibleGapCount: true);
    }

    public void ExportToPng(string filePath, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(filePath) || width <= 0 || height <= 0)
        {
            return;
        }
        Size renderSize = new Size(width, height);
        MousePerformanceChartRenderFrame renderFrame = RenderFrame;
        ImageSource imageSource = null;
        string gpuFailureMessage = string.Empty;
        if (renderFrame != null && renderFrame.IsAvailable)
        {
            imageSource = BuildExportPlotBitmap(renderSize, renderFrame, ref gpuFailureMessage);
            if (imageSource == null)
            {
                throw new InvalidOperationException(ResolveGpuExportFailureMessage(gpuFailureMessage));
            }
        }
        DrawingVisual drawingVisual = new DrawingVisual();
        using (DrawingContext drawingContext = drawingVisual.RenderOpen())
        {
            RenderCore(drawingContext, renderSize, renderFrame, reportVisibleGapCount: false, updateGpuHost: false, imageSource, gpuFailureMessage);
        }
        RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
        renderTargetBitmap.Render(drawingVisual);
        PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
        pngBitmapEncoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));
        using FileStream stream = File.Create(filePath);
        pngBitmapEncoder.Save(stream);
    }

    private void RenderCore(DrawingContext drawingContext, Size renderSize, MousePerformanceChartRenderFrame frame, bool reportVisibleGapCount, bool updateGpuHost = true, ImageSource plotBitmap = null, string gpuRendererFailureMessage = null)
    {
        if (drawingContext == null || renderSize.Width <= 0.0 || renderSize.Height <= 0.0)
        {
            return;
        }
        Rect backgroundBounds = new Rect(0.0, 0.0, renderSize.Width, renderSize.Height);
        drawingContext.DrawRectangle(_backgroundBrush, null, backgroundBounds);
        HeaderLayout headerLayout = ResolveHeaderLayout(renderSize, frame);
        if (headerLayout.TitleText != null)
        {
            drawingContext.DrawText(headerLayout.TitleText, headerLayout.TitleOrigin);
        }
        if (headerLayout.SubtitleText != null)
        {
            drawingContext.DrawText(headerLayout.SubtitleText, headerLayout.SubtitleOrigin);
        }
        if (headerLayout.DescriptionText != null)
        {
            drawingContext.DrawText(headerLayout.DescriptionText, headerLayout.DescriptionOrigin);
        }
        Rect plotArea = GetPlotArea(renderSize, frame);
        drawingContext.DrawRectangle(_panelBrush, null, plotArea);
        if (frame == null || !frame.IsAvailable)
        {
            if (updateGpuHost)
            {
                UpdateGpuHostScene(Rect.Empty, new ChartViewport(0.0, 1.0, 0.0, 1.0), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<GapMarker>(), null);
            }
            if (reportVisibleGapCount)
            {
                UpdateVisibleGapMetrics(0, null);
            }
            DrawUnavailableState(drawingContext, plotArea, ResolveUnavailableMessage(frame));
            return;
        }
        ChartViewport viewport = ResolveViewport(frame);
        IReadOnlyList<GapMarker> gapMarkers = Array.Empty<GapMarker>();
        IReadOnlyList<GapMarker> visibleGapMarkers = Array.Empty<GapMarker>();
        if (ShowGapOverlay)
        {
            gapMarkers = ResolveGapMarkers(frame, GapAnalysisDatasetSlot);
            visibleGapMarkers = ResolveVisibleGapMarkers(frame, viewport, GapAnalysisDatasetSlot);
        }
        IReadOnlyList<double> xAxisTicks = ResolveXAxisLabelTicks(viewport, frame);
        IReadOnlyList<double> yAxisTicks = ResolveYAxisTicks(viewport);
        ImageSource imageSource = plotBitmap;
        if (updateGpuHost)
        {
            imageSource = imageSource ?? UpdateGpuHostScene(plotArea, viewport, xAxisTicks, yAxisTicks, gapMarkers, frame);
        }
        string effectiveGpuFailureMessage = ResolveEffectiveGpuRendererFailureMessage(gpuRendererFailureMessage);
        if (!string.IsNullOrWhiteSpace(effectiveGpuFailureMessage))
        {
            if (reportVisibleGapCount)
            {
                UpdateVisibleGapMetrics(0, null);
            }
            DrawUnavailableState(drawingContext, plotArea, effectiveGpuFailureMessage);
            return;
        }
        if (reportVisibleGapCount)
        {
            if (ShowGapOverlay)
            {
                UpdateVisibleGapMetrics(visibleGapMarkers.Count, ResolveAverageGapDurationMs(visibleGapMarkers));
            }
            else
            {
                UpdateVisibleGapMetrics(0, null);
            }
        }
        if (imageSource != null)
        {
            drawingContext.DrawImage(imageSource, plotArea);
        }
        DrawHistogramBarValueLabels(drawingContext, plotArea, viewport, frame);
        DrawAxisLabels(drawingContext, plotArea, viewport, yAxisTicks, frame);
        DrawAxisTitles(drawingContext, plotArea, frame);
    }

    private void UpdateVisibleGapMetrics(int count, double? averageDurationMs)
    {
        int sanitizedCount = Math.Max(0, count);
        double? sanitizedAverageDurationMs = null;
        if (averageDurationMs.HasValue && !double.IsNaN(averageDurationMs.Value) && !double.IsInfinity(averageDurationMs.Value) && averageDurationMs.Value > 0.0)
        {
            sanitizedAverageDurationMs = averageDurationMs.Value;
        }
        if (_visibleGapCount != sanitizedCount || !Nullable.Equals(_visibleGapAverageDurationMs, sanitizedAverageDurationMs))
        {
            _visibleGapCount = sanitizedCount;
            _visibleGapAverageDurationMs = sanitizedAverageDurationMs;
            VisibleGapCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrawUnavailableState(DrawingContext drawingContext, Rect plotArea, string message)
    {
        DrawPlotBorder(drawingContext, plotArea);
        string displayMessage = ResolveText(message);
        if (string.IsNullOrWhiteSpace(displayMessage))
        {
            displayMessage = ResolveDefaultUnavailableMessage();
        }
        FormattedText messageText = CreateText(displayMessage, 15.0, _strongLabelBrush);
        drawingContext.DrawText(messageText, new Point(plotArea.Left + (plotArea.Width - messageText.Width) / 2.0, plotArea.Top + (plotArea.Height - messageText.Height) / 2.0));
    }

    private void DrawPlotBorder(DrawingContext drawingContext, Rect plotArea)
    {
        if (drawingContext != null && _axisPen != null && !plotArea.IsEmpty)
        {
            double left = SnapXStrokeCoordinate(plotArea.Left, _axisPen);
            double right = SnapXStrokeCoordinate(plotArea.Right, _axisPen);
            double top = SnapYStrokeCoordinate(plotArea.Top, _axisPen);
            double bottom = SnapYStrokeCoordinate(plotArea.Bottom, _axisPen);
            drawingContext.DrawLine(_axisPen, new Point(left, top), new Point(right, top));
            drawingContext.DrawLine(_axisPen, new Point(left, bottom), new Point(right, bottom));
            drawingContext.DrawLine(_axisPen, new Point(left, top), new Point(left, bottom));
            drawingContext.DrawLine(_axisPen, new Point(right, top), new Point(right, bottom));
        }
    }

    private double SnapXStrokeCoordinate(double value, Pen pen)
    {
        return SnapStrokeCoordinate(value, pen, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private double SnapYStrokeCoordinate(double value, Pen pen)
    {
        return SnapStrokeCoordinate(value, pen, VisualTreeHelper.GetDpi(this).DpiScaleY);
    }

    private static double SnapStrokeCoordinate(double value, Pen pen, double dpiScale)
    {
        if (pen == null || double.IsNaN(value) || double.IsInfinity(value) || dpiScale <= 0.0)
        {
            return value;
        }
        double strokeThicknessInPixels = Math.Max(0.0, pen.Thickness) * dpiScale;
        if (strokeThicknessInPixels <= 0.0)
        {
            return value;
        }
        return (Math.Round(value * dpiScale - strokeThicknessInPixels / 2.0) + strokeThicknessInPixels / 2.0) / dpiScale;
    }

    private static bool IsPlotEdgeCoordinate(double value, double lowerBound, double upperBound)
    {
        if (!(Math.Abs(value - lowerBound) <= 0.5))
        {
            return Math.Abs(value - upperBound) <= 0.5;
        }
        return true;
    }

    private static bool IsInteriorPlotCoordinate(double value, double lowerBound, double upperBound)
    {
        return !IsPlotEdgeCoordinate(value, lowerBound, upperBound) && value >= lowerBound - 0.5 && value <= upperBound + 0.5;
    }

    private static IReadOnlyList<double> ResolveXAxisLabelTicks(ChartViewport viewport)
    {
        double xRange = viewport.XMaximum - viewport.XMinimum;
        if (double.IsNaN(xRange) || double.IsInfinity(xRange) || xRange <= 0.0)
        {
            return Array.Empty<double>();
        }

        List<double> ticks = new List<double>(GridLineCount + 2);
        for (int tickIndex = 0; tickIndex <= GridLineCount; tickIndex++)
        {
            double value = viewport.XMinimum + xRange * tickIndex / GridLineCount;
            AddAxisTick(ticks, value);
        }
        if (IsAxisValueVisible(viewport.XMinimum, viewport.XMaximum, 0.0))
        {
            AddAxisTick(ticks, 0.0);
        }
        ticks.Sort();
        return ticks;
    }

    private static IReadOnlyList<double> ResolveXAxisLabelTicks(ChartViewport viewport, MousePerformanceChartRenderFrame frame)
    {
        if (MousePerformancePlotTraits.IsHistogramPlot(frame?.PlotType ?? MousePerformancePlotType.XCountVsTime))
        {
            return ResolveHistogramXAxisLabelTicks(viewport, frame);
        }
        return ResolveXAxisLabelTicks(viewport);
    }

    private static IReadOnlyList<double> ResolveHistogramXAxisLabelTicks(ChartViewport viewport, MousePerformanceChartRenderFrame frame)
    {
        IReadOnlyList<MousePerformanceHistogramBin> bins = ResolvePrimaryHistogramBins(frame);
        if (bins.Count == 0)
        {
            return Array.Empty<double>();
        }

        if (MousePerformanceHistogramPresentationPolicy.ShouldUseBinCenterTicks(frame.PlotType, bins))
        {
            List<double> centerTicks = new List<double>(bins.Count);
            for (int binIndex = 0; binIndex < bins.Count; binIndex++)
            {
                AddVisibleHistogramAxisTick(centerTicks, bins[binIndex].CenterX, viewport);
            }
            centerTicks.Sort();
            return centerTicks.Count <= HistogramAxisBoundaryLabelLimit ? centerTicks : DownsampleAxisTicks(centerTicks, HistogramAxisBoundaryLabelLimit);
        }

        List<double> boundaryTicks = new List<double>(bins.Count + 1);
        for (int binIndex = 0; binIndex < bins.Count; binIndex++)
        {
            MousePerformanceHistogramBin bin = bins[binIndex];
            AddVisibleHistogramAxisTick(boundaryTicks, bin.MinimumX, viewport);
            if (binIndex == bins.Count - 1)
            {
                AddVisibleHistogramAxisTick(boundaryTicks, bin.MaximumX, viewport);
            }
        }
        boundaryTicks.Sort();
        if (boundaryTicks.Count > 0 && boundaryTicks.Count <= HistogramAxisBoundaryLabelLimit)
        {
            return boundaryTicks;
        }
        return DownsampleAxisTicks(boundaryTicks, HistogramAxisBoundaryLabelLimit);
    }

    private static void AddVisibleHistogramAxisTick(List<double> ticks, double value, ChartViewport viewport)
    {
        if (!IsAxisValueVisible(viewport.XMinimum, viewport.XMaximum, value))
        {
            return;
        }
        AddAxisTick(ticks, value);
    }

    private static IReadOnlyList<double> DownsampleAxisTicks(IReadOnlyList<double> ticks, int maximumCount)
    {
        if (ticks == null || ticks.Count == 0 || maximumCount <= 0)
        {
            return Array.Empty<double>();
        }
        if (ticks.Count <= maximumCount)
        {
            return ticks;
        }

        List<double> result = new List<double>(maximumCount);
        int lastIndex = ticks.Count - 1;
        int targetCount = Math.Max(2, maximumCount);
        for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            int sourceIndex = (int)Math.Round((double)targetIndex * lastIndex / (targetCount - 1), MidpointRounding.AwayFromZero);
            AddAxisTick(result, ticks[Math.Max(0, Math.Min(lastIndex, sourceIndex))]);
        }
        result.Sort();
        return result;
    }

    private static IReadOnlyList<double> ResolveMinorXAxisLabelTicks(ChartViewport viewport, IReadOnlyList<double> xAxisTicks)
    {
        double xRange = viewport.XMaximum - viewport.XMinimum;
        if (double.IsNaN(xRange) || double.IsInfinity(xRange) || xRange <= 0.0)
        {
            return Array.Empty<double>();
        }

        List<double> ticks = new List<double>(GridLineCount * Math.Max(0, MinorGridSubdivisionCount - 1));
        for (int majorIntervalIndex = 0; majorIntervalIndex < GridLineCount; majorIntervalIndex++)
        {
            for (int subdivision = 1; subdivision <= MinorGridSubdivisionCount - 1; subdivision++)
            {
                double normalizedX = (majorIntervalIndex + (double)subdivision / MinorGridSubdivisionCount) / GridLineCount;
                double value = viewport.XMinimum + xRange * normalizedX;
                if (!ContainsAxisTick(xAxisTicks, value))
                {
                    AddAxisTick(ticks, value);
                }
            }
        }
        ticks.Sort();
        return ticks;
    }

    private static void AddAxisTick(List<double> ticks, double value)
    {
        if (ticks == null || !IsFinite(value))
        {
            return;
        }

        double normalizedValue = Math.Abs(value) <= AxisZeroTolerance ? 0.0 : value;
        for (int tickIndex = 0; tickIndex < ticks.Count; tickIndex++)
        {
            if (Math.Abs(ticks[tickIndex] - normalizedValue) <= AxisZeroTolerance)
            {
                return;
            }
        }
        ticks.Add(normalizedValue);
    }

    private static bool ContainsAxisTick(IReadOnlyList<double> ticks, double value)
    {
        if (ticks == null || !IsFinite(value))
        {
            return false;
        }

        double normalizedValue = Math.Abs(value) <= AxisZeroTolerance ? 0.0 : value;
        for (int tickIndex = 0; tickIndex < ticks.Count; tickIndex++)
        {
            if (Math.Abs(ticks[tickIndex] - normalizedValue) <= AxisZeroTolerance)
            {
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<MousePerformanceHistogramBin> ResolvePrimaryHistogramBins(MousePerformanceChartRenderFrame frame)
    {
        MousePerformanceChartSeries series = ResolvePrimaryHistogramSeries(frame);
        return series?.HistogramBins ?? Array.Empty<MousePerformanceHistogramBin>();
    }

    private static MousePerformanceChartSeries ResolvePrimaryHistogramSeries(MousePerformanceChartRenderFrame frame)
    {
        if (frame?.Series == null || !MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            return null;
        }
        return frame.Series.FirstOrDefault(series => series != null && series.Kind == MousePerformanceChartSeriesKind.Histogram && series.HistogramBins != null && series.HistogramBins.Count > 0);
    }

    private static bool IsAxisValueVisible(double minimum, double maximum, double value)
    {
        if (!IsFinite(minimum) || !IsFinite(maximum) || !IsFinite(value))
        {
            return false;
        }

        double lowerBound = Math.Min(minimum, maximum);
        double upperBound = Math.Max(minimum, maximum);
        return value >= lowerBound - AxisZeroTolerance && value <= upperBound + AxisZeroTolerance;
    }

    private static IReadOnlyList<double> ResolveYAxisTicks(ChartViewport viewport)
    {
        double yRange = viewport.YMaximum - viewport.YMinimum;
        if (double.IsNaN(yRange) || double.IsInfinity(yRange) || yRange <= 0.0)
        {
            return Array.Empty<double>();
        }
        double majorStep = ResolveNiceAxisStep(yRange / GridLineCount);
        if (double.IsNaN(majorStep) || double.IsInfinity(majorStep) || majorStep <= 0.0)
        {
            return Array.Empty<double>();
        }
        double tolerance = Math.Max(1E-06, majorStep * 0.0001);
        double firstTick = Math.Ceiling((viewport.YMinimum - tolerance) / majorStep) * majorStep;
        double lastTick = Math.Floor((viewport.YMaximum + tolerance) / majorStep) * majorStep;
        List<double> ticks = new List<double>();
        double tick = firstTick;
        int guard = 0;
        while (tick <= lastTick + tolerance && guard < 256)
        {
            ticks.Add((Math.Abs(tick) <= tolerance) ? 0.0 : tick);
            tick += majorStep;
            guard++;
        }
        return ticks;
    }

    private static IReadOnlyList<double> ResolveMinorYAxisTicks(ChartViewport viewport, IReadOnlyList<double> yAxisTicks)
    {
        double yRange = viewport.YMaximum - viewport.YMinimum;
        if (double.IsNaN(yRange) || double.IsInfinity(yRange) || yRange <= 0.0)
        {
            return Array.Empty<double>();
        }
        double majorStep = ResolveMajorYAxisStep(viewport, yAxisTicks);
        if (double.IsNaN(majorStep) || double.IsInfinity(majorStep) || majorStep <= 0.0)
        {
            return Array.Empty<double>();
        }
        double tolerance = Math.Max(1E-06, majorStep * 0.0001);
        double firstMajorTick = Math.Floor((viewport.YMinimum + tolerance) / majorStep) * majorStep;
        double lastMajorTick = Math.Ceiling((viewport.YMaximum - tolerance) / majorStep) * majorStep;
        List<double> ticks = new List<double>();
        double majorTick = firstMajorTick;
        int guard = 0;
        while (majorTick < lastMajorTick - tolerance && guard < 512)
        {
            double nextMajorTick = majorTick + majorStep;
            for (int subdivision = 1; subdivision <= MinorGridSubdivisionCount - 1; subdivision++)
            {
                double minorTick = majorTick + majorStep * subdivision / MinorGridSubdivisionCount;
                if (minorTick > viewport.YMinimum + tolerance && minorTick < viewport.YMaximum - tolerance)
                {
                    ticks.Add((Math.Abs(minorTick) <= tolerance) ? 0.0 : minorTick);
                }
            }
            majorTick = nextMajorTick;
            guard++;
        }
        return ticks;
    }

    private static double ResolveMajorYAxisStep(ChartViewport viewport, IReadOnlyList<double> yAxisTicks)
    {
        if (yAxisTicks != null && yAxisTicks.Count >= 2)
        {
            double explicitStep = Math.Abs(yAxisTicks[1] - yAxisTicks[0]);
            if (!double.IsNaN(explicitStep) && !double.IsInfinity(explicitStep) && explicitStep > 0.0)
            {
                return explicitStep;
            }
        }
        double yRange = viewport.YMaximum - viewport.YMinimum;
        if (double.IsNaN(yRange) || double.IsInfinity(yRange) || yRange <= 0.0)
        {
            return double.NaN;
        }
        return ResolveNiceAxisStep(yRange / GridLineCount);
    }

    private static double ResolveNiceAxisStep(double approximateStep)
    {
        if (double.IsNaN(approximateStep) || double.IsInfinity(approximateStep) || approximateStep <= 0.0)
        {
            return 1.0;
        }
        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(approximateStep)));
        double normalizedStep = approximateStep / magnitude;
        double niceNormalizedStep = normalizedStep <= 1.0 ? 1.0 : normalizedStep <= 2.0 ? 2.0 : normalizedStep <= 2.5 ? 2.5 : normalizedStep <= 5.0 ? 5.0 : 10.0;
        return niceNormalizedStep * magnitude;
    }

    private void DrawAxisLabels(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, IReadOnlyList<double> yAxisTicks, MousePerformanceChartRenderFrame frame)
    {
        MousePerformancePlotType plotType = frame?.PlotType ?? MousePerformancePlotType.XCountVsTime;
        bool screenYAxisPositiveDown = IsScreenYAxisPositiveDown(plotType);
        DrawXAxisLabels(drawingContext, plotArea, viewport, frame);
        DrawYAxisLabels(drawingContext, plotArea, viewport, yAxisTicks, screenYAxisPositiveDown);
    }

    private void DrawXAxisLabels(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, MousePerformanceChartRenderFrame frame)
    {
        IReadOnlyList<double> xAxisTicks = ResolveXAxisLabelTicks(viewport, frame);
        if (xAxisTicks.Count == 0)
        {
            return;
        }

        IReadOnlyList<double> minorXAxisTicks = MousePerformancePlotTraits.IsHistogramPlot(frame?.PlotType ?? MousePerformancePlotType.XCountVsTime)
            ? Array.Empty<double>()
            : ResolveMinorXAxisLabelTicks(viewport, xAxisTicks);
        List<Rect> occupiedBounds = new List<Rect>(xAxisTicks.Count + minorXAxisTicks.Count);
        HashSet<string> occupiedLabels = new HashSet<string>(StringComparer.Ordinal);
        int zeroTickIndex = IndexOfAxisTick(xAxisTicks, 0.0);
        if (zeroTickIndex >= 0)
        {
            TryDrawXAxisLabel(drawingContext, plotArea, viewport, xAxisTicks[zeroTickIndex], occupiedBounds, occupiedLabels, force: true, MajorAxisLabelFontSize, _labelBrush);
        }
        for (int tickIndex = 0; tickIndex < xAxisTicks.Count; tickIndex++)
        {
            if (tickIndex != zeroTickIndex)
            {
                TryDrawXAxisLabel(drawingContext, plotArea, viewport, xAxisTicks[tickIndex], occupiedBounds, occupiedLabels, force: false, MajorAxisLabelFontSize, _labelBrush);
            }
        }
        for (int tickIndex = 0; tickIndex < minorXAxisTicks.Count; tickIndex++)
        {
            TryDrawXAxisLabel(drawingContext, plotArea, viewport, minorXAxisTicks[tickIndex], occupiedBounds, occupiedLabels, force: false, MinorAxisLabelFontSize, _minorAxisLabelBrush);
        }
    }

    private void DrawYAxisLabels(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, IReadOnlyList<double> yAxisTicks, bool screenYAxisPositiveDown)
    {
        if (yAxisTicks == null || yAxisTicks.Count == 0)
        {
            return;
        }

        IReadOnlyList<double> minorYAxisTicks = ResolveMinorYAxisTicks(viewport, yAxisTicks);
        List<Rect> occupiedBounds = new List<Rect>(yAxisTicks.Count + minorYAxisTicks.Count);
        HashSet<string> occupiedLabels = new HashSet<string>(StringComparer.Ordinal);
        int zeroTickIndex = IndexOfAxisTick(yAxisTicks, 0.0);
        if (zeroTickIndex >= 0)
        {
            TryDrawYAxisLabel(drawingContext, plotArea, viewport, yAxisTicks[zeroTickIndex], screenYAxisPositiveDown, occupiedBounds, occupiedLabels, force: true, MajorAxisLabelFontSize, _labelBrush);
        }
        for (int tickIndex = 0; tickIndex < yAxisTicks.Count; tickIndex++)
        {
            if (tickIndex != zeroTickIndex)
            {
                TryDrawYAxisLabel(drawingContext, plotArea, viewport, yAxisTicks[tickIndex], screenYAxisPositiveDown, occupiedBounds, occupiedLabels, force: true, MajorAxisLabelFontSize, _labelBrush);
            }
        }
        for (int tickIndex = 0; tickIndex < minorYAxisTicks.Count; tickIndex++)
        {
            TryDrawYAxisLabel(drawingContext, plotArea, viewport, minorYAxisTicks[tickIndex], screenYAxisPositiveDown, occupiedBounds, occupiedLabels, force: false, MinorAxisLabelFontSize, _minorAxisLabelBrush);
        }
    }

    private bool TryDrawXAxisLabel(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, double value, List<Rect> occupiedBounds, HashSet<string> occupiedLabels, bool force, double fontSize, Brush brush)
    {
        if (drawingContext == null || occupiedBounds == null || occupiedLabels == null || !IsFinite(value))
        {
            return false;
        }

        string labelText = FormatAxisValue(value);
        if (!TryReserveAxisLabelText(occupiedLabels, labelText))
        {
            return false;
        }

        double x = MapX(plotArea, viewport, value);
        if (x < plotArea.Left - 0.5 || x > plotArea.Right + 0.5)
        {
            occupiedLabels.Remove(labelText);
            return false;
        }

        FormattedText xLabel = CreateText(labelText, fontSize, brush ?? _labelBrush);
        Rect labelBounds = new Rect(x - xLabel.Width / 2.0, plotArea.Bottom + 10.0, xLabel.Width, xLabel.Height);
        Rect paddedBounds = InflateRect(labelBounds, AxisLabelSpacingPixels, 0.0);
        if (!force && IntersectsAny(paddedBounds, occupiedBounds))
        {
            occupiedLabels.Remove(labelText);
            return false;
        }

        drawingContext.DrawText(xLabel, labelBounds.TopLeft);
        occupiedBounds.Add(paddedBounds);
        return true;
    }

    private bool TryDrawYAxisLabel(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, double value, bool screenYAxisPositiveDown, List<Rect> occupiedBounds, HashSet<string> occupiedLabels, bool force, double fontSize, Brush brush)
    {
        if (drawingContext == null || occupiedBounds == null || occupiedLabels == null || !IsFinite(value))
        {
            return false;
        }

        string labelText = FormatAxisValue(value);
        if (!TryReserveAxisLabelText(occupiedLabels, labelText))
        {
            return false;
        }

        double y = MapY(plotArea, viewport, value, screenYAxisPositiveDown);
        if (y < plotArea.Top - 0.5 || y > plotArea.Bottom + 0.5)
        {
            occupiedLabels.Remove(labelText);
            return false;
        }

        FormattedText yLabel = CreateText(labelText, fontSize, brush ?? _labelBrush);
        Rect labelBounds = new Rect(plotArea.Left - yLabel.Width - 10.0, y - yLabel.Height / 2.0, yLabel.Width, yLabel.Height);
        Rect paddedBounds = InflateRect(labelBounds, 0.0, AxisLabelSpacingPixels / 2.0);
        if (!force && IntersectsAny(paddedBounds, occupiedBounds))
        {
            occupiedLabels.Remove(labelText);
            return false;
        }

        drawingContext.DrawText(yLabel, labelBounds.TopLeft);
        occupiedBounds.Add(paddedBounds);
        return true;
    }

    private void DrawHistogramBarValueLabels(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, MousePerformanceChartRenderFrame frame)
    {
        if (drawingContext == null || frame?.Series == null || !MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            return;
        }

        List<Rect> occupiedBounds = new List<Rect>();
        int histogramSeriesCount = CountHistogramLabelSeries(frame.Series);
        int labelLimitPerSeries = ResolveHistogramBarValueLabelLimitPerSeries(histogramSeriesCount);
        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series?.Kind != MousePerformanceChartSeriesKind.Histogram || series.HistogramBins == null)
            {
                continue;
            }
            int visibleLabelCount = 0;
            foreach (MousePerformanceHistogramBin bin in series.HistogramBins)
            {
                if (visibleLabelCount >= labelLimitPerSeries)
                {
                    break;
                }
                if (TryDrawHistogramBarValueLabel(drawingContext, plotArea, viewport, series, bin, occupiedBounds))
                {
                    visibleLabelCount++;
                }
            }
        }
    }

    private static int CountHistogramLabelSeries(IReadOnlyList<MousePerformanceChartSeries> series)
    {
        if (series == null || series.Count == 0)
        {
            return 0;
        }

        int count = 0;
        foreach (MousePerformanceChartSeries chartSeries in series)
        {
            if (chartSeries?.Kind == MousePerformanceChartSeriesKind.Histogram && chartSeries.HistogramBins != null && chartSeries.HistogramBins.Count > 0)
            {
                count++;
            }
        }
        return count;
    }

    private static int ResolveHistogramBarValueLabelLimitPerSeries(int histogramSeriesCount)
    {
        return Math.Max(1, HistogramBarValueLabelLimit / Math.Max(1, histogramSeriesCount));
    }

    private bool TryDrawHistogramBarValueLabel(DrawingContext drawingContext, Rect plotArea, ChartViewport viewport, MousePerformanceChartSeries series, MousePerformanceHistogramBin bin, List<Rect> occupiedBounds)
    {
        MousePerformanceSeriesBuilder.ResolveHistogramBinXRange(bin, series.XOffset, series.GroupScale, out double minimumX, out double maximumX);
        double value = bin.Value;
        if (!IsFinite(minimumX) || !IsFinite(maximumX) || !CanDisplayHistogramBarValueLabel(value) || maximumX <= viewport.XMinimum || minimumX >= viewport.XMaximum)
        {
            return false;
        }

        double visibleMinimumX = Math.Max(minimumX, viewport.XMinimum);
        double visibleMaximumX = Math.Min(maximumX, viewport.XMaximum);
        if (visibleMaximumX <= visibleMinimumX)
        {
            return false;
        }

        double centerX = MapX(plotArea, viewport, (visibleMinimumX + visibleMaximumX) * 0.5);
        double valueY = MapY(plotArea, viewport, value);
        if (centerX < plotArea.Left - 0.5 || centerX > plotArea.Right + 0.5 || valueY < plotArea.Top - 20.0 || valueY > plotArea.Bottom + 0.5)
        {
            return false;
        }

        string labelText = FormatHistogramBarValue(value);
        if (string.IsNullOrWhiteSpace(labelText))
        {
            return false;
        }

        FormattedText label = CreateText(labelText, HistogramBarValueLabelFontSize, _strongLabelBrush);
        Rect labelBounds = new Rect(centerX - label.Width / 2.0, valueY - label.Height - 3.0, label.Width, label.Height);
        if (labelBounds.Top < plotArea.Top + 2.0)
        {
            labelBounds.Y = valueY + 3.0;
        }
        if (labelBounds.Left < plotArea.Left || labelBounds.Right > plotArea.Right || labelBounds.Bottom > plotArea.Bottom)
        {
            return false;
        }

        Rect paddedBounds = InflateRect(labelBounds, AxisLabelSpacingPixels * 0.5, 1.0);
        if (IntersectsAny(paddedBounds, occupiedBounds))
        {
            return false;
        }

        Rect backgroundBounds = InflateRect(labelBounds, 3.0, 1.0);
        drawingContext.DrawRoundedRectangle(CreateBrush(ApplyOpacity(ResolveBrushColor(_backgroundBrush, Colors.Black), 0.72)), null, backgroundBounds, 2.0, 2.0);
        drawingContext.DrawText(label, labelBounds.TopLeft);
        occupiedBounds.Add(paddedBounds);
        return true;
    }

    private static bool CanDisplayHistogramBarValueLabel(double value)
    {
        return IsFinite(value) && value > AxisZeroTolerance;
    }

    private static bool TryReserveAxisLabelText(HashSet<string> occupiedLabels, string labelText)
    {
        if (occupiedLabels == null || string.IsNullOrWhiteSpace(labelText))
        {
            return false;
        }
        return occupiedLabels.Add(labelText);
    }

    private static int IndexOfAxisTick(IReadOnlyList<double> ticks, double value)
    {
        if (ticks == null)
        {
            return -1;
        }

        for (int tickIndex = 0; tickIndex < ticks.Count; tickIndex++)
        {
            if (Math.Abs(ticks[tickIndex] - value) <= AxisZeroTolerance)
            {
                return tickIndex;
            }
        }
        return -1;
    }

    private static bool IntersectsAny(Rect bounds, IReadOnlyList<Rect> occupiedBounds)
    {
        if (occupiedBounds == null)
        {
            return false;
        }

        for (int boundsIndex = 0; boundsIndex < occupiedBounds.Count; boundsIndex++)
        {
            if (bounds.IntersectsWith(occupiedBounds[boundsIndex]))
            {
                return true;
            }
        }
        return false;
    }

    private static Rect InflateRect(Rect bounds, double horizontalPadding, double verticalPadding)
    {
        bounds.Inflate(Math.Max(0.0, horizontalPadding), Math.Max(0.0, verticalPadding));
        return bounds;
    }

    private void DrawAxisTitles(DrawingContext drawingContext, Rect plotArea, MousePerformanceChartRenderFrame frame)
    {
        FormattedText xTitle = CreateText(ResolveText(frame.XAxisTitle), 12.0, _strongLabelBrush);
        drawingContext.DrawText(xTitle, new Point(plotArea.Left + (plotArea.Width - xTitle.Width) / 2.0, plotArea.Bottom + 34.0));
        string yTitleText = ResolveText(frame.YAxisTitle);
        if (!string.IsNullOrWhiteSpace(yTitleText))
        {
            FormattedText yTitle = CreateText(yTitleText, 12.0, _strongLabelBrush);
            drawingContext.PushTransform(new RotateTransform(-90.0, 18.0, plotArea.Top + plotArea.Height / 2.0));
            drawingContext.DrawText(yTitle, new Point(18.0 - yTitle.Width / 2.0, plotArea.Top + (plotArea.Height - yTitle.Height) / 2.0));
            drawingContext.Pop();
        }
    }

    private IReadOnlyList<GapMarker> ResolveVisibleGapMarkers(MousePerformanceChartRenderFrame frame, ChartViewport viewport, MousePerformanceChartDatasetSlot datasetSlot)
    {
        IReadOnlyList<GapMarker> gapMarkers = ResolveGapMarkers(frame, datasetSlot);
        if (gapMarkers.Count == 0)
        {
            return gapMarkers;
        }
        return FilterGapMarkersForViewport(gapMarkers, viewport);
    }

    private IReadOnlyList<GapMarker> ResolveGapMarkers(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        if (frame == null || frame.PlotType == MousePerformancePlotType.XVsY)
        {
            return Array.Empty<GapMarker>();
        }
        MousePerformanceChartGapSource gapSource = frame.GapSources?.FirstOrDefault(source => source != null && source.DatasetSlot == datasetSlot);
        if (gapSource == null)
        {
            return Array.Empty<GapMarker>();
        }
        if (_cachedGapMarkerEntry != null && ReferenceEquals(_cachedGapMarkerEntry.Source, gapSource) && _cachedGapMarkerEntry.PlotType == frame.PlotType && _cachedGapMarkerEntry.TimeBasis == frame.TimeBasis)
        {
            return _cachedGapMarkerEntry.GapMarkers;
        }
        IReadOnlyList<MousePerformanceChartGapInterval> intervals = gapSource.Intervals;
        if (intervals == null || intervals.Count == 0)
        {
            return Array.Empty<GapMarker>();
        }
        List<double> sampleIntervals = new List<double>();
        for (int index = 0; index < intervals.Count; index++)
        {
            MousePerformanceChartGapInterval interval = intervals[index];
            if (interval.DurationMs > 0.0)
            {
                sampleIntervals.Add(interval.DurationMs);
            }
        }
        double gapThreshold = GapThresholdMinimumMs;
        if (sampleIntervals.Count > 0)
        {
            sampleIntervals.Sort();
            double? medianInterval = ResolvePercentile(sampleIntervals, 0.5);
            if (medianInterval.HasValue)
            {
                gapThreshold = Math.Max(GapThresholdMinimumMs, medianInterval.Value * GapThresholdMultiplier);
            }
        }
        List<GapMarker> gapMarkers = new List<GapMarker>();
        for (int index = 0; index < intervals.Count; index++)
        {
            MousePerformanceChartGapInterval interval = intervals[index];
            if (interval.DurationMs > gapThreshold)
            {
                gapMarkers.Add(new GapMarker(interval.StartX + gapSource.XOffset, interval.EndX + gapSource.XOffset, touchesViewportBoundary: false));
            }
        }
        _cachedGapMarkerEntry = new GapMarkerCacheEntry(gapSource, frame.PlotType, frame.TimeBasis, gapMarkers);
        return gapMarkers;
    }

    private static IReadOnlyList<GapMarker> FilterGapMarkersForViewport(IReadOnlyList<GapMarker> gapMarkers, ChartViewport viewport)
    {
        if (gapMarkers == null || gapMarkers.Count == 0)
        {
            return Array.Empty<GapMarker>();
        }
        List<GapMarker> visibleGapMarkers = new List<GapMarker>();
        foreach (GapMarker gapMarker in gapMarkers)
        {
            if (gapMarker != null && !(gapMarker.EndX < viewport.XMinimum) && !(gapMarker.StartX > viewport.XMaximum))
            {
                double visibleStartX = Math.Max(gapMarker.StartX, viewport.XMinimum);
                double visibleEndX = Math.Min(gapMarker.EndX, viewport.XMaximum);
                if (!(visibleEndX < visibleStartX))
                {
                    bool touchesViewportBoundary = gapMarker.StartX < viewport.XMinimum || gapMarker.EndX > viewport.XMaximum;
                    visibleGapMarkers.Add(new GapMarker(visibleStartX, visibleEndX, touchesViewportBoundary));
                }
            }
        }
        return visibleGapMarkers;
    }

    private static double? ResolveAverageGapDurationMs(IReadOnlyList<GapMarker> gapMarkers)
    {
        if (gapMarkers == null || gapMarkers.Count == 0)
        {
            return null;
        }
        GapMarker[] completeGapMarkers = gapMarkers.Where((GapMarker marker) => marker != null && !marker.TouchesViewportBoundary && marker.DurationMs > 0.0).ToArray();
        GapMarker[] durationSamples = completeGapMarkers.Length > 0 ? completeGapMarkers : gapMarkers.Where((GapMarker marker) => marker != null && marker.DurationMs > 0.0).ToArray();
        if (durationSamples.Length == 0)
        {
            return null;
        }
        double totalDurationMs = 0.0;
        foreach (GapMarker gapMarker in durationSamples)
        {
            totalDurationMs += gapMarker.DurationMs;
        }
        return totalDurationMs / durationSamples.Length;
    }

    private static bool HasGapBetween(double leftX, double rightX, IReadOnlyList<GapMarker> gapMarkers)
    {
        if (gapMarkers == null || gapMarkers.Count == 0)
        {
            return false;
        }
        double startX = Math.Min(leftX, rightX);
        double endX = Math.Max(leftX, rightX);
        foreach (GapMarker gapMarker in gapMarkers)
        {
            if (gapMarker != null && gapMarker.EndX >= startX && gapMarker.StartX <= endX)
            {
                return true;
            }
        }
        return false;
    }

    private IReadOnlyList<MousePerformanceChartPoint> ExtractVisiblePoints(IReadOnlyList<MousePerformanceChartPoint> points, ChartViewport viewport, MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, bool includeNeighbors, double xOffset = 0.0)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        PrepareVisiblePointCache(viewport);
        foreach (VisiblePointCacheEntry visiblePointCacheEntry in _visiblePointCacheEntries)
        {
            if (ReferenceEquals(visiblePointCacheEntry.Points, points) && visiblePointCacheEntry.PlotType == plotType && visiblePointCacheEntry.TimeBasis == timeBasis && visiblePointCacheEntry.IncludeNeighbors == includeNeighbors && AreClose(visiblePointCacheEntry.XOffset, xOffset))
            {
                return visiblePointCacheEntry.VisiblePoints;
            }
        }
        IReadOnlyList<MousePerformanceChartPoint> visiblePoints = ExtractVisiblePointsCore(points, viewport, plotType, timeBasis, includeNeighbors, xOffset);
        if (_visiblePointCacheEntries.Count >= 24)
        {
            _visiblePointCacheEntries.Clear();
        }
        _visiblePointCacheEntries.Add(new VisiblePointCacheEntry(points, plotType, timeBasis, includeNeighbors, xOffset, visiblePoints));
        return visiblePoints;
    }

    private IReadOnlyList<MousePerformanceChartPoint> ExtractVisiblePointsCore(IReadOnlyList<MousePerformanceChartPoint> points, ChartViewport viewport, MousePerformancePlotType plotType, MousePerformanceTimeBasis timeBasis, bool includeNeighbors, double xOffset)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        if (plotType == MousePerformancePlotType.XVsY)
        {
            List<MousePerformanceChartPoint> visiblePoints = new List<MousePerformanceChartPoint>();
            foreach (MousePerformanceChartPoint point in points)
            {
                double effectivePointX = GetEffectivePointX(point, xOffset);
                if (effectivePointX >= viewport.XMinimum && effectivePointX <= viewport.XMaximum && point.Y >= viewport.YMinimum && point.Y <= viewport.YMaximum)
                {
                    visiblePoints.Add(point);
                }
            }
            if (visiblePoints.Count > 0)
            {
                return visiblePoints;
            }
            return Array.Empty<MousePerformanceChartPoint>();
        }
        if (!CanUseIndexedVisiblePointExtraction(points, timeBasis))
        {
            return ExtractVisiblePointsByScan(points, viewport, includeNeighbors, xOffset);
        }
        int firstVisibleIndex = FindFirstIndexAtOrAfter(points, viewport.XMinimum - xOffset);
        int lastVisibleIndex = FindLastIndexAtOrBefore(points, viewport.XMaximum - xOffset);
        if (includeNeighbors)
        {
            firstVisibleIndex = Math.Max(0, firstVisibleIndex - 1);
            lastVisibleIndex = Math.Min(points.Count - 1, lastVisibleIndex + 1);
        }
        if (firstVisibleIndex < 0 || firstVisibleIndex >= points.Count || lastVisibleIndex < firstVisibleIndex)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        List<MousePerformanceChartPoint> visibleRange = new List<MousePerformanceChartPoint>(lastVisibleIndex - firstVisibleIndex + 1);
        for (int index = firstVisibleIndex; index <= lastVisibleIndex; index++)
        {
            visibleRange.Add(points[index]);
        }
        return visibleRange;
    }

    private bool CanUseIndexedVisiblePointExtraction(IReadOnlyList<MousePerformanceChartPoint> points, MousePerformanceTimeBasis timeBasis)
    {
        if (points == null || points.Count <= 1)
        {
            return true;
        }
        if (timeBasis != MousePerformanceTimeBasis.RawCapture)
        {
            return true;
        }
        return IsNonDecreasingByX(points);
    }

    private bool IsNonDecreasingByX(IReadOnlyList<MousePerformanceChartPoint> points)
    {
        if (points == null || points.Count <= 1)
        {
            return true;
        }
        if (_rawCaptureMonotonicityCache.TryGetValue(points, out bool cachedResult))
        {
            return cachedResult;
        }
        double previousX = points[0].X;
        bool isNonDecreasing = true;
        for (int index = 1; index < points.Count; index++)
        {
            double x = points[index].X;
            if (x < previousX)
            {
                isNonDecreasing = false;
                break;
            }
            previousX = x;
        }
        if (_rawCaptureMonotonicityCache.Count >= 96)
        {
            _rawCaptureMonotonicityCache.Clear();
        }
        _rawCaptureMonotonicityCache[points] = isNonDecreasing;
        return isNonDecreasing;
    }

    private static IReadOnlyList<MousePerformanceChartPoint> ExtractVisiblePointsByScan(IReadOnlyList<MousePerformanceChartPoint> points, ChartViewport viewport, bool includeNeighbors, double xOffset)
    {
        int firstVisibleIndex = -1;
        int lastVisibleIndex = -1;
        for (int index = 0; index < points.Count; index++)
        {
            double effectivePointX = GetEffectivePointX(points[index], xOffset);
            if (effectivePointX >= viewport.XMinimum && effectivePointX <= viewport.XMaximum)
            {
                if (firstVisibleIndex < 0)
                {
                    firstVisibleIndex = index;
                }
                lastVisibleIndex = index;
            }
        }
        if (firstVisibleIndex < 0 || lastVisibleIndex < firstVisibleIndex)
        {
            return Array.Empty<MousePerformanceChartPoint>();
        }
        if (includeNeighbors)
        {
            firstVisibleIndex = Math.Max(0, firstVisibleIndex - 1);
            lastVisibleIndex = Math.Min(points.Count - 1, lastVisibleIndex + 1);
        }
        List<MousePerformanceChartPoint> visiblePoints = new List<MousePerformanceChartPoint>(lastVisibleIndex - firstVisibleIndex + 1);
        for (int index = firstVisibleIndex; index <= lastVisibleIndex; index++)
        {
            visiblePoints.Add(points[index]);
        }
        return visiblePoints;
    }

    private static int FindFirstIndexAtOrAfter(IReadOnlyList<MousePerformanceChartPoint> points, double minimumX)
    {
        int low = 0;
        int high = points.Count - 1;
        int result = points.Count;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (points[middle].X >= minimumX)
            {
                result = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }
        return result;
    }

    private static int FindLastIndexAtOrBefore(IReadOnlyList<MousePerformanceChartPoint> points, double maximumX)
    {
        int low = 0;
        int high = points.Count - 1;
        int result = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (points[middle].X <= maximumX)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return result;
    }

    private static double? ResolvePercentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues == null || sortedValues.Count == 0)
        {
            return null;
        }
        double position = Math.Max(0.0, Math.Min(1.0, percentile)) * (sortedValues.Count - 1);
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return sortedValues[lowerIndex];
        }
        double blend = position - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * blend;
    }

    private static bool IsFinite(double value)
    {
        if (!double.IsNaN(value))
        {
            return !double.IsInfinity(value);
        }
        return false;
    }

    private static double GetEffectivePointX(MousePerformanceChartPoint point, double xOffset)
    {
        return point.X + xOffset;
    }

    private static double MapX(Rect plotArea, ChartViewport viewport, double value)
    {
        if (Math.Abs(viewport.XMaximum - viewport.XMinimum) < 1E-06)
        {
            return plotArea.Left;
        }
        return plotArea.Left + (value - viewport.XMinimum) / (viewport.XMaximum - viewport.XMinimum) * plotArea.Width;
    }

    private static double MapY(Rect plotArea, ChartViewport viewport, double value, bool screenYAxisPositiveDown = false)
    {
        if (Math.Abs(viewport.YMaximum - viewport.YMinimum) < 1E-06)
        {
            return screenYAxisPositiveDown ? plotArea.Top : plotArea.Bottom;
        }
        if (screenYAxisPositiveDown)
        {
            return plotArea.Top + (value - viewport.YMinimum) / (viewport.YMaximum - viewport.YMinimum) * plotArea.Height;
        }
        return plotArea.Bottom - (value - viewport.YMinimum) / (viewport.YMaximum - viewport.YMinimum) * plotArea.Height;
    }

    private HeaderLayout ResolveHeaderLayout(Size renderSize, MousePerformanceChartRenderFrame frame)
    {
        string title = ResolveText(frame?.Title);
        string subtitle = ResolveText(frame?.Subtitle);
        string description = ResolveText(frame?.Description);
        Point titleOrigin = new Point(HeaderHorizontalPadding, HeaderVerticalPadding);
        FormattedText titleText = null;
        if (!string.IsNullOrWhiteSpace(title))
        {
            titleText = CreateText(title, 20.0, _strongLabelBrush, strong: true, Math.Max(180.0, renderSize.Width * 0.42));
        }
        FormattedText subtitleText = null;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            subtitleText = CreateText(subtitle, 12.0, _labelBrush, strong: false, Math.Max(160.0, renderSize.Width * 0.34), HeaderSubtitleMaxHeight);
        }
        Point subtitleOrigin = new Point(titleOrigin.X, titleOrigin.Y);
        double stackedTextBottom = titleOrigin.Y;
        double stackedTextWidth = 0.0;
        if (titleText != null)
        {
            stackedTextBottom = Math.Max(stackedTextBottom, titleOrigin.Y + titleText.Height);
            stackedTextWidth = Math.Max(stackedTextWidth, titleText.Width);
        }
        if (subtitleText != null)
        {
            subtitleOrigin = new Point(titleOrigin.X, titleOrigin.Y + (titleText == null ? 0.0 : titleText.Height + HeaderTitleSubtitleSpacing));
            stackedTextBottom = Math.Max(stackedTextBottom, subtitleOrigin.Y + subtitleText.Height);
            stackedTextWidth = Math.Max(stackedTextWidth, subtitleText.Width);
        }
        FormattedText descriptionText = null;
        Point descriptionOrigin = new Point(titleOrigin.X, stackedTextBottom);
        double inlineDescriptionLeft = titleOrigin.X + stackedTextWidth + HeaderBlockSpacing;
        double inlineDescriptionWidth = renderSize.Width - inlineDescriptionLeft - HeaderHorizontalPadding;
        if (!string.IsNullOrWhiteSpace(description))
        {
            if (inlineDescriptionWidth >= HeaderDescriptionMinimumInlineWidth)
            {
                descriptionText = CreateText(description, 11.0, _labelBrush, strong: false, inlineDescriptionWidth, HeaderDescriptionMaxHeight);
                descriptionOrigin = new Point(inlineDescriptionLeft, titleOrigin.Y + HeaderDescriptionTopOffset);
            }
            else
            {
                double stackedDescriptionWidth = Math.Max(200.0, renderSize.Width - HeaderHorizontalPadding * 2.0);
                descriptionText = CreateText(description, 11.0, _labelBrush, strong: false, stackedDescriptionWidth, HeaderDescriptionMaxHeight);
                descriptionOrigin = new Point(HeaderHorizontalPadding, stackedTextBottom + HeaderDescriptionStackSpacing);
            }
        }
        double headerBottom = stackedTextBottom;
        if (descriptionText != null)
        {
            headerBottom = Math.Max(headerBottom, descriptionOrigin.Y + descriptionText.Height);
        }
        double plotAreaTop = Math.Max(DefaultTopMargin, headerBottom + HeaderBottomPadding);
        return new HeaderLayout(titleText, titleOrigin, subtitleText, subtitleOrigin, descriptionText, descriptionOrigin, plotAreaTop);
    }

    private Rect GetPlotArea(Size renderSize, MousePerformanceChartRenderFrame frame = null)
    {
        MousePerformanceChartRenderFrame effectiveFrame = frame ?? RenderFrame;
        HeaderLayout headerLayout = ResolveHeaderLayout(renderSize, effectiveFrame);
        return new Rect(DefaultLeftMargin, headerLayout.PlotAreaTop, Math.Max(40.0, renderSize.Width - DefaultLeftMargin - DefaultRightMargin), Math.Max(40.0, renderSize.Height - headerLayout.PlotAreaTop - DefaultBottomMargin));
    }

    private static Rect GetCollapsedArrangeRect()
    {
        return new Rect(0.0, 0.0, 0.0, 0.0);
    }

    private static double CoerceArrangeCoordinate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }
        return value;
    }

    private static double CoerceArrangeLength(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
        {
            return 0.0;
        }
        return value;
    }

    private static Rect CoerceArrangeRect(Rect rect)
    {
        if (rect.IsEmpty)
        {
            return GetCollapsedArrangeRect();
        }

        return new Rect(CoerceArrangeCoordinate(rect.X), CoerceArrangeCoordinate(rect.Y), CoerceArrangeLength(rect.Width), CoerceArrangeLength(rect.Height));
    }

    private Rect ResolveGpuHostArrangeRect(Size finalSize, MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || !frame.IsAvailable)
        {
            return GetCollapsedArrangeRect();
        }
        return CoerceArrangeRect(GetPlotArea(finalSize, frame));
    }

    private void OnGpuHostViewportChanged(object sender, GpuViewportChangedEventArgs e)
    {
        if (e != null && e.Viewport != null && RenderFrame != null && RenderFrame.IsAvailable)
        {
            ApplyViewport(RenderFrame, new ChartViewport(e.Viewport.XMinimum, e.Viewport.XMaximum, e.Viewport.YMinimum, e.Viewport.YMaximum));
            RequestDeferredVisualRefresh();
        }
    }

    private ImageSource UpdateGpuHostScene(Rect plotArea, ChartViewport viewport, IReadOnlyList<double> xAxisTicks, IReadOnlyList<double> yAxisTicks, IReadOnlyList<GapMarker> gapMarkers, MousePerformanceChartRenderFrame frame)
    {
        if (_gpuHost == null)
        {
            return null;
        }
        if (frame == null || !frame.IsAvailable || plotArea.Width <= 0.0 || plotArea.Height <= 0.0)
        {
            ClearGpuDataChunkCaches();
            ClearPlotBitmapCache();
            if (_gpuHost.Visibility != Visibility.Hidden)
            {
                _gpuHost.Visibility = Visibility.Hidden;
            }
            _gpuHost.SetScene(null);
            SyncGpuRendererStatus(isAvailable: false, string.Empty);
            return null;
        }
        GpuPlotSceneFrame scene = BuildGpuSceneFrame(plotArea, viewport, xAxisTicks, yAxisTicks, gapMarkers, frame);
        if (MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            if (_gpuHost.Visibility != Visibility.Hidden)
            {
                _gpuHost.Visibility = Visibility.Hidden;
            }
            _gpuHost.SetScene(null);
            string histogramFailureMessage = string.Empty;
            ImageSource histogramBitmap = RenderCachedGpuSceneBitmap(scene, plotArea, viewport, gapMarkers, frame, ref histogramFailureMessage);
            SyncGpuRendererStatus(histogramBitmap != null, histogramFailureMessage);
            return histogramBitmap;
        }
        bool wasVisible = _gpuHost.Visibility == Visibility.Visible;
        _gpuHost.SetScene(scene);
        ImageSource result = null;
        if (_gpuHost.IsRendererAvailable)
        {
            if (!wasVisible)
            {
                string gpuFailureMessage = string.Empty;
                result = RenderGpuSceneBitmap(scene, plotArea, preferHostRenderer: true, ref gpuFailureMessage);
            }
            if (_gpuHost.Visibility != Visibility.Visible)
            {
                _gpuHost.Visibility = Visibility.Visible;
            }
        }
        else if (_gpuHost.Visibility != Visibility.Hidden)
        {
            _gpuHost.Visibility = Visibility.Hidden;
        }
        SyncGpuRendererStatus(_gpuHost.IsRendererAvailable, _gpuHost.RendererUnavailableMessage);
        if (!_isGpuRendererAvailable && !string.IsNullOrWhiteSpace(_gpuRendererFailureMessage))
        {
            InvalidateVisual();
        }
        return result;
    }

    private ImageSource BuildExportPlotBitmap(Size renderSize, MousePerformanceChartRenderFrame frame, ref string gpuFailureMessage)
    {
        gpuFailureMessage = string.Empty;
        if (frame == null || !frame.IsAvailable)
        {
            return null;
        }
        Rect plotArea = GetPlotArea(renderSize, frame);
        if (plotArea.Width <= 0.0 || plotArea.Height <= 0.0)
        {
            return null;
        }
        ChartViewport viewport = ResolveViewport(frame);
        IReadOnlyList<double> xAxisTicks = ResolveXAxisLabelTicks(viewport, frame);
        IReadOnlyList<double> yAxisTicks = ResolveYAxisTicks(viewport);
        IReadOnlyList<GapMarker> gapMarkers = Array.Empty<GapMarker>();
        if (ShowGapOverlay)
        {
            gapMarkers = ResolveGapMarkers(frame, GapAnalysisDatasetSlot);
            ResolveVisibleGapMarkers(frame, viewport, GapAnalysisDatasetSlot);
        }
        GpuPlotSceneFrame scene = BuildGpuSceneFrame(plotArea, viewport, xAxisTicks, yAxisTicks, gapMarkers, frame);
        return RenderGpuSceneBitmap(scene, plotArea, preferHostRenderer: true, ref gpuFailureMessage);
    }

    private ImageSource RenderGpuSceneBitmap(GpuPlotSceneFrame scene, Rect plotArea, bool preferHostRenderer, ref string gpuFailureMessage)
    {
        gpuFailureMessage = string.Empty;
        if (scene == null || plotArea.Width <= 0.0 || plotArea.Height <= 0.0)
        {
            return null;
        }
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(plotArea.Width));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(plotArea.Height));
        string failureReason = string.Empty;
        if (preferHostRenderer && _gpuHost != null && _gpuHost.IsRendererAvailable && _gpuHost.CanRenderBitmapSize(pixelWidth, pixelHeight))
        {
            BitmapSource hostBitmap = _gpuHost.RenderSceneBitmap(scene, plotArea.Width, plotArea.Height, pixelWidth, pixelHeight, out failureReason);
            if (hostBitmap != null)
            {
                return hostBitmap;
            }
        }
        string fallbackFailureReason = string.Empty;
        BitmapSource offscreenBitmap = ResolveOffscreenGpuRenderer().Render(scene, plotArea.Width, plotArea.Height, pixelWidth, pixelHeight, out fallbackFailureReason);
        if (offscreenBitmap != null)
        {
            return offscreenBitmap;
        }
        gpuFailureMessage = ResolvePreferredGpuExportFailureDetail(failureReason, fallbackFailureReason);
        return null;
    }

    private ImageSource RenderCachedGpuSceneBitmap(GpuPlotSceneFrame scene, Rect plotArea, ChartViewport viewport, IReadOnlyList<GapMarker> gapMarkers, MousePerformanceChartRenderFrame frame, ref string gpuFailureMessage)
    {
        gpuFailureMessage = string.Empty;
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(plotArea.Width));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(plotArea.Height));
        if (TryGetCachedPlotBitmap(frame, viewport, gapMarkers, plotArea.Width, plotArea.Height, pixelWidth, pixelHeight, out ImageSource cachedBitmap))
        {
            return cachedBitmap;
        }

        ImageSource bitmap = RenderGpuSceneBitmap(scene, plotArea, preferHostRenderer: false, ref gpuFailureMessage);
        if (bitmap != null)
        {
            _cachedPlotBitmapEntry = new PlotBitmapCacheEntry(frame, viewport, gapMarkers, ShowGapOverlay, GapAnalysisDatasetSlot, plotArea.Width, plotArea.Height, pixelWidth, pixelHeight, bitmap);
        }
        return bitmap;
    }

    private bool TryGetCachedPlotBitmap(MousePerformanceChartRenderFrame frame, ChartViewport viewport, IReadOnlyList<GapMarker> gapMarkers, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight, out ImageSource bitmap)
    {
        PlotBitmapCacheEntry cacheEntry = _cachedPlotBitmapEntry;
        if (cacheEntry != null
            && ReferenceEquals(cacheEntry.Frame, frame)
            && AreSameViewport(cacheEntry.Viewport, viewport)
            && ReferenceEquals(cacheEntry.GapMarkers, gapMarkers)
            && cacheEntry.ShowGapOverlay == ShowGapOverlay
            && cacheEntry.GapAnalysisDatasetSlot == GapAnalysisDatasetSlot
            && AreClose(cacheEntry.LogicalWidth, logicalWidth)
            && AreClose(cacheEntry.LogicalHeight, logicalHeight)
            && cacheEntry.PixelWidth == pixelWidth
            && cacheEntry.PixelHeight == pixelHeight)
        {
            bitmap = cacheEntry.Bitmap;
            return bitmap != null;
        }

        bitmap = null;
        return false;
    }

    private MousePerformanceChartGpuOffscreenRenderer ResolveOffscreenGpuRenderer()
    {
        _offscreenGpuRenderer ??= new MousePerformanceChartGpuOffscreenRenderer();
        return _offscreenGpuRenderer;
    }

    private void DisposeOffscreenGpuRenderer()
    {
        if (_offscreenGpuRenderer == null)
        {
            return;
        }

        _offscreenGpuRenderer.Dispose();
        _offscreenGpuRenderer = null;
    }

    private void SyncGpuRendererStatus(bool isAvailable, string failureMessage)
    {
        string normalizedFailureMessage = isAvailable ? string.Empty : ResolveText(failureMessage).Trim();
        if (_isGpuRendererAvailable != isAvailable || !string.Equals(_gpuRendererFailureMessage, normalizedFailureMessage, StringComparison.Ordinal))
        {
            _isGpuRendererAvailable = isAvailable;
            _gpuRendererFailureMessage = normalizedFailureMessage;
            GpuRendererAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string ResolveEffectiveGpuRendererFailureMessage(string overrideFailureMessage)
    {
        string failureDetail = overrideFailureMessage != null ? ResolveText(overrideFailureMessage) : _gpuRendererFailureMessage;
        if (string.IsNullOrWhiteSpace(failureDetail))
        {
            return string.Empty;
        }
        return ResolveGpuRendererUnavailableMessage(failureDetail);
    }

    private string ResolveUnavailableMessage(MousePerformanceChartRenderFrame frame)
    {
        if (frame != null)
        {
            string frameMessage = ResolveText(frame.Message);
            if (!string.IsNullOrWhiteSpace(frameMessage))
            {
                return frameMessage;
            }
        }
        return ResolveDefaultUnavailableMessage();
    }

    private static string ResolveDefaultUnavailableMessage()
    {
        return ResolveStringResource("MousePerformance.Chart.Unavailable.NoData", "No chart data.");
    }

    private static string ResolveGpuRendererUnavailableMessage(string detail)
    {
        string failureDetail = ResolveText(detail).Trim();
        if (string.IsNullOrWhiteSpace(failureDetail))
        {
            return ResolveStringResource("MousePerformance.Chart.Unavailable.GpuRenderer", "The GPU chart renderer is unavailable.");
        }
        return FormatStringResource("MousePerformance.Chart.Unavailable.GpuRenderer.WithDetail", "The GPU chart renderer is unavailable. Direct3D 11: {0}", failureDetail);
    }

    private static string ResolveGpuExportFailureMessage(string detail)
    {
        string failureDetail = ResolveText(detail).Trim();
        if (string.IsNullOrWhiteSpace(failureDetail))
        {
            return ResolveStringResource("MousePerformance.Chart.Export.GpuError", "The GPU chart could not be exported.");
        }
        return FormatStringResource("MousePerformance.Chart.Export.GpuError.WithDetail", "The GPU chart could not be exported. Direct3D 11: {0}", failureDetail);
    }

    private static string ResolvePreferredGpuExportFailureDetail(string primaryDetail, string secondaryDetail)
    {
        string primaryFailureDetail = ResolveText(primaryDetail).Trim();
        if (!string.IsNullOrWhiteSpace(primaryFailureDetail))
        {
            return primaryFailureDetail;
        }
        return ResolveText(secondaryDetail).Trim();
    }

    private GpuPlotSceneFrame BuildGpuSceneFrame(Rect plotArea, ChartViewport viewport, IReadOnlyList<double> xAxisTicks, IReadOnlyList<double> yAxisTicks, IReadOnlyList<GapMarker> gapMarkers, MousePerformanceChartRenderFrame frame)
    {
        EnsureGpuDataChunkCacheFrame(frame);
        Rect localPlotArea = new Rect(0.0, 0.0, plotArea.Width, plotArea.Height);
        ChartViewport dataBounds = new ChartViewport(frame.XMinimum, frame.XMaximum, frame.YMinimum, frame.YMaximum);
        ChartViewport defaultViewport = ResolveAutomaticViewport(frame);
        return new GpuPlotSceneFrame
        {
            IsAvailable = true,
            ScreenYAxisPositiveDown = IsScreenYAxisPositiveDown(frame.PlotType),
            EnableAutomaticWheelZoom = (frame.PlotType != MousePerformancePlotType.XVsY),
            UnavailableMessage = ResolveText(frame.Message),
            DefaultViewport = ToGpuViewport(defaultViewport),
            Viewport = ToGpuViewport(viewport),
            DataBounds = ToGpuViewport(dataBounds),
            Style = new GpuPlotStyleSnapshot
            {
                PlotBackgroundColor = ResolveBrushColor(_panelBrush, Colors.Transparent),
                PlotBorderColor = ResolvePenColor(_axisPen, Colors.Transparent),
                UnavailableForegroundColor = ResolveBrushColor(_strongLabelBrush, Colors.White)
            },
            GridLines = BuildGpuGridLines(localPlotArea, viewport, xAxisTicks, yAxisTicks, frame.PlotType).ToArray(),
            GapBands = BuildGpuGapBands(localPlotArea, viewport, gapMarkers).ToArray(),
            Series = BuildGpuSeriesSubmissions(frame, gapMarkers).ToArray()
        };
    }

    private void EnsureGpuDataChunkCacheFrame(MousePerformanceChartRenderFrame frame)
    {
        HashSet<DataPointSourceCacheKey> activeSources = ResolveGpuDataPointSources(frame);
        if (activeSources.Count == 0)
        {
            ClearGpuDataChunkCaches();
            return;
        }

        foreach (DataPointSourceCacheKey activeSource in activeSources)
        {
            MarkGpuDataChunkCacheSourceUsed(activeSource);
        }

        TrimGpuDataChunkCaches(activeSources);
    }

    private void ClearGpuDataChunkCaches()
    {
        _gpuDataChunkCacheRecentSources.Clear();
        _gpuDataPointChunkCache.Clear();
        _gpuDataSegmentChunkCache.Clear();
        _gpuHistogramBinChunkCache.Clear();
    }

    private static HashSet<DataPointSourceCacheKey> ResolveGpuDataPointSources(MousePerformanceChartRenderFrame frame)
    {
        HashSet<DataPointSourceCacheKey> sources = new HashSet<DataPointSourceCacheKey>();
        if (frame?.Series == null)
        {
            return sources;
        }
        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series?.Points != null && series.Points.Count > 0)
            {
                sources.Add(new DataPointSourceCacheKey(series.Points, series.XOffset));
            }
            if (series?.HistogramBins != null && series.HistogramBins.Count > 0)
            {
                sources.Add(new DataPointSourceCacheKey(series.HistogramBins, series.XOffset));
            }
        }
        return sources;
    }

    private void TrimGpuDataChunkCaches(HashSet<DataPointSourceCacheKey> activeSources)
    {
        if (activeSources == null || activeSources.Count == 0)
        {
            ClearGpuDataChunkCaches();
            return;
        }

        int retainedSourceLimit = activeSources.Count + GpuChunkCacheRetainedInactiveSourceCount;
        while (_gpuDataChunkCacheRecentSources.Count > retainedSourceLimit)
        {
            int sourceIndex = FindOldestInactiveGpuDataChunkCacheSourceIndex(activeSources);
            if (sourceIndex < 0)
            {
                break;
            }

            DataPointSourceCacheKey source = _gpuDataChunkCacheRecentSources[sourceIndex];
            _gpuDataChunkCacheRecentSources.RemoveAt(sourceIndex);
            RemoveGpuDataChunkCacheSource(source);
        }
    }

    private void MarkGpuDataChunkCacheSourceUsed(DataPointSourceCacheKey source)
    {
        for (int sourceIndex = 0; sourceIndex < _gpuDataChunkCacheRecentSources.Count; sourceIndex++)
        {
            if (_gpuDataChunkCacheRecentSources[sourceIndex].Equals(source))
            {
                _gpuDataChunkCacheRecentSources.RemoveAt(sourceIndex);
                break;
            }
        }
        _gpuDataChunkCacheRecentSources.Add(source);
    }

    private int FindOldestInactiveGpuDataChunkCacheSourceIndex(HashSet<DataPointSourceCacheKey> activeSources)
    {
        for (int sourceIndex = 0; sourceIndex < _gpuDataChunkCacheRecentSources.Count; sourceIndex++)
        {
            if (!activeSources.Contains(_gpuDataChunkCacheRecentSources[sourceIndex]))
            {
                return sourceIndex;
            }
        }
        return -1;
    }

    private void RemoveGpuDataChunkCacheSource(DataPointSourceCacheKey source)
    {
        DataPointChunkCacheKey[] pointKeys = _gpuDataPointChunkCache.Keys.Where(key => key.Source.Equals(source)).ToArray();
        foreach (DataPointChunkCacheKey pointKey in pointKeys)
        {
            _gpuDataPointChunkCache.Remove(pointKey);
        }

        DataSegmentChunkCacheKey[] segmentKeys = _gpuDataSegmentChunkCache.Keys.Where(key => key.Source.Equals(source)).ToArray();
        foreach (DataSegmentChunkCacheKey segmentKey in segmentKeys)
        {
            _gpuDataSegmentChunkCache.Remove(segmentKey);
        }

        HistogramBinChunkCacheKey[] histogramKeys = _gpuHistogramBinChunkCache.Keys.Where(key => key.Source.Equals(source)).ToArray();
        foreach (HistogramBinChunkCacheKey histogramKey in histogramKeys)
        {
            _gpuHistogramBinChunkCache.Remove(histogramKey);
        }
    }

    private void RequestDeferredVisualRefresh()
    {
        if (!_deferredVisualRefreshPending)
        {
            _deferredVisualRefreshPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _deferredVisualRefreshPending = false;
                InvalidateVisual();
            }));
        }
    }

    private List<GpuGridLine> BuildGpuGridLines(Rect localPlotArea, ChartViewport viewport, IReadOnlyList<double> xAxisTicks, IReadOnlyList<double> yAxisTicks, MousePerformancePlotType plotType)
    {
        List<GpuGridLine> gridLines = new List<GpuGridLine>();
        Color majorGridColor = ResolvePenColor(_gridPen, Colors.Transparent);
        Color minorGridColor = ResolvePenColor(_minorGridPen, Colors.Transparent);
        Color zeroAxisColor = ResolveZeroAxisColor(majorGridColor, ResolvePenColor(_axisPen, Colors.Transparent));
        bool screenYAxisPositiveDown = IsScreenYAxisPositiveDown(plotType);
        if (MousePerformancePlotTraits.IsHistogramPlot(plotType))
        {
            foreach (double xAxisTick in xAxisTicks ?? Array.Empty<double>())
            {
                double x = MapX(localPlotArea, viewport, xAxisTick);
                if (!IsPlotEdgeCoordinate(x, localPlotArea.Left, localPlotArea.Right) && x >= localPlotArea.Left - 0.5 && x <= localPlotArea.Right + 0.5)
                {
                    gridLines.Add(new GpuGridLine
                    {
                        IsVertical = true,
                        PositionPixels = (float)x,
                        ThicknessPixels = 1f,
                        Color = majorGridColor
                    });
                }
            }
        }
        else
        {
            for (int majorIntervalIndex = 0; majorIntervalIndex < GridLineCount; majorIntervalIndex++)
            {
                for (int subdivision = 1; subdivision <= MinorGridSubdivisionCount - 1; subdivision++)
                {
                    double normalizedX = (majorIntervalIndex + (double)subdivision / MinorGridSubdivisionCount) / GridLineCount;
                    gridLines.Add(new GpuGridLine
                    {
                        IsVertical = true,
                        PositionPixels = (float)(localPlotArea.Left + localPlotArea.Width * normalizedX),
                        ThicknessPixels = 1f,
                        Color = minorGridColor
                    });
                }
            }
            for (int tickIndex = 1; tickIndex < GridLineCount; tickIndex++)
            {
                gridLines.Add(new GpuGridLine
                {
                    IsVertical = true,
                    PositionPixels = (float)(localPlotArea.Left + localPlotArea.Width * tickIndex / GridLineCount),
                    ThicknessPixels = 1f,
                    Color = majorGridColor
                });
            }
        }
        foreach (double minorTick in ResolveMinorYAxisTicks(viewport, yAxisTicks))
        {
            double y = MapY(localPlotArea, viewport, minorTick, screenYAxisPositiveDown);
            if (!IsPlotEdgeCoordinate(y, localPlotArea.Top, localPlotArea.Bottom))
            {
                gridLines.Add(new GpuGridLine
                {
                    IsVertical = false,
                    PositionPixels = (float)y,
                    ThicknessPixels = 1f,
                    Color = minorGridColor
                });
            }
        }
        foreach (double yAxisTick in yAxisTicks)
        {
            double y = MapY(localPlotArea, viewport, yAxisTick, screenYAxisPositiveDown);
            if (!IsPlotEdgeCoordinate(y, localPlotArea.Top, localPlotArea.Bottom) && y >= localPlotArea.Top - 0.5 && y <= localPlotArea.Bottom + 0.5)
            {
                gridLines.Add(new GpuGridLine
                {
                    IsVertical = false,
                    PositionPixels = (float)y,
                    ThicknessPixels = 1f,
                    Color = majorGridColor
                });
            }
        }
        AddVisibleZeroAxisGridLines(gridLines, localPlotArea, viewport, screenYAxisPositiveDown, zeroAxisColor);
        return gridLines;
    }

    private static void AddVisibleZeroAxisGridLines(List<GpuGridLine> gridLines, Rect localPlotArea, ChartViewport viewport, bool screenYAxisPositiveDown, Color zeroAxisColor)
    {
        if (gridLines == null || localPlotArea.Width <= 0.0 || localPlotArea.Height <= 0.0)
        {
            return;
        }

        if (IsAxisValueVisible(viewport.XMinimum, viewport.XMaximum, 0.0))
        {
            double x = MapX(localPlotArea, viewport, 0.0);
            if (IsInteriorPlotCoordinate(x, localPlotArea.Left, localPlotArea.Right))
            {
                gridLines.Add(new GpuGridLine
                {
                    IsVertical = true,
                    PositionPixels = (float)x,
                    ThicknessPixels = ZeroAxisThicknessPixels,
                    Color = zeroAxisColor
                });
            }
        }

        if (IsAxisValueVisible(viewport.YMinimum, viewport.YMaximum, 0.0))
        {
            double y = MapY(localPlotArea, viewport, 0.0, screenYAxisPositiveDown);
            if (IsInteriorPlotCoordinate(y, localPlotArea.Top, localPlotArea.Bottom))
            {
                gridLines.Add(new GpuGridLine
                {
                    IsVertical = false,
                    PositionPixels = (float)y,
                    ThicknessPixels = ZeroAxisThicknessPixels,
                    Color = zeroAxisColor
                });
            }
        }
    }

    private static Color ResolveZeroAxisColor(Color majorGridColor, Color axisColor)
    {
        if (majorGridColor.A == 0)
        {
            return axisColor;
        }
        if (axisColor.A == 0)
        {
            return majorGridColor;
        }
        return BlendColors(majorGridColor, axisColor, 0.62);
    }

    private List<GpuGapBand> BuildGpuGapBands(Rect localPlotArea, ChartViewport viewport, IReadOnlyList<GapMarker> gapMarkers)
    {
        List<GpuGapBand> gapBands = new List<GpuGapBand>();
        if (!ShowGapOverlay || gapMarkers == null || gapMarkers.Count == 0)
        {
            return gapBands;
        }
        Color fillColor = ResolveBrushColor(_gapBrush, Colors.Transparent);
        Color lineColor = ResolvePenColor(_gapPen, Colors.Transparent);
        foreach (GapMarker gapMarker in FilterGapMarkersForViewport(gapMarkers, viewport))
        {
            double startX = MapX(localPlotArea, viewport, gapMarker.StartX);
            double endX = MapX(localPlotArea, viewport, gapMarker.EndX);
            double left = Math.Min(startX, endX);
            double mappedWidth = Math.Abs(endX - startX);
            double width = Math.Max(GapBandMinimumWidth, mappedWidth);
            double adjustedLeft = left - (width - mappedWidth) / 2.0;
            gapBands.Add(new GpuGapBand
            {
                LeftPixels = (float)adjustedLeft,
                WidthPixels = (float)width,
                CenterXPixels = (float)(adjustedLeft + width / 2.0),
                FillColor = fillColor,
                LineColor = lineColor,
                LineThicknessPixels = (float)GapLineThickness
            });
        }
        return gapBands;
    }

    private List<GpuSeriesSubmission> BuildGpuSeriesSubmissions(MousePerformanceChartRenderFrame frame, IReadOnlyList<GapMarker> gapMarkers)
    {
        List<GpuSeriesSubmission> submissions = new List<GpuSeriesSubmission>();
        if (frame == null || frame.Series == null || frame.Series.Count == 0)
        {
            return submissions;
        }

        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series == null)
            {
                continue;
            }

            GpuSeriesSubmission submission = series.Kind switch
            {
                MousePerformanceChartSeriesKind.Scatter => BuildGpuScatterSubmission(frame, series),
                MousePerformanceChartSeriesKind.Stem => BuildGpuStemSubmission(frame, series),
                MousePerformanceChartSeriesKind.Line => BuildGpuLineSubmission(frame, series, gapMarkers),
                MousePerformanceChartSeriesKind.Histogram => BuildGpuHistogramSubmission(frame, series),
                _ => null
            };
            if (submission != null)
            {
                submissions.Add(submission);
            }
        }

        return submissions;
    }

    private GpuSeriesSubmission BuildGpuScatterSubmission(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series)
    {
        GpuPointChunk[] chunks = ResolveDataPointChunks(series);
        if (chunks == null || chunks.Length == 0)
        {
            return null;
        }

        return new GpuSeriesSubmission
        {
            SourceKey = series.Points,
            Kind = GpuSeriesKind.Scatter,
            DatasetSlot = (int)series.DatasetSlot,
            XOffset = series.XOffset,
            Color = ResolveBrushColor(ResolveSeriesBrush(frame, series), Colors.Transparent),
            RadiusPixels = 1.55f,
            ThicknessPixels = 1f,
            UseDataCoordinates = true,
            PointChunks = chunks
        };
    }

    private GpuSeriesSubmission BuildGpuStemSubmission(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series)
    {
        GpuSegmentChunk[] chunks = ResolveDataStemSegmentChunks(series);
        if (chunks == null || chunks.Length == 0)
        {
            return null;
        }

        return new GpuSeriesSubmission
        {
            SourceKey = series.Points,
            Kind = GpuSeriesKind.Stem,
            DatasetSlot = (int)series.DatasetSlot,
            XOffset = series.XOffset,
            Color = ResolvePenColor(ResolveSeriesPen(frame, series, MousePerformanceChartSeriesKind.Stem), Colors.Transparent),
            RadiusPixels = 0f,
            ThicknessPixels = 0.7f,
            UseDataCoordinates = true,
            SegmentChunks = chunks
        };
    }

    private GpuSeriesSubmission BuildGpuLineSubmission(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series, IReadOnlyList<GapMarker> gapMarkers)
    {
        IReadOnlyList<GapMarker> effectiveGapMarkers = frame.PlotType == MousePerformancePlotType.XVsY ? Array.Empty<GapMarker>() : gapMarkers;
        object geometryKey = ResolveLineGeometryKey(effectiveGapMarkers);
        GpuSegmentChunk[] chunks = ResolveDataLineSegmentChunks(series, effectiveGapMarkers, geometryKey);
        if (chunks == null || chunks.Length == 0)
        {
            return null;
        }

        return new GpuSeriesSubmission
        {
            SourceKey = series.Points,
            GeometryKey = geometryKey,
            Kind = GpuSeriesKind.Line,
            DatasetSlot = (int)series.DatasetSlot,
            XOffset = series.XOffset,
            Color = ResolvePenColor(ResolveSeriesPen(frame, series, MousePerformanceChartSeriesKind.Line), Colors.Transparent),
            RadiusPixels = 0f,
            ThicknessPixels = ResolveLineThicknessPixels(frame, series),
            UseDataCoordinates = true,
            SegmentChunks = chunks
        };
    }

    private GpuSeriesSubmission BuildGpuHistogramSubmission(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series)
    {
        GpuHistogramBinChunk[] chunks = ResolveHistogramBinChunks(series);
        if (chunks == null || chunks.Length == 0)
        {
            return null;
        }

        return new GpuSeriesSubmission
        {
            SourceKey = series.HistogramBins,
            Kind = GpuSeriesKind.Histogram,
            DatasetSlot = (int)series.DatasetSlot,
            XOffset = series.XOffset,
            Color = ResolveBrushColor(ResolveSeriesBrush(frame, series), Colors.Transparent),
            RadiusPixels = 0f,
            ThicknessPixels = 1f,
            UseDataCoordinates = true,
            HistogramBinChunks = chunks
        };
    }

    private static object ResolveLineGeometryKey(IReadOnlyList<GapMarker> gapMarkers)
    {
        if (gapMarkers == null || gapMarkers.Count == 0)
        {
            return null;
        }
        return gapMarkers;
    }

    private GpuPointChunk[] ResolveDataPointChunks(MousePerformanceChartSeries series)
    {
        if (series?.Points == null || series.Points.Count == 0)
        {
            return Array.Empty<GpuPointChunk>();
        }
        DataPointChunkCacheKey cacheKey = new DataPointChunkCacheKey(series.Points, series.XOffset);
        if (_gpuDataPointChunkCache.TryGetValue(cacheKey, out GpuPointChunk[] chunks))
        {
            return chunks;
        }

        chunks = BuildDataPointChunks(series.Points, series.XOffset);
        _gpuDataPointChunkCache[cacheKey] = chunks;
        return chunks;
    }

    private GpuSegmentChunk[] ResolveDataStemSegmentChunks(MousePerformanceChartSeries series)
    {
        if (series?.Points == null || series.Points.Count == 0)
        {
            return Array.Empty<GpuSegmentChunk>();
        }
        DataSegmentChunkCacheKey cacheKey = new DataSegmentChunkCacheKey(series.Points, MousePerformanceChartSeriesKind.Stem, series.XOffset, null);
        if (_gpuDataSegmentChunkCache.TryGetValue(cacheKey, out GpuSegmentChunk[] chunks))
        {
            return chunks;
        }

        chunks = BuildDataStemSegmentChunks(series.Points, series.XOffset);
        _gpuDataSegmentChunkCache[cacheKey] = chunks;
        return chunks;
    }

    private GpuSegmentChunk[] ResolveDataLineSegmentChunks(MousePerformanceChartSeries series, IReadOnlyList<GapMarker> gapMarkers, object geometryKey)
    {
        if (series?.Points == null || series.Points.Count < 2)
        {
            return Array.Empty<GpuSegmentChunk>();
        }
        DataSegmentChunkCacheKey cacheKey = new DataSegmentChunkCacheKey(series.Points, MousePerformanceChartSeriesKind.Line, series.XOffset, geometryKey);
        if (_gpuDataSegmentChunkCache.TryGetValue(cacheKey, out GpuSegmentChunk[] chunks))
        {
            return chunks;
        }

        chunks = BuildDataLineSegmentChunks(series.Points, series.XOffset, gapMarkers);
        _gpuDataSegmentChunkCache[cacheKey] = chunks;
        return chunks;
    }

    private GpuHistogramBinChunk[] ResolveHistogramBinChunks(MousePerformanceChartSeries series)
    {
        if (series?.HistogramBins == null || series.HistogramBins.Count == 0)
        {
            return Array.Empty<GpuHistogramBinChunk>();
        }
        HistogramBinChunkCacheKey cacheKey = new HistogramBinChunkCacheKey(series.HistogramBins, series.XOffset, series.GroupScale);
        if (_gpuHistogramBinChunkCache.TryGetValue(cacheKey, out GpuHistogramBinChunk[] chunks))
        {
            return chunks;
        }

        chunks = BuildHistogramBinChunks(series.HistogramBins, series.XOffset, series.GroupScale);
        _gpuHistogramBinChunkCache[cacheKey] = chunks;
        return chunks;
    }

    private static GpuPointChunk[] BuildDataPointChunks(IReadOnlyList<MousePerformanceChartPoint> points, double xOffset)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<GpuPointChunk>();
        }

        List<GpuPointChunk> chunks = new List<GpuPointChunk>((points.Count + GpuChunkSize - 1) / GpuChunkSize);
        int chunkIndex = 0;
        for (int firstPointIndex = 0; firstPointIndex < points.Count; firstPointIndex += GpuChunkSize)
        {
            int chunkLength = Math.Min(GpuChunkSize, points.Count - firstPointIndex);
            List<GpuPointVertex> vertices = new List<GpuPointVertex>(chunkLength);
            bool hasOrigin = false;
            double originX = 0.0;
            double originY = 0.0;
            double minimumX = double.PositiveInfinity;
            double maximumX = double.NegativeInfinity;
            double minimumY = double.PositiveInfinity;
            double maximumY = double.NegativeInfinity;

            for (int localIndex = 0; localIndex < chunkLength; localIndex++)
            {
                MousePerformanceChartPoint point = points[firstPointIndex + localIndex];
                double x = GetEffectivePointX(point, xOffset);
                double y = point.Y;
                if (!IsFinite(x) || !IsFinite(y))
                {
                    continue;
                }
                if (!hasOrigin)
                {
                    originX = x;
                    originY = y;
                    hasOrigin = true;
                }

                vertices.Add(new GpuPointVertex((float)(x - originX), (float)(y - originY)));
                UpdateDataChunkBounds(x, y, ref minimumX, ref maximumX, ref minimumY, ref maximumY);
            }

            if (vertices.Count > 0)
            {
                chunks.Add(CreateDataPointChunk(chunkIndex, originX, originY, minimumX, maximumX, minimumY, maximumY, vertices.ToArray()));
            }
            chunkIndex++;
        }

        return chunks.ToArray();
    }

    private static GpuSegmentChunk[] BuildDataStemSegmentChunks(IReadOnlyList<MousePerformanceChartPoint> points, double xOffset)
    {
        if (points == null || points.Count == 0)
        {
            return Array.Empty<GpuSegmentChunk>();
        }

        List<GpuSegmentChunk> chunks = new List<GpuSegmentChunk>((points.Count + GpuChunkSize - 1) / GpuChunkSize);
        int chunkIndex = 0;
        for (int firstPointIndex = 0; firstPointIndex < points.Count; firstPointIndex += GpuChunkSize)
        {
            int chunkLength = Math.Min(GpuChunkSize, points.Count - firstPointIndex);
            List<GpuSegmentVertex> segments = new List<GpuSegmentVertex>(chunkLength);
            bool hasOrigin = false;
            double originX = 0.0;
            const double originY = 0.0;
            double minimumX = double.PositiveInfinity;
            double maximumX = double.NegativeInfinity;
            double minimumY = double.PositiveInfinity;
            double maximumY = double.NegativeInfinity;

            for (int localIndex = 0; localIndex < chunkLength; localIndex++)
            {
                MousePerformanceChartPoint point = points[firstPointIndex + localIndex];
                double x = GetEffectivePointX(point, xOffset);
                double y = point.Y;
                if (!IsFinite(x) || !IsFinite(y))
                {
                    continue;
                }
                if (!hasOrigin)
                {
                    originX = x;
                    hasOrigin = true;
                }

                segments.Add(new GpuSegmentVertex((float)(x - originX), 0f, (float)(x - originX), (float)(y - originY)));
                UpdateDataChunkBounds(x, 0.0, ref minimumX, ref maximumX, ref minimumY, ref maximumY);
                UpdateDataChunkBounds(x, y, ref minimumX, ref maximumX, ref minimumY, ref maximumY);
            }

            if (segments.Count > 0)
            {
                chunks.Add(CreateDataSegmentChunk(chunkIndex, originX, originY, minimumX, maximumX, minimumY, maximumY, segments.ToArray()));
            }
            chunkIndex++;
        }

        return chunks.ToArray();
    }

    private static GpuHistogramBinChunk[] BuildHistogramBinChunks(IReadOnlyList<MousePerformanceHistogramBin> bins, double xOffset, double groupScale)
    {
        if (bins == null || bins.Count == 0)
        {
            return Array.Empty<GpuHistogramBinChunk>();
        }

        List<GpuHistogramBinChunk> chunks = new List<GpuHistogramBinChunk>((bins.Count + GpuChunkSize - 1) / GpuChunkSize);
        int chunkIndex = 0;
        for (int firstBinIndex = 0; firstBinIndex < bins.Count; firstBinIndex += GpuChunkSize)
        {
            int chunkLength = Math.Min(GpuChunkSize, bins.Count - firstBinIndex);
            List<GpuHistogramBinVertex> vertices = new List<GpuHistogramBinVertex>(chunkLength);
            bool hasOrigin = false;
            double originX = 0.0;
            const double originY = 0.0;
            double minimumX = double.PositiveInfinity;
            double maximumX = double.NegativeInfinity;
            double minimumY = 0.0;
            double maximumY = double.NegativeInfinity;

            for (int localIndex = 0; localIndex < chunkLength; localIndex++)
            {
                MousePerformanceHistogramBin bin = bins[firstBinIndex + localIndex];
                MousePerformanceSeriesBuilder.ResolveHistogramBinXRange(bin, xOffset, groupScale, out double minimumBinX, out double maximumBinX);
                double value = bin.Value;
                if (!IsFinite(minimumBinX) || !IsFinite(maximumBinX) || !IsFinite(value) || maximumBinX <= minimumBinX)
                {
                    continue;
                }
                if (!hasOrigin)
                {
                    originX = minimumBinX;
                    hasOrigin = true;
                }

                vertices.Add(new GpuHistogramBinVertex((float)(minimumBinX - originX), (float)(maximumBinX - originX), (float)(value - originY)));
                UpdateDataChunkBounds(minimumBinX, 0.0, ref minimumX, ref maximumX, ref minimumY, ref maximumY);
                UpdateDataChunkBounds(maximumBinX, value, ref minimumX, ref maximumX, ref minimumY, ref maximumY);
            }

            if (vertices.Count > 0)
            {
                chunks.Add(new GpuHistogramBinChunk
                {
                    ChunkIndex = chunkIndex,
                    OriginX = originX,
                    OriginY = originY,
                    MinimumX = minimumX,
                    MaximumX = maximumX,
                    MinimumY = minimumY,
                    MaximumY = maximumY,
                    Bins = vertices.ToArray()
                });
            }
            chunkIndex++;
        }

        return chunks.ToArray();
    }

    private static GpuSegmentChunk[] BuildDataLineSegmentChunks(IReadOnlyList<MousePerformanceChartPoint> points, double xOffset, IReadOnlyList<GapMarker> gapMarkers)
    {
        if (points == null || points.Count < 2)
        {
            return Array.Empty<GpuSegmentChunk>();
        }

        List<GpuSegmentChunk> chunks = new List<GpuSegmentChunk>((points.Count + GpuChunkSize - 1) / GpuChunkSize);
        List<GpuSegmentVertex> segments = new List<GpuSegmentVertex>(GpuChunkSize);
        int chunkIndex = 0;
        bool hasOrigin = false;
        double originX = 0.0;
        double originY = 0.0;
        double minimumX = double.PositiveInfinity;
        double maximumX = double.NegativeInfinity;
        double minimumY = double.PositiveInfinity;
        double maximumY = double.NegativeInfinity;

        for (int pointIndex = 1; pointIndex < points.Count; pointIndex++)
        {
            MousePerformanceChartPoint previousPoint = points[pointIndex - 1];
            MousePerformanceChartPoint currentPoint = points[pointIndex];
            double previousX = GetEffectivePointX(previousPoint, xOffset);
            double currentX = GetEffectivePointX(currentPoint, xOffset);
            double previousY = previousPoint.Y;
            double currentY = currentPoint.Y;
            if (!IsFinite(previousX) || !IsFinite(currentX) || !IsFinite(previousY) || !IsFinite(currentY) || HasGapBetween(previousX, currentX, gapMarkers))
            {
                continue;
            }
            if (!hasOrigin)
            {
                originX = Math.Min(previousX, currentX);
                originY = Math.Min(previousY, currentY);
                hasOrigin = true;
            }

            segments.Add(new GpuSegmentVertex((float)(previousX - originX), (float)(previousY - originY), (float)(currentX - originX), (float)(currentY - originY)));
            UpdateDataChunkBounds(previousX, previousY, ref minimumX, ref maximumX, ref minimumY, ref maximumY);
            UpdateDataChunkBounds(currentX, currentY, ref minimumX, ref maximumX, ref minimumY, ref maximumY);

            if (segments.Count >= GpuChunkSize)
            {
                chunks.Add(CreateDataSegmentChunk(chunkIndex, originX, originY, minimumX, maximumX, minimumY, maximumY, segments.ToArray()));
                chunkIndex++;
                segments.Clear();
                hasOrigin = false;
                originX = 0.0;
                originY = 0.0;
                minimumX = double.PositiveInfinity;
                maximumX = double.NegativeInfinity;
                minimumY = double.PositiveInfinity;
                maximumY = double.NegativeInfinity;
            }
        }

        if (segments.Count > 0)
        {
            chunks.Add(CreateDataSegmentChunk(chunkIndex, originX, originY, minimumX, maximumX, minimumY, maximumY, segments.ToArray()));
        }

        return chunks.ToArray();
    }

    private static GpuPointChunk CreateDataPointChunk(int chunkIndex, double originX, double originY, double minimumX, double maximumX, double minimumY, double maximumY, GpuPointVertex[] vertices)
    {
        return new GpuPointChunk
        {
            ChunkIndex = chunkIndex,
            OriginX = originX,
            OriginY = originY,
            MinimumX = minimumX,
            MaximumX = maximumX,
            MinimumY = minimumY,
            MaximumY = maximumY,
            IsMonotonicX = false,
            Points = vertices ?? Array.Empty<GpuPointVertex>()
        };
    }

    private static GpuSegmentChunk CreateDataSegmentChunk(int chunkIndex, double originX, double originY, double minimumX, double maximumX, double minimumY, double maximumY, GpuSegmentVertex[] segments)
    {
        return new GpuSegmentChunk
        {
            ChunkIndex = chunkIndex,
            OriginX = originX,
            OriginY = originY,
            MinimumX = minimumX,
            MaximumX = maximumX,
            MinimumY = minimumY,
            MaximumY = maximumY,
            Segments = segments ?? Array.Empty<GpuSegmentVertex>()
        };
    }

    private static void UpdateDataChunkBounds(double x, double y, ref double minimumX, ref double maximumX, ref double minimumY, ref double maximumY)
    {
        minimumX = Math.Min(minimumX, x);
        maximumX = Math.Max(maximumX, x);
        minimumY = Math.Min(minimumY, y);
        maximumY = Math.Max(maximumY, y);
    }

    private static GpuViewportState ToGpuViewport(ChartViewport viewport)
    {
        return new GpuViewportState
        {
            XMinimum = viewport.XMinimum,
            XMaximum = viewport.XMaximum,
            YMinimum = viewport.YMinimum,
            YMaximum = viewport.YMaximum
        };
    }

    private static Color ResolveBrushColor(Brush brush, Color fallback)
    {
        if (!(brush is SolidColorBrush { Color: var color }))
        {
            return fallback;
        }
        return color;
    }

    private static Color ResolvePenColor(Pen pen, Color fallback)
    {
        if (pen == null)
        {
            return fallback;
        }
        return ResolveBrushColor(pen.Brush, fallback);
    }

    private ChartViewport ResolveViewport(MousePerformanceChartRenderFrame frame)
    {
        ChartViewport result;
        if (frame == null || !frame.IsAvailable)
        {
            result = new ChartViewport(0.0, 1.0, 0.0, 1.0);
        }
        else
        {
            if (!_hasCustomViewport)
            {
                return ResolveAutomaticViewport(frame);
            }
            result = new ChartViewport(_viewXMinimum, _viewXMaximum, _viewYMinimum, _viewYMaximum);
        }
        return result;
    }

    private void ApplyViewport(MousePerformanceChartRenderFrame frame, ChartViewport viewport)
    {
        if (frame == null || !frame.IsAvailable)
        {
            ClearViewportState();
            return;
        }
        ChartViewport chartViewport = ClampViewport(frame, viewport);
        _viewXMinimum = chartViewport.XMinimum;
        _viewXMaximum = chartViewport.XMaximum;
        _viewYMinimum = chartViewport.YMinimum;
        _viewYMaximum = chartViewport.YMaximum;
        _hasCustomViewport = true;
    }

    private static ChartViewport ClampViewport(MousePerformanceChartRenderFrame frame, ChartViewport viewport)
    {
        double fullXSpan = Math.Max(MinimumViewportSpan, frame.XMaximum - frame.XMinimum);
        double fullYSpan = Math.Max(MinimumViewportSpan, frame.YMaximum - frame.YMinimum);
        double viewportWidth = Math.Max(GetMinimumViewportSpan(fullXSpan), Math.Min(viewport.XMaximum - viewport.XMinimum, fullXSpan));
        double viewportHeight = Math.Max(GetMinimumViewportSpan(fullYSpan), Math.Min(viewport.YMaximum - viewport.YMinimum, fullYSpan));

        double xMinimum = frame.XMinimum;
        if (viewportWidth < fullXSpan)
        {
            xMinimum = Math.Max(frame.XMinimum, Math.Min(viewport.XMinimum, frame.XMaximum - viewportWidth));
        }

        double yMinimum = frame.YMinimum;
        if (viewportHeight < fullYSpan)
        {
            yMinimum = Math.Max(frame.YMinimum, Math.Min(viewport.YMinimum, frame.YMaximum - viewportHeight));
        }

        return new ChartViewport(xMinimum, xMinimum + viewportWidth, yMinimum, yMinimum + viewportHeight);
    }

    private static double GetMinimumViewportSpan(double fullSpan)
    {
        return Math.Max(0.0001, Math.Abs(fullSpan) * 0.0005);
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) <= 1E-06;
    }

    private ChartViewport ResolveAutomaticViewport(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || !frame.IsAvailable)
        {
            return new ChartViewport(0.0, 1.0, 0.0, 1.0);
        }
        if (_hasCachedAutomaticViewport && ReferenceEquals(_cachedAutomaticViewportFrame, frame))
        {
            return _cachedAutomaticViewport;
        }
        ChartViewport result = (_cachedAutomaticViewport = ClampViewport(frame, BuildAutomaticViewport(frame)));
        _cachedAutomaticViewportFrame = frame;
        _hasCachedAutomaticViewport = true;
        return result;
    }

    private static ChartViewport BuildAutomaticViewport(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null)
        {
            return new ChartViewport(0.0, 1.0, 0.0, 1.0);
        }

        ChartViewport fullFrameViewport = new ChartViewport(frame.XMinimum, frame.XMaximum, frame.YMinimum, frame.YMaximum);
        if (!frame.IsAvailable || frame.Series == null || frame.Series.Count == 0)
        {
            return fullFrameViewport;
        }
        if (MousePerformancePlotTraits.IsHistogramPlot(frame.PlotType))
        {
            return fullFrameViewport;
        }

        int pointCapacity = 0;
        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series != null && IsAutomaticViewportSourceSeries(frame.PlotType, series.Kind) && series.Points != null && series.Points.Count > 0)
            {
                pointCapacity = Math.Min(int.MaxValue, pointCapacity + series.Points.Count);
            }
            if (series != null && IsAutomaticViewportSourceSeries(frame.PlotType, series.Kind) && series.HistogramBins != null && series.HistogramBins.Count > 0)
            {
                pointCapacity = Math.Min(int.MaxValue, pointCapacity + series.HistogramBins.Count * 2);
            }
        }

        List<double> allXValues = new List<double>(pointCapacity);
        List<double> allYValues = new List<double>(pointCapacity);
        bool preferNonZeroYAxisRange = MousePerformanceChartViewportPolicy.ShouldPreferNonZeroSingleAxisViewportRange(frame.PlotType);
        List<double> focusedXValues = preferNonZeroYAxisRange ? new List<double>(pointCapacity) : allXValues;
        List<double> focusedYValues = preferNonZeroYAxisRange ? new List<double>(pointCapacity) : allYValues;

        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series == null || !IsAutomaticViewportSourceSeries(frame.PlotType, series.Kind))
            {
                continue;
            }

            if (series.Kind == MousePerformanceChartSeriesKind.Histogram)
            {
                AppendHistogramViewportValues(series, allXValues, allYValues, focusedXValues, focusedYValues, preferNonZeroYAxisRange);
                continue;
            }
            if (series.Points == null || series.Points.Count == 0)
            {
                continue;
            }
            foreach (MousePerformanceChartPoint point in series.Points)
            {
                double effectiveX = GetEffectivePointX(point, series.XOffset);
                if (!IsFinite(effectiveX) || !IsFinite(point.Y))
                {
                    continue;
                }

                allXValues.Add(effectiveX);
                allYValues.Add(point.Y);
                if (preferNonZeroYAxisRange && Math.Abs(point.Y) > AutomaticViewportZeroFocusThreshold)
                {
                    focusedXValues.Add(effectiveX);
                    focusedYValues.Add(point.Y);
                }
            }
        }

        if (allXValues.Count == 0 || allYValues.Count == 0)
        {
            return fullFrameViewport;
        }
        if (preferNonZeroYAxisRange && (focusedXValues.Count == 0 || focusedYValues.Count == 0))
        {
            focusedXValues.AddRange(allXValues);
            focusedYValues.AddRange(allYValues);
        }

        MousePerformanceChartViewportPolicy.AutomaticViewportRobustRangeSettings horizontalSettings = MousePerformanceChartViewportPolicy.ResolveAutomaticViewportRobustRangeSettings(frame.PlotType, isHorizontalAxis: true);
        MousePerformanceChartViewportPolicy.AutomaticViewportRobustRangeSettings verticalSettings = MousePerformanceChartViewportPolicy.ResolveAutomaticViewportRobustRangeSettings(frame.PlotType, isHorizontalAxis: false);
        AxisRange xAxisRange = frame.PlotType == MousePerformancePlotType.XVsY
            ? ResolveRobustAxisRange(focusedXValues, horizontalSettings)
            : preferNonZeroYAxisRange ? ResolveFullAxisRange(focusedXValues) : ResolveFullAxisRange(allXValues);
        MousePerformanceChartViewportPolicy.AutomaticViewportDenseRangeSettings? denseVerticalSettings = MousePerformanceChartViewportPolicy.ResolveAutomaticViewportDenseRangeSettings(frame.PlotType, isHorizontalAxis: false);
        AxisRange yAxisRange = denseVerticalSettings.HasValue
            ? ResolveDensestCoverageAxisRange(focusedYValues, denseVerticalSettings.Value)
            : ResolveRobustAxisRange(focusedYValues, verticalSettings);

        double xMinimum = xAxisRange.Minimum;
        double xMaximum = xAxisRange.Maximum;
        double yMinimum = yAxisRange.Minimum;
        double yMaximum = yAxisRange.Maximum;
        MousePerformanceChartViewportPolicy.ExpandAxisRange(ref xMinimum, ref xMaximum, MousePerformanceChartViewportPolicy.ResolveHorizontalAxisPaddingRatio(frame.PlotType), MousePerformanceChartViewportPolicy.ResolveSinglePointAxisPaddingRatio(frame.PlotType, isHorizontalAxis: true));
        MousePerformanceChartViewportPolicy.ExpandYAxisRange(frame.PlotType, ref yMinimum, ref yMaximum);
        return new ChartViewport(xMinimum, xMaximum, yMinimum, yMaximum);
    }

    private static void AppendHistogramViewportValues(MousePerformanceChartSeries series, ICollection<double> allXValues, ICollection<double> allYValues, ICollection<double> focusedXValues, ICollection<double> focusedYValues, bool preferNonZeroYAxisRange)
    {
        if (series?.HistogramBins == null)
        {
            return;
        }

        foreach (MousePerformanceHistogramBin bin in series.HistogramBins)
        {
            double minimumX = bin.MinimumX + series.XOffset;
            double maximumX = bin.MaximumX + series.XOffset;
            MousePerformanceSeriesBuilder.ResolveHistogramBinXRange(bin, series.XOffset, series.GroupScale, out minimumX, out maximumX);
            double value = bin.Value;
            if (!IsFinite(minimumX) || !IsFinite(maximumX) || !IsFinite(value))
            {
                continue;
            }

            allXValues.Add(minimumX);
            allXValues.Add(maximumX);
            allYValues.Add(0.0);
            allYValues.Add(value);
            if (preferNonZeroYAxisRange && Math.Abs(value) > AutomaticViewportZeroFocusThreshold)
            {
                focusedXValues.Add(minimumX);
                focusedXValues.Add(maximumX);
                focusedYValues.Add(0.0);
                focusedYValues.Add(value);
            }
        }
    }

    private static bool IsAutomaticViewportSourceSeries(MousePerformancePlotType plotType, MousePerformanceChartSeriesKind seriesKind)
    {
        return MousePerformancePlotPresentationPolicy.Resolve(plotType).ShouldUseSeriesAsAutomaticViewportSource(seriesKind);
    }

    private static AxisRange ResolveFullAxisRange(List<double> values)
    {
        if (values == null || values.Count == 0)
        {
            return new AxisRange(-1.0, 1.0);
        }

        double minimum = double.MaxValue;
        double maximum = double.MinValue;
        foreach (double value in values)
        {
            if (value < minimum)
            {
                minimum = value;
            }
            if (value > maximum)
            {
                maximum = value;
            }
        }

        return minimum != double.MaxValue && maximum != double.MinValue
            ? new AxisRange(minimum, maximum)
            : new AxisRange(-1.0, 1.0);
    }

    private static AxisRange ResolveRobustAxisRange(List<double> values, MousePerformanceChartViewportPolicy.AutomaticViewportRobustRangeSettings settings)
    {
        AxisRange fullRange = ResolveFullAxisRange(values);
        if (values == null || values.Count < settings.MinimumRobustSampleCount)
        {
            return fullRange;
        }

        List<double> sortedValues = values.ToList();
        sortedValues.Sort();
        double? firstQuartile = ResolvePercentile(sortedValues, 0.25);
        double? thirdQuartile = ResolvePercentile(sortedValues, 0.75);
        if (!firstQuartile.HasValue || !thirdQuartile.HasValue)
        {
            return fullRange;
        }

        double interquartileRange = Math.Max(0.0, thirdQuartile.Value - firstQuartile.Value);
        double lowerOutlierFence = firstQuartile.Value - interquartileRange * settings.OutlierFenceMultiplier;
        double upperOutlierFence = thirdQuartile.Value + interquartileRange * settings.OutlierFenceMultiplier;
        int trimCountPerSide = Math.Max(settings.TrimCountFloorPerSide, (int)Math.Ceiling(sortedValues.Count * settings.TrimRatioPerSide));

        int firstInlierIndex = 0;
        while (firstInlierIndex < sortedValues.Count && sortedValues[firstInlierIndex] < lowerOutlierFence)
        {
            firstInlierIndex++;
        }

        int lastInlierIndex = sortedValues.Count - 1;
        while (lastInlierIndex >= 0 && sortedValues[lastInlierIndex] > upperOutlierFence)
        {
            lastInlierIndex--;
        }

        if (firstInlierIndex > lastInlierIndex)
        {
            return fullRange;
        }

        int rangeStartIndex = Math.Min(firstInlierIndex, trimCountPerSide);
        int rangeEndIndex = Math.Max(lastInlierIndex, sortedValues.Count - 1 - trimCountPerSide);
        if (rangeStartIndex >= sortedValues.Count || rangeEndIndex < 0 || rangeStartIndex > rangeEndIndex)
        {
            return fullRange;
        }

        return new AxisRange(sortedValues[rangeStartIndex], sortedValues[rangeEndIndex]);
    }

    private static AxisRange ResolveDensestCoverageAxisRange(List<double> values, MousePerformanceChartViewportPolicy.AutomaticViewportDenseRangeSettings settings)
    {
        AxisRange fullRange = ResolveFullAxisRange(values);
        if (values == null || values.Count < settings.MinimumSampleCount)
        {
            return fullRange;
        }

        List<double> sortedValues = values.ToList();
        sortedValues.Sort();
        int windowSampleCount = Math.Min(sortedValues.Count, Math.Max(2, (int)Math.Ceiling(sortedValues.Count * settings.CoverageRatio)));
        if (windowSampleCount >= sortedValues.Count)
        {
            return fullRange;
        }

        int densestWindowStartIndex = -1;
        double densestWindowSpan = double.MaxValue;
        int lastWindowStartIndex = sortedValues.Count - windowSampleCount;
        for (int windowStartIndex = 0; windowStartIndex <= lastWindowStartIndex; windowStartIndex++)
        {
            int windowEndIndex = windowStartIndex + windowSampleCount - 1;
            double windowSpan = sortedValues[windowEndIndex] - sortedValues[windowStartIndex];
            if (densestWindowStartIndex < 0 || windowSpan < densestWindowSpan)
            {
                densestWindowStartIndex = windowStartIndex;
                densestWindowSpan = windowSpan;
            }
        }

        return densestWindowStartIndex >= 0
            ? new AxisRange(sortedValues[densestWindowStartIndex], sortedValues[densestWindowStartIndex + windowSampleCount - 1])
            : fullRange;
    }

    private void ClearAutomaticViewportCache()
    {
        _cachedAutomaticViewportFrame = null;
        _cachedAutomaticViewport = new ChartViewport(0.0, 0.0, 0.0, 0.0);
        _hasCachedAutomaticViewport = false;
    }

    private void ClearViewportState()
    {
        _viewXMinimum = 0.0;
        _viewXMaximum = 0.0;
        _viewYMinimum = 0.0;
        _viewYMaximum = 0.0;
        _hasCustomViewport = false;
        ClearVisiblePointCache();
        ClearGapMarkerCache();
    }

    private void PrepareVisiblePointCache(ChartViewport viewport)
    {
        if (!_hasCachedVisiblePointViewport || !AreSameViewport(_cachedVisiblePointViewport, viewport))
        {
            ClearVisiblePointCache();
            _cachedVisiblePointViewport = viewport;
            _hasCachedVisiblePointViewport = true;
        }
    }

    private void ClearVisiblePointCache(bool clearMonotonicity = false)
    {
        _visiblePointCacheEntries.Clear();
        _hasCachedVisiblePointViewport = false;
        if (clearMonotonicity)
        {
            _rawCaptureMonotonicityCache.Clear();
        }
    }

    private void ClearGapMarkerCache()
    {
        _cachedGapMarkerEntry = null;
    }

    private void ClearPlotBitmapCache()
    {
        _cachedPlotBitmapEntry = null;
    }

    private static bool AreSameViewport(ChartViewport left, ChartViewport right)
    {
        if (AreClose(left.XMinimum, right.XMinimum) && AreClose(left.XMaximum, right.XMaximum) && AreClose(left.YMinimum, right.YMinimum))
        {
            return AreClose(left.YMaximum, right.YMaximum);
        }
        return false;
    }

    private FormattedText CreateText(string content, double fontSize, Brush brush, bool strong = false, double maxTextWidth = 0.0, double maxTextHeight = 0.0, TextAlignment textAlignment = TextAlignment.Left)
    {
        double pixelsPerDip = NormalizeTextCacheMetric(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        TextLayoutCacheKey? textLayoutCacheKey = null;
        int brushCacheRole = ResolveTextBrushCacheRole(brush);
        if (brushCacheRole != 0 && !string.IsNullOrWhiteSpace(content))
        {
            textLayoutCacheKey = new TextLayoutCacheKey(content, NormalizeTextCacheMetric(fontSize), strong, brushCacheRole, NormalizeTextCacheMetric(maxTextWidth), NormalizeTextCacheMetric(maxTextHeight), textAlignment, pixelsPerDip);
            FormattedText value = null;
            if (_textLayoutCache.TryGetValue(textLayoutCacheKey.Value, out value))
            {
                return value;
            }
        }
        FormattedText formattedText = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(ResolveFontFamily(strong ? "Font.DisplaySans" : "Font.Body", new FontFamily("Segoe UI")), FontStyles.Normal, strong ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), fontSize, brush, pixelsPerDip);
        if (maxTextWidth > 0.0)
        {
            formattedText.MaxTextWidth = maxTextWidth;
            formattedText.Trimming = TextTrimming.CharacterEllipsis;
        }
        if (maxTextHeight > 0.0)
        {
            formattedText.MaxTextHeight = maxTextHeight;
            formattedText.Trimming = TextTrimming.CharacterEllipsis;
        }
        formattedText.TextAlignment = textAlignment;
        if (textLayoutCacheKey.HasValue)
        {
            if (_textLayoutCache.Count >= 512)
            {
                _textLayoutCache.Clear();
            }
            _textLayoutCache[textLayoutCacheKey.Value] = formattedText;
        }
        return formattedText;
    }

    private void ApplyThemeResources()
    {
        Color strongTextColor = ResolveColor("TextStrongBrush", Color.FromRgb(232, 232, 232));
        Color mutedTextColor = BlendColors(ResolveColor("TextMutedBrush", Color.FromRgb(155, 155, 161)), ResolveColor("TextStrongBrush", Color.FromRgb(245, 245, 245)), 0.18);
        _backgroundBrush = ResolveBrush("WindowBackgroundBrush", Color.FromRgb(8, 8, 10));
        _panelBrush = ResolveBrush("GlassShellBackgroundBrush", Color.FromRgb(16, 16, 18));
        _axisPen = CreatePen(ResolveColor("GlassShellBorderBrush", Color.FromRgb(82, 82, 87)), 1.0);
        _gridPen = CreatePen(ResolveColor("HairlineBrush", Color.FromRgb(52, 52, 56)), 0.8);
        _minorGridPen = CreatePen(ApplyOpacity(ResolveColor("HairlineBrush", Color.FromRgb(52, 52, 56)), 0.38), 0.55);
        _primaryBrush = MousePerformanceChartColorPalette.CreateBrush(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Primary, 0.66);
        _primaryPen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Primary, 0.58, 1.05);
        _primaryContinuousEstimatePen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Primary, ContinuousEstimateLineOpacityFactor, ContinuousEstimateLineThickness);
        _primaryStemPen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Primary, 0.16, 0.7);
        _secondaryBrush = MousePerformanceChartColorPalette.CreateBrush(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Secondary, 0.66);
        _secondaryPen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Secondary, 0.58, 1.05);
        _secondaryContinuousEstimatePen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Secondary, ContinuousEstimateLineOpacityFactor, ContinuousEstimateLineThickness);
        _secondaryStemPen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Secondary, 0.16, 0.7);
        _accentBrush = MousePerformanceChartColorPalette.CreateBrush(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Accent, 0.66);
        _accentPen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Accent, 0.58, 1.05);
        _accentContinuousEstimatePen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Accent, ContinuousEstimateLineOpacityFactor, ContinuousEstimateLineThickness);
        _accentStemPen = MousePerformanceChartColorPalette.CreatePen(MousePerformanceChartDatasetSlot.Baseline, MousePerformanceChartSeriesPalette.Accent, 0.16, 0.7);
        Color legacyXAxisColor = ResolveColor("ChartAccentColor", Color.FromRgb(182, 47, byte.MaxValue));
        Color legacyYAxisColor = Color.FromRgb(byte.MaxValue, 95, 95);
        _legacySingleSessionXBrush = CreateBrush(ApplyOpacity(legacyXAxisColor, 0.66));
        _legacySingleSessionXPen = CreatePen(ApplyOpacity(mutedTextColor, 0.58), 1.05);
        _legacySingleSessionXContinuousEstimatePen = CreatePen(ApplyOpacity(legacyXAxisColor, ContinuousEstimateLineOpacityFactor), ContinuousEstimateLineThickness);
        _legacySingleSessionXStemPen = CreatePen(ApplyOpacity(mutedTextColor, 0.16), 0.7);
        _legacySingleSessionYBrush = CreateBrush(ApplyOpacity(legacyYAxisColor, 0.66));
        _legacySingleSessionYPen = CreatePen(ApplyOpacity(mutedTextColor, 0.58), 1.05);
        _legacySingleSessionYContinuousEstimatePen = CreatePen(ApplyOpacity(legacyYAxisColor, ContinuousEstimateLineOpacityFactor), ContinuousEstimateLineThickness);
        _legacySingleSessionYStemPen = CreatePen(ApplyOpacity(mutedTextColor, 0.16), 0.7);
        _neutralBrush = CreateBrush(ApplyOpacity(strongTextColor, 0.66));
        _neutralPen = CreatePen(ApplyOpacity(mutedTextColor, 0.58), 1.05);
        _neutralStemPen = CreatePen(ApplyOpacity(mutedTextColor, 0.16), 0.7);
        _gapBrush = CreateBrush(ApplyOpacity(mutedTextColor, 0.08));
        _gapPen = CreatePen(ApplyOpacity(mutedTextColor, 0.2), 0.9);
        _labelBrush = ResolveBrush("TextMutedBrush", Color.FromRgb(155, 155, 161));
        _minorAxisLabelBrush = CreateBrush(ApplyOpacity(ResolveBrushColor(_labelBrush, Color.FromRgb(155, 155, 161)), MinorAxisLabelOpacityFactor));
        _strongLabelBrush = ResolveBrush("TextStrongBrush", Color.FromRgb(245, 245, 245));
        _textLayoutCache.Clear();
    }

    private Brush ResolveSeriesBrush(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series)
    {
        if (series == null)
        {
            return _neutralBrush;
        }
        if (UsesLegacySingleSessionPalette(frame))
        {
            return ShouldUseLegacySingleSessionYFamily(frame.PlotType, series) ? _legacySingleSessionYBrush : _legacySingleSessionXBrush;
        }
        if (series.DatasetSlot == MousePerformanceChartDatasetSlot.Baseline)
        {
            return series.Palette switch
            {
                MousePerformanceChartSeriesPalette.Primary => _primaryBrush,
                MousePerformanceChartSeriesPalette.Secondary => _secondaryBrush,
                MousePerformanceChartSeriesPalette.Accent => _accentBrush,
                _ => _neutralBrush,
            };
        }
        return MousePerformanceChartColorPalette.CreateBrush(series.DatasetSlot, series.Palette, 0.66);
    }

    private Pen ResolveSeriesPen(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series, MousePerformanceChartSeriesKind seriesKind)
    {
        if (series == null)
        {
            return (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _neutralStemPen : _neutralPen;
        }
        if (UsesLegacySingleSessionPalette(frame))
        {
            if (ShouldUseLegacySingleSessionYFamily(frame.PlotType, series))
            {
                if (IsContinuousEstimateMainLine(frame, series, seriesKind))
                {
                    return _legacySingleSessionYContinuousEstimatePen;
                }
                return (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _legacySingleSessionYStemPen : _legacySingleSessionYPen;
            }
            if (IsContinuousEstimateMainLine(frame, series, seriesKind))
            {
                return _legacySingleSessionXContinuousEstimatePen;
            }
            return (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _legacySingleSessionXStemPen : _legacySingleSessionXPen;
        }
        if (series.DatasetSlot == MousePerformanceChartDatasetSlot.Baseline)
        {
            if (IsContinuousEstimateMainLine(frame, series, seriesKind))
            {
                return series.Palette switch
                {
                    MousePerformanceChartSeriesPalette.Primary => _primaryContinuousEstimatePen,
                    MousePerformanceChartSeriesPalette.Secondary => _secondaryContinuousEstimatePen,
                    MousePerformanceChartSeriesPalette.Accent => _accentContinuousEstimatePen,
                    _ => _neutralPen,
                };
            }
            return series.Palette switch
            {
                MousePerformanceChartSeriesPalette.Primary => (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _primaryStemPen : _primaryPen,
                MousePerformanceChartSeriesPalette.Secondary => (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _secondaryStemPen : _secondaryPen,
                MousePerformanceChartSeriesPalette.Accent => (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _accentStemPen : _accentPen,
                _ => (seriesKind == MousePerformanceChartSeriesKind.Stem) ? _neutralStemPen : _neutralPen,
            };
        }
        bool isContinuousEstimateMainLine = IsContinuousEstimateMainLine(frame, series, seriesKind);
        double opacity = seriesKind == MousePerformanceChartSeriesKind.Stem ? 0.16 : isContinuousEstimateMainLine ? ContinuousEstimateLineOpacityFactor : 0.58;
        double thickness = seriesKind == MousePerformanceChartSeriesKind.Stem ? 0.7 : isContinuousEstimateMainLine ? ContinuousEstimateLineThickness : 1.05;
        return MousePerformanceChartColorPalette.CreatePen(series.DatasetSlot, series.Palette, opacity, thickness);
    }

    private static float ResolveLineThicknessPixels(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series)
    {
        return (float)(IsContinuousEstimateMainLine(frame, series, MousePerformanceChartSeriesKind.Line) ? ContinuousEstimateLineThickness : LineThickness);
    }

    private static bool IsContinuousEstimateMainLine(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeries series, MousePerformanceChartSeriesKind seriesKind)
    {
        return frame != null
            && series != null
            && seriesKind == MousePerformanceChartSeriesKind.Line
            && series.Kind == MousePerformanceChartSeriesKind.Line
            && MousePerformancePlotPresentationPolicy.Resolve(frame.PlotType).UsesContinuousEstimateLine;
    }

    private static bool UsesLegacySingleSessionPalette(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null)
        {
            return false;
        }
        return !frame.HasComparisonDatasets;
    }

    private static bool ShouldUseLegacySingleSessionYFamily(MousePerformancePlotType plotType, MousePerformanceChartSeries series)
    {
        if (plotType == MousePerformancePlotType.XYCountVsTime || plotType == MousePerformancePlotType.XYVelocityVsTime || plotType == MousePerformancePlotType.XYSumVsTime)
        {
            return series != null && series.Palette == MousePerformanceChartSeriesPalette.Secondary;
        }
        return false;
    }

    private static string ResolveText(string content)
    {
        return content ?? string.Empty;
    }

    private static bool IsScreenYAxisPositiveDown(MousePerformancePlotType plotType)
    {
        return plotType == MousePerformancePlotType.XVsY;
    }

    private int ResolveTextBrushCacheRole(Brush brush)
    {
        if (ReferenceEquals(brush, _strongLabelBrush))
        {
            return 1;
        }
        if (ReferenceEquals(brush, _labelBrush))
        {
            return 2;
        }
        if (ReferenceEquals(brush, _minorAxisLabelBrush))
        {
            return 3;
        }
        return 0;
    }

    private static double NormalizeTextCacheMetric(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }
        return Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    private static string FormatAxisValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "--";
        }
        if (Math.Abs(value) <= AxisZeroTolerance)
        {
            return "0";
        }
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatHistogramBarValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return string.Empty;
        }
        if (Math.Abs(value) >= 10.0)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static FontFamily ResolveFontFamily(string resourceKey, FontFamily fallback)
    {
        object resource = null;
        if (System.Windows.Application.Current != null)
        {
            resource = System.Windows.Application.Current.TryFindResource(resourceKey);
        }
        if (resource is FontFamily result)
        {
            return result;
        }
        return fallback;
    }

    private static Brush ResolveBrush(string resourceKey, Color fallback)
    {
        object resource = null;
        if (System.Windows.Application.Current != null)
        {
            resource = System.Windows.Application.Current.TryFindResource(resourceKey);
        }
        if (resource is Brush source)
        {
            return FreezeBrush(source);
        }
        return CreateBrush(fallback);
    }

    private static Color ResolveColor(string resourceKey, Color fallback)
    {
        object resource = null;
        if (System.Windows.Application.Current != null)
        {
            resource = System.Windows.Application.Current.TryFindResource(resourceKey);
        }
        if (resource is Color colorValue)
        {
            return colorValue;
        }
        if (!(resource is SolidColorBrush { Color: var color }))
        {
            return fallback;
        }
        return color;
    }

    private static string ResolveStringResource(string resourceKey, string fallback)
    {
        object resource = null;
        if (System.Windows.Application.Current != null)
        {
            resource = System.Windows.Application.Current.TryFindResource(resourceKey);
        }
        if (resource is string)
        {
            return (string)resource;
        }
        return fallback ?? string.Empty;
    }

    private static string FormatStringResource(string resourceKey, string fallback, params object[] args)
    {
        string format = ResolveStringResource(resourceKey, fallback);
        if (args == null || args.Length == 0)
        {
            return format;
        }
        return string.Format(CultureInfo.CurrentCulture, format, args);
    }

    private static Brush CreateBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    private static Brush FreezeBrush(Brush source)
    {
        Brush brush = source.CloneCurrentValue();
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    private static Color ApplyOpacity(Color color, double opacityFactor)
    {
        double clampedOpacityFactor = Math.Max(0.0, Math.Min(1.0, opacityFactor));
        byte alpha = (byte)Math.Round(color.A * clampedOpacityFactor);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color BlendColors(Color baseColor, Color targetColor, double blendFactor)
    {
        double clampedBlendFactor = Math.Max(0.0, Math.Min(1.0, blendFactor));
        double baseWeight = 1.0 - clampedBlendFactor;
        byte red = (byte)Math.Round(baseColor.R * baseWeight + targetColor.R * clampedBlendFactor);
        byte green = (byte)Math.Round(baseColor.G * baseWeight + targetColor.G * clampedBlendFactor);
        byte blue = (byte)Math.Round(baseColor.B * baseWeight + targetColor.B * clampedBlendFactor);
        return Color.FromArgb(baseColor.A, red, green, blue);
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        Pen pen = new Pen(CreateBrush(color), thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        return pen;
    }
}




