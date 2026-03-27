Imports WpfApp1.Models

Namespace Controls
    Friend Structure MousePerformanceTrajectoryViewport
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

    Friend NotInheritable Class MousePerformanceTrajectoryFigure
        Public Sub New(points As IReadOnlyList(Of Point))
            Me.Points = If(points, Array.Empty(Of Point)())
        End Sub

        Public ReadOnly Property Points As IReadOnlyList(Of Point)
    End Class

    Friend NotInheritable Class MousePerformanceTrajectoryPath
        Public Sub New(figures As IReadOnlyList(Of MousePerformanceTrajectoryFigure))
            Me.Figures = If(figures, Array.Empty(Of MousePerformanceTrajectoryFigure)())
        End Sub

        Public ReadOnly Property Figures As IReadOnlyList(Of MousePerformanceTrajectoryFigure)
    End Class

    Friend NotInheritable Class MousePerformanceTrajectoryGeometryBuilder
        Private Const ScreenContinuityEpsilon As Double = 0.01

        Public Shared Function BuildPath(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                         plotArea As Rect,
                                         viewport As MousePerformanceTrajectoryViewport) As MousePerformanceTrajectoryPath
            If points Is Nothing OrElse points.Count < 2 OrElse plotArea.Width <= 0.0 OrElse plotArea.Height <= 0.0 Then
                Return New MousePerformanceTrajectoryPath(Array.Empty(Of MousePerformanceTrajectoryFigure)())
            End If

            Dim figurePointLists As New List(Of List(Of Point))()
            Dim currentFigure As List(Of Point) = Nothing
            Dim hasCurrentFigureEnd = False
            Dim lastFigureEnd As Point = New Point()

            For index = 1 To points.Count - 1
                Dim clippedStartX = 0.0
                Dim clippedStartY = 0.0
                Dim clippedEndX = 0.0
                Dim clippedEndY = 0.0

                If Not TryClipSegment(points(index - 1),
                                      points(index),
                                      viewport,
                                      clippedStartX,
                                      clippedStartY,
                                      clippedEndX,
                                      clippedEndY) Then
                    hasCurrentFigureEnd = False
                    currentFigure = Nothing
                    Continue For
                End If

                Dim screenStart = MapToScreen(plotArea, viewport, clippedStartX, clippedStartY)
                Dim screenEnd = MapToScreen(plotArea, viewport, clippedEndX, clippedEndY)
                If IsScreenSegmentIndistinguishable(screenStart, screenEnd) Then
                    Continue For
                End If

                If currentFigure Is Nothing OrElse Not hasCurrentFigureEnd OrElse Not AreContinuous(lastFigureEnd, screenStart) Then
                    currentFigure = New List(Of Point) From {
                        screenStart,
                        screenEnd
                    }
                    figurePointLists.Add(currentFigure)
                    lastFigureEnd = screenEnd
                    hasCurrentFigureEnd = True
                    Continue For
                End If

                currentFigure.Add(screenEnd)
                lastFigureEnd = screenEnd
            Next

            If figurePointLists.Count = 0 Then
                Return New MousePerformanceTrajectoryPath(Array.Empty(Of MousePerformanceTrajectoryFigure)())
            End If

            Dim figures(figurePointLists.Count - 1) As MousePerformanceTrajectoryFigure
            For index = 0 To figurePointLists.Count - 1
                figures(index) = New MousePerformanceTrajectoryFigure(figurePointLists(index).ToArray())
            Next

            Return New MousePerformanceTrajectoryPath(figures)
        End Function

        Public Shared Function BuildGeometry(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                             plotArea As Rect,
                                             viewport As MousePerformanceTrajectoryViewport) As StreamGeometry
            Return BuildGeometry(BuildPath(points, plotArea, viewport))
        End Function

        Public Shared Function BuildGeometry(path As MousePerformanceTrajectoryPath) As StreamGeometry
            Return CreateGeometry(path)
        End Function

        Public Shared Function CreateGeometry(path As MousePerformanceTrajectoryPath) As StreamGeometry
            Dim geometry As New StreamGeometry()
            If path Is Nothing OrElse path.Figures Is Nothing OrElse path.Figures.Count = 0 Then
                If geometry.CanFreeze Then
                    geometry.Freeze()
                End If

                Return geometry
            End If

            Using context = geometry.Open()
                For Each figure In path.Figures
                    If figure Is Nothing OrElse figure.Points Is Nothing OrElse figure.Points.Count < 2 Then
                        Continue For
                    End If

                    context.BeginFigure(figure.Points(0), False, False)
                    For index = 1 To figure.Points.Count - 1
                        context.LineTo(figure.Points(index), True, True)
                    Next
                Next
            End Using

            If geometry.CanFreeze Then
                geometry.Freeze()
            End If

            Return geometry
        End Function

        Private Shared Function TryClipSegment(startPoint As MousePerformanceChartPoint,
                                               endPoint As MousePerformanceChartPoint,
                                               viewport As MousePerformanceTrajectoryViewport,
                                               ByRef clippedStartX As Double,
                                               ByRef clippedStartY As Double,
                                               ByRef clippedEndX As Double,
                                               ByRef clippedEndY As Double) As Boolean
            If startPoint Is Nothing OrElse endPoint Is Nothing Then
                Return False
            End If

            Dim x0 = startPoint.X
            Dim y0 = startPoint.Y
            Dim x1 = endPoint.X
            Dim y1 = endPoint.Y

            If x0 = x1 AndAlso y0 = y1 Then
                If x0 < viewport.XMinimum OrElse x0 > viewport.XMaximum OrElse
                   y0 < viewport.YMinimum OrElse y0 > viewport.YMaximum Then
                    Return False
                End If

                clippedStartX = x0
                clippedStartY = y0
                clippedEndX = x1
                clippedEndY = y1
                Return True
            End If

            Dim dx = x1 - x0
            Dim dy = y1 - y0
            Dim enterT = 0.0
            Dim leaveT = 1.0

            If Not UpdateClip(-dx, x0 - viewport.XMinimum, enterT, leaveT) Then
                Return False
            End If

            If Not UpdateClip(dx, viewport.XMaximum - x0, enterT, leaveT) Then
                Return False
            End If

            If Not UpdateClip(-dy, y0 - viewport.YMinimum, enterT, leaveT) Then
                Return False
            End If

            If Not UpdateClip(dy, viewport.YMaximum - y0, enterT, leaveT) Then
                Return False
            End If

            clippedStartX = x0 + (enterT * dx)
            clippedStartY = y0 + (enterT * dy)
            clippedEndX = x0 + (leaveT * dx)
            clippedEndY = y0 + (leaveT * dy)
            Return True
        End Function

        Private Shared Function UpdateClip(p As Double,
                                           q As Double,
                                           ByRef enterT As Double,
                                           ByRef leaveT As Double) As Boolean
            If Math.Abs(p) < Double.Epsilon Then
                Return q >= 0.0
            End If

            Dim ratio = q / p
            If p < 0.0 Then
                If ratio > leaveT Then
                    Return False
                End If

                If ratio > enterT Then
                    enterT = ratio
                End If
                Return True
            End If

            If ratio < enterT Then
                Return False
            End If

            If ratio < leaveT Then
                leaveT = ratio
            End If

            Return True
        End Function

        Private Shared Function MapToScreen(plotArea As Rect,
                                            viewport As MousePerformanceTrajectoryViewport,
                                            x As Double,
                                            y As Double) As Point
            Return New Point(MapX(plotArea, viewport, x), MapY(plotArea, viewport, y))
        End Function

        Private Shared Function MapX(plotArea As Rect,
                                     viewport As MousePerformanceTrajectoryViewport,
                                     value As Double) As Double
            If Math.Abs(viewport.XMaximum - viewport.XMinimum) < 0.000001 Then
                Return plotArea.Left
            End If

            Return plotArea.Left + ((value - viewport.XMinimum) / (viewport.XMaximum - viewport.XMinimum)) * plotArea.Width
        End Function

        Private Shared Function MapY(plotArea As Rect,
                                     viewport As MousePerformanceTrajectoryViewport,
                                     value As Double) As Double
            If Math.Abs(viewport.YMaximum - viewport.YMinimum) < 0.000001 Then
                Return plotArea.Bottom
            End If

            Return plotArea.Bottom - ((value - viewport.YMinimum) / (viewport.YMaximum - viewport.YMinimum)) * plotArea.Height
        End Function

        Private Shared Function IsScreenSegmentIndistinguishable(startPoint As Point, endPoint As Point) As Boolean
            Return Math.Floor(startPoint.X) = Math.Floor(endPoint.X) AndAlso
                   Math.Floor(startPoint.Y) = Math.Floor(endPoint.Y)
        End Function

        Private Shared Function AreContinuous(left As Point, right As Point) As Boolean
            Return Math.Abs(left.X - right.X) <= ScreenContinuityEpsilon AndAlso
                   Math.Abs(left.Y - right.Y) <= ScreenContinuityEpsilon
        End Function
    End Class
End Namespace
