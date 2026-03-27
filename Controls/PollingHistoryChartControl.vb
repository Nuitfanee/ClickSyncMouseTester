Imports System.Collections
Imports System.Globalization
Imports System.Windows.Media
Imports WpfApp1.Models
Imports WpfApp1.Services

Namespace Controls
    Public Class PollingHistoryChartControl
        Inherits FrameworkElement

        Private Const HistoryMilliseconds As Double = 3000.0
        Private Const HeadSmoothingTimeMilliseconds As Double = 10.0
        Private Const LiveHeadStaleMilliseconds As Double = 120.0
        Private Const CurveUnderlayThickness As Double = 5.0
        Private Const CurveThickness As Double = 1.8
        Private Const LatestPointOuterRadius As Double = 4.8
        Private Const LatestPointInnerRadius As Double = 2.4

        Private Shared ReadOnly TickValues As Double() = {0.0, 1000.0, 2000.0, 4000.0, 8000.0}

        Private ReadOnly _visiblePoints As New List(Of Point)()
        Private _samples As IReadOnlyList(Of PollingHistoryPoint) = Array.Empty(Of PollingHistoryPoint)()
        Private _samplePeakRate As Double
        Private _cachedStaticDecorDrawing As DrawingGroup
        Private _cachedStaticDecorPlotArea As Rect = Rect.Empty
        Private _cachedStaticDecorRenderSize As Size = Size.Empty
        Private _cachedStaticDecorAxisMax As Double = Double.NaN
        Private _cachedStaticDecorPixelsPerDip As Double = Double.NaN
        Private _isStaticDecorCacheDirty As Boolean = True
        Private _cachedAreaGeometry As StreamGeometry
        Private _cachedCurveGeometry As StreamGeometry
        Private _cachedGeometryPlotArea As Rect = Rect.Empty
        Private _cachedGeometryRenderSize As Size = Size.Empty
        Private _cachedGeometryAxisMax As Double = Double.NaN
        Private _cachedLatestStaticPoint As Point
        Private _hasCachedLatestStaticPoint As Boolean
        Private _isCurveGeometryCacheDirty As Boolean = True
        Private _backgroundBrush As Brush = CreateFrozenBrush(CreateColor(&HFF, &H05, &H05, &H05))
        Private _gridPen As Pen = CreateFrozenPen(CreateColor(&HFF, &H18, &H18, &H1A), 1.0)
        Private _gridStrongPen As Pen = CreateFrozenPen(CreateColor(&HFF, &H21, &H21, &H24), 1.0)
        Private _axisPen As Pen = CreateFrozenPen(CreateColor(&HFF, &H33, &H33, &H38), 1.0)
        Private _areaFillBrush As Brush = CreateFrozenBrush(CreateColor(&H18, &HFF, &HFF, &HFF))
        Private _liveBandBrush As Brush = CreateFrozenBrush(CreateColor(&H16, &HB6, &H2F, &HFF))
        Private _curveUnderlayPen As Pen = CreateCurvePen(CreateColor(&H38, &H00, &H00, &H00), CurveUnderlayThickness)
        Private _curvePen As Pen = CreateCurvePen(CreateColor(&HFF, &HF1, &HF1, &HF1), CurveThickness)
        Private _accentPen As Pen = CreateCurvePen(CreateColor(&HFF, &HB6, &H2F, &HFF), 1.0)
        Private _labelBrush As Brush = CreateFrozenBrush(CreateColor(&HFF, &H73, &H73, &H73))
        Private _strongLabelBrush As Brush = CreateFrozenBrush(CreateColor(&HFF, &HFF, &HFF, &HFF))
        Private _backdropBrush As Brush = CreateFrozenBrush(CreateColor(&HFF, &H0B, &H0B, &H0D))
        Private _latestPointFillBrush As Brush = CreateFrozenBrush(CreateColor(&HFF, &HB6, &H2F, &HFF))
        Private _latestPointOuterBrush As Brush = CreateFrozenBrush(CreateColor(&HFF, &H05, &H05, &H05))
        Private _latestPointOuterPen As Pen = CreateFrozenPen(CreateColor(&H99, &HFF, &HFF, &HFF), 1.0)
        Private _smallTypeface As Typeface = CreateTypeface(New FontFamily("Segoe UI"), FontWeights.Normal)
        Private _strongTypeface As Typeface = CreateTypeface(New FontFamily("Segoe UI"), FontWeights.Bold)

        Private _cachedLabelAxisMax As Double = Double.NaN
        Private _cachedPixelsPerDip As Double = Double.NaN
        Private _cachedLabelTicks As Double() = Array.Empty(Of Double)()
        Private _cachedLabels As FormattedText() = Array.Empty(Of FormattedText)()
        Private _cachedBackdropAxisMax As Double = Double.NaN
        Private _cachedBackdropPixelsPerDip As Double = Double.NaN
        Private _cachedBackdropLabel As FormattedText
        Private _cachedStaticTextPixelsPerDip As Double = Double.NaN
        Private _cachedNegativeThreeSecondsLabel As FormattedText
        Private _cachedNegativeTwoSecondsLabel As FormattedText
        Private _cachedNegativeOneSecondLabel As FormattedText
        Private _cachedNowLabel As FormattedText
        Private _cachedLiveLabel As FormattedText
        Private _isRenderingSubscribed As Boolean
        Private _lastRenderingTimeMilliseconds As Double
        Private _frameDeltaMilliseconds As Double
        Private _smoothedHeadRate As Double
        Private _hasSmoothedHeadRate As Boolean
        Private _isThemeSubscribed As Boolean
        Private _isLocalizationSubscribed As Boolean

        Public Shared ReadOnly HistoryPointsProperty As DependencyProperty =
            DependencyProperty.Register(
                "HistoryPoints",
                GetType(IEnumerable),
                GetType(PollingHistoryChartControl),
                New FrameworkPropertyMetadata(Nothing,
                                              AddressOf OnHistoryPointsChanged))

        Public Shared ReadOnly PeakRateProperty As DependencyProperty =
            DependencyProperty.Register(
                "PeakRate",
                GetType(Double),
                GetType(PollingHistoryChartControl),
                New FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender))

        Public Shared ReadOnly CurrentRateProperty As DependencyProperty =
            DependencyProperty.Register(
                "CurrentRate",
                GetType(Double),
                GetType(PollingHistoryChartControl),
                New FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender))

        Public Shared ReadOnly IsLockedProperty As DependencyProperty =
            DependencyProperty.Register(
                "IsLocked",
                GetType(Boolean),
                GetType(PollingHistoryChartControl),
                New FrameworkPropertyMetadata(False,
                                              FrameworkPropertyMetadataOptions.AffectsRender,
                                              AddressOf OnIsLockedChanged))

        Public Sub New()
            SnapsToDevicePixels = True
            UseLayoutRounding = True
            TextOptions.SetTextRenderingMode(Me, TextRenderingMode.ClearType)
            TextOptions.SetTextFormattingMode(Me, TextFormattingMode.Display)
            TextOptions.SetTextHintingMode(Me, TextHintingMode.Fixed)
            RenderOptions.SetClearTypeHint(Me, ClearTypeHint.Enabled)
            ApplyThemeResources()
            AddHandler Loaded, AddressOf OnLoaded
            AddHandler Unloaded, AddressOf OnUnloaded
        End Sub

        Public Property HistoryPoints As IEnumerable
            Get
                Return CType(GetValue(HistoryPointsProperty), IEnumerable)
            End Get
            Set(value As IEnumerable)
                SetValue(HistoryPointsProperty, value)
            End Set
        End Property

        Public Property PeakRate As Double
            Get
                Return CDbl(GetValue(PeakRateProperty))
            End Get
            Set(value As Double)
                SetValue(PeakRateProperty, value)
            End Set
        End Property

        Public Property CurrentRate As Double
            Get
                Return CDbl(GetValue(CurrentRateProperty))
            End Get
            Set(value As Double)
                SetValue(CurrentRateProperty, value)
            End Set
        End Property

        Public Property IsLocked As Boolean
            Get
                Return CBool(GetValue(IsLockedProperty))
            End Get
            Set(value As Boolean)
                SetValue(IsLockedProperty, value)
            End Set
        End Property

        Private Shared Sub OnHistoryPointsChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            CType(d, PollingHistoryChartControl).RebuildSampleCache()
        End Sub

        Private Shared Sub OnIsLockedChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            Dim control = CType(d, PollingHistoryChartControl)
            control.UpdateRenderingSubscription()
            control.InvalidateVisual()
        End Sub

        Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
            If Not _isThemeSubscribed Then
                AddHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = True
            End If

            If Not _isLocalizationSubscribed Then
                AddHandler LocalizationManager.Instance.LanguageChanged, AddressOf OnLanguageChanged
                _isLocalizationSubscribed = True
            End If

            ApplyThemeResources()
            RebuildSampleCache()
            UpdateRenderingSubscription()
        End Sub

        Private Sub OnUnloaded(sender As Object, e As RoutedEventArgs)
            If _isThemeSubscribed Then
                RemoveHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = False
            End If

            If _isLocalizationSubscribed Then
                RemoveHandler LocalizationManager.Instance.LanguageChanged, AddressOf OnLanguageChanged
                _isLocalizationSubscribed = False
            End If

            UpdateRenderingSubscription(forceDetach:=True)
        End Sub

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            ApplyThemeResources()
            InvalidateVisual()
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            ApplyThemeResources()
            InvalidateVisual()
        End Sub

        Private Sub ApplyThemeResources()
            _backgroundBrush = ResolveBrush("ChartBackgroundBrush", CreateColor(&HFF, &H05, &H05, &H05))
            _gridPen = CreateFrozenPen(ResolveColor("ChartGridColor", CreateColor(&HFF, &H18, &H18, &H1A)), 1.0)
            _gridStrongPen = CreateFrozenPen(ResolveColor("ChartGridStrongColor", CreateColor(&HFF, &H21, &H21, &H24)), 1.0)
            _axisPen = CreateFrozenPen(ResolveColor("ChartAxisColor", CreateColor(&HFF, &H33, &H33, &H38)), 1.0)
            _areaFillBrush = ResolveBrush("ChartAreaFillBrush", CreateColor(&H18, &HFF, &HFF, &HFF))
            _liveBandBrush = ResolveBrush("ChartLiveBandBrush", CreateColor(&H16, &HB6, &H2F, &HFF))
            _curveUnderlayPen = CreateCurvePen(ResolveColor("ChartCurveUnderlayColor", CreateColor(&H38, &H00, &H00, &H00)), CurveUnderlayThickness)
            _curvePen = CreateCurvePen(ResolveColor("ChartCurveColor", CreateColor(&HFF, &HF1, &HF1, &HF1)), CurveThickness)
            _accentPen = CreateCurvePen(ResolveColor("ChartAccentColor", CreateColor(&HFF, &HB6, &H2F, &HFF)), 1.0)
            _labelBrush = ResolveBrush("ChartLabelBrush", CreateColor(&HFF, &H73, &H73, &H73))
            _strongLabelBrush = ResolveBrush("ChartStrongLabelBrush", CreateColor(&HFF, &HFF, &HFF, &HFF))
            _backdropBrush = ResolveBrush("ChartBackdropBrush", CreateColor(&HFF, &H0B, &H0B, &H0D))
            _latestPointFillBrush = ResolveBrush("ChartLatestPointFillBrush", CreateColor(&HFF, &HB6, &H2F, &HFF))
            _latestPointOuterBrush = ResolveBrush("ChartLatestPointOuterBrush", CreateColor(&HFF, &H05, &H05, &H05))
            _latestPointOuterPen = CreateFrozenPen(ResolveColor("ChartLatestPointOuterStrokeColor", CreateColor(&H99, &HFF, &HFF, &HFF)), 1.0)
            _smallTypeface = CreateTypeface(ResolveFontFamily("Font.DisplaySans", New FontFamily("Segoe UI")), FontWeights.Normal)
            _strongTypeface = CreateTypeface(ResolveFontFamily("Font.DisplaySans", New FontFamily("Segoe UI")), FontWeights.Bold)

            _cachedLabelAxisMax = Double.NaN
            _cachedPixelsPerDip = Double.NaN
            _cachedLabelTicks = Array.Empty(Of Double)()
            _cachedLabels = Array.Empty(Of FormattedText)()
            _cachedBackdropAxisMax = Double.NaN
            _cachedBackdropPixelsPerDip = Double.NaN
            _cachedBackdropLabel = Nothing
            _cachedStaticTextPixelsPerDip = Double.NaN
            _cachedNegativeThreeSecondsLabel = Nothing
            _cachedNegativeTwoSecondsLabel = Nothing
            _cachedNegativeOneSecondLabel = Nothing
            _cachedNowLabel = Nothing
            _cachedLiveLabel = Nothing
            InvalidateStaticDecorCache()
        End Sub

        Private Sub UpdateRenderingSubscription(Optional forceDetach As Boolean = False)
            Dim shouldRenderLive = Not forceDetach AndAlso
                                   IsLoaded AndAlso
                                   HasLiveHead(GetRealtimeNowMilliseconds())

            If Not shouldRenderLive Then
                If _isRenderingSubscribed Then
                    RemoveHandler CompositionTarget.Rendering, AddressOf OnRendering
                    _isRenderingSubscribed = False
                End If

                _lastRenderingTimeMilliseconds = 0.0
                _frameDeltaMilliseconds = 0.0
                Return
            End If

            If Not _isRenderingSubscribed Then
                AddHandler CompositionTarget.Rendering, AddressOf OnRendering
                _isRenderingSubscribed = True
            End If
        End Sub

        Private Sub OnRendering(sender As Object, e As EventArgs)
            Dim renderingArgs = TryCast(e, RenderingEventArgs)
            If renderingArgs IsNot Nothing Then
                Dim renderingTimeMilliseconds = renderingArgs.RenderingTime.TotalMilliseconds
                If _lastRenderingTimeMilliseconds > 0.0 Then
                    _frameDeltaMilliseconds = Math.Max(0.0, Math.Min(100.0, renderingTimeMilliseconds - _lastRenderingTimeMilliseconds))
                Else
                    _frameDeltaMilliseconds = 0.0
                End If

                _lastRenderingTimeMilliseconds = renderingTimeMilliseconds
            Else
                _frameDeltaMilliseconds = 0.0
            End If

            If Not HasLiveHead(GetRealtimeNowMilliseconds()) Then
                UpdateRenderingSubscription(forceDetach:=True)
                InvalidateVisual()
                Return
            End If

            InvalidateVisual()
        End Sub

        Protected Overrides Sub OnRender(drawingContext As DrawingContext)
            MyBase.OnRender(drawingContext)

            If ActualWidth <= 0 OrElse ActualHeight <= 0 Then
                Return
            End If

            Dim realtimeNowMs = GetRealtimeNowMilliseconds()
            Dim nowMs = GetRenderNow(realtimeNowMs)
            Dim peakFromSamples = GetPeakRateFromSamples()
            Dim axisMax = AxisMaxFor(Math.Max(Math.Max(PeakRate, CurrentRate), peakFromSamples))
            Dim plotArea As New Rect(72.0, 22.0, Math.Max(10.0, ActualWidth - 96.0), Math.Max(10.0, ActualHeight - 62.0))

            EnsureStaticDecorCache(plotArea, axisMax)
            If _cachedStaticDecorDrawing IsNot Nothing Then
                drawingContext.DrawDrawing(_cachedStaticDecorDrawing)
            Else
                drawingContext.DrawRectangle(_backgroundBrush, Nothing, New Rect(0.0, 0.0, ActualWidth, ActualHeight))
                EnsureLabelCache(axisMax)
                DrawBackdropLabel(drawingContext, plotArea, axisMax)
                DrawGuides(drawingContext, plotArea, axisMax)
                DrawAxisLabels(drawingContext, plotArea, axisMax)
                DrawTimeLabels(drawingContext, plotArea)
            End If

            DrawCurve(drawingContext, plotArea, axisMax, nowMs, HasLiveHead(realtimeNowMs))
        End Sub

        Private Shared Function AxisMaxFor(peak As Double) As Double
            If peak <= 0 OrElse Double.IsNaN(peak) OrElse Double.IsInfinity(peak) Then
                Return 1000.0
            End If

            If peak <= 1200.0 Then
                Return 1000.0
            End If

            If peak <= 2600.0 Then
                Return 2000.0
            End If

            If peak <= 5200.0 Then
                Return 4000.0
            End If

            Return 8000.0
        End Function

        Private Sub DrawBackdropLabel(drawingContext As DrawingContext, plotArea As Rect, axisMax As Double)
            EnsureBackdropLabelCache(axisMax)
            drawingContext.DrawText(_cachedBackdropLabel, New Point(plotArea.Left + 12.0, plotArea.Top - 16.0))
        End Sub

        Private Sub DrawGuides(drawingContext As DrawingContext, plotArea As Rect, axisMax As Double)
            drawingContext.DrawLine(_axisPen, New Point(plotArea.Left, plotArea.Top), New Point(plotArea.Right, plotArea.Top))
            drawingContext.DrawLine(_axisPen, New Point(plotArea.Left, plotArea.Bottom), New Point(plotArea.Right, plotArea.Bottom))

            For Each tick In TickValues
                If tick > axisMax Then
                    Exit For
                End If

                Dim y = plotArea.Top + (1.0 - tick / axisMax) * plotArea.Height
                Dim pen = If(tick = 0.0 OrElse Math.Abs(tick - axisMax) < 0.1, _axisPen, _gridPen)
                drawingContext.DrawLine(pen, New Point(plotArea.Left, y), New Point(plotArea.Right, y))
            Next

            For column = 1 To 2
                Dim x = plotArea.Left + plotArea.Width * column / 3.0
                drawingContext.DrawLine(_gridStrongPen, New Point(x, plotArea.Top), New Point(x, plotArea.Bottom))
            Next

            drawingContext.DrawLine(_axisPen, New Point(plotArea.Right, plotArea.Top), New Point(plotArea.Right, plotArea.Bottom))
        End Sub

        Private Sub DrawAxisLabels(drawingContext As DrawingContext, plotArea As Rect, axisMax As Double)
            For index = 0 To _cachedLabelTicks.Length - 1
                Dim tick = _cachedLabelTicks(index)
                Dim label = _cachedLabels(index)
                Dim y = plotArea.Top + (1.0 - tick / axisMax) * plotArea.Height
                drawingContext.DrawText(label, New Point(8.0, y - label.Height / 2.0))
            Next
        End Sub

        Private Sub DrawTimeLabels(drawingContext As DrawingContext, plotArea As Rect)
            EnsureStaticLabelCache()

            Dim labels = {
                Tuple.Create(_cachedNegativeThreeSecondsLabel, plotArea.Left),
                Tuple.Create(_cachedNegativeTwoSecondsLabel, plotArea.Left + plotArea.Width / 3.0),
                Tuple.Create(_cachedNegativeOneSecondLabel, plotArea.Left + plotArea.Width * 2.0 / 3.0),
                Tuple.Create(_cachedNowLabel, plotArea.Right)
            }

            For Each labelInfo In labels
                Dim label = labelInfo.Item1
                Dim x = labelInfo.Item2
                If ReferenceEquals(label, _cachedNowLabel) Then
                    x -= label.Width
                End If

                drawingContext.DrawText(label, New Point(x, plotArea.Bottom + 10.0))
            Next
        End Sub

        Private Sub DrawCurve(drawingContext As DrawingContext,
                              plotArea As Rect,
                              axisMax As Double,
                              nowMs As Double,
                              isHeadLive As Boolean)
            If _samples.Count = 0 Then
                _hasSmoothedHeadRate = False
                Return
            End If

            EnsureCurveGeometryCache(plotArea, axisMax)
            If Not _hasCachedLatestStaticPoint Then
                _hasSmoothedHeadRate = False
                Return
            End If

            Dim latestStaticPoint = _cachedLatestStaticPoint
            Dim latestPoint = latestStaticPoint
            Dim shouldAppendPinnedHead = False

            If isHeadLive Then
                Dim pinnedHead = GetPinnedHeadPoint(plotArea, axisMax)
                shouldAppendPinnedHead = Math.Abs(pinnedHead.X - latestStaticPoint.X) > 0.5 OrElse
                                         Math.Abs(pinnedHead.Y - latestStaticPoint.Y) > 0.5
                latestPoint = pinnedHead
            Else
                _hasSmoothedHeadRate = False
            End If

            drawingContext.PushClip(New RectangleGeometry(CreateCurveClipRect(plotArea)))
            If isHeadLive Then
                drawingContext.DrawRectangle(_liveBandBrush, Nothing, New Rect(plotArea.Right - 16.0, plotArea.Top, 16.0, plotArea.Height))
            End If

            If _cachedAreaGeometry IsNot Nothing Then
                drawingContext.DrawGeometry(_areaFillBrush, Nothing, _cachedAreaGeometry)
            End If

            If _cachedCurveGeometry IsNot Nothing Then
                drawingContext.DrawGeometry(Nothing, _curveUnderlayPen, _cachedCurveGeometry)
                drawingContext.DrawGeometry(Nothing, _curvePen, _cachedCurveGeometry)
            End If

            If shouldAppendPinnedHead Then
                Dim liveTailAreaGeometry = BuildLiveTailAreaGeometry(latestStaticPoint, latestPoint, plotArea.Bottom)
                If liveTailAreaGeometry IsNot Nothing Then
                    drawingContext.DrawGeometry(_areaFillBrush, Nothing, liveTailAreaGeometry)
                End If

                Dim liveTailCurveGeometry = BuildLiveTailCurveGeometry(latestStaticPoint, latestPoint)
                If liveTailCurveGeometry IsNot Nothing Then
                    drawingContext.DrawGeometry(Nothing, _curveUnderlayPen, liveTailCurveGeometry)
                    drawingContext.DrawGeometry(Nothing, _curvePen, liveTailCurveGeometry)
                End If
            End If

            If isHeadLive Then
                drawingContext.DrawLine(_accentPen, latestPoint, New Point(latestPoint.X, plotArea.Bottom))
            End If
            drawingContext.DrawEllipse(_latestPointOuterBrush, _latestPointOuterPen, latestPoint, LatestPointOuterRadius, LatestPointOuterRadius)
            drawingContext.DrawEllipse(_latestPointFillBrush, Nothing, latestPoint, LatestPointInnerRadius, LatestPointInnerRadius)
            drawingContext.Pop()

            If isHeadLive Then
                EnsureStaticLabelCache()
                drawingContext.DrawText(_cachedLiveLabel, New Point(plotArea.Right - _cachedLiveLabel.Width, plotArea.Top - _cachedLiveLabel.Height - 4.0))
            End If
        End Sub

        Private Sub RebuildSampleCache()
            _samplePeakRate = 0.0

            Dim sourceList = TryCast(HistoryPoints, IReadOnlyList(Of PollingHistoryPoint))
            If sourceList Is Nothing Then
                _samples = Array.Empty(Of PollingHistoryPoint)()
            Else
                _samples = sourceList
                For index = 0 To _samples.Count - 1
                    Dim point = _samples(index)
                    If point Is Nothing Then
                        Continue For
                    End If

                    If point.Rate > _samplePeakRate Then
                        _samplePeakRate = point.Rate
                    End If
                Next
            End If

            If _samples.Count = 0 Then
                _hasSmoothedHeadRate = False
            End If

            InvalidateCurveGeometryCache()
            UpdateRenderingSubscription()
            InvalidateVisual()
        End Sub

        Private Sub EnsureLabelCache(axisMax As Double)
            Dim pixelsPerDip = VisualTreeHelper.GetDpi(Me).PixelsPerDip
            If axisMax = _cachedLabelAxisMax AndAlso Math.Abs(pixelsPerDip - _cachedPixelsPerDip) < 0.001 Then
                Return
            End If

            Dim ticks As New List(Of Double)()
            Dim labels As New List(Of FormattedText)()

            For Each tick In TickValues
                If tick > axisMax Then
                    Exit For
                End If

                ticks.Add(tick)
                Dim text = If(tick >= 1000.0,
                              (tick / 1000.0).ToString("0", CultureInfo.InvariantCulture) & "k",
                              tick.ToString("0", CultureInfo.InvariantCulture))
                Dim fontSize = If(tick = 0.0 OrElse Math.Abs(tick - axisMax) < 0.1, 14.0, 11.0)
                Dim brush = If(tick = 0.0 OrElse Math.Abs(tick - axisMax) < 0.1, _strongLabelBrush, _labelBrush)
                Dim typeface = If(tick = 0.0 OrElse Math.Abs(tick - axisMax) < 0.1, _strongTypeface, _smallTypeface)
                labels.Add(CreateText(text, fontSize, brush, typeface, pixelsPerDip))
            Next

            _cachedLabelAxisMax = axisMax
            _cachedPixelsPerDip = pixelsPerDip
            _cachedLabelTicks = ticks.ToArray()
            _cachedLabels = labels.ToArray()
        End Sub

        Private Sub EnsureBackdropLabelCache(axisMax As Double)
            Dim pixelsPerDip = VisualTreeHelper.GetDpi(Me).PixelsPerDip
            If _cachedBackdropLabel IsNot Nothing AndAlso
               axisMax = _cachedBackdropAxisMax AndAlso
               Math.Abs(pixelsPerDip - _cachedBackdropPixelsPerDip) < 0.001 Then
                Return
            End If

            Dim labelText = (axisMax / 1000.0).ToString("0", CultureInfo.InvariantCulture) & "k"
            _cachedBackdropAxisMax = axisMax
            _cachedBackdropPixelsPerDip = pixelsPerDip
            _cachedBackdropLabel = CreateText(labelText, 96.0, _backdropBrush, _strongTypeface, pixelsPerDip)
        End Sub

        Private Sub EnsureStaticLabelCache()
            Dim pixelsPerDip = VisualTreeHelper.GetDpi(Me).PixelsPerDip
            If _cachedNegativeThreeSecondsLabel IsNot Nothing AndAlso Math.Abs(pixelsPerDip - _cachedStaticTextPixelsPerDip) < 0.001 Then
                Return
            End If

            _cachedStaticTextPixelsPerDip = pixelsPerDip
            _cachedNegativeThreeSecondsLabel = CreateText("-3S", 11.0, _labelBrush, _smallTypeface, pixelsPerDip)
            _cachedNegativeTwoSecondsLabel = CreateText("-2S", 11.0, _labelBrush, _smallTypeface, pixelsPerDip)
            _cachedNegativeOneSecondLabel = CreateText("-1S", 11.0, _labelBrush, _smallTypeface, pixelsPerDip)
            _cachedNowLabel = CreateText("NOW", 11.0, _labelBrush, _smallTypeface, pixelsPerDip)
            _cachedLiveLabel = CreateText("LIVE", 11.0, _latestPointFillBrush, _smallTypeface, pixelsPerDip)
        End Sub

        Private Function GetPeakRateFromSamples() As Double
            Return _samplePeakRate
        End Function

        Private Sub EnsureStaticDecorCache(plotArea As Rect, axisMax As Double)
            Dim pixelsPerDip = VisualTreeHelper.GetDpi(Me).PixelsPerDip
            Dim renderSize As New Size(ActualWidth, ActualHeight)
            If Not _isStaticDecorCacheDirty AndAlso
               _cachedStaticDecorPlotArea = plotArea AndAlso
               _cachedStaticDecorRenderSize = renderSize AndAlso
               _cachedStaticDecorAxisMax = axisMax AndAlso
               Math.Abs(_cachedStaticDecorPixelsPerDip - pixelsPerDip) < 0.001 Then
                Return
            End If

            EnsureLabelCache(axisMax)
            EnsureBackdropLabelCache(axisMax)
            EnsureStaticLabelCache()

            Dim drawing As New DrawingGroup()
            Using context = drawing.Open()
                context.DrawRectangle(_backgroundBrush, Nothing, New Rect(0.0, 0.0, ActualWidth, ActualHeight))
                DrawBackdropLabel(context, plotArea, axisMax)
                DrawGuides(context, plotArea, axisMax)
                DrawAxisLabels(context, plotArea, axisMax)
                DrawTimeLabels(context, plotArea)
            End Using

            If drawing.CanFreeze Then
                drawing.Freeze()
            End If

            _cachedStaticDecorDrawing = drawing
            _cachedStaticDecorPlotArea = plotArea
            _cachedStaticDecorRenderSize = renderSize
            _cachedStaticDecorAxisMax = axisMax
            _cachedStaticDecorPixelsPerDip = pixelsPerDip
            _isStaticDecorCacheDirty = False
        End Sub

        Private Sub InvalidateStaticDecorCache()
            _cachedStaticDecorDrawing = Nothing
            _cachedStaticDecorPlotArea = Rect.Empty
            _cachedStaticDecorRenderSize = Size.Empty
            _cachedStaticDecorAxisMax = Double.NaN
            _cachedStaticDecorPixelsPerDip = Double.NaN
            _isStaticDecorCacheDirty = True
        End Sub

        Private Sub EnsureCurveGeometryCache(plotArea As Rect, axisMax As Double)
            Dim renderSize As New Size(ActualWidth, ActualHeight)
            If Not _isCurveGeometryCacheDirty AndAlso
               _cachedGeometryPlotArea = plotArea AndAlso
               _cachedGeometryRenderSize = renderSize AndAlso
               _cachedGeometryAxisMax = axisMax Then
                Return
            End If

            RebuildCurveGeometryCache(plotArea, axisMax, renderSize)
        End Sub

        Private Sub RebuildCurveGeometryCache(plotArea As Rect, axisMax As Double, renderSize As Size)
            _cachedAreaGeometry = Nothing
            _cachedCurveGeometry = Nothing
            _cachedGeometryPlotArea = plotArea
            _cachedGeometryRenderSize = renderSize
            _cachedGeometryAxisMax = axisMax
            _hasCachedLatestStaticPoint = False
            _isCurveGeometryCacheDirty = False

            If _samples.Count = 0 Then
                Return
            End If

            Dim latestSample = _samples(_samples.Count - 1)
            If latestSample Is Nothing Then
                Return
            End If

            Dim cutoff = latestSample.TimestampMs - HistoryMilliseconds
            Dim lastSampleBeforeCutoff As PollingHistoryPoint = Nothing
            Dim firstSampleAtOrAfterCutoff As PollingHistoryPoint = Nothing

            _visiblePoints.Clear()

            For index = 0 To _samples.Count - 1
                Dim sample = _samples(index)
                If sample Is Nothing Then
                    Continue For
                End If

                If sample.TimestampMs < cutoff Then
                    lastSampleBeforeCutoff = sample
                    Continue For
                End If

                If firstSampleAtOrAfterCutoff Is Nothing Then
                    firstSampleAtOrAfterCutoff = sample
                End If

                Dim x = plotArea.Left + ((sample.TimestampMs - cutoff) / HistoryMilliseconds) * plotArea.Width
                Dim y = GetYForRate(plotArea, axisMax, sample.Rate)
                _visiblePoints.Add(New Point(x, y))
            Next

            If _visiblePoints.Count = 0 Then
                Return
            End If

            Dim firstVisiblePoint = _visiblePoints(0)
            If firstVisiblePoint.X > plotArea.Left Then
                Dim leadingPoint As Point
                If lastSampleBeforeCutoff IsNot Nothing AndAlso firstSampleAtOrAfterCutoff IsNot Nothing Then
                    leadingPoint = CreateLeadingEdgePoint(plotArea,
                                                          axisMax,
                                                          cutoff,
                                                          lastSampleBeforeCutoff,
                                                          firstSampleAtOrAfterCutoff,
                                                          firstVisiblePoint)
                Else
                    leadingPoint = New Point(plotArea.Left, firstVisiblePoint.Y)
                End If

                If Math.Abs(firstVisiblePoint.X - leadingPoint.X) > 0.01 OrElse
                   Math.Abs(firstVisiblePoint.Y - leadingPoint.Y) > 0.01 Then
                    _visiblePoints.Insert(0, leadingPoint)
                Else
                    _visiblePoints(0) = leadingPoint
                End If
            ElseIf firstVisiblePoint.X < plotArea.Left Then
                _visiblePoints(0) = New Point(plotArea.Left, firstVisiblePoint.Y)
            End If

            _cachedLatestStaticPoint = _visiblePoints(_visiblePoints.Count - 1)
            _hasCachedLatestStaticPoint = True

            If _visiblePoints.Count >= 2 Then
                _cachedAreaGeometry = BuildAreaGeometry(_visiblePoints, plotArea.Bottom)
                _cachedCurveGeometry = BuildCurveGeometry(_visiblePoints)
            End If
        End Sub

        Private Sub InvalidateCurveGeometryCache()
            _cachedAreaGeometry = Nothing
            _cachedCurveGeometry = Nothing
            _cachedGeometryPlotArea = Rect.Empty
            _cachedGeometryRenderSize = Size.Empty
            _cachedGeometryAxisMax = Double.NaN
            _hasCachedLatestStaticPoint = False
            _isCurveGeometryCacheDirty = True
        End Sub

        Private Function GetRenderNow(realtimeNowMs As Double) As Double
            If _samples.Count = 0 Then
                Return realtimeNowMs
            End If

            Dim latestSample = _samples(_samples.Count - 1)
            If latestSample Is Nothing Then
                Return realtimeNowMs
            End If

            If Not IsLocked Then
                Return latestSample.TimestampMs
            End If

            If (realtimeNowMs - latestSample.RealtimeTimestampMs) > LiveHeadStaleMilliseconds Then
                Return latestSample.TimestampMs
            End If

            Return latestSample.TimestampMs + Math.Max(0.0, realtimeNowMs - latestSample.RealtimeTimestampMs)
        End Function

        Private Function HasLiveHead(nowMs As Double) As Boolean
            If Not IsLocked OrElse _samples.Count = 0 Then
                Return False
            End If

            Dim latestSample = _samples(_samples.Count - 1)
            If latestSample Is Nothing Then
                Return False
            End If

            Return (nowMs - latestSample.RealtimeTimestampMs) <= LiveHeadStaleMilliseconds
        End Function

        Private Shared Function GetRealtimeNowMilliseconds() As Double
            Return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency
        End Function

        Private Function GetPinnedHeadPoint(plotArea As Rect, axisMax As Double) As Point
            Dim targetRate = NormalizeRate(CurrentRate)
            Dim displayedRate = SmoothHeadRate(targetRate)
            Dim y = GetYForRate(plotArea, axisMax, displayedRate)
            Return New Point(plotArea.Right, y)
        End Function

        Private Function SmoothHeadRate(targetRate As Double) As Double
            If Not _hasSmoothedHeadRate Then
                _smoothedHeadRate = targetRate
                _hasSmoothedHeadRate = True
                Return _smoothedHeadRate
            End If

            If _frameDeltaMilliseconds <= 0.0 Then
                Return _smoothedHeadRate
            End If

            Dim alpha = 1.0 - Math.Exp(-_frameDeltaMilliseconds / HeadSmoothingTimeMilliseconds)
            _smoothedHeadRate += (targetRate - _smoothedHeadRate) * alpha

            If Math.Abs(_smoothedHeadRate - targetRate) < 0.01 Then
                _smoothedHeadRate = targetRate
            End If

            Return _smoothedHeadRate
        End Function

        Private Shared Function NormalizeRate(rate As Double) As Double
            If Double.IsNaN(rate) OrElse Double.IsInfinity(rate) OrElse rate < 0.0 Then
                Return 0.0
            End If

            Return rate
        End Function

        Private Shared Function GetYForRate(plotArea As Rect, axisMax As Double, rate As Double) As Double
            Dim normalized = Math.Max(0.0, Math.Min(1.0, NormalizeRate(rate) / axisMax))
            Return plotArea.Top + (1.0 - normalized) * plotArea.Height
        End Function

        Private Function CreateCurveClipRect(plotArea As Rect) As Rect
            Dim curveHalfThickness = Math.Max(_curveUnderlayPen.Thickness, _curvePen.Thickness) / 2.0
            Dim latestPointStrokeHalfThickness = _latestPointOuterPen.Thickness / 2.0
            Dim edgePadding = Math.Ceiling(Math.Max(curveHalfThickness, LatestPointOuterRadius + latestPointStrokeHalfThickness)) + 1.0

            Return New Rect(plotArea.Left - edgePadding,
                            plotArea.Top - edgePadding,
                            plotArea.Width + edgePadding * 2.0,
                            plotArea.Height + edgePadding * 2.0)
        End Function

        Private Function CreateLeadingEdgePoint(plotArea As Rect,
                                                axisMax As Double,
                                                cutoff As Double,
                                                lastSampleBeforeCutoff As PollingHistoryPoint,
                                                firstSampleAtOrAfterCutoff As PollingHistoryPoint,
                                                firstVisiblePoint As Point) As Point
            If lastSampleBeforeCutoff Is Nothing Then
                Return New Point(plotArea.Left, firstVisiblePoint.Y)
            End If

            If firstSampleAtOrAfterCutoff Is Nothing Then
                Return New Point(plotArea.Left, firstVisiblePoint.Y)
            End If

            Dim span = firstSampleAtOrAfterCutoff.TimestampMs - lastSampleBeforeCutoff.TimestampMs
            If span <= 0.0 Then
                Return New Point(plotArea.Left, firstVisiblePoint.Y)
            End If

            Dim t = Math.Max(0.0, Math.Min(1.0, (cutoff - lastSampleBeforeCutoff.TimestampMs) / span))
            Dim interpolatedRate = lastSampleBeforeCutoff.Rate + (firstSampleAtOrAfterCutoff.Rate - lastSampleBeforeCutoff.Rate) * t
            Return New Point(plotArea.Left, GetYForRate(plotArea, axisMax, interpolatedRate))
        End Function

        Private Shared Function BuildCurveGeometry(points As IList(Of Point)) As StreamGeometry
            If points Is Nothing OrElse points.Count < 2 Then
                Return Nothing
            End If

            Dim geometry As New StreamGeometry()
            Using context = geometry.Open()
                context.BeginFigure(points(0), False, False)
                For index = 0 To points.Count - 2
                    Dim p0 = points(index)
                    Dim p1 = points(index + 1)
                    Dim mid As New Point((p0.X + p1.X) / 2.0, (p0.Y + p1.Y) / 2.0)
                    context.QuadraticBezierTo(p0, mid, True, True)
                Next
                context.LineTo(points(points.Count - 1), True, True)
            End Using

            geometry.Freeze()
            Return geometry
        End Function

        Private Shared Function BuildAreaGeometry(points As IList(Of Point), baselineY As Double) As StreamGeometry
            If points Is Nothing OrElse points.Count < 2 Then
                Return Nothing
            End If

            Dim geometry As New StreamGeometry()
            Using context = geometry.Open()
                context.BeginFigure(New Point(points(0).X, baselineY), True, True)
                context.LineTo(points(0), True, True)

                For index = 0 To points.Count - 2
                    Dim p0 = points(index)
                    Dim p1 = points(index + 1)
                    Dim mid As New Point((p0.X + p1.X) / 2.0, (p0.Y + p1.Y) / 2.0)
                    context.QuadraticBezierTo(p0, mid, True, True)
                Next

                context.LineTo(points(points.Count - 1), True, True)
                context.LineTo(New Point(points(points.Count - 1).X, baselineY), True, True)
            End Using

            geometry.Freeze()
            Return geometry
        End Function

        Private Shared Function BuildLiveTailCurveGeometry(startPoint As Point, endPoint As Point) As StreamGeometry
            If Math.Abs(startPoint.X - endPoint.X) < 0.01 AndAlso Math.Abs(startPoint.Y - endPoint.Y) < 0.01 Then
                Return Nothing
            End If

            Dim midpoint As New Point((startPoint.X + endPoint.X) / 2.0, (startPoint.Y + endPoint.Y) / 2.0)
            Dim geometry As New StreamGeometry()
            Using context = geometry.Open()
                context.BeginFigure(startPoint, False, False)
                context.QuadraticBezierTo(startPoint, midpoint, True, True)
                context.LineTo(endPoint, True, True)
            End Using

            geometry.Freeze()
            Return geometry
        End Function

        Private Shared Function BuildLiveTailAreaGeometry(startPoint As Point, endPoint As Point, baselineY As Double) As StreamGeometry
            If Math.Abs(startPoint.X - endPoint.X) < 0.01 AndAlso Math.Abs(startPoint.Y - endPoint.Y) < 0.01 Then
                Return Nothing
            End If

            Dim midpoint As New Point((startPoint.X + endPoint.X) / 2.0, (startPoint.Y + endPoint.Y) / 2.0)
            Dim geometry As New StreamGeometry()
            Using context = geometry.Open()
                context.BeginFigure(New Point(startPoint.X, baselineY), True, True)
                context.LineTo(startPoint, True, True)
                context.QuadraticBezierTo(startPoint, midpoint, True, True)
                context.LineTo(endPoint, True, True)
                context.LineTo(New Point(endPoint.X, baselineY), True, True)
            End Using

            geometry.Freeze()
            Return geometry
        End Function

        Private Function CreateText(text As String, fontSize As Double, brush As Brush, typeface As Typeface) As FormattedText
            Return CreateText(text, fontSize, brush, typeface, VisualTreeHelper.GetDpi(Me).PixelsPerDip)
        End Function

        Private Shared Function CreateText(text As String, fontSize As Double, brush As Brush, typeface As Typeface, pixelsPerDip As Double) As FormattedText
            Return New FormattedText(text,
                                     CultureInfo.InvariantCulture,
                                     FlowDirection.LeftToRight,
                                     typeface,
                                     fontSize,
                                     brush,
                                     pixelsPerDip)
        End Function

        Private Shared Function ResolveFontFamily(resourceKey As String, fallback As FontFamily) As FontFamily
            Dim resource As Object = Nothing
            If Application.Current IsNot Nothing Then
                resource = Application.Current.TryFindResource(resourceKey)
            End If

            Dim family = TryCast(resource, FontFamily)
            If family IsNot Nothing Then
                Return family
            End If

            Return fallback
        End Function

        Private Shared Function CreateTypeface(fontFamily As FontFamily, fontWeight As FontWeight) As Typeface
            Return New Typeface(fontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal)
        End Function

        Private Shared Function ResolveBrush(resourceKey As String, fallbackColor As Color) As Brush
            Dim resource As Object = Nothing
            If Application.Current IsNot Nothing Then
                resource = Application.Current.TryFindResource(resourceKey)
            End If

            Dim brush = TryCast(resource, Brush)
            If brush IsNot Nothing Then
                Return CreateFrozenBrush(brush)
            End If

            Return CreateFrozenBrush(fallbackColor)
        End Function

        Private Shared Function ResolveColor(resourceKey As String, fallbackColor As Color) As Color
            Dim resource As Object = Nothing
            If Application.Current IsNot Nothing Then
                resource = Application.Current.TryFindResource(resourceKey)
            End If

            If TypeOf resource Is Color Then
                Return CType(resource, Color)
            End If

            Dim brush = TryCast(resource, SolidColorBrush)
            If brush IsNot Nothing Then
                Return brush.Color
            End If

            Return fallbackColor
        End Function

        Private Shared Function CreateCurvePen(color As Color, thickness As Double) As Pen
            Dim pen As New Pen(CreateFrozenBrush(color), thickness) With {
                .StartLineCap = PenLineCap.Round,
                .EndLineCap = PenLineCap.Round,
                .LineJoin = PenLineJoin.Round
            }
            pen.Freeze()
            Return pen
        End Function

        Private Shared Function CreateFrozenPen(color As Color, thickness As Double) As Pen
            Dim pen As New Pen(CreateFrozenBrush(color), thickness)
            pen.Freeze()
            Return pen
        End Function

        Private Shared Function CreateFrozenBrush(color As Color) As SolidColorBrush
            Dim brush As New SolidColorBrush(color)
            brush.Freeze()
            Return brush
        End Function

        Private Shared Function CreateFrozenBrush(source As Brush) As Brush
            If source Is Nothing Then
                Return CreateFrozenBrush(Colors.Transparent)
            End If

            Dim brush = source.CloneCurrentValue()
            If brush.CanFreeze Then
                brush.Freeze()
            End If

            Return brush
        End Function

        Private Shared Function CreateColor(alpha As Integer, red As Integer, green As Integer, blue As Integer) As Color
            Return Color.FromArgb(CByte(alpha), CByte(red), CByte(green), CByte(blue))
        End Function
    End Class
End Namespace
