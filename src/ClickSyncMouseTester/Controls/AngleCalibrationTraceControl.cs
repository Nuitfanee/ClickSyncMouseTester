using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using System;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Controls;

public class AngleCalibrationTraceControl : FrameworkElement
{
    private const double ViewPadding = 26.0;

    private const double MinimumScale = 0.08;

    private const double MaximumScale = 1.8;

    private const double HistoryStrokeOpacity = 0.22;

    private const double CurrentStrokeOpacity = 0.55;

    private const double StrokeThickness = 1.65;

    public static readonly DependencyProperty RenderFrameProperty = DependencyProperty.Register("RenderFrame", typeof(AngleCalibrationRenderFrame), typeof(AngleCalibrationTraceControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, new PropertyChangedCallback(OnRenderFrameChanged)));

    private Pen _historyPen;

    private Pen _currentPen;

    private double _targetCenterX;

    private double _targetCenterY;

    private double _targetScale;

    private double _currentCenterX;

    private double _currentCenterY;

    private double _currentScale;

    private bool _hasViewState;

    private int _pendingAnimationFrames;

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
        _targetScale = 1.0;
        _currentScale = 1.0;
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
        _pendingAnimationFrames = 10;
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
        if (forceDetach || !base.IsLoaded || (_pendingAnimationFrames <= 0 && (RenderFrame == null || !RenderFrame.IsLocked)))
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
        if (Math.Abs(_targetCenterX - _currentCenterX) < 0.15 && Math.Abs(_targetCenterY - _currentCenterY) < 0.15 && Math.Abs(_targetScale - _currentScale) < 0.002)
        {
            _currentCenterX = _targetCenterX;
            _currentCenterY = _targetCenterY;
            _currentScale = _targetScale;
            _pendingAnimationFrames = Math.Max(0, _pendingAnimationFrames - 1);
        }
        else
        {
            _pendingAnimationFrames = Math.Max(_pendingAnimationFrames, 2);
        }
        InvalidateVisual();
        UpdateRenderingSubscription();
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
        drawingContext.PushClip(new RectangleGeometry(new Rect(0.0, 0.0, base.ActualWidth, base.ActualHeight)));
        foreach (AngleCalibrationTraceStroke traceStroke in renderFrame.TraceStrokes)
        {
            DrawStroke(drawingContext, traceStroke);
        }
        drawingContext.Pop();
    }

    private void DrawStroke(DrawingContext drawingContext, AngleCalibrationTraceStroke stroke)
    {
        if (stroke == null || stroke.Points == null || stroke.Points.Count < 2)
        {
            return;
        }

        StreamGeometry strokeGeometry = new StreamGeometry();
        using (StreamGeometryContext geometryContext = strokeGeometry.Open())
        {
            Point startPoint = ToScreenPoint(stroke.Points[0]);
            geometryContext.BeginFigure(startPoint, isFilled: false, isClosed: false);
            for (int pointIndex = 1; pointIndex < stroke.Points.Count; pointIndex++)
            {
                geometryContext.LineTo(ToScreenPoint(stroke.Points[pointIndex]), isStroked: true, isSmoothJoin: true);
            }
        }

        if (strokeGeometry.CanFreeze)
        {
            strokeGeometry.Freeze();
        }
        Pen pen = stroke.IsCurrent ? _currentPen : _historyPen;
        drawingContext.PushOpacity(stroke.IsCurrent ? CurrentStrokeOpacity : HistoryStrokeOpacity);
        drawingContext.DrawGeometry(null, pen, strokeGeometry);
        drawingContext.Pop();
    }

    private Point ToScreenPoint(AngleCalibrationTracePoint point)
    {
        double screenX = (point.X - _currentCenterX) * _currentScale + base.ActualWidth / 2.0;
        double screenY = (point.Y - _currentCenterY) * _currentScale + base.ActualHeight / 2.0;
        return new Point(screenX, screenY);
    }

    private void RecalculateTargetView()
    {
        double minX = -200.0;
        double maxX = 200.0;
        double minY = -80.0;
        double maxY = 80.0;
        bool hasPoints = false;
        AngleCalibrationRenderFrame renderFrame = RenderFrame;
        if (renderFrame != null && renderFrame.TraceStrokes != null)
        {
            foreach (AngleCalibrationTraceStroke traceStroke in renderFrame.TraceStrokes)
            {
                if (traceStroke == null || traceStroke.Points == null)
                {
                    continue;
                }
                foreach (AngleCalibrationTracePoint point in traceStroke.Points)
                {
                    if (!hasPoints)
                    {
                        minX = point.X;
                        maxX = point.X;
                        minY = point.Y;
                        maxY = point.Y;
                        hasPoints = true;
                    }
                    else
                    {
                        minX = Math.Min(minX, point.X);
                        maxX = Math.Max(maxX, point.X);
                        minY = Math.Min(minY, point.Y);
                        maxY = Math.Max(maxY, point.Y);
                    }
                }
            }
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

    private void ApplyThemeResources()
    {
        _historyPen = CreatePen(ResolveColor("TextMutedColor", Color.FromRgb(152, 152, 152)));
        _currentPen = CreatePen(ResolveColor("TextStrongColor", Color.FromRgb(200, 200, 200)));
    }

    private static Pen CreatePen(Color color)
    {
        Pen pen = new Pen(new SolidColorBrush(color), StrokeThickness)
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





