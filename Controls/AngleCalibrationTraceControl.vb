Imports System.ComponentModel
Imports System.Windows.Media
Imports WpfApp1.Models
Imports WpfApp1.Services

Namespace Controls
    Public Class AngleCalibrationTraceControl
        Inherits FrameworkElement

        Private Const ViewPadding As Double = 26.0
        Private Const MinimumScale As Double = 0.08
        Private Const MaximumScale As Double = 1.8
        Private Const HistoryStrokeOpacity As Double = 0.22
        Private Const CurrentStrokeOpacity As Double = 0.55
        Private Const StrokeThickness As Double = 1.65

        Public Shared ReadOnly RenderFrameProperty As DependencyProperty =
            DependencyProperty.Register("RenderFrame",
                                        GetType(AngleCalibrationRenderFrame),
                                        GetType(AngleCalibrationTraceControl),
                                        New FrameworkPropertyMetadata(Nothing,
                                                                      FrameworkPropertyMetadataOptions.AffectsRender,
                                                                      AddressOf OnRenderFrameChanged))

        Private _historyPen As Pen
        Private _currentPen As Pen
        Private _targetCenterX As Double
        Private _targetCenterY As Double
        Private _targetScale As Double = 1.0
        Private _currentCenterX As Double
        Private _currentCenterY As Double
        Private _currentScale As Double = 1.0
        Private _hasViewState As Boolean
        Private _pendingAnimationFrames As Integer
        Private _isRenderingSubscribed As Boolean
        Private _isThemeSubscribed As Boolean

        Public Sub New()
            SnapsToDevicePixels = True
            UseLayoutRounding = True
            ApplyThemeResources()
            AddHandler Loaded, AddressOf OnLoaded
            AddHandler Unloaded, AddressOf OnUnloaded
        End Sub

        Public Property RenderFrame As AngleCalibrationRenderFrame
            Get
                Return CType(GetValue(RenderFrameProperty), AngleCalibrationRenderFrame)
            End Get
            Set(value As AngleCalibrationRenderFrame)
                SetValue(RenderFrameProperty, value)
            End Set
        End Property

        Private Shared Sub OnRenderFrameChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            CType(d, AngleCalibrationTraceControl).HandleRenderFrameChanged()
        End Sub

        Private Sub HandleRenderFrameChanged()
            RecalculateTargetView()

            If Not _hasViewState Then
                _currentCenterX = _targetCenterX
                _currentCenterY = _targetCenterY
                _currentScale = _targetScale
                _hasViewState = True
            End If

            _pendingAnimationFrames = 10
            UpdateRenderingSubscription()
            InvalidateVisual()
        End Sub

        Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
            If Not _isThemeSubscribed Then
                AddHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = True
            End If

            ApplyThemeResources()
            RecalculateTargetView()
            UpdateRenderingSubscription()
        End Sub

        Private Sub OnUnloaded(sender As Object, e As RoutedEventArgs)
            If _isThemeSubscribed Then
                RemoveHandler ThemeManager.Instance.ThemeChanged, AddressOf OnThemeChanged
                _isThemeSubscribed = False
            End If

            UpdateRenderingSubscription(forceDetach:=True)
        End Sub

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            ApplyThemeResources()
            InvalidateVisual()
        End Sub

        Private Sub UpdateRenderingSubscription(Optional forceDetach As Boolean = False)
            Dim shouldRender = Not forceDetach AndAlso IsLoaded AndAlso (_pendingAnimationFrames > 0 OrElse (RenderFrame IsNot Nothing AndAlso RenderFrame.IsLocked))

            If Not shouldRender Then
                If _isRenderingSubscribed Then
                    RemoveHandler CompositionTarget.Rendering, AddressOf OnRendering
                    _isRenderingSubscribed = False
                End If

                Return
            End If

            If Not _isRenderingSubscribed Then
                AddHandler CompositionTarget.Rendering, AddressOf OnRendering
                _isRenderingSubscribed = True
            End If
        End Sub

        Private Sub OnRendering(sender As Object, e As EventArgs)
            If Not _hasViewState Then
                UpdateRenderingSubscription(forceDetach:=True)
                Return
            End If

            Dim alpha = If(RenderFrame IsNot Nothing AndAlso RenderFrame.IsLocked, 0.18, 0.24)
            _currentCenterX += (_targetCenterX - _currentCenterX) * alpha
            _currentCenterY += (_targetCenterY - _currentCenterY) * alpha
            _currentScale += (_targetScale - _currentScale) * alpha

            If Math.Abs(_targetCenterX - _currentCenterX) < 0.15 AndAlso
               Math.Abs(_targetCenterY - _currentCenterY) < 0.15 AndAlso
               Math.Abs(_targetScale - _currentScale) < 0.002 Then
                _currentCenterX = _targetCenterX
                _currentCenterY = _targetCenterY
                _currentScale = _targetScale
                _pendingAnimationFrames = Math.Max(0, _pendingAnimationFrames - 1)
            Else
                _pendingAnimationFrames = Math.Max(_pendingAnimationFrames, 2)
            End If

            InvalidateVisual()
            UpdateRenderingSubscription()
        End Sub

        Protected Overrides Sub OnRender(drawingContext As DrawingContext)
            MyBase.OnRender(drawingContext)

            Dim renderFrameValue = RenderFrame
            If renderFrameValue Is Nothing OrElse ActualWidth <= 0 OrElse ActualHeight <= 0 Then
                Return
            End If

            If Not _hasViewState Then
                RecalculateTargetView()
                _currentCenterX = _targetCenterX
                _currentCenterY = _targetCenterY
                _currentScale = _targetScale
                _hasViewState = True
            End If

            drawingContext.PushClip(New RectangleGeometry(New Rect(0.0, 0.0, ActualWidth, ActualHeight)))

            For Each stroke In renderFrameValue.TraceStrokes
                DrawStroke(drawingContext, stroke)
            Next

            drawingContext.Pop()
        End Sub

        Private Sub DrawStroke(drawingContext As DrawingContext, stroke As AngleCalibrationTraceStroke)
            If stroke Is Nothing OrElse stroke.Points Is Nothing OrElse stroke.Points.Count < 2 Then
                Return
            End If

            Dim geometry As New StreamGeometry()
            Using context = geometry.Open()
                Dim firstPoint = ToScreenPoint(stroke.Points(0))
                context.BeginFigure(firstPoint, False, False)

                For pointIndex = 1 To stroke.Points.Count - 1
                    context.LineTo(ToScreenPoint(stroke.Points(pointIndex)), True, True)
                Next
            End Using

            geometry.Freeze()

            Dim pen = If(stroke.IsCurrent, _currentPen, _historyPen)
            drawingContext.PushOpacity(If(stroke.IsCurrent, CurrentStrokeOpacity, HistoryStrokeOpacity))
            drawingContext.DrawGeometry(Nothing, pen, geometry)
            drawingContext.Pop()
        End Sub

        Private Function ToScreenPoint(point As AngleCalibrationTracePoint) As Point
            Dim x = ((point.X - _currentCenterX) * _currentScale) + (ActualWidth / 2.0)
            Dim y = ((point.Y - _currentCenterY) * _currentScale) + (ActualHeight / 2.0)
            Return New Point(x, y)
        End Function

        Private Sub RecalculateTargetView()
            Dim minX = -200.0
            Dim maxX = 200.0
            Dim minY = -80.0
            Dim maxY = 80.0
            Dim hasPoint = False

            Dim renderFrameValue = RenderFrame
            If renderFrameValue IsNot Nothing AndAlso renderFrameValue.TraceStrokes IsNot Nothing Then
                For Each stroke In renderFrameValue.TraceStrokes
                    If stroke Is Nothing OrElse stroke.Points Is Nothing Then
                        Continue For
                    End If

                    For Each point In stroke.Points
                        If Not hasPoint Then
                            minX = point.X
                            maxX = point.X
                            minY = point.Y
                            maxY = point.Y
                            hasPoint = True
                        Else
                            minX = Math.Min(minX, point.X)
                            maxX = Math.Max(maxX, point.X)
                            minY = Math.Min(minY, point.Y)
                            maxY = Math.Max(maxY, point.Y)
                        End If
                    Next
                Next
            End If

            Dim spanX = Math.Max(1.0, maxX - minX)
            Dim spanY = Math.Max(1.0, maxY - minY)
            Dim availableWidth = Math.Max(32.0, ActualWidth - (ViewPadding * 2.0))
            Dim availableHeight = Math.Max(32.0, ActualHeight - (ViewPadding * 2.0))
            Dim fitScale = Math.Min(availableWidth / spanX, availableHeight / spanY)

            _targetScale = Math.Max(MinimumScale, Math.Min(MaximumScale, fitScale))
            _targetCenterX = (minX + maxX) / 2.0
            _targetCenterY = (minY + maxY) / 2.0
        End Sub

        Private Sub ApplyThemeResources()
            _historyPen = CreatePen(ResolveColor("TextMutedColor", Color.FromRgb(&H98, &H98, &H98)))
            _currentPen = CreatePen(ResolveColor("TextStrongColor", Color.FromRgb(&HC8, &HC8, &HC8)))
        End Sub

        Private Shared Function CreatePen(color As Color) As Pen
            Dim pen As New Pen(New SolidColorBrush(color), StrokeThickness) With {
                .StartLineCap = PenLineCap.Round,
                .EndLineCap = PenLineCap.Round,
                .LineJoin = PenLineJoin.Round
            }

            If pen.Brush.CanFreeze Then
                pen.Brush.Freeze()
            End If

            pen.Freeze()
            Return pen
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
    End Class
End Namespace
