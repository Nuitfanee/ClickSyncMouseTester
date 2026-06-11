using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Controls;

public class AngleCalibrationTraceControl : FrameworkElement
{
    private sealed class CachedTraceStrokeGeometry
    {
        public StreamGeometry Geometry { get; }

        public Rect DataBounds { get; }

        public bool HasDataBounds { get; }

        public CachedTraceStrokeGeometry(StreamGeometry geometry, Rect dataBounds, bool hasDataBounds)
        {
            Geometry = geometry;
            DataBounds = dataBounds;
            HasDataBounds = hasDataBounds;
        }
    }

    private sealed class TraceGeometryEntry
    {
        public StreamGeometry Geometry { get; }

        public bool IsCurrent { get; }

        public TraceGeometryEntry(StreamGeometry geometry, bool isCurrent)
        {
            Geometry = geometry;
            IsCurrent = isCurrent;
        }
    }

    private sealed class TraceGeometryFrame
    {
        public IReadOnlyList<TraceGeometryEntry> Strokes { get; }

        public Rect DataBounds { get; }

        public bool HasDataBounds { get; }

        public TraceGeometryFrame(IReadOnlyList<TraceGeometryEntry> strokes, Rect dataBounds, bool hasDataBounds)
        {
            Strokes = strokes;
            DataBounds = dataBounds;
            HasDataBounds = hasDataBounds;
        }
    }

    private const double ViewPadding = 26.0;

    private const double MinimumScale = 0.08;

    private const double MaximumScale = 1.8;

    private const double HistoryStrokeOpacity = 0.22;

    private const double CurrentStrokeOpacity = 0.55;

    private const double StrokeThickness = 1.65;

    private const double ViewCenterTolerance = 0.15;

    private const double ViewScaleTolerance = 0.002;

    public static readonly DependencyProperty RenderFrameProperty = DependencyProperty.Register("RenderFrame", typeof(AngleCalibrationRenderFrame), typeof(AngleCalibrationTraceControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, new PropertyChangedCallback(OnRenderFrameChanged)));

    private readonly Dictionary<AngleCalibrationTraceStroke, CachedTraceStrokeGeometry> _strokeGeometryCache;

    private Pen _historyPen;

    private Pen _currentPen;

    private Color _historyPenColor;

    private Color _currentPenColor;

    private double _penThicknessKey;

    private AngleCalibrationRenderFrame _cachedGeometryFrameSource;

    private TraceGeometryFrame _cachedGeometryFrame;

    private RectangleGeometry _clipGeometry;

    private double _clipWidth;

    private double _clipHeight;

    private double _targetCenterX;

    private double _targetCenterY;

    private double _targetScale;

    private double _currentCenterX;

    private double _currentCenterY;

    private double _currentScale;

    private bool _hasViewState;

    private bool _isRenderingSubscribed;

    private bool _isThemeSubscribed;

    public AngleCalibrationRenderFrame RenderFrame
    {
        get
        {
            return (AngleCalibrationRenderFrame)GetValue(RenderFrameProperty);
        }
        set
        {
            SetValue(RenderFrameProperty, value);
        }
    }

    public AngleCalibrationTraceControl()
    {
        _strokeGeometryCache = new Dictionary<AngleCalibrationTraceStroke, CachedTraceStrokeGeometry>();
        _targetScale = 1.0;
        _currentScale = 1.0;
        _penThicknessKey = double.NaN;
        base.SnapsToDevicePixels = true;
        base.UseLayoutRounding = true;
        ApplyThemeResources();
        base.Loaded += OnLoaded;
        base.Unloaded += OnUnloaded;
    }

    private static void OnRenderFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((AngleCalibrationTraceControl)d).HandleRenderFrameChanged();
    }

    private void HandleRenderFrameChanged()
    {
        RecalculateTargetView();
        if (!_hasViewState)
        {
            _currentCenterX = _targetCenterX;
            _currentCenterY = _targetCenterY;
            _currentScale = _targetScale;
            _hasViewState = true;
        }
        else
        {
            SnapCurrentViewIfClose();
        }
        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isThemeSubscribed)
        {
            ThemeManager.Instance.ThemeChanged += OnThemeChanged;
            _isThemeSubscribed = true;
        }
        ApplyThemeResources();
        RecalculateTargetView();
        UpdateRenderingSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isThemeSubscribed)
        {
            ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
            _isThemeSubscribed = false;
        }
        UpdateRenderingSubscription(forceDetach: true);
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        ApplyThemeResources();
        InvalidateVisual();
    }

    private void UpdateRenderingSubscription(bool forceDetach = false)
    {
        if (forceDetach || !base.IsLoaded || !IsViewAnimationActive())
        {
            if (_isRenderingSubscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRenderingSubscribed = false;
            }
        }
        else if (!_isRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _isRenderingSubscribed = true;
        }
    }

    private void OnRendering(object sender, EventArgs e)
    {
        if (!_hasViewState)
        {
            UpdateRenderingSubscription(forceDetach: true);
            return;
        }
        double interpolationFactor = RenderFrame != null && RenderFrame.IsLocked ? 0.18 : 0.24;
        _currentCenterX += (_targetCenterX - _currentCenterX) * interpolationFactor;
        _currentCenterY += (_targetCenterY - _currentCenterY) * interpolationFactor;
        _currentScale += (_targetScale - _currentScale) * interpolationFactor;
        SnapCurrentViewIfClose();
        InvalidateVisual();
        UpdateRenderingSubscription();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _clipGeometry = null;
        RecalculateTargetView();
        if (!_hasViewState && RenderFrame != null)
        {
            _currentCenterX = _targetCenterX;
            _currentCenterY = _targetCenterY;
            _currentScale = _targetScale;
            _hasViewState = true;
        }
        else
        {
            SnapCurrentViewIfClose();
        }
        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        AngleCalibrationRenderFrame renderFrame = RenderFrame;
        if (renderFrame == null || base.ActualWidth <= 0.0 || base.ActualHeight <= 0.0)
        {
            return;
        }
        if (!_hasViewState)
        {
            RecalculateTargetView();
            _currentCenterX = _targetCenterX;
            _currentCenterY = _targetCenterY;
            _currentScale = _targetScale;
            _hasViewState = true;
        }
        TraceGeometryFrame geometryFrame = EnsureGeometryFrame(renderFrame);
        if (geometryFrame == null || geometryFrame.Strokes.Count == 0)
        {
            return;
        }
        EnsurePensForScale(_currentScale);
        double offsetX = base.ActualWidth / 2.0 - _currentCenterX * _currentScale;
        double offsetY = base.ActualHeight / 2.0 - _currentCenterY * _currentScale;
        drawingContext.PushClip(GetClipGeometry());
        drawingContext.PushTransform(new MatrixTransform(new Matrix(_currentScale, 0.0, 0.0, _currentScale, offsetX, offsetY)));
        foreach (TraceGeometryEntry traceStroke in geometryFrame.Strokes)
        {
            DrawStroke(drawingContext, traceStroke);
        }
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void DrawStroke(DrawingContext drawingContext, TraceGeometryEntry stroke)
    {
        if (stroke == null || stroke.Geometry == null)
        {
            return;
        }

        Pen pen = stroke.IsCurrent ? _currentPen : _historyPen;
        drawingContext.PushOpacity(stroke.IsCurrent ? CurrentStrokeOpacity : HistoryStrokeOpacity);
        drawingContext.DrawGeometry(null, pen, stroke.Geometry);
        drawingContext.Pop();
    }

    private TraceGeometryFrame EnsureGeometryFrame(AngleCalibrationRenderFrame renderFrame)
    {
        if (renderFrame == null)
        {
            _cachedGeometryFrameSource = null;
            _cachedGeometryFrame = null;
            _strokeGeometryCache.Clear();
            return null;
        }
        if (ReferenceEquals(_cachedGeometryFrameSource, renderFrame))
        {
            return _cachedGeometryFrame;
        }

        TraceGeometryFrame geometryFrame = CreateGeometryFrame(renderFrame);
        _cachedGeometryFrameSource = renderFrame;
        _cachedGeometryFrame = geometryFrame;
        return geometryFrame;
    }

    private TraceGeometryFrame CreateGeometryFrame(AngleCalibrationRenderFrame renderFrame)
    {
        List<TraceGeometryEntry> strokeEntries = new List<TraceGeometryEntry>();
        HashSet<AngleCalibrationTraceStroke> liveStrokes = new HashSet<AngleCalibrationTraceStroke>();
        Rect dataBounds = Rect.Empty;
        bool hasDataBounds = false;
        if (renderFrame.TraceStrokes != null)
        {
            foreach (AngleCalibrationTraceStroke traceStroke in renderFrame.TraceStrokes)
            {
                if (traceStroke == null)
                {
                    continue;
                }
                liveStrokes.Add(traceStroke);
                CachedTraceStrokeGeometry cachedStrokeGeometry = GetOrCreateStrokeGeometry(traceStroke);
                if (cachedStrokeGeometry == null)
                {
                    continue;
                }
                if (cachedStrokeGeometry.HasDataBounds)
                {
                    IncludeBounds(ref dataBounds, ref hasDataBounds, cachedStrokeGeometry.DataBounds);
                }
                if (cachedStrokeGeometry.Geometry != null)
                {
                    strokeEntries.Add(new TraceGeometryEntry(cachedStrokeGeometry.Geometry, traceStroke.IsCurrent));
                }
            }
        }
        RemoveStaleStrokeGeometry(liveStrokes);
        return new TraceGeometryFrame(strokeEntries, dataBounds, hasDataBounds);
    }

    private CachedTraceStrokeGeometry GetOrCreateStrokeGeometry(AngleCalibrationTraceStroke traceStroke)
    {
        if (_strokeGeometryCache.TryGetValue(traceStroke, out CachedTraceStrokeGeometry cachedGeometry))
        {
            return cachedGeometry;
        }
        cachedGeometry = CreateStrokeGeometry(traceStroke);
        _strokeGeometryCache[traceStroke] = cachedGeometry;
        return cachedGeometry;
    }

    private static CachedTraceStrokeGeometry CreateStrokeGeometry(AngleCalibrationTraceStroke traceStroke)
    {
        if (traceStroke == null || traceStroke.Points == null || traceStroke.Points.Count == 0)
        {
            return null;
        }

        Rect dataBounds = Rect.Empty;
        bool hasDataBounds = false;
        foreach (AngleCalibrationTracePoint point in traceStroke.Points)
        {
            IncludePoint(ref dataBounds, ref hasDataBounds, point);
        }

        StreamGeometry strokeGeometry = null;
        if (traceStroke.Points.Count >= 2)
        {
            strokeGeometry = new StreamGeometry();
            using (StreamGeometryContext geometryContext = strokeGeometry.Open())
            {
                AngleCalibrationTracePoint startPoint = traceStroke.Points[0];
                geometryContext.BeginFigure(new Point(startPoint.X, startPoint.Y), isFilled: false, isClosed: false);
                for (int pointIndex = 1; pointIndex < traceStroke.Points.Count; pointIndex++)
                {
                    AngleCalibrationTracePoint point = traceStroke.Points[pointIndex];
                    geometryContext.LineTo(new Point(point.X, point.Y), isStroked: true, isSmoothJoin: true);
                }
            }
            if (strokeGeometry.CanFreeze)
            {
                strokeGeometry.Freeze();
            }
        }

        return new CachedTraceStrokeGeometry(strokeGeometry, dataBounds, hasDataBounds);
    }

    private void RemoveStaleStrokeGeometry(HashSet<AngleCalibrationTraceStroke> liveStrokes)
    {
        List<AngleCalibrationTraceStroke> staleStrokes = null;
        foreach (AngleCalibrationTraceStroke cachedStroke in _strokeGeometryCache.Keys)
        {
            if (!liveStrokes.Contains(cachedStroke))
            {
                if (staleStrokes == null)
                {
                    staleStrokes = new List<AngleCalibrationTraceStroke>();
                }
                staleStrokes.Add(cachedStroke);
            }
        }
        if (staleStrokes == null)
        {
            return;
        }
        foreach (AngleCalibrationTraceStroke staleStroke in staleStrokes)
        {
            _strokeGeometryCache.Remove(staleStroke);
        }
    }

    private static void IncludePoint(ref Rect dataBounds, ref bool hasDataBounds, AngleCalibrationTracePoint point)
    {
        if (point == null)
        {
            return;
        }
        IncludePoint(ref dataBounds, ref hasDataBounds, point.X, point.Y);
    }

    private static void IncludePoint(ref Rect dataBounds, ref bool hasDataBounds, double x, double y)
    {
        if (!hasDataBounds)
        {
            dataBounds = new Rect(new Point(x, y), new Size(0.0, 0.0));
            hasDataBounds = true;
            return;
        }
        dataBounds.Union(new Point(x, y));
    }

    private static void IncludeBounds(ref Rect dataBounds, ref bool hasDataBounds, Rect bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }
        if (!hasDataBounds)
        {
            dataBounds = bounds;
            hasDataBounds = true;
            return;
        }
        dataBounds.Union(bounds);
    }

    private void RecalculateTargetView()
    {
        double minX = -200.0;
        double maxX = 200.0;
        double minY = -80.0;
        double maxY = 80.0;
        TraceGeometryFrame geometryFrame = EnsureGeometryFrame(RenderFrame);
        if (geometryFrame != null && geometryFrame.HasDataBounds)
        {
            minX = geometryFrame.DataBounds.Left;
            maxX = geometryFrame.DataBounds.Right;
            minY = geometryFrame.DataBounds.Top;
            maxY = geometryFrame.DataBounds.Bottom;
        }

        double contentWidth = Math.Max(1.0, maxX - minX);
        double contentHeight = Math.Max(1.0, maxY - minY);
        double availableWidth = Math.Max(32.0, base.ActualWidth - ViewPadding * 2.0);
        double availableHeight = Math.Max(32.0, base.ActualHeight - ViewPadding * 2.0);
        double scaleToFit = Math.Min(availableWidth / contentWidth, availableHeight / contentHeight);
        _targetScale = Math.Max(MinimumScale, Math.Min(MaximumScale, scaleToFit));
        _targetCenterX = (minX + maxX) / 2.0;
        _targetCenterY = (minY + maxY) / 2.0;
    }

    private bool IsViewAnimationActive()
    {
        return _hasViewState && !IsCurrentViewCloseToTarget();
    }

    private bool IsCurrentViewCloseToTarget()
    {
        return Math.Abs(_targetCenterX - _currentCenterX) < ViewCenterTolerance
            && Math.Abs(_targetCenterY - _currentCenterY) < ViewCenterTolerance
            && Math.Abs(_targetScale - _currentScale) < ViewScaleTolerance;
    }

    private void SnapCurrentViewIfClose()
    {
        if (!IsCurrentViewCloseToTarget())
        {
            return;
        }
        _currentCenterX = _targetCenterX;
        _currentCenterY = _targetCenterY;
        _currentScale = _targetScale;
    }

    private RectangleGeometry GetClipGeometry()
    {
        double actualWidth = base.ActualWidth;
        double actualHeight = base.ActualHeight;
        if (_clipGeometry != null && Math.Abs(_clipWidth - actualWidth) < 0.001 && Math.Abs(_clipHeight - actualHeight) < 0.001)
        {
            return _clipGeometry;
        }
        _clipWidth = actualWidth;
        _clipHeight = actualHeight;
        _clipGeometry = new RectangleGeometry(new Rect(0.0, 0.0, actualWidth, actualHeight));
        if (_clipGeometry.CanFreeze)
        {
            _clipGeometry.Freeze();
        }
        return _clipGeometry;
    }

    private void ApplyThemeResources()
    {
        _historyPenColor = ResolveColor("TextMutedColor", Color.FromRgb(152, 152, 152));
        _currentPenColor = ResolveColor("TextStrongColor", Color.FromRgb(200, 200, 200));
        _penThicknessKey = double.NaN;
        EnsurePensForScale(_currentScale);
    }

    private void EnsurePensForScale(double scale)
    {
        double safeScale = Math.Max(0.001, Math.Abs(scale));
        double penThickness = StrokeThickness / safeScale;
        double penThicknessKey = Math.Round(penThickness * 64.0) / 64.0;
        if (_historyPen != null && _currentPen != null && Math.Abs(_penThicknessKey - penThicknessKey) < 0.0001)
        {
            return;
        }

        _penThicknessKey = penThicknessKey;
        _historyPen = CreatePen(_historyPenColor, penThicknessKey);
        _currentPen = CreatePen(_currentPenColor, penThicknessKey);
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        Pen pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (pen.Brush.CanFreeze)
        {
            pen.Brush.Freeze();
        }
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        return pen;
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
}
