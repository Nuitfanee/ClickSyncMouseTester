Imports System.Globalization
Imports System.IO
Imports System.Windows.Input
Imports System.Windows.Media.Imaging
Imports WpfApp1.Models
Imports WpfApp1.Services

Namespace Controls
    Public Class MousePerformanceChartControl
        Inherits FrameworkElement

        Private Const DefaultLeftMargin As Double = 78.0
        Private Const DefaultTopMargin As Double = 70.0
        Private Const DefaultRightMargin As Double = 28.0
        Private Const DefaultBottomMargin As Double = 72.0
        Private Const GridLineCount As Integer = 5
        Private Const MinorGridSubdivisionCount As Integer = 5
        Private Const AutomaticHorizontalGridThresholdMs As Double = 1.0
        Private Const MinorGridOpacityFactor As Double = 0.38
        Private Const MinorGridThickness As Double = 0.55
        Private Const ScatterRadius As Double = 1.55
        Private Const ZoomStepFactor As Double = 1.18
        Private Const MinimumViewportSpan As Double = 0.0001
        Private Const MaximumVisibleScatterSamples As Integer = 20000
        Private Const LineSamplesPerPixelFactor As Double = 3.0
        Private Const StemSamplesPerPixelFactor As Double = 0.65
        Private Const ScatterSamplesPerPixelFactor As Double = 0.55
        Private Const LineOpacityFactor As Double = 0.58
        Private Const StemOpacityFactor As Double = 0.16
        Private Const ScatterOpacityFactor As Double = 0.66
        Private Const LineThickness As Double = 1.05
        Private Const StemThickness As Double = 0.7
        Private Const MinimumLineCachePointBudget As Integer = 512
        Private Const LineCacheOverscanFactor As Double = 1.2
        Private Structure ChartViewport
            Public Sub New(xMinimum As Double,
                           xMaximum As Double,
                           yMinimum As Double,
                           yMaximum As Double)
                Me.XMinimum = xMinimum
                Me.XMaximum = xMaximum
                Me.YMinimum = yMinimum
                Me.YMaximum = yMaximum
            End Sub

            Public ReadOnly Property XMinimum As Double
            Public ReadOnly Property XMaximum As Double
            Public ReadOnly Property YMinimum As Double
            Public ReadOnly Property YMaximum As Double
        End Structure

        Private NotInheritable Class LineSeriesPointCache
            Private ReadOnly _plotType As MousePerformancePlotType
            Private ReadOnly _points As IReadOnlyList(Of MousePerformanceChartPoint)
            Private ReadOnly _pointsByBudget As Dictionary(Of Integer, IReadOnlyList(Of MousePerformanceChartPoint))

            Public Sub New(plotType As MousePerformancePlotType,
                           points As IReadOnlyList(Of MousePerformanceChartPoint))
                _plotType = plotType
                _points = If(points, Array.Empty(Of MousePerformanceChartPoint)())
                _pointsByBudget = New Dictionary(Of Integer, IReadOnlyList(Of MousePerformanceChartPoint))()
            End Sub

            Public Function ResolvePoints(targetBudget As Integer) As IReadOnlyList(Of MousePerformanceChartPoint)
                If _points.Count = 0 Then
                    Return Array.Empty(Of MousePerformanceChartPoint)()
                End If

                Dim normalizedBudget = MousePerformanceChartControl.NormalizeLinePointBudget(targetBudget, _points.Count)
                If normalizedBudget >= _points.Count Then
                    Return _points
                End If

                Dim cachedPoints As IReadOnlyList(Of MousePerformanceChartPoint) = Nothing
                If _pointsByBudget.TryGetValue(normalizedBudget, cachedPoints) Then
                    Return cachedPoints
                End If

                Dim reducedPoints As IReadOnlyList(Of MousePerformanceChartPoint)
                If _plotType = MousePerformancePlotType.XVsY Then
                    reducedPoints = MousePerformanceChartControl.SampleByStep(_points, normalizedBudget)
                Else
                    reducedPoints = MousePerformanceChartControl.ReduceCachedLinePoints(_points, normalizedBudget)
                End If

                _pointsByBudget(normalizedBudget) = reducedPoints
                Return reducedPoints
            End Function
        End Class

        Public Shared ReadOnly RenderFrameProperty As DependencyProperty =
            DependencyProperty.Register("RenderFrame",
                                        GetType(MousePerformanceChartRenderFrame),
                                        GetType(MousePerformanceChartControl),
                                        New FrameworkPropertyMetadata(Nothing,
                                                                      FrameworkPropertyMetadataOptions.AffectsRender,
                                                                      AddressOf OnRenderFrameChanged))

        Private _backgroundBrush As Brush
        Private _panelBrush As Brush
        Private _axisPen As Pen
        Private _gridPen As Pen
        Private _minorGridPen As Pen
        Private _primaryBrush As Brush
        Private _primaryPen As Pen
        Private _primaryStemPen As Pen
        Private _secondaryBrush As Brush
        Private _secondaryPen As Pen
        Private _secondaryStemPen As Pen
        Private _accentBrush As Brush
        Private _accentPen As Pen
        Private _accentStemPen As Pen
        Private _neutralBrush As Brush
        Private _neutralPen As Pen
        Private _neutralStemPen As Pen
        Private _labelBrush As Brush
        Private _strongLabelBrush As Brush
        Private _isThemeSubscribed As Boolean
        Private _viewXMinimum As Double
        Private _viewXMaximum As Double
        Private _viewYMinimum As Double
        Private _viewYMaximum As Double
        Private _hasCustomViewport As Boolean
        Private _isPanning As Boolean
        Private _panStartPoint As Point
        Private _panStartViewport As ChartViewport
        Private ReadOnly _lineSeriesPointCaches As New Dictionary(Of MousePerformanceChartSeries, LineSeriesPointCache)()
        Private _panPreviewBitmap As RenderTargetBitmap
        Private _panPreviewPlotArea As Rect = Rect.Empty
        Private _panPreviewOffset As Vector

        Public Sub New()
            SnapsToDevicePixels = True
            UseLayoutRounding = True
            Focusable = True
            ApplyThemeResources()
            AddHandler Loaded, AddressOf OnLoaded
            AddHandler Unloaded, AddressOf OnUnloaded
        End Sub

        Public Property RenderFrame As MousePerformanceChartRenderFrame
            Get
                Return CType(GetValue(RenderFrameProperty), MousePerformanceChartRenderFrame)
            End Get
            Set(value As MousePerformanceChartRenderFrame)
                SetValue(RenderFrameProperty, value)
            End Set
        End Property

        Public Sub ResetViewport()
            If RenderFrame Is Nothing OrElse Not RenderFrame.IsAvailable Then
                ClearViewportState()
                InvalidateVisual()
                Return
            End If

            ClearViewportState()
            InvalidateVisual()
        End Sub

        Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
            If Not _isThemeSubscribed Then
                AddHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = True
            End If

            ApplyThemeResources()
            InvalidateVisual()
        End Sub

        Private Sub OnUnloaded(sender As Object, e As RoutedEventArgs)
            If _isThemeSubscribed Then
                RemoveHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = False
            End If

            EndPanning()
            ResetLineSeriesPointCaches()
        End Sub

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            ApplyThemeResources()
            InvalidateVisual()
        End Sub

        Private Shared Sub OnRenderFrameChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            Dim control = TryCast(d, MousePerformanceChartControl)
            If control Is Nothing Then
                Return
            End If

            control.HandleRenderFrameChanged(TryCast(e.OldValue, MousePerformanceChartRenderFrame),
                                             TryCast(e.NewValue, MousePerformanceChartRenderFrame))
        End Sub

        Private Sub HandleRenderFrameChanged(previousFrame As MousePerformanceChartRenderFrame,
                                             nextFrame As MousePerformanceChartRenderFrame)
            EndPanning()
            ResetLineSeriesPointCaches()

            If nextFrame Is Nothing OrElse Not nextFrame.IsAvailable Then
                ClearViewportState()
                Return
            End If

            If Not _hasCustomViewport OrElse
               previousFrame Is Nothing OrElse
               Not previousFrame.IsAvailable OrElse
               previousFrame.PlotType <> nextFrame.PlotType Then
                ClearViewportState()
                Return
            End If

            ApplyViewport(nextFrame,
                          New ChartViewport(_viewXMinimum,
                                            _viewXMaximum,
                                            _viewYMinimum,
                                            _viewYMaximum))
        End Sub

        Protected Overrides Sub OnRender(drawingContext As DrawingContext)
            MyBase.OnRender(drawingContext)
            RenderCore(drawingContext, New Size(ActualWidth, ActualHeight), RenderFrame)
        End Sub

        Protected Overrides Sub OnMouseLeftButtonDown(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonDown(e)

            If e Is Nothing OrElse RenderFrame Is Nothing OrElse Not RenderFrame.IsAvailable Then
                Return
            End If

            Focus()

            Dim plotArea = GetPlotArea(New Size(ActualWidth, ActualHeight))
            Dim position = e.GetPosition(Me)
            If Not plotArea.Contains(position) Then
                Return
            End If

            If e.ClickCount >= 2 Then
                ResetViewport()
                e.Handled = True
                Return
            End If

            _isPanning = True
            _panStartPoint = position
            _panStartViewport = ResolveViewport(RenderFrame)
            CreatePanPreview(RenderFrame, plotArea, _panStartViewport)
            CaptureMouse()
            Cursor = Cursors.SizeAll
            e.Handled = True
        End Sub

        Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
            MyBase.OnMouseMove(e)

            If e Is Nothing OrElse Not _isPanning OrElse RenderFrame Is Nothing OrElse Not RenderFrame.IsAvailable Then
                Return
            End If

            Dim plotArea = GetPlotArea(New Size(ActualWidth, ActualHeight))
            If plotArea.Width <= 0.0 OrElse plotArea.Height <= 0.0 Then
                Return
            End If

            Dim position = e.GetPosition(Me)
            Dim nextViewport = BuildPannedViewport(RenderFrame, position, plotArea)
            Dim nextOffset = ResolvePanPreviewOffset(nextViewport, plotArea)

            If _panPreviewBitmap Is Nothing Then
                ApplyViewport(RenderFrame, nextViewport)
                InvalidateVisual()
            ElseIf Not AreClose(_panPreviewOffset.X, nextOffset.X) OrElse
                   Not AreClose(_panPreviewOffset.Y, nextOffset.Y) Then
                _panPreviewOffset = nextOffset
                InvalidateVisual()
            End If
            e.Handled = True
        End Sub

        Protected Overrides Sub OnMouseLeftButtonUp(e As MouseButtonEventArgs)
            MyBase.OnMouseLeftButtonUp(e)

            If Not _isPanning Then
                Return
            End If

            If RenderFrame IsNot Nothing AndAlso RenderFrame.IsAvailable Then
                Dim plotArea = GetPlotArea(New Size(ActualWidth, ActualHeight))
                If plotArea.Width > 0.0 AndAlso plotArea.Height > 0.0 Then
                    ApplyViewport(RenderFrame, BuildPannedViewport(RenderFrame, e.GetPosition(Me), plotArea))
                End If
            End If

            EndPanning()
            InvalidateVisual()
            e.Handled = True
        End Sub

        Protected Overrides Sub OnLostMouseCapture(e As MouseEventArgs)
            MyBase.OnLostMouseCapture(e)
            EndPanning()
            InvalidateVisual()
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseWheelEventArgs)
            MyBase.OnMouseWheel(e)

            If e Is Nothing OrElse RenderFrame Is Nothing OrElse Not RenderFrame.IsAvailable Then
                Return
            End If

            Dim plotArea = GetPlotArea(New Size(ActualWidth, ActualHeight))
            Dim position = e.GetPosition(Me)
            If Not plotArea.Contains(position) Then
                Return
            End If

            Dim viewport = ResolveViewport(RenderFrame)
            Dim modifiers = Keyboard.Modifiers
            Dim zoomX = True
            Dim zoomY = True
            Dim minimumNextXSpan = Double.NaN
            Dim maximumNextYSpan = Double.NaN
            Dim shiftPressed = (modifiers And ModifierKeys.Shift) = ModifierKeys.Shift
            Dim controlPressed = (modifiers And ModifierKeys.Control) = ModifierKeys.Control

            If shiftPressed AndAlso Not controlPressed Then
                zoomY = False
            ElseIf controlPressed AndAlso Not shiftPressed Then
                zoomX = False
            ElseIf Not shiftPressed AndAlso Not controlPressed Then
                ConfigureAutomaticWheelZoom(RenderFrame,
                                            viewport,
                                            e.Delta > 0,
                                            zoomX,
                                            zoomY,
                                            minimumNextXSpan,
                                            maximumNextYSpan)
            End If

            Dim zoomFactor = If(e.Delta > 0, 1.0 / ZoomStepFactor, ZoomStepFactor)
            Dim nextViewport = viewport

            If zoomX Then
                Dim anchorX = ScreenToDataX(plotArea, viewport, position.X)
                Dim width = viewport.XMaximum - viewport.XMinimum
                Dim nextWidth = Math.Max(GetMinimumViewportSpan(RenderFrame.XMaximum - RenderFrame.XMinimum),
                                         width * zoomFactor)
                If Not Double.IsNaN(minimumNextXSpan) Then
                    nextWidth = Math.Max(minimumNextXSpan, nextWidth)
                End If
                Dim anchorRatioX = (position.X - plotArea.Left) / plotArea.Width
                Dim nextXMinimum = anchorX - (anchorRatioX * nextWidth)
                nextViewport = New ChartViewport(nextXMinimum,
                                                 nextXMinimum + nextWidth,
                                                 nextViewport.YMinimum,
                                                 nextViewport.YMaximum)
            End If

            If zoomY Then
                Dim anchorY = ScreenToDataY(plotArea, viewport, position.Y)
                Dim height = viewport.YMaximum - viewport.YMinimum
                Dim nextHeight = Math.Max(GetMinimumViewportSpan(RenderFrame.YMaximum - RenderFrame.YMinimum),
                                          height * zoomFactor)
                If Not Double.IsNaN(maximumNextYSpan) Then
                    nextHeight = Math.Min(maximumNextYSpan, nextHeight)
                End If
                Dim anchorRatioY = (plotArea.Bottom - position.Y) / plotArea.Height
                Dim nextYMinimum = anchorY - (anchorRatioY * nextHeight)
                nextViewport = New ChartViewport(nextViewport.XMinimum,
                                                 nextViewport.XMaximum,
                                                 nextYMinimum,
                                                 nextYMinimum + nextHeight)
            End If

            ApplyViewport(RenderFrame, nextViewport)
            InvalidateVisual()
            e.Handled = True
        End Sub

        Public Sub ExportToPng(filePath As String, width As Integer, height As Integer)
            If String.IsNullOrWhiteSpace(filePath) OrElse width <= 0 OrElse height <= 0 Then
                Return
            End If

            Dim visual As New DrawingVisual()
            Using context = visual.RenderOpen()
                RenderCore(context, New Size(width, height), RenderFrame)
            End Using

            Dim bitmap As New RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32)
            bitmap.Render(visual)

            Dim encoder As New PngBitmapEncoder()
            encoder.Frames.Add(BitmapFrame.Create(bitmap))

            Using stream = File.Create(filePath)
                encoder.Save(stream)
            End Using
        End Sub

        Private Sub RenderCore(drawingContext As DrawingContext,
                               renderSize As Size,
                               frame As MousePerformanceChartRenderFrame)
            If drawingContext Is Nothing OrElse renderSize.Width <= 0 OrElse renderSize.Height <= 0 Then
                Return
            End If

            Dim bounds As New Rect(0.0, 0.0, renderSize.Width, renderSize.Height)
            drawingContext.DrawRectangle(_backgroundBrush, Nothing, bounds)

            Dim titleY = 18.0
            Dim titleTextValue = ResolveText(frame?.Title)
            Dim subtitleTextValue = ResolveText(frame?.Subtitle)
            Dim descriptionTextValue = ResolveText(frame?.Description)
            Dim titleOrigin = New Point(20.0, titleY)
            Dim titleText As FormattedText = Nothing
            If Not String.IsNullOrWhiteSpace(titleTextValue) Then
                titleText = CreateText(titleTextValue, 20.0, _strongLabelBrush, True)
            End If

            Dim subtitleText As FormattedText = Nothing
            If Not String.IsNullOrWhiteSpace(subtitleTextValue) Then
                subtitleText = CreateText(subtitleTextValue, 12.0, _labelBrush)
            End If

            Dim referenceBaselineY = titleOrigin.Y
            If titleText IsNot Nothing Then
                referenceBaselineY = titleOrigin.Y + titleText.Baseline
            ElseIf subtitleText IsNot Nothing Then
                referenceBaselineY = titleOrigin.Y + subtitleText.Baseline
            End If

            Dim subtitleOriginX = titleOrigin.X
            If titleText IsNot Nothing Then
                subtitleOriginX += titleText.Width + 14.0
            End If

            If Not String.IsNullOrWhiteSpace(titleTextValue) Then
                drawingContext.DrawText(titleText, titleOrigin)
            End If

            If subtitleText IsNot Nothing Then
                Dim subtitleY = referenceBaselineY - subtitleText.Baseline
                drawingContext.DrawText(subtitleText, New Point(subtitleOriginX, subtitleY))
            End If

            If Not String.IsNullOrWhiteSpace(descriptionTextValue) Then
                Dim leftEdge = titleOrigin.X
                If titleText IsNot Nothing Then
                    leftEdge += titleText.Width + 24.0
                End If

                If subtitleText IsNot Nothing Then
                    leftEdge = Math.Max(leftEdge, subtitleOriginX + subtitleText.Width + 24.0)
                End If

                Dim maximumDescriptionWidth = Math.Max(180.0, renderSize.Width - leftEdge - 24.0)
                Dim descriptionText = CreateText(descriptionTextValue,
                                                 11.0,
                                                 _labelBrush,
                                                 maxTextWidth:=maximumDescriptionWidth,
                                                 textAlignment:=TextAlignment.Left)
                Dim descriptionY = referenceBaselineY - descriptionText.Baseline
                drawingContext.DrawText(descriptionText, New Point(leftEdge, descriptionY))
            End If

            Dim plotArea = GetPlotArea(renderSize)
            drawingContext.DrawRectangle(_panelBrush, Nothing, plotArea)

            If frame Is Nothing OrElse Not frame.IsAvailable Then
                DrawUnavailableState(drawingContext, plotArea, frame)
                Return
            End If

            Dim viewport = ResolveViewport(frame)

            Dim yAxisTicks = ResolveYAxisTicks(viewport)

            DrawGrid(drawingContext, plotArea, viewport, yAxisTicks)
            DrawAxisLabels(drawingContext, plotArea, viewport, yAxisTicks)
            DrawAxisTitles(drawingContext, plotArea, frame)
            If Not TryDrawPanPreview(drawingContext, plotArea, frame) Then
                DrawSeries(drawingContext, plotArea, viewport, frame)
            End If
        End Sub

        Private Sub DrawUnavailableState(drawingContext As DrawingContext,
                                         plotArea As Rect,
                                         frame As MousePerformanceChartRenderFrame)
            drawingContext.DrawRectangle(Nothing, _axisPen, plotArea)

            Dim message = ResolveText(frame?.Message)
            If String.IsNullOrWhiteSpace(message) Then
                message = "No chart data."
            End If

            Dim text = CreateText(message, 15.0, _strongLabelBrush)
            drawingContext.DrawText(text,
                                    New Point(plotArea.Left + (plotArea.Width - text.Width) / 2.0,
                                              plotArea.Top + (plotArea.Height - text.Height) / 2.0))
        End Sub

        Private Sub DrawGrid(drawingContext As DrawingContext,
                             plotArea As Rect,
                             viewport As ChartViewport,
                             yAxisTicks As IReadOnlyList(Of Double))
            drawingContext.DrawRectangle(Nothing, _axisPen, plotArea)

            If MinorGridSubdivisionCount > 1 Then
                For index = 0 To GridLineCount - 1
                    For subdivision = 1 To MinorGridSubdivisionCount - 1
                        Dim offset = (index + subdivision / CDbl(MinorGridSubdivisionCount)) / GridLineCount
                        Dim x = plotArea.Left + plotArea.Width * offset
                        drawingContext.DrawLine(_minorGridPen, New Point(x, plotArea.Top), New Point(x, plotArea.Bottom))
                    Next
                Next

                For Each tick In ResolveMinorYAxisTicks(yAxisTicks)
                    Dim y = MapY(plotArea, viewport, tick)
                    drawingContext.DrawLine(_minorGridPen, New Point(plotArea.Left, y), New Point(plotArea.Right, y))
                Next
            End If

            For index = 0 To GridLineCount
                Dim x = plotArea.Left + plotArea.Width * index / GridLineCount
                drawingContext.DrawLine(_gridPen, New Point(x, plotArea.Top), New Point(x, plotArea.Bottom))
            Next

            For Each tick In yAxisTicks
                Dim y = MapY(plotArea, viewport, tick)
                If y >= plotArea.Top - 0.5 AndAlso y <= plotArea.Bottom + 0.5 Then
                    drawingContext.DrawLine(_gridPen, New Point(plotArea.Left, y), New Point(plotArea.Right, y))
                End If
            Next
        End Sub

        Private Shared Function ResolveYAxisTicks(viewport As ChartViewport) As IReadOnlyList(Of Double)
            Dim span = viewport.YMaximum - viewport.YMinimum
            If Double.IsNaN(span) OrElse Double.IsInfinity(span) OrElse span <= 0.0 Then
                Return Array.Empty(Of Double)()
            End If

            Dim stepSize = ResolveNiceAxisStep(span / Math.Max(1, GridLineCount))
            If Double.IsNaN(stepSize) OrElse Double.IsInfinity(stepSize) OrElse stepSize <= 0.0 Then
                Return Array.Empty(Of Double)()
            End If

            Dim epsilon = Math.Max(0.000001, stepSize * 0.0001)
            Dim firstTick = Math.Ceiling((viewport.YMinimum - epsilon) / stepSize) * stepSize
            Dim lastTick = Math.Floor((viewport.YMaximum + epsilon) / stepSize) * stepSize
            Dim ticks As New List(Of Double)()
            Dim tickValue = firstTick
            Dim guard = 0

            While tickValue <= lastTick + epsilon AndAlso guard < 256
                ticks.Add(If(Math.Abs(tickValue) <= epsilon, 0.0, tickValue))
                tickValue += stepSize
                guard += 1
            End While

            Return ticks
        End Function

        Private Shared Function ResolveMinorYAxisTicks(yAxisTicks As IReadOnlyList(Of Double)) As IReadOnlyList(Of Double)
            If yAxisTicks Is Nothing OrElse yAxisTicks.Count < 2 OrElse MinorGridSubdivisionCount <= 1 Then
                Return Array.Empty(Of Double)()
            End If

            Dim ticks As New List(Of Double)()
            For index = 0 To yAxisTicks.Count - 2
                Dim startTick = yAxisTicks(index)
                Dim endTick = yAxisTicks(index + 1)
                Dim span = endTick - startTick
                If Math.Abs(span) < 0.000001 Then
                    Continue For
                End If

                For subdivision = 1 To MinorGridSubdivisionCount - 1
                    ticks.Add(startTick + span * subdivision / CDbl(MinorGridSubdivisionCount))
                Next
            Next

            Return ticks
        End Function

        Private Shared Function ResolveNiceAxisStep(approximateStep As Double) As Double
            If Double.IsNaN(approximateStep) OrElse Double.IsInfinity(approximateStep) OrElse approximateStep <= 0.0 Then
                Return 1.0
            End If

            Dim magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(approximateStep)))
            Dim normalized = approximateStep / magnitude
            Dim stepScale As Double

            If normalized <= 1.0 Then
                stepScale = 1.0
            ElseIf normalized <= 2.0 Then
                stepScale = 2.0
            ElseIf normalized <= 2.5 Then
                stepScale = 2.5
            ElseIf normalized <= 5.0 Then
                stepScale = 5.0
            Else
                stepScale = 10.0
            End If

            Return stepScale * magnitude
        End Function

        Private Sub DrawAxisLabels(drawingContext As DrawingContext,
                                   plotArea As Rect,
                                   viewport As ChartViewport,
                                   yAxisTicks As IReadOnlyList(Of Double))
            For index = 0 To GridLineCount
                Dim xValue = viewport.XMinimum + (viewport.XMaximum - viewport.XMinimum) * index / GridLineCount

                Dim xLabel = CreateText(FormatAxisValue(xValue), 11.0, _labelBrush)

                Dim x = plotArea.Left + plotArea.Width * index / GridLineCount

                drawingContext.DrawText(xLabel,
                                        New Point(x - (xLabel.Width / 2.0),
                                                  plotArea.Bottom + 10.0))
            Next

            For Each tick In yAxisTicks
                Dim yLabel = CreateText(FormatAxisValue(tick), 11.0, _labelBrush)
                Dim y = MapY(plotArea, viewport, tick)
                drawingContext.DrawText(yLabel,
                                        New Point(plotArea.Left - yLabel.Width - 10.0,
                                                  y - (yLabel.Height / 2.0)))
            Next
        End Sub

        Private Sub DrawAxisTitles(drawingContext As DrawingContext,
                                   plotArea As Rect,
                                   frame As MousePerformanceChartRenderFrame)
            Dim xTitle = CreateText(ResolveText(frame.XAxisTitle), 12.0, _strongLabelBrush)
            drawingContext.DrawText(xTitle,
                                    New Point(plotArea.Left + (plotArea.Width - xTitle.Width) / 2.0,
                                              plotArea.Bottom + 34.0))

            Dim yTitleText = ResolveText(frame.YAxisTitle)
            If String.IsNullOrWhiteSpace(yTitleText) Then
                Return
            End If

            Dim yTitle = CreateText(yTitleText, 12.0, _strongLabelBrush)
            drawingContext.PushTransform(New RotateTransform(-90.0, 18.0, plotArea.Top + plotArea.Height / 2.0))
            drawingContext.DrawText(yTitle,
                                    New Point(18.0 - (yTitle.Width / 2.0),
                                              plotArea.Top + (plotArea.Height - yTitle.Height) / 2.0))
            drawingContext.Pop()
        End Sub

        Private Function TryDrawPanPreview(drawingContext As DrawingContext,
                                           plotArea As Rect,
                                           frame As MousePerformanceChartRenderFrame) As Boolean
            If Not _isPanning OrElse
               drawingContext Is Nothing OrElse
               frame Is Nothing OrElse
               Not frame.IsAvailable OrElse
               _panPreviewBitmap Is Nothing OrElse
               _panPreviewPlotArea.IsEmpty OrElse
               Not AreClose(plotArea.Width, _panPreviewPlotArea.Width) OrElse
               Not AreClose(plotArea.Height, _panPreviewPlotArea.Height) Then
                Return False
            End If

            drawingContext.PushClip(New RectangleGeometry(plotArea))
            drawingContext.DrawImage(_panPreviewBitmap,
                                     New Rect(plotArea.Left + _panPreviewOffset.X,
                                              plotArea.Top + _panPreviewOffset.Y,
                                              _panPreviewPlotArea.Width,
                                              _panPreviewPlotArea.Height))
            drawingContext.Pop()
            Return True
        End Function

        Private Sub DrawSeries(drawingContext As DrawingContext,
                               plotArea As Rect,
                               viewport As ChartViewport,
                               frame As MousePerformanceChartRenderFrame)
            If frame.Series Is Nothing OrElse frame.Series.Count = 0 Then
                Return
            End If

            Dim stems = frame.Series.Where(Function(item) item IsNot Nothing AndAlso item.Kind = MousePerformanceChartSeriesKind.Stem).ToList()
            Dim lines = frame.Series.Where(Function(item) item IsNot Nothing AndAlso item.Kind = MousePerformanceChartSeriesKind.Line).ToList()
            Dim scatters = frame.Series.Where(Function(item) item IsNot Nothing AndAlso item.Kind = MousePerformanceChartSeriesKind.Scatter).ToList()

            drawingContext.PushClip(New RectangleGeometry(plotArea))

            For Each series In stems
                DrawStemSeries(drawingContext, plotArea, viewport, frame.PlotType, series)
            Next

            For Each series In lines
                DrawLineSeries(drawingContext, plotArea, viewport, frame, series)
            Next

            For Each series In scatters
                DrawScatterSeries(drawingContext, plotArea, viewport, frame.PlotType, series)
            Next

            drawingContext.Pop()
        End Sub

        Private Sub DrawStemSeries(drawingContext As DrawingContext,
                                   plotArea As Rect,
                                   viewport As ChartViewport,
                                   plotType As MousePerformancePlotType,
                                   series As MousePerformanceChartSeries)
            Dim pen = ResolveSeriesPen(series.Palette, MousePerformanceChartSeriesKind.Stem)
            Dim baselineY = MapY(plotArea, viewport, 0.0)
            Dim points = ReducePointsForDisplay(series.Points,
                                                plotArea,
                                                viewport,
                                                plotType,
                                                MousePerformanceChartSeriesKind.Stem)

            For Each point In points
                Dim x = MapX(plotArea, viewport, point.X)
                Dim y = MapY(plotArea, viewport, point.Y)
                drawingContext.DrawLine(pen, New Point(x, baselineY), New Point(x, y))
            Next
        End Sub

        Private Sub DrawLineSeries(drawingContext As DrawingContext,
                                   plotArea As Rect,
                                   viewport As ChartViewport,
                                   frame As MousePerformanceChartRenderFrame,
                                   series As MousePerformanceChartSeries)
            If frame Is Nothing OrElse series Is Nothing Then
                Return
            End If

            Dim plotType = frame.PlotType
            Dim sourcePoints = ResolveLineSourcePoints(frame, plotArea, viewport, series)

            If plotType = MousePerformancePlotType.XVsY Then
                Dim trajectoryPath = MousePerformanceTrajectoryGeometryBuilder.BuildPath(
                    sourcePoints,
                    plotArea,
                    New MousePerformanceTrajectoryViewport(viewport.XMinimum,
                                                           viewport.XMaximum,
                                                           viewport.YMinimum,
                                                           viewport.YMaximum))
                If trajectoryPath Is Nothing OrElse trajectoryPath.Figures.Count = 0 Then
                    Return
                End If

                drawingContext.DrawGeometry(Nothing,
                                            ResolveSeriesPen(series.Palette, MousePerformanceChartSeriesKind.Line),
                                            MousePerformanceTrajectoryGeometryBuilder.BuildGeometry(trajectoryPath))
                Return
            End If

            Dim points = ReducePointsForDisplay(sourcePoints,
                                                plotArea,
                                                viewport,
                                                plotType,
                                                MousePerformanceChartSeriesKind.Line)
            If points Is Nothing OrElse points.Count < 2 Then
                Return
            End If

            Dim geometry As New StreamGeometry()
            Using context = geometry.Open()
                context.BeginFigure(ToScreenPoint(plotArea, viewport, points(0)), False, False)
                For index = 1 To points.Count - 1
                    context.LineTo(ToScreenPoint(plotArea, viewport, points(index)), True, True)
                Next
            End Using

            geometry.Freeze()
            drawingContext.DrawGeometry(Nothing,
                                        ResolveSeriesPen(series.Palette, MousePerformanceChartSeriesKind.Line),
                                        geometry)
        End Sub

        Private Sub DrawScatterSeries(drawingContext As DrawingContext,
                                      plotArea As Rect,
                                      viewport As ChartViewport,
                                      plotType As MousePerformancePlotType,
                                      series As MousePerformanceChartSeries)
            Dim brush = ResolveSeriesBrush(series.Palette)
            Dim points = ReducePointsForDisplay(series.Points,
                                                plotArea,
                                                viewport,
                                                plotType,
                                                MousePerformanceChartSeriesKind.Scatter)

            For Each point In points
                drawingContext.DrawEllipse(brush,
                                           Nothing,
                                           ToScreenPoint(plotArea, viewport, point),
                                           ScatterRadius,
                                           ScatterRadius)
            Next
        End Sub

        Private Function ReducePointsForDisplay(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                               plotArea As Rect,
                                               viewport As ChartViewport,
                                               plotType As MousePerformancePlotType,
                                               seriesKind As MousePerformanceChartSeriesKind) As IReadOnlyList(Of MousePerformanceChartPoint)
            If points Is Nothing OrElse points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            Dim visiblePoints = ExtractVisiblePoints(points, viewport, plotType, seriesKind = MousePerformanceChartSeriesKind.Line)
            If visiblePoints.Count = 0 Then
                Return visiblePoints
            End If

            Select Case seriesKind
                Case MousePerformanceChartSeriesKind.Line
                    Return ReduceLinePoints(visiblePoints, plotArea, viewport, plotType)
                Case MousePerformanceChartSeriesKind.Stem
                    Return ReduceStemPoints(visiblePoints, plotArea, viewport, plotType)
                Case MousePerformanceChartSeriesKind.Scatter
                    Return ReduceScatterPoints(visiblePoints, plotArea, plotType)
                Case Else
                    Return visiblePoints
            End Select
        End Function

        Private Function ResolveLineSourcePoints(frame As MousePerformanceChartRenderFrame,
                                                 plotArea As Rect,
                                                 viewport As ChartViewport,
                                                 series As MousePerformanceChartSeries) As IReadOnlyList(Of MousePerformanceChartPoint)
            If frame Is Nothing OrElse series Is Nothing OrElse series.Points Is Nothing OrElse series.Points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            Dim pointBudget = ResolveLineCachePointBudget(frame, plotArea, viewport, series)
            If pointBudget >= series.Points.Count Then
                Return series.Points
            End If

            Dim cache As LineSeriesPointCache = Nothing
            If Not _lineSeriesPointCaches.TryGetValue(series, cache) Then
                cache = New LineSeriesPointCache(frame.PlotType, series.Points)
                _lineSeriesPointCaches(series) = cache
            End If

            Return cache.ResolvePoints(pointBudget)
        End Function

        Private Shared Function ResolveLineCachePointBudget(frame As MousePerformanceChartRenderFrame,
                                                            plotArea As Rect,
                                                            viewport As ChartViewport,
                                                            series As MousePerformanceChartSeries) As Integer
            If frame Is Nothing OrElse series Is Nothing OrElse series.Points Is Nothing Then
                Return 0
            End If

            If series.Points.Count <= MinimumLineCachePointBudget Then
                Return series.Points.Count
            End If

            Dim desiredPointCount As Double

            If frame.PlotType = MousePerformancePlotType.XVsY Then
                Dim fullSpanX = Math.Max(MinimumViewportSpan, frame.XMaximum - frame.XMinimum)
                Dim fullSpanY = Math.Max(MinimumViewportSpan, frame.YMaximum - frame.YMinimum)
                Dim visibleSpanX = Math.Max(MinimumViewportSpan, viewport.XMaximum - viewport.XMinimum)
                Dim visibleSpanY = Math.Max(MinimumViewportSpan, viewport.YMaximum - viewport.YMinimum)
                Dim zoomRatio = Math.Max(fullSpanX / visibleSpanX, fullSpanY / visibleSpanY)
                Dim plotDiagonal = Math.Sqrt((plotArea.Width * plotArea.Width) + (plotArea.Height * plotArea.Height))
                desiredPointCount = Math.Max(1.0, plotDiagonal) * LineSamplesPerPixelFactor * zoomRatio * LineCacheOverscanFactor
            Else
                Dim fullSpanX = Math.Max(MinimumViewportSpan, frame.XMaximum - frame.XMinimum)
                Dim visibleSpanX = Math.Max(MinimumViewportSpan, viewport.XMaximum - viewport.XMinimum)
                Dim zoomRatio = fullSpanX / visibleSpanX
                desiredPointCount = Math.Max(1.0, plotArea.Width) * LineSamplesPerPixelFactor * zoomRatio * LineCacheOverscanFactor
            End If

            If Double.IsNaN(desiredPointCount) OrElse Double.IsInfinity(desiredPointCount) Then
                Return series.Points.Count
            End If

            Return NormalizeLinePointBudget(CInt(Math.Ceiling(Math.Min(Integer.MaxValue, desiredPointCount))),
                                            series.Points.Count)
        End Function

        Private Shared Function NormalizeLinePointBudget(desiredPointCount As Integer,
                                                         availablePoints As Integer) As Integer
            If availablePoints <= 0 Then
                Return 0
            End If

            If availablePoints <= MinimumLineCachePointBudget Then
                Return availablePoints
            End If

            Dim normalizedPointCount = MinimumLineCachePointBudget
            Dim clampedDesiredPointCount = Math.Max(MinimumLineCachePointBudget,
                                                    Math.Min(desiredPointCount, availablePoints))

            While normalizedPointCount < clampedDesiredPointCount AndAlso normalizedPointCount < availablePoints
                If normalizedPointCount > Integer.MaxValue \ 2 Then
                    normalizedPointCount = availablePoints
                    Exit While
                End If

                normalizedPointCount *= 2
            End While

            Return Math.Min(availablePoints, normalizedPointCount)
        End Function

        Private Function ExtractVisiblePoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                             viewport As ChartViewport,
                                             plotType As MousePerformancePlotType,
                                             includeNeighbors As Boolean) As IReadOnlyList(Of MousePerformanceChartPoint)
            If plotType = MousePerformancePlotType.XVsY Then
                Dim visible As New List(Of MousePerformanceChartPoint)()
                For Each point In points
                    If point.X >= viewport.XMinimum AndAlso point.X <= viewport.XMaximum AndAlso
                       point.Y >= viewport.YMinimum AndAlso point.Y <= viewport.YMaximum Then
                        visible.Add(point)
                    End If
                Next

                If visible.Count > 0 Then
                    Return visible
                End If

                Return points
            End If

            Dim startIndex = FindFirstIndexAtOrAfter(points, viewport.XMinimum)
            Dim endIndex = FindLastIndexAtOrBefore(points, viewport.XMaximum)

            If includeNeighbors Then
                startIndex = Math.Max(0, startIndex - 1)
                endIndex = Math.Min(points.Count - 1, endIndex + 1)
            End If

            If startIndex < 0 OrElse endIndex < startIndex Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            Dim visiblePoints As New List(Of MousePerformanceChartPoint)(endIndex - startIndex + 1)
            For index = startIndex To endIndex
                visiblePoints.Add(points(index))
            Next

            Return visiblePoints
        End Function

        Private Shared Function FindFirstIndexAtOrAfter(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                        minimumX As Double) As Integer
            Dim low = 0
            Dim high = points.Count - 1
            Dim result = points.Count

            While low <= high
                Dim mid = (low + high) \ 2
                If points(mid).X >= minimumX Then
                    result = mid
                    high = mid - 1
                Else
                    low = mid + 1
                End If
            End While

            If result >= points.Count Then
                Return points.Count - 1
            End If

            Return result
        End Function

        Private Shared Function FindLastIndexAtOrBefore(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                        maximumX As Double) As Integer
            Dim low = 0
            Dim high = points.Count - 1
            Dim result = -1

            While low <= high
                Dim mid = (low + high) \ 2
                If points(mid).X <= maximumX Then
                    result = mid
                    low = mid + 1
                Else
                    high = mid - 1
                End If
            End While

            Return result
        End Function

        Private Function ReduceLinePoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                          plotArea As Rect,
                                          viewport As ChartViewport,
                                          plotType As MousePerformancePlotType) As IReadOnlyList(Of MousePerformanceChartPoint)
            If points Is Nothing OrElse points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            Dim maximumPoints = Math.Max(64, CInt(Math.Ceiling(Math.Max(1.0, plotArea.Width) * LineSamplesPerPixelFactor)))
            If points.Count <= maximumPoints Then
                Return points
            End If

            If plotType = MousePerformancePlotType.XVsY Then
                Return SampleByStep(points, maximumPoints)
            End If

            Dim bucketCount = Math.Max(1, CInt(Math.Ceiling(Math.Max(1.0, plotArea.Width))))
            Dim reduced As New List(Of MousePerformanceChartPoint)(Math.Min(points.Count, bucketCount * 4))
            Dim viewportSpan = Math.Max(0.000001, viewport.XMaximum - viewport.XMinimum)
            Dim bucketStart = 0

            While bucketStart < points.Count
                Dim bucketIndex = GetBucketIndex(points(bucketStart).X, viewport.XMinimum, viewportSpan, bucketCount)
                Dim bucketEnd = bucketStart
                While bucketEnd + 1 < points.Count AndAlso
                      GetBucketIndex(points(bucketEnd + 1).X, viewport.XMinimum, viewportSpan, bucketCount) = bucketIndex
                    bucketEnd += 1
                End While

                AppendBucketExtremes(points, bucketStart, bucketEnd, reduced)
                bucketStart = bucketEnd + 1
            End While

            Return reduced
        End Function

        Private Shared Function ReduceCachedLinePoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                       targetPointCount As Integer) As IReadOnlyList(Of MousePerformanceChartPoint)
            If points Is Nothing OrElse points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            If targetPointCount <= 0 OrElse points.Count <= targetPointCount Then
                Return points
            End If

            Dim minimumX = points(0).X
            Dim maximumX = points(points.Count - 1).X
            Dim spanX = Math.Max(0.000001, maximumX - minimumX)
            Dim bucketCount = Math.Max(1, CInt(Math.Ceiling(targetPointCount / 4.0)))
            Dim reduced As New List(Of MousePerformanceChartPoint)(Math.Min(points.Count, bucketCount * 4))
            Dim bucketStart = 0

            While bucketStart < points.Count
                Dim bucketIndex = GetBucketIndex(points(bucketStart).X, minimumX, spanX, bucketCount)
                Dim bucketEnd = bucketStart
                While bucketEnd + 1 < points.Count AndAlso
                      GetBucketIndex(points(bucketEnd + 1).X, minimumX, spanX, bucketCount) = bucketIndex
                    bucketEnd += 1
                End While

                AppendBucketExtremes(points, bucketStart, bucketEnd, reduced)
                bucketStart = bucketEnd + 1
            End While

            Return reduced
        End Function

        Private Function ReduceStemPoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                          plotArea As Rect,
                                          viewport As ChartViewport,
                                          plotType As MousePerformancePlotType) As IReadOnlyList(Of MousePerformanceChartPoint)
            If points Is Nothing OrElse points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            Dim maximumPoints = Math.Max(48, CInt(Math.Ceiling(Math.Max(1.0, plotArea.Width) * StemSamplesPerPixelFactor)))
            If plotType = MousePerformancePlotType.XVsY Then
                Return SampleByStep(points, maximumPoints)
            End If

            Dim bucketCount = Math.Max(1, maximumPoints)
            Dim reduced As New List(Of MousePerformanceChartPoint)(Math.Min(points.Count, bucketCount * 2))
            Dim viewportSpan = Math.Max(0.000001, viewport.XMaximum - viewport.XMinimum)
            Dim bucketStart = 0

            While bucketStart < points.Count
                Dim bucketIndex = GetBucketIndex(points(bucketStart).X, viewport.XMinimum, viewportSpan, bucketCount)
                Dim bucketEnd = bucketStart
                While bucketEnd + 1 < points.Count AndAlso
                      GetBucketIndex(points(bucketEnd + 1).X, viewport.XMinimum, viewportSpan, bucketCount) = bucketIndex
                    bucketEnd += 1
                End While

                AppendStemBucketPoints(points, bucketStart, bucketEnd, reduced)
                bucketStart = bucketEnd + 1
            End While

            Return reduced
        End Function

        Private Shared Function GetBucketIndex(x As Double,
                                               minimumX As Double,
                                               spanX As Double,
                                               bucketCount As Integer) As Integer
            Dim normalized = (x - minimumX) / spanX
            Dim bucketIndex = CInt(Math.Floor(normalized * bucketCount))
            Return Math.Max(0, Math.Min(bucketCount - 1, bucketIndex))
        End Function

        Private Shared Sub AppendBucketExtremes(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                startIndex As Integer,
                                                endIndex As Integer,
                                                target As ICollection(Of MousePerformanceChartPoint))
            If startIndex > endIndex Then
                Return
            End If

            Dim firstIndex = startIndex
            Dim lastIndex = endIndex
            Dim minIndex = startIndex
            Dim maxIndex = startIndex

            For index = startIndex + 1 To endIndex
                If points(index).Y < points(minIndex).Y Then
                    minIndex = index
                End If

                If points(index).Y > points(maxIndex).Y Then
                    maxIndex = index
                End If
            Next

            Dim orderedIndexes = {firstIndex, minIndex, maxIndex, lastIndex}.
                Distinct().
                OrderBy(Function(index) index)

            For Each index In orderedIndexes
                target.Add(points(index))
            Next
        End Sub

        Private Shared Sub AppendStemBucketPoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                  startIndex As Integer,
                                                  endIndex As Integer,
                                                  target As ICollection(Of MousePerformanceChartPoint))
            If startIndex > endIndex Then
                Return
            End If

            Dim minIndex = startIndex
            Dim maxIndex = startIndex
            Dim furthestFromBaselineIndex = startIndex
            Dim hasNegative = points(startIndex).Y < 0.0
            Dim hasPositive = points(startIndex).Y > 0.0

            For index = startIndex + 1 To endIndex
                Dim value = points(index).Y
                If value < points(minIndex).Y Then
                    minIndex = index
                End If

                If value > points(maxIndex).Y Then
                    maxIndex = index
                End If

                If Math.Abs(value) > Math.Abs(points(furthestFromBaselineIndex).Y) Then
                    furthestFromBaselineIndex = index
                End If

                hasNegative = hasNegative OrElse value < 0.0
                hasPositive = hasPositive OrElse value > 0.0
            Next

            Dim orderedIndexes As IEnumerable(Of Integer)
            If hasNegative AndAlso hasPositive Then
                orderedIndexes = {minIndex, maxIndex}.
                    Distinct().
                    OrderBy(Function(index) index)
            Else
                orderedIndexes = {furthestFromBaselineIndex}.
                    Distinct().
                    OrderBy(Function(index) index)
            End If

            For Each index In orderedIndexes
                target.Add(points(index))
            Next
        End Sub

        Private Function ReduceScatterPoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                             plotArea As Rect,
                                             plotType As MousePerformancePlotType) As IReadOnlyList(Of MousePerformanceChartPoint)
            If points Is Nothing OrElse points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            Dim maximumPoints = Math.Min(MaximumVisibleScatterSamples,
                                         Math.Max(384, CInt(Math.Ceiling(Math.Max(1.0, plotArea.Width) * ScatterSamplesPerPixelFactor))))
            If plotType = MousePerformancePlotType.XVsY Then
                maximumPoints = Math.Min(MaximumVisibleScatterSamples,
                                         Math.Max(maximumPoints,
                                                  CInt(Math.Ceiling(Math.Max(1.0, plotArea.Height) * ScatterSamplesPerPixelFactor))))
                If points.Count <= maximumPoints Then
                    Return points
                End If

                Return SampleByStep(points, maximumPoints)
            End If

            Dim bucketCount = Math.Max(1, maximumPoints)
            If points.Count <= bucketCount Then
                Return points
            End If

            Dim reduced As New List(Of MousePerformanceChartPoint)(Math.Min(points.Count, bucketCount * 2))
            Dim minimumX = points(0).X
            Dim maximumX = points(points.Count - 1).X
            Dim spanX = Math.Max(0.000001, maximumX - minimumX)
            Dim bucketStart = 0

            While bucketStart < points.Count
                Dim bucketIndex = GetBucketIndex(points(bucketStart).X, minimumX, spanX, bucketCount)
                Dim bucketEnd = bucketStart
                While bucketEnd + 1 < points.Count AndAlso
                      GetBucketIndex(points(bucketEnd + 1).X, minimumX, spanX, bucketCount) = bucketIndex
                    bucketEnd += 1
                End While

                AppendScatterBucketPoints(points, bucketStart, bucketEnd, reduced)
                bucketStart = bucketEnd + 1
            End While

            Return reduced
        End Function

        Private Shared Sub AppendScatterBucketPoints(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                     startIndex As Integer,
                                                     endIndex As Integer,
                                                     target As ICollection(Of MousePerformanceChartPoint))
            If startIndex > endIndex Then
                Return
            End If

            Dim minIndex = startIndex
            Dim maxIndex = startIndex

            For index = startIndex + 1 To endIndex
                If points(index).Y < points(minIndex).Y Then
                    minIndex = index
                End If

                If points(index).Y > points(maxIndex).Y Then
                    maxIndex = index
                End If
            Next

            Dim orderedIndexes As IEnumerable(Of Integer)
            If AreClose(points(minIndex).Y, points(maxIndex).Y) Then
                orderedIndexes = {maxIndex}
            Else
                orderedIndexes = {minIndex, maxIndex}.
                    Distinct().
                    OrderBy(Function(index) index)
            End If

            For Each index In orderedIndexes
                target.Add(points(index))
            Next
        End Sub

        Private Shared Function SampleByStep(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                             maximumPoints As Integer) As IReadOnlyList(Of MousePerformanceChartPoint)
            If points Is Nothing OrElse points.Count = 0 Then
                Return Array.Empty(Of MousePerformanceChartPoint)()
            End If

            If maximumPoints <= 0 OrElse points.Count <= maximumPoints Then
                Return points
            End If

            Dim stepSize = Math.Max(1, CInt(Math.Ceiling(points.Count / CDbl(maximumPoints))))
            Dim reduced As New List(Of MousePerformanceChartPoint)(maximumPoints + 1)

            For index = 0 To points.Count - 1 Step stepSize
                reduced.Add(points(index))
            Next

            If Not ReferenceEquals(reduced(reduced.Count - 1), points(points.Count - 1)) Then
                reduced.Add(points(points.Count - 1))
            End If

            Return reduced
        End Function

        Private Function ToScreenPoint(plotArea As Rect,
                                       viewport As ChartViewport,
                                       point As MousePerformanceChartPoint) As Point
            Return New Point(MapX(plotArea, viewport, point.X),
                             MapY(plotArea, viewport, point.Y))
        End Function

        Private Shared Function MapX(plotArea As Rect,
                                     viewport As ChartViewport,
                                     value As Double) As Double
            If Math.Abs(viewport.XMaximum - viewport.XMinimum) < 0.000001 Then
                Return plotArea.Left
            End If

            Return plotArea.Left + ((value - viewport.XMinimum) / (viewport.XMaximum - viewport.XMinimum)) * plotArea.Width
        End Function

        Private Shared Function MapY(plotArea As Rect,
                                     viewport As ChartViewport,
                                     value As Double) As Double
            If Math.Abs(viewport.YMaximum - viewport.YMinimum) < 0.000001 Then
                Return plotArea.Bottom
            End If

            Return plotArea.Bottom - ((value - viewport.YMinimum) / (viewport.YMaximum - viewport.YMinimum)) * plotArea.Height
        End Function

        Private Shared Function ScreenToDataX(plotArea As Rect,
                                              viewport As ChartViewport,
                                              screenX As Double) As Double
            If plotArea.Width <= 0.0 Then
                Return viewport.XMinimum
            End If

            Return viewport.XMinimum + ((screenX - plotArea.Left) / plotArea.Width) * (viewport.XMaximum - viewport.XMinimum)
        End Function

        Private Shared Function ScreenToDataY(plotArea As Rect,
                                              viewport As ChartViewport,
                                              screenY As Double) As Double
            If plotArea.Height <= 0.0 Then
                Return viewport.YMinimum
            End If

            Return viewport.YMinimum + ((plotArea.Bottom - screenY) / plotArea.Height) * (viewport.YMaximum - viewport.YMinimum)
        End Function

        Private Sub ConfigureAutomaticWheelZoom(frame As MousePerformanceChartRenderFrame,
                                                viewport As ChartViewport,
                                                zoomIn As Boolean,
                                                ByRef zoomX As Boolean,
                                                ByRef zoomY As Boolean,
                                                ByRef minimumNextXSpan As Double,
                                                ByRef maximumNextYSpan As Double)
            If frame Is Nothing OrElse frame.PlotType = MousePerformancePlotType.XVsY OrElse GridLineCount <= 0 Then
                Return
            End If

            If zoomIn Then
                Dim horizontalGridStep = GetHorizontalGridStep(viewport)
                If horizontalGridStep > AutomaticHorizontalGridThresholdMs AndAlso
                   Not AreClose(horizontalGridStep, AutomaticHorizontalGridThresholdMs) Then
                    zoomX = True
                    zoomY = False
                    minimumNextXSpan = GetAutomaticHorizontalThresholdSpan()
                Else
                    zoomX = False
                    zoomY = True
                End If

                Return
            End If

            Dim automaticViewport = ResolveAutomaticViewport(frame)
            Dim currentHeight = viewport.YMaximum - viewport.YMinimum
            Dim automaticHeight = automaticViewport.YMaximum - automaticViewport.YMinimum

            If currentHeight < automaticHeight AndAlso Not AreClose(currentHeight, automaticHeight) Then
                zoomX = False
                zoomY = True
                maximumNextYSpan = automaticHeight
                Return
            End If

            zoomX = True
            zoomY = False
        End Sub

        Private Shared Function GetPlotArea(renderSize As Size) As Rect
            Return New Rect(DefaultLeftMargin,
                            DefaultTopMargin,
                            Math.Max(40.0, renderSize.Width - DefaultLeftMargin - DefaultRightMargin),
                            Math.Max(40.0, renderSize.Height - DefaultTopMargin - DefaultBottomMargin))
        End Function

        Private Function BuildPannedViewport(frame As MousePerformanceChartRenderFrame,
                                             position As Point,
                                             plotArea As Rect) As ChartViewport
            Dim deltaX = position.X - _panStartPoint.X
            Dim deltaY = position.Y - _panStartPoint.Y
            Dim viewportWidth = _panStartViewport.XMaximum - _panStartViewport.XMinimum
            Dim viewportHeight = _panStartViewport.YMaximum - _panStartViewport.YMinimum

            Dim xShift = -deltaX * viewportWidth / plotArea.Width
            Dim yShift = deltaY * viewportHeight / plotArea.Height

            Dim desiredViewport = New ChartViewport(_panStartViewport.XMinimum + xShift,
                                                    _panStartViewport.XMaximum + xShift,
                                                    _panStartViewport.YMinimum + yShift,
                                                    _panStartViewport.YMaximum + yShift)

            If frame Is Nothing OrElse Not frame.IsAvailable Then
                Return desiredViewport
            End If

            Return ClampViewport(frame, desiredViewport)
        End Function

        Private Function ResolvePanPreviewOffset(viewport As ChartViewport,
                                                 plotArea As Rect) As Vector
            Dim viewportWidth = Math.Max(MinimumViewportSpan, _panStartViewport.XMaximum - _panStartViewport.XMinimum)
            Dim viewportHeight = Math.Max(MinimumViewportSpan, _panStartViewport.YMaximum - _panStartViewport.YMinimum)
            Dim xShift = viewport.XMinimum - _panStartViewport.XMinimum
            Dim yShift = viewport.YMinimum - _panStartViewport.YMinimum

            Return New Vector(-xShift * plotArea.Width / viewportWidth,
                              yShift * plotArea.Height / viewportHeight)
        End Function

        Private Sub CreatePanPreview(frame As MousePerformanceChartRenderFrame,
                                     plotArea As Rect,
                                     viewport As ChartViewport)
            ClearPanPreview()

            If frame Is Nothing OrElse Not frame.IsAvailable OrElse
               plotArea.Width <= 0.0 OrElse plotArea.Height <= 0.0 Then
                Return
            End If

            Dim dpi = VisualTreeHelper.GetDpi(Me)
            Dim pixelWidth = Math.Max(1, CInt(Math.Ceiling(plotArea.Width * Math.Max(1.0, dpi.DpiScaleX))))
            Dim pixelHeight = Math.Max(1, CInt(Math.Ceiling(plotArea.Height * Math.Max(1.0, dpi.DpiScaleY))))
            Dim previewVisual As New DrawingVisual()
            Dim localPlotArea As New Rect(0.0, 0.0, plotArea.Width, plotArea.Height)

            Using context = previewVisual.RenderOpen()
                DrawSeries(context, localPlotArea, viewport, frame)
            End Using

            Dim bitmap As New RenderTargetBitmap(pixelWidth,
                                                 pixelHeight,
                                                 96.0 * Math.Max(1.0, dpi.DpiScaleX),
                                                 96.0 * Math.Max(1.0, dpi.DpiScaleY),
                                                 PixelFormats.Pbgra32)
            bitmap.Render(previewVisual)

            _panPreviewBitmap = bitmap
            _panPreviewPlotArea = plotArea
            _panPreviewOffset = New Vector()
        End Sub

        Private Sub ClearPanPreview()
            _panPreviewBitmap = Nothing
            _panPreviewPlotArea = Rect.Empty
            _panPreviewOffset = New Vector()
        End Sub

        Private Function ResolveViewport(frame As MousePerformanceChartRenderFrame) As ChartViewport
            If frame Is Nothing OrElse Not frame.IsAvailable Then
                Return New ChartViewport(0.0, 1.0, 0.0, 1.0)
            End If

            If Not _hasCustomViewport Then
                Return ResolveAutomaticViewport(frame)
            End If

            Return New ChartViewport(_viewXMinimum, _viewXMaximum, _viewYMinimum, _viewYMaximum)
        End Function

        Private Sub ApplyViewport(frame As MousePerformanceChartRenderFrame,
                                  viewport As ChartViewport)
            If frame Is Nothing OrElse Not frame.IsAvailable Then
                ClearViewportState()
                Return
            End If

            Dim clampedViewport = ClampViewport(frame, viewport)
            _viewXMinimum = clampedViewport.XMinimum
            _viewXMaximum = clampedViewport.XMaximum
            _viewYMinimum = clampedViewport.YMinimum
            _viewYMaximum = clampedViewport.YMaximum
            _hasCustomViewport = True
        End Sub

        Private Shared Function ClampViewport(frame As MousePerformanceChartRenderFrame,
                                              viewport As ChartViewport) As ChartViewport
            Dim fullXSpan = Math.Max(MinimumViewportSpan, frame.XMaximum - frame.XMinimum)
            Dim fullYSpan = Math.Max(MinimumViewportSpan, frame.YMaximum - frame.YMinimum)
            Dim width = Math.Max(GetMinimumViewportSpan(fullXSpan),
                                 Math.Min(viewport.XMaximum - viewport.XMinimum, fullXSpan))
            Dim height = Math.Max(GetMinimumViewportSpan(fullYSpan),
                                  Math.Min(viewport.YMaximum - viewport.YMinimum, fullYSpan))

            Dim xMinimum = frame.XMinimum
            If width < fullXSpan Then
                xMinimum = Math.Max(frame.XMinimum,
                                    Math.Min(viewport.XMinimum, frame.XMaximum - width))
            End If

            Dim yMinimum = frame.YMinimum
            If height < fullYSpan Then
                yMinimum = Math.Max(frame.YMinimum,
                                    Math.Min(viewport.YMinimum, frame.YMaximum - height))
            End If

            Return New ChartViewport(xMinimum,
                                     xMinimum + width,
                                     yMinimum,
                                     yMinimum + height)
        End Function

        Private Shared Function GetMinimumViewportSpan(fullSpan As Double) As Double
            Return Math.Max(MinimumViewportSpan, Math.Abs(fullSpan) * 0.0005)
        End Function

        Private Shared Function GetHorizontalGridStep(viewport As ChartViewport) As Double
            If GridLineCount <= 0 Then
                Return 0.0
            End If

            Return (viewport.XMaximum - viewport.XMinimum) / GridLineCount
        End Function

        Private Shared Function GetAutomaticHorizontalThresholdSpan() As Double
            Return AutomaticHorizontalGridThresholdMs * GridLineCount
        End Function

        Private Shared Function AreClose(left As Double, right As Double) As Boolean
            Return Math.Abs(left - right) <= 0.000001
        End Function

        Private Function ResolveAutomaticViewport(frame As MousePerformanceChartRenderFrame) As ChartViewport
            If frame Is Nothing OrElse Not frame.IsAvailable Then
                Return New ChartViewport(0.0, 1.0, 0.0, 1.0)
            End If

            Return New ChartViewport(frame.XMinimum,
                                     frame.XMaximum,
                                     frame.YMinimum,
                                     frame.YMaximum)
        End Function

        Private Sub ClearViewportState()
            _viewXMinimum = 0.0
            _viewXMaximum = 0.0
            _viewYMinimum = 0.0
            _viewYMaximum = 0.0
            _hasCustomViewport = False
            EndPanning()
        End Sub

        Private Sub ResetLineSeriesPointCaches()
            _lineSeriesPointCaches.Clear()
        End Sub

        Private Sub EndPanning()
            ClearPanPreview()

            If _isPanning Then
                _isPanning = False
                If IsMouseCaptured Then
                    ReleaseMouseCapture()
                End If
            End If

            ClearValue(CursorProperty)
        End Sub

        Private Sub DrawText(drawingContext As DrawingContext,
                             text As String,
                             fontSize As Double,
                             brush As Brush,
                             origin As Point,
                             Optional strong As Boolean = False)
            If String.IsNullOrWhiteSpace(text) Then
                Return
            End If

            drawingContext.DrawText(CreateText(text, fontSize, brush, strong), origin)
        End Sub

        Private Function CreateText(text As String,
                                    fontSize As Double,
                                    brush As Brush,
                                    Optional strong As Boolean = False,
                                    Optional maxTextWidth As Double = 0.0,
                                    Optional textAlignment As TextAlignment = TextAlignment.Left) As FormattedText
            Dim formattedText = New FormattedText(text,
                                                  CultureInfo.InvariantCulture,
                                                  FlowDirection.LeftToRight,
                                                  New Typeface(ResolveFontFamily(If(strong, "Font.DisplaySans", "Font.Body"),
                                                                                 New FontFamily("Segoe UI")),
                                                               FontStyles.Normal,
                                                               If(strong, FontWeights.SemiBold, FontWeights.Normal),
                                                               FontStretches.Normal),
                                                  fontSize,
                                                  brush,
                                                  VisualTreeHelper.GetDpi(Me).PixelsPerDip)

            If maxTextWidth > 0.0 Then
                formattedText.MaxTextWidth = maxTextWidth
                formattedText.Trimming = TextTrimming.CharacterEllipsis
            End If

            formattedText.TextAlignment = textAlignment
            Return formattedText
        End Function

        Private Sub ApplyThemeResources()
            Dim primaryColor = ResolveColor("ChartAccentColor", Color.FromRgb(&H58, &H8F, &HFF))
            Dim secondaryColor = Color.FromRgb(&HFF, &H5F, &H5F)
            Dim accentColor = ResolveColor("VoltGreenBrush", Color.FromRgb(&H9A, &HFF, &H79))
            Dim neutralColor = ResolveColor("TextStrongBrush", Color.FromRgb(&HE8, &HE8, &HE8))
            Dim lineGrayColor = BlendColors(ResolveColor("TextMutedBrush", Color.FromRgb(&H9B, &H9B, &HA1)),
                                            ResolveColor("TextStrongBrush", Color.FromRgb(&HF5, &HF5, &HF5)),
                                            0.18)

            _backgroundBrush = ResolveBrush("WindowBackgroundBrush", Color.FromRgb(&H8, &H8, &HA))
            _panelBrush = ResolveBrush("GlassShellBackgroundBrush", Color.FromRgb(&H10, &H10, &H12))
            _axisPen = CreatePen(ResolveColor("GlassShellBorderBrush", Color.FromRgb(&H52, &H52, &H57)), 1.0)
            _gridPen = CreatePen(ResolveColor("HairlineBrush", Color.FromRgb(&H34, &H34, &H38)), 0.8)
            _minorGridPen = CreatePen(ApplyOpacity(ResolveColor("HairlineBrush", Color.FromRgb(&H34, &H34, &H38)), MinorGridOpacityFactor),
                                       MinorGridThickness)
            _primaryBrush = CreateBrush(ApplyOpacity(primaryColor, ScatterOpacityFactor))
            _primaryPen = CreatePen(ApplyOpacity(lineGrayColor, LineOpacityFactor), LineThickness)
            _primaryStemPen = CreatePen(ApplyOpacity(lineGrayColor, StemOpacityFactor), StemThickness)
            _secondaryBrush = CreateBrush(ApplyOpacity(secondaryColor, ScatterOpacityFactor))
            _secondaryPen = CreatePen(ApplyOpacity(lineGrayColor, LineOpacityFactor), LineThickness)
            _secondaryStemPen = CreatePen(ApplyOpacity(lineGrayColor, StemOpacityFactor), StemThickness)
            _accentBrush = CreateBrush(ApplyOpacity(accentColor, ScatterOpacityFactor))
            _accentPen = CreatePen(ApplyOpacity(lineGrayColor, LineOpacityFactor), LineThickness)
            _accentStemPen = CreatePen(ApplyOpacity(lineGrayColor, StemOpacityFactor), StemThickness)
            _neutralBrush = CreateBrush(ApplyOpacity(neutralColor, ScatterOpacityFactor))
            _neutralPen = CreatePen(ApplyOpacity(lineGrayColor, LineOpacityFactor), LineThickness)
            _neutralStemPen = CreatePen(ApplyOpacity(lineGrayColor, StemOpacityFactor), StemThickness)
            _labelBrush = ResolveBrush("TextMutedBrush", Color.FromRgb(&H9B, &H9B, &HA1))
            _strongLabelBrush = ResolveBrush("TextStrongBrush", Color.FromRgb(&HF5, &HF5, &HF5))
        End Sub

        Private Function ResolveSeriesBrush(palette As MousePerformanceChartSeriesPalette) As Brush
            Select Case palette
                Case MousePerformanceChartSeriesPalette.Primary
                    Return _primaryBrush
                Case MousePerformanceChartSeriesPalette.Secondary
                    Return _secondaryBrush
                Case MousePerformanceChartSeriesPalette.Accent
                    Return _accentBrush
                Case Else
                    Return _neutralBrush
            End Select
        End Function

        Private Function ResolveSeriesPen(palette As MousePerformanceChartSeriesPalette,
                                          seriesKind As MousePerformanceChartSeriesKind) As Pen
            Select Case palette
                Case MousePerformanceChartSeriesPalette.Primary
                    Return If(seriesKind = MousePerformanceChartSeriesKind.Stem, _primaryStemPen, _primaryPen)
                Case MousePerformanceChartSeriesPalette.Secondary
                    Return If(seriesKind = MousePerformanceChartSeriesKind.Stem, _secondaryStemPen, _secondaryPen)
                Case MousePerformanceChartSeriesPalette.Accent
                    Return If(seriesKind = MousePerformanceChartSeriesKind.Stem, _accentStemPen, _accentPen)
                Case Else
                    Return If(seriesKind = MousePerformanceChartSeriesKind.Stem, _neutralStemPen, _neutralPen)
            End Select
        End Function

        Private Shared Function ResolveText(text As String) As String
            Return If(text, String.Empty)
        End Function

        Private Shared Function FormatAxisValue(value As Double) As String
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then
                Return "--"
            End If

            If Math.Abs(value) <= 0.000001 Then
                Return "0"
            End If

            Dim absValue = Math.Abs(value)
            If absValue >= 1000.0 Then
                Return value.ToString("0", CultureInfo.InvariantCulture)
            End If

            If absValue >= 10.0 Then
                Return value.ToString("0.0", CultureInfo.InvariantCulture)
            End If

            Return value.ToString("0.00", CultureInfo.InvariantCulture)
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

        Private Shared Function ResolveBrush(resourceKey As String, fallback As Color) As Brush
            Dim resource As Object = Nothing
            If Application.Current IsNot Nothing Then
                resource = Application.Current.TryFindResource(resourceKey)
            End If

            Dim brush = TryCast(resource, Brush)
            If brush IsNot Nothing Then
                Return FreezeBrush(brush)
            End If

            Return CreateBrush(fallback)
        End Function

        Private Shared Function ResolveColor(resourceKey As String, fallback As Color) As Color
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

            Return fallback
        End Function

        Private Shared Function CreateBrush(color As Color) As Brush
            Dim brush As New SolidColorBrush(color)
            brush.Freeze()
            Return brush
        End Function

        Private Shared Function FreezeBrush(source As Brush) As Brush
            Dim brush = source.CloneCurrentValue()
            If brush.CanFreeze Then
                brush.Freeze()
            End If

            Return brush
        End Function

        Private Shared Function ApplyOpacity(color As Color, opacityFactor As Double) As Color
            Dim clampedOpacity = Math.Max(0.0, Math.Min(1.0, opacityFactor))
            Return Color.FromArgb(CByte(Math.Round(color.A * clampedOpacity)),
                                  color.R,
                                  color.G,
                                  color.B)
        End Function

        Private Shared Function BlendColors(baseColor As Color,
                                            targetColor As Color,
                                            blendFactor As Double) As Color
            Dim clampedBlendFactor = Math.Max(0.0, Math.Min(1.0, blendFactor))
            Dim inverseBlendFactor = 1.0 - clampedBlendFactor

            Return Color.FromArgb(baseColor.A,
                                  CByte(Math.Round((baseColor.R * inverseBlendFactor) + (targetColor.R * clampedBlendFactor))),
                                  CByte(Math.Round((baseColor.G * inverseBlendFactor) + (targetColor.G * clampedBlendFactor))),
                                  CByte(Math.Round((baseColor.B * inverseBlendFactor) + (targetColor.B * clampedBlendFactor))))
        End Function

        Private Shared Function CreatePen(color As Color, thickness As Double) As Pen
            Dim pen As New Pen(CreateBrush(color), thickness) With {
                .LineJoin = PenLineJoin.Round,
                .StartLineCap = PenLineCap.Round,
                .EndLineCap = PenLineCap.Round
            }
            pen.Freeze()
            Return pen
        End Function
    End Class
End Namespace
