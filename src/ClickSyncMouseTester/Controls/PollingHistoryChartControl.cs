using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Controls;

public class PollingHistoryChartControl : FrameworkElement
{
    private const double HistoryMilliseconds = 3000.0;

    private const double HeadSmoothingTimeMilliseconds = 10.0;

    private const double LiveHeadStaleMilliseconds = 120.0;

    private const double CurveUnderlayThickness = 5.0;

    private const double CurveThickness = 1.8;

    private const double LatestPointOuterRadius = 4.8;

    private const double LatestPointInnerRadius = 2.4;

    private static readonly double[] TickValues = new double[5] { 0.0, 1000.0, 2000.0, 4000.0, 8000.0 };

    private readonly List<Point> _visiblePoints;

    private IReadOnlyList<PollingHistoryPoint> _samples;

    private double _samplePeakRate;

    private DrawingGroup _cachedStaticDecorDrawing;

    private Rect _cachedStaticDecorPlotArea;

    private Size _cachedStaticDecorRenderSize;

    private double _cachedStaticDecorAxisMax;

    private double _cachedStaticDecorPixelsPerDip;

    private bool _isStaticDecorCacheDirty;

    private StreamGeometry _cachedAreaGeometry;

    private StreamGeometry _cachedCurveGeometry;

    private Rect _cachedGeometryPlotArea;

    private Size _cachedGeometryRenderSize;

    private double _cachedGeometryAxisMax;

    private Point _cachedLatestStaticPoint;

    private bool _hasCachedLatestStaticPoint;

    private bool _isCurveGeometryCacheDirty;

    private Brush _backgroundBrush;

    private Pen _gridPen;

    private Pen _gridStrongPen;

    private Pen _axisPen;

    private Brush _areaFillBrush;

    private Brush _liveBandBrush;

    private Pen _curveUnderlayPen;

    private Pen _curvePen;

    private Pen _accentPen;

    private Brush _labelBrush;

    private Brush _strongLabelBrush;

    private Brush _backdropBrush;

    private Brush _latestPointFillBrush;

    private Brush _latestPointOuterBrush;

    private Pen _latestPointOuterPen;

    private Typeface _smallTypeface;

    private Typeface _strongTypeface;

    private double _cachedLabelAxisMax;

    private double _cachedPixelsPerDip;

    private double[] _cachedLabelTicks;

    private FormattedText[] _cachedLabels;

    private double _cachedBackdropAxisMax;

    private double _cachedBackdropPixelsPerDip;

    private FormattedText _cachedBackdropLabel;

    private double _cachedStaticTextPixelsPerDip;

    private FormattedText _cachedNegativeThreeSecondsLabel;

    private FormattedText _cachedNegativeTwoSecondsLabel;

    private FormattedText _cachedNegativeOneSecondLabel;

    private FormattedText _cachedNowLabel;

    private FormattedText _cachedLiveLabel;

    private bool _isRenderingSubscribed;

    private double _lastRenderingTimeMilliseconds;

    private double _frameDeltaMilliseconds;

    private double _smoothedHeadRate;

    private bool _hasSmoothedHeadRate;

    private bool _isThemeSubscribed;

    private bool _isLocalizationSubscribed;

    public static readonly DependencyProperty HistoryPointsProperty = DependencyProperty.Register("HistoryPoints", typeof(IEnumerable), typeof(PollingHistoryChartControl), new FrameworkPropertyMetadata(null, new PropertyChangedCallback(OnHistoryPointsChanged)));

    public static readonly DependencyProperty PeakRateProperty = DependencyProperty.Register("PeakRate", typeof(double), typeof(PollingHistoryChartControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentRateProperty = DependencyProperty.Register("CurrentRate", typeof(double), typeof(PollingHistoryChartControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register("IsLocked", typeof(bool), typeof(PollingHistoryChartControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, new PropertyChangedCallback(OnIsLockedChanged)));

    public IEnumerable HistoryPoints
    {
        get
        {
            return (IEnumerable)GetValue(HistoryPointsProperty);
        }
        set
        {
            SetValue(HistoryPointsProperty, value);
        }
    }

    public double PeakRate
    {
        get
        {
            return (double)GetValue(PeakRateProperty);
        }
        set
        {
            SetValue(PeakRateProperty, value);
        }
    }

    public double CurrentRate
    {
        get
        {
            return (double)GetValue(CurrentRateProperty);
        }
        set
        {
            SetValue(CurrentRateProperty, value);
        }
    }

    public bool IsLocked
    {
        get
        {
            return (bool)GetValue(IsLockedProperty);
        }
        set
        {
            SetValue(IsLockedProperty, value);
        }
    }

    public PollingHistoryChartControl()
    {
        _visiblePoints = new List<Point>();
        _samples = Array.Empty<PollingHistoryPoint>();
        _cachedStaticDecorPlotArea = Rect.Empty;
        _cachedStaticDecorRenderSize = Size.Empty;
        _cachedStaticDecorAxisMax = double.NaN;
        _cachedStaticDecorPixelsPerDip = double.NaN;
        _isStaticDecorCacheDirty = true;
        _cachedGeometryPlotArea = Rect.Empty;
        _cachedGeometryRenderSize = Size.Empty;
        _cachedGeometryAxisMax = double.NaN;
        _isCurveGeometryCacheDirty = true;
        _backgroundBrush = CreateFrozenBrush(CreateColor(255, 5, 5, 5));
        _gridPen = CreateFrozenPen(CreateColor(255, 24, 24, 26), 1.0);
        _gridStrongPen = CreateFrozenPen(CreateColor(255, 33, 33, 36), 1.0);
        _axisPen = CreateFrozenPen(CreateColor(255, 51, 51, 56), 1.0);
        _areaFillBrush = CreateFrozenBrush(CreateColor(24, 255, 255, 255));
        _liveBandBrush = CreateFrozenBrush(CreateColor(22, 182, 47, 255));
        _curveUnderlayPen = CreateCurvePen(CreateColor(56, 0, 0, 0), 5.0);
        _curvePen = CreateCurvePen(CreateColor(255, 241, 241, 241), 1.8);
        _accentPen = CreateCurvePen(CreateColor(255, 182, 47, 255), 1.0);
        _labelBrush = CreateFrozenBrush(CreateColor(255, 115, 115, 115));
        _strongLabelBrush = CreateFrozenBrush(CreateColor(255, 255, 255, 255));
        _backdropBrush = CreateFrozenBrush(CreateColor(255, 11, 11, 13));
        _latestPointFillBrush = CreateFrozenBrush(CreateColor(255, 182, 47, 255));
        _latestPointOuterBrush = CreateFrozenBrush(CreateColor(255, 5, 5, 5));
        _latestPointOuterPen = CreateFrozenPen(CreateColor(153, 255, 255, 255), 1.0);
        _smallTypeface = CreateTypeface(new FontFamily("Segoe UI"), FontWeights.Normal);
        _strongTypeface = CreateTypeface(new FontFamily("Segoe UI"), FontWeights.Bold);
        _cachedLabelAxisMax = double.NaN;
        _cachedPixelsPerDip = double.NaN;
        _cachedLabelTicks = Array.Empty<double>();
        _cachedLabels = Array.Empty<FormattedText>();
        _cachedBackdropAxisMax = double.NaN;
        _cachedBackdropPixelsPerDip = double.NaN;
        _cachedStaticTextPixelsPerDip = double.NaN;
        base.SnapsToDevicePixels = true;
        base.UseLayoutRounding = true;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        ApplyThemeResources();
        base.Loaded += OnLoaded;
        base.Unloaded += OnUnloaded;
    }

    private static void OnHistoryPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PollingHistoryChartControl)d).RebuildSampleCache();
    }

    private static void OnIsLockedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        PollingHistoryChartControl chartControl = (PollingHistoryChartControl)d;
        chartControl.UpdateRenderingSubscription();
        chartControl.InvalidateVisual();
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
        RebuildSampleCache();
        UpdateRenderingSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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
        UpdateRenderingSubscription(forceDetach: true);
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        ApplyThemeResources();
        InvalidateVisual();
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        ApplyThemeResources();
        InvalidateVisual();
    }

    private void ApplyThemeResources()
    {
        _backgroundBrush = ResolveBrush("ChartBackgroundBrush", CreateColor(255, 5, 5, 5));
        _gridPen = CreateFrozenPen(ResolveColor("ChartGridColor", CreateColor(255, 24, 24, 26)), 1.0);
        _gridStrongPen = CreateFrozenPen(ResolveColor("ChartGridStrongColor", CreateColor(255, 33, 33, 36)), 1.0);
        _axisPen = CreateFrozenPen(ResolveColor("ChartAxisColor", CreateColor(255, 51, 51, 56)), 1.0);
        _areaFillBrush = ResolveBrush("ChartAreaFillBrush", CreateColor(24, 255, 255, 255));
        _liveBandBrush = ResolveBrush("ChartLiveBandBrush", CreateColor(22, 182, 47, 255));
        _curveUnderlayPen = CreateCurvePen(ResolveColor("ChartCurveUnderlayColor", CreateColor(56, 0, 0, 0)), 5.0);
        _curvePen = CreateCurvePen(ResolveColor("ChartCurveColor", CreateColor(255, 241, 241, 241)), 1.8);
        _accentPen = CreateCurvePen(ResolveColor("ChartAccentColor", CreateColor(255, 182, 47, 255)), 1.0);
        _labelBrush = ResolveBrush("ChartLabelBrush", CreateColor(255, 115, 115, 115));
        _strongLabelBrush = ResolveBrush("ChartStrongLabelBrush", CreateColor(255, 255, 255, 255));
        _backdropBrush = ResolveBrush("ChartBackdropBrush", CreateColor(255, 11, 11, 13));
        _latestPointFillBrush = ResolveBrush("ChartLatestPointFillBrush", CreateColor(255, 182, 47, 255));
        _latestPointOuterBrush = ResolveBrush("ChartLatestPointOuterBrush", CreateColor(255, 5, 5, 5));
        _latestPointOuterPen = CreateFrozenPen(ResolveColor("ChartLatestPointOuterStrokeColor", CreateColor(153, 255, 255, 255)), 1.0);
        _smallTypeface = CreateTypeface(ResolveFontFamily("Font.DisplaySans", new FontFamily("Segoe UI")), FontWeights.Normal);
        _strongTypeface = CreateTypeface(ResolveFontFamily("Font.DisplaySans", new FontFamily("Segoe UI")), FontWeights.Bold);
        _cachedLabelAxisMax = double.NaN;
        _cachedPixelsPerDip = double.NaN;
        _cachedLabelTicks = Array.Empty<double>();
        _cachedLabels = Array.Empty<FormattedText>();
        _cachedBackdropAxisMax = double.NaN;
        _cachedBackdropPixelsPerDip = double.NaN;
        _cachedBackdropLabel = null;
        _cachedStaticTextPixelsPerDip = double.NaN;
        _cachedNegativeThreeSecondsLabel = null;
        _cachedNegativeTwoSecondsLabel = null;
        _cachedNegativeOneSecondLabel = null;
        _cachedNowLabel = null;
        _cachedLiveLabel = null;
        InvalidateStaticDecorCache();
    }

    private void UpdateRenderingSubscription(bool forceDetach = false)
    {
        if (forceDetach || !base.IsLoaded || !HasLiveHead(GetRealtimeNowMilliseconds()))
        {
            if (_isRenderingSubscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRenderingSubscribed = false;
            }
            _lastRenderingTimeMilliseconds = 0.0;
            _frameDeltaMilliseconds = 0.0;
        }
        else if (!_isRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _isRenderingSubscribed = true;
        }
    }

    private void OnRendering(object sender, EventArgs e)
    {
        if (e is RenderingEventArgs { RenderingTime: { TotalMilliseconds: var totalMilliseconds } })
        {
            if (_lastRenderingTimeMilliseconds > 0.0)
            {
                _frameDeltaMilliseconds = Math.Max(0.0, Math.Min(100.0, totalMilliseconds - _lastRenderingTimeMilliseconds));
            }
            else
            {
                _frameDeltaMilliseconds = 0.0;
            }
            _lastRenderingTimeMilliseconds = totalMilliseconds;
        }
        else
        {
            _frameDeltaMilliseconds = 0.0;
        }
        if (!HasLiveHead(GetRealtimeNowMilliseconds()))
        {
            UpdateRenderingSubscription(forceDetach: true);
            InvalidateVisual();
        }
        else
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!(base.ActualWidth <= 0.0) && !(base.ActualHeight <= 0.0))
        {
            double realtimeNowMilliseconds = GetRealtimeNowMilliseconds();
            double renderNowMilliseconds = GetRenderNow(realtimeNowMilliseconds);
            double peakRateFromSamples = GetPeakRateFromSamples();
            double axisMax = AxisMaxFor(Math.Max(Math.Max(PeakRate, CurrentRate), peakRateFromSamples));
            Rect plotArea = new Rect(72.0, 22.0, Math.Max(10.0, base.ActualWidth - 96.0), Math.Max(10.0, base.ActualHeight - 62.0));
            EnsureStaticDecorCache(plotArea, axisMax);
            if (_cachedStaticDecorDrawing != null)
            {
                drawingContext.DrawDrawing(_cachedStaticDecorDrawing);
            }
            else
            {
                drawingContext.DrawRectangle(_backgroundBrush, null, new Rect(0.0, 0.0, base.ActualWidth, base.ActualHeight));
                EnsureLabelCache(axisMax);
                DrawBackdropLabel(drawingContext, plotArea, axisMax);
                DrawGuides(drawingContext, plotArea, axisMax);
                DrawAxisLabels(drawingContext, plotArea, axisMax);
                DrawTimeLabels(drawingContext, plotArea);
            }
            DrawCurve(drawingContext, plotArea, axisMax, renderNowMilliseconds, HasLiveHead(realtimeNowMilliseconds));
        }
    }

    private static double AxisMaxFor(double peak)
    {
        if (peak <= 0.0 || double.IsNaN(peak) || double.IsInfinity(peak))
        {
            return 1000.0;
        }
        if (peak <= 1200.0)
        {
            return 1000.0;
        }
        if (peak <= 2600.0)
        {
            return 2000.0;
        }
        if (peak <= 5200.0)
        {
            return 4000.0;
        }
        return 8000.0;
    }

    private void DrawBackdropLabel(DrawingContext drawingContext, Rect plotArea, double axisMax)
    {
        EnsureBackdropLabelCache(axisMax);
        drawingContext.DrawText(_cachedBackdropLabel, new Point(plotArea.Left + 12.0, plotArea.Top - 16.0));
    }

    private void DrawGuides(DrawingContext drawingContext, Rect plotArea, double axisMax)
    {
        drawingContext.DrawLine(_axisPen, new Point(plotArea.Left, plotArea.Top), new Point(plotArea.Right, plotArea.Top));
        drawingContext.DrawLine(_axisPen, new Point(plotArea.Left, plotArea.Bottom), new Point(plotArea.Right, plotArea.Bottom));
        foreach (double tick in TickValues)
        {
            if (tick > axisMax)
            {
                break;
            }

            double y = plotArea.Top + (1.0 - tick / axisMax) * plotArea.Height;
            Pen guidePen = tick == 0.0 || Math.Abs(tick - axisMax) < 0.1 ? _axisPen : _gridPen;
            drawingContext.DrawLine(guidePen, new Point(plotArea.Left, y), new Point(plotArea.Right, y));
        }

        for (int column = 1; column <= 2; column++)
        {
            double x = plotArea.Left + plotArea.Width * column / 3.0;
            drawingContext.DrawLine(_gridStrongPen, new Point(x, plotArea.Top), new Point(x, plotArea.Bottom));
        }
        drawingContext.DrawLine(_axisPen, new Point(plotArea.Right, plotArea.Top), new Point(plotArea.Right, plotArea.Bottom));
    }

    private void DrawAxisLabels(DrawingContext drawingContext, Rect plotArea, double axisMax)
    {
        for (int index = 0; index < _cachedLabelTicks.Length; index++)
        {
            double tick = _cachedLabelTicks[index];
            FormattedText label = _cachedLabels[index];
            double y = plotArea.Top + (1.0 - tick / axisMax) * plotArea.Height;
            drawingContext.DrawText(label, new Point(8.0, y - label.Height / 2.0));
        }
    }

    private void DrawTimeLabels(DrawingContext drawingContext, Rect plotArea)
    {
        EnsureStaticLabelCache();
        Tuple<FormattedText, double>[] labels =
        {
            Tuple.Create(_cachedNegativeThreeSecondsLabel, plotArea.Left),
            Tuple.Create(_cachedNegativeTwoSecondsLabel, plotArea.Left + plotArea.Width / 3.0),
            Tuple.Create(_cachedNegativeOneSecondLabel, plotArea.Left + plotArea.Width * 2.0 / 3.0),
            Tuple.Create(_cachedNowLabel, plotArea.Right)
        };

        foreach (Tuple<FormattedText, double> labelInfo in labels)
        {
            FormattedText label = labelInfo.Item1;
            double x = labelInfo.Item2;
            if (ReferenceEquals(label, _cachedNowLabel))
            {
                x -= label.Width;
            }

            drawingContext.DrawText(label, new Point(x, plotArea.Bottom + 10.0));
        }
    }

    private void DrawCurve(DrawingContext drawingContext, Rect plotArea, double axisMax, double nowMs, bool isHeadLive)
    {
        if (_samples.Count == 0)
        {
            _hasSmoothedHeadRate = false;
            return;
        }
        EnsureCurveGeometryCache(plotArea, axisMax);
        if (!_hasCachedLatestStaticPoint)
        {
            _hasSmoothedHeadRate = false;
            return;
        }
        Point latestStaticPoint = _cachedLatestStaticPoint;
        Point latestPoint = latestStaticPoint;
        bool shouldAppendPinnedHead = false;
        if (isHeadLive)
        {
            Point pinnedHeadPoint = GetPinnedHeadPoint(plotArea, axisMax);
            shouldAppendPinnedHead = Math.Abs(pinnedHeadPoint.X - latestStaticPoint.X) > 0.5 || Math.Abs(pinnedHeadPoint.Y - latestStaticPoint.Y) > 0.5;
            latestPoint = pinnedHeadPoint;
        }
        else
        {
            _hasSmoothedHeadRate = false;
        }
        drawingContext.PushClip(new RectangleGeometry(CreateCurveClipRect(plotArea)));
        if (isHeadLive)
        {
            drawingContext.DrawRectangle(_liveBandBrush, null, new Rect(plotArea.Right - 16.0, plotArea.Top, 16.0, plotArea.Height));
        }
        if (_cachedAreaGeometry != null)
        {
            drawingContext.DrawGeometry(_areaFillBrush, null, _cachedAreaGeometry);
        }
        if (_cachedCurveGeometry != null)
        {
            drawingContext.DrawGeometry(null, _curveUnderlayPen, _cachedCurveGeometry);
            drawingContext.DrawGeometry(null, _curvePen, _cachedCurveGeometry);
        }
        if (shouldAppendPinnedHead)
        {
            StreamGeometry liveTailArea = BuildLiveTailAreaGeometry(latestStaticPoint, latestPoint, plotArea.Bottom);
            if (liveTailArea != null)
            {
                drawingContext.DrawGeometry(_areaFillBrush, null, liveTailArea);
            }

            StreamGeometry liveTailCurve = BuildLiveTailCurveGeometry(latestStaticPoint, latestPoint);
            if (liveTailCurve != null)
            {
                drawingContext.DrawGeometry(null, _curveUnderlayPen, liveTailCurve);
                drawingContext.DrawGeometry(null, _curvePen, liveTailCurve);
            }
        }
        if (isHeadLive)
        {
            drawingContext.DrawLine(_accentPen, latestPoint, new Point(latestPoint.X, plotArea.Bottom));
        }
        drawingContext.DrawEllipse(_latestPointOuterBrush, _latestPointOuterPen, latestPoint, LatestPointOuterRadius, LatestPointOuterRadius);
        drawingContext.DrawEllipse(_latestPointFillBrush, null, latestPoint, LatestPointInnerRadius, LatestPointInnerRadius);
        drawingContext.Pop();
        if (isHeadLive)
        {
            EnsureStaticLabelCache();
            drawingContext.DrawText(_cachedLiveLabel, new Point(plotArea.Right - _cachedLiveLabel.Width, plotArea.Top - _cachedLiveLabel.Height - 4.0));
        }
    }

    private void RebuildSampleCache()
    {
        _samplePeakRate = 0.0;
        if (!(HistoryPoints is IReadOnlyList<PollingHistoryPoint> samples))
        {
            _samples = Array.Empty<PollingHistoryPoint>();
        }
        else
        {
            _samples = samples;
            for (int index = 0; index < _samples.Count; index++)
            {
                PollingHistoryPoint sample = _samples[index];
                if (sample != null && sample.Rate > _samplePeakRate)
                {
                    _samplePeakRate = sample.Rate;
                }
            }
        }
        if (_samples.Count == 0)
        {
            _hasSmoothedHeadRate = false;
        }
        InvalidateCurveGeometryCache();
        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    private void EnsureLabelCache(double axisMax)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (axisMax == _cachedLabelAxisMax && Math.Abs(pixelsPerDip - _cachedPixelsPerDip) < 0.001)
        {
            return;
        }
        List<double> labelTicks = new List<double>();
        List<FormattedText> labels = new List<FormattedText>();
        double[] tickValues = TickValues;
        for (int i = 0; i < tickValues.Length; i++)
        {
            double tick = tickValues[i];
            if (tick > axisMax)
            {
                break;
            }
            labelTicks.Add(tick);
            string labelText = tick >= 1000.0 ? (tick / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k" : tick.ToString("0", CultureInfo.InvariantCulture);
            double fontSize = tick == 0.0 || Math.Abs(tick - axisMax) < 0.1 ? 14.0 : 11.0;
            Brush brush = tick == 0.0 || Math.Abs(tick - axisMax) < 0.1 ? _strongLabelBrush : _labelBrush;
            Typeface typeface = tick == 0.0 || Math.Abs(tick - axisMax) < 0.1 ? _strongTypeface : _smallTypeface;
            labels.Add(CreateText(labelText, fontSize, brush, typeface, pixelsPerDip));
        }
        _cachedLabelAxisMax = axisMax;
        _cachedPixelsPerDip = pixelsPerDip;
        _cachedLabelTicks = labelTicks.ToArray();
        _cachedLabels = labels.ToArray();
    }

    private void EnsureBackdropLabelCache(double axisMax)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cachedBackdropLabel == null || axisMax != _cachedBackdropAxisMax || !(Math.Abs(pixelsPerDip - _cachedBackdropPixelsPerDip) < 0.001))
        {
            string backdropLabelText = (axisMax / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k";
            _cachedBackdropAxisMax = axisMax;
            _cachedBackdropPixelsPerDip = pixelsPerDip;
            _cachedBackdropLabel = CreateText(backdropLabelText, 96.0, _backdropBrush, _strongTypeface, pixelsPerDip);
        }
    }

    private void EnsureStaticLabelCache()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cachedNegativeThreeSecondsLabel == null || !(Math.Abs(pixelsPerDip - _cachedStaticTextPixelsPerDip) < 0.001))
        {
            _cachedStaticTextPixelsPerDip = pixelsPerDip;
            _cachedNegativeThreeSecondsLabel = CreateText("-3S", 11.0, _labelBrush, _smallTypeface, pixelsPerDip);
            _cachedNegativeTwoSecondsLabel = CreateText("-2S", 11.0, _labelBrush, _smallTypeface, pixelsPerDip);
            _cachedNegativeOneSecondLabel = CreateText("-1S", 11.0, _labelBrush, _smallTypeface, pixelsPerDip);
            _cachedNowLabel = CreateText("NOW", 11.0, _labelBrush, _smallTypeface, pixelsPerDip);
            _cachedLiveLabel = CreateText("LIVE", 11.0, _latestPointFillBrush, _smallTypeface, pixelsPerDip);
        }
    }

    private double GetPeakRateFromSamples()
    {
        return _samplePeakRate;
    }

    private void EnsureStaticDecorCache(Rect plotArea, double axisMax)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Size renderSize = new Size(base.ActualWidth, base.ActualHeight);
        if (_isStaticDecorCacheDirty || !(_cachedStaticDecorPlotArea == plotArea) || !(_cachedStaticDecorRenderSize == renderSize) || _cachedStaticDecorAxisMax != axisMax || !(Math.Abs(_cachedStaticDecorPixelsPerDip - pixelsPerDip) < 0.001))
        {
            EnsureLabelCache(axisMax);
            EnsureBackdropLabelCache(axisMax);
            EnsureStaticLabelCache();
            DrawingGroup drawingGroup = new DrawingGroup();
            using (DrawingContext drawingContext = drawingGroup.Open())
            {
                drawingContext.DrawRectangle(_backgroundBrush, null, new Rect(0.0, 0.0, base.ActualWidth, base.ActualHeight));
                DrawBackdropLabel(drawingContext, plotArea, axisMax);
                DrawGuides(drawingContext, plotArea, axisMax);
                DrawAxisLabels(drawingContext, plotArea, axisMax);
                DrawTimeLabels(drawingContext, plotArea);
            }
            if (drawingGroup.CanFreeze)
            {
                drawingGroup.Freeze();
            }
            _cachedStaticDecorDrawing = drawingGroup;
            _cachedStaticDecorPlotArea = plotArea;
            _cachedStaticDecorRenderSize = renderSize;
            _cachedStaticDecorAxisMax = axisMax;
            _cachedStaticDecorPixelsPerDip = pixelsPerDip;
            _isStaticDecorCacheDirty = false;
        }
    }

    private void InvalidateStaticDecorCache()
    {
        _cachedStaticDecorDrawing = null;
        _cachedStaticDecorPlotArea = Rect.Empty;
        _cachedStaticDecorRenderSize = Size.Empty;
        _cachedStaticDecorAxisMax = double.NaN;
        _cachedStaticDecorPixelsPerDip = double.NaN;
        _isStaticDecorCacheDirty = true;
    }

    private void EnsureCurveGeometryCache(Rect plotArea, double axisMax)
    {
        Size renderSize = new Size(base.ActualWidth, base.ActualHeight);
        if (_isCurveGeometryCacheDirty || !(_cachedGeometryPlotArea == plotArea) || !(_cachedGeometryRenderSize == renderSize) || _cachedGeometryAxisMax != axisMax)
        {
            RebuildCurveGeometryCache(plotArea, axisMax, renderSize);
        }
    }

    private void RebuildCurveGeometryCache(Rect plotArea, double axisMax, Size renderSize)
    {
        _cachedAreaGeometry = null;
        _cachedCurveGeometry = null;
        _cachedGeometryPlotArea = plotArea;
        _cachedGeometryRenderSize = renderSize;
        _cachedGeometryAxisMax = axisMax;
        _hasCachedLatestStaticPoint = false;
        _isCurveGeometryCacheDirty = false;
        if (_samples.Count == 0)
        {
            return;
        }
        PollingHistoryPoint latestSample = _samples[_samples.Count - 1];
        if (latestSample == null)
        {
            return;
        }
        double cutoff = latestSample.TimestampMs - HistoryMilliseconds;
        PollingHistoryPoint lastSampleBeforeCutoff = null;
        PollingHistoryPoint firstSampleAtOrAfterCutoff = null;
        _visiblePoints.Clear();
        for (int index = 0; index < _samples.Count; index++)
        {
            PollingHistoryPoint sample = _samples[index];
            if (sample == null)
            {
                continue;
            }
            if (sample.TimestampMs < cutoff)
            {
                lastSampleBeforeCutoff = sample;
                continue;
            }
            if (firstSampleAtOrAfterCutoff == null)
            {
                firstSampleAtOrAfterCutoff = sample;
            }
            double x = plotArea.Left + (sample.TimestampMs - cutoff) / HistoryMilliseconds * plotArea.Width;
            double y = GetYForRate(plotArea, axisMax, sample.Rate);
            _visiblePoints.Add(new Point(x, y));
        }
        if (_visiblePoints.Count == 0)
        {
            return;
        }
        Point firstVisiblePoint = _visiblePoints[0];
        if (firstVisiblePoint.X > plotArea.Left)
        {
            Point leadingPoint;
            if (lastSampleBeforeCutoff != null && firstSampleAtOrAfterCutoff != null)
            {
                leadingPoint = CreateLeadingEdgePoint(plotArea, axisMax, cutoff, lastSampleBeforeCutoff, firstSampleAtOrAfterCutoff, firstVisiblePoint);
            }
            else
            {
                leadingPoint = new Point(plotArea.Left, firstVisiblePoint.Y);
            }
            if (Math.Abs(firstVisiblePoint.X - leadingPoint.X) > 0.01 || Math.Abs(firstVisiblePoint.Y - leadingPoint.Y) > 0.01)
            {
                _visiblePoints.Insert(0, leadingPoint);
            }
            else
            {
                _visiblePoints[0] = leadingPoint;
            }
        }
        else if (firstVisiblePoint.X < plotArea.Left)
        {
            _visiblePoints[0] = new Point(plotArea.Left, firstVisiblePoint.Y);
        }
        _cachedLatestStaticPoint = _visiblePoints[_visiblePoints.Count - 1];
        _hasCachedLatestStaticPoint = true;
        if (_visiblePoints.Count >= 2)
        {
            _cachedAreaGeometry = BuildAreaGeometry(_visiblePoints, plotArea.Bottom);
            _cachedCurveGeometry = BuildCurveGeometry(_visiblePoints);
        }
    }

    private void InvalidateCurveGeometryCache()
    {
        _cachedAreaGeometry = null;
        _cachedCurveGeometry = null;
        _cachedGeometryPlotArea = Rect.Empty;
        _cachedGeometryRenderSize = Size.Empty;
        _cachedGeometryAxisMax = double.NaN;
        _hasCachedLatestStaticPoint = false;
        _isCurveGeometryCacheDirty = true;
    }

    private double GetRenderNow(double realtimeNowMs)
    {
        if (_samples.Count == 0)
        {
            return realtimeNowMs;
        }
        PollingHistoryPoint latestSample = _samples[_samples.Count - 1];
        if (latestSample == null)
        {
            return realtimeNowMs;
        }
        if (!IsLocked)
        {
            return latestSample.TimestampMs;
        }
        if (realtimeNowMs - latestSample.RealtimeTimestampMs > LiveHeadStaleMilliseconds)
        {
            return latestSample.TimestampMs;
        }
        return latestSample.TimestampMs + Math.Max(0.0, realtimeNowMs - latestSample.RealtimeTimestampMs);
    }

    private bool HasLiveHead(double nowMs)
    {
        if (!IsLocked || _samples.Count == 0)
        {
            return false;
        }
        PollingHistoryPoint latestSample = _samples[_samples.Count - 1];
        if (latestSample == null)
        {
            return false;
        }
        return nowMs - latestSample.RealtimeTimestampMs <= LiveHeadStaleMilliseconds;
    }

    private static double GetRealtimeNowMilliseconds()
    {
        return (double)Stopwatch.GetTimestamp() * 1000.0 / (double)Stopwatch.Frequency;
    }

    private Point GetPinnedHeadPoint(Rect plotArea, double axisMax)
    {
        double targetRate = NormalizeRate(CurrentRate);
        double rate = SmoothHeadRate(targetRate);
        double yForRate = GetYForRate(plotArea, axisMax, rate);
        return new Point(plotArea.Right, yForRate);
    }

    private double SmoothHeadRate(double targetRate)
    {
        if (!_hasSmoothedHeadRate)
        {
            _smoothedHeadRate = targetRate;
            _hasSmoothedHeadRate = true;
            return _smoothedHeadRate;
        }
        if (_frameDeltaMilliseconds <= 0.0)
        {
            return _smoothedHeadRate;
        }
        double alpha = 1.0 - Math.Exp(-_frameDeltaMilliseconds / HeadSmoothingTimeMilliseconds);
        _smoothedHeadRate += (targetRate - _smoothedHeadRate) * alpha;
        if (Math.Abs(_smoothedHeadRate - targetRate) < 0.01)
        {
            _smoothedHeadRate = targetRate;
        }
        return _smoothedHeadRate;
    }

    private static double NormalizeRate(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0.0)
        {
            return 0.0;
        }
        return rate;
    }

    private static double GetYForRate(Rect plotArea, double axisMax, double rate)
    {
        double normalizedRate = Math.Max(0.0, Math.Min(1.0, NormalizeRate(rate) / axisMax));
        return plotArea.Top + (1.0 - normalizedRate) * plotArea.Height;
    }

    private Rect CreateCurveClipRect(Rect plotArea)
    {
        double curveHalfThickness = Math.Max(_curveUnderlayPen.Thickness, _curvePen.Thickness) / 2.0;
        double latestPointStrokeHalfThickness = _latestPointOuterPen.Thickness / 2.0;
        double edgePadding = Math.Ceiling(Math.Max(curveHalfThickness, LatestPointOuterRadius + latestPointStrokeHalfThickness)) + 1.0;
        return new Rect(plotArea.Left - edgePadding, plotArea.Top - edgePadding, plotArea.Width + edgePadding * 2.0, plotArea.Height + edgePadding * 2.0);
    }

    private Point CreateLeadingEdgePoint(Rect plotArea, double axisMax, double cutoff, PollingHistoryPoint lastSampleBeforeCutoff, PollingHistoryPoint firstSampleAtOrAfterCutoff, Point firstVisiblePoint)
    {
        if (lastSampleBeforeCutoff == null)
        {
            return new Point(plotArea.Left, firstVisiblePoint.Y);
        }
        if (firstSampleAtOrAfterCutoff == null)
        {
            return new Point(plotArea.Left, firstVisiblePoint.Y);
        }
        double span = firstSampleAtOrAfterCutoff.TimestampMs - lastSampleBeforeCutoff.TimestampMs;
        if (span <= 0.0)
        {
            return new Point(plotArea.Left, firstVisiblePoint.Y);
        }
        double t = Math.Max(0.0, Math.Min(1.0, (cutoff - lastSampleBeforeCutoff.TimestampMs) / span));
        double interpolatedRate = lastSampleBeforeCutoff.Rate + (firstSampleAtOrAfterCutoff.Rate - lastSampleBeforeCutoff.Rate) * t;
        return new Point(plotArea.Left, GetYForRate(plotArea, axisMax, interpolatedRate));
    }

    private static StreamGeometry BuildCurveGeometry(IList<Point> points)
    {
        if (points == null || points.Count < 2)
        {
            return null;
        }
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (int index = 0; index < points.Count - 1; index++)
            {
                Point currentPoint = points[index];
                Point nextPoint = points[index + 1];
                Point midpoint = new Point((currentPoint.X + nextPoint.X) / 2.0, (currentPoint.Y + nextPoint.Y) / 2.0);
                context.QuadraticBezierTo(currentPoint, midpoint, isStroked: true, isSmoothJoin: true);
            }
            context.LineTo(points[points.Count - 1], isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildAreaGeometry(IList<Point> points, double baselineY)
    {
        if (points == null || points.Count < 2)
        {
            return null;
        }
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, baselineY), isFilled: true, isClosed: true);
            context.LineTo(points[0], isStroked: true, isSmoothJoin: true);
            for (int index = 0; index < points.Count - 1; index++)
            {
                Point currentPoint = points[index];
                Point nextPoint = points[index + 1];
                Point midpoint = new Point((currentPoint.X + nextPoint.X) / 2.0, (currentPoint.Y + nextPoint.Y) / 2.0);
                context.QuadraticBezierTo(currentPoint, midpoint, isStroked: true, isSmoothJoin: true);
            }
            context.LineTo(points[points.Count - 1], isStroked: true, isSmoothJoin: true);
            context.LineTo(new Point(points[points.Count - 1].X, baselineY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildLiveTailCurveGeometry(Point startPoint, Point endPoint)
    {
        if (Math.Abs(startPoint.X - endPoint.X) < 0.01 && Math.Abs(startPoint.Y - endPoint.Y) < 0.01)
        {
            return null;
        }
        Point midpoint = new Point((startPoint.X + endPoint.X) / 2.0, (startPoint.Y + endPoint.Y) / 2.0);
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(startPoint, isFilled: false, isClosed: false);
            context.QuadraticBezierTo(startPoint, midpoint, isStroked: true, isSmoothJoin: true);
            context.LineTo(endPoint, isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildLiveTailAreaGeometry(Point startPoint, Point endPoint, double baselineY)
    {
        if (Math.Abs(startPoint.X - endPoint.X) < 0.01 && Math.Abs(startPoint.Y - endPoint.Y) < 0.01)
        {
            return null;
        }
        Point midpoint = new Point((startPoint.X + endPoint.X) / 2.0, (startPoint.Y + endPoint.Y) / 2.0);
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(startPoint.X, baselineY), isFilled: true, isClosed: true);
            context.LineTo(startPoint, isStroked: true, isSmoothJoin: true);
            context.QuadraticBezierTo(startPoint, midpoint, isStroked: true, isSmoothJoin: true);
            context.LineTo(endPoint, isStroked: true, isSmoothJoin: true);
            context.LineTo(new Point(endPoint.X, baselineY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private FormattedText CreateText(string content, double fontSize, Brush brush, Typeface typeface)
    {
        return CreateText(content, fontSize, brush, typeface, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static FormattedText CreateText(string content, double fontSize, Brush brush, Typeface typeface, double pixelsPerDip)
    {
        return new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, brush, pixelsPerDip);
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

    private static Typeface CreateTypeface(FontFamily fontFamily, FontWeight fontWeight)
    {
        return new Typeface(fontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal);
    }

    private static Brush ResolveBrush(string resourceKey, Color fallbackColor)
    {
        object resource = null;
        if (System.Windows.Application.Current != null)
        {
            resource = System.Windows.Application.Current.TryFindResource(resourceKey);
        }
        if (resource is Brush source)
        {
            return CreateFrozenBrush(source);
        }
        return CreateFrozenBrush(fallbackColor);
    }

    private static Color ResolveColor(string resourceKey, Color fallbackColor)
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
            return fallbackColor;
        }
        return color;
    }

    private static Pen CreateCurvePen(Color color, double thickness)
    {
        Pen pen = new Pen(CreateFrozenBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        Pen pen = new Pen(CreateFrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        SolidColorBrush solidColorBrush = new SolidColorBrush(color);
        solidColorBrush.Freeze();
        return solidColorBrush;
    }

    private static Brush CreateFrozenBrush(Brush source)
    {
        if (source == null)
        {
            return CreateFrozenBrush(Colors.Transparent);
        }
        Brush brush = source.CloneCurrentValue();
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    private static Color CreateColor(int alpha, int red, int green, int blue)
    {
        return Color.FromArgb((byte)alpha, (byte)red, (byte)green, (byte)blue);
    }
}





