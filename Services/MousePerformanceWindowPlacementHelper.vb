Imports System.Windows
Imports WpfApp1.Models

Namespace Services
    Public NotInheritable Class MousePerformanceWindowPlacementHelper
        Private Sub New()
        End Sub

        Public Shared Function Capture(window As Window) As MousePerformanceChartWindowPlacement
            If window Is Nothing Then
                Return New MousePerformanceChartWindowPlacement(False, 0.0, 0.0, 0.0, 0.0, False)
            End If

            Dim bounds = window.RestoreBounds
            Dim width = If(bounds.Width > 0.0, bounds.Width, If(window.ActualWidth > 0.0, window.ActualWidth, window.Width))
            Dim height = If(bounds.Height > 0.0, bounds.Height, If(window.ActualHeight > 0.0, window.ActualHeight, window.Height))
            Dim left = If(Double.IsNaN(bounds.Left), window.Left, bounds.Left)
            Dim top = If(Double.IsNaN(bounds.Top), window.Top, bounds.Top)

            Return New MousePerformanceChartWindowPlacement(True,
                                                            left,
                                                            top,
                                                            width,
                                                            height,
                                                            window.WindowState = WindowState.Maximized)
        End Function

        Public Shared Function TryNormalizeForRestore(placement As MousePerformanceChartWindowPlacement,
                                                      minWidth As Double,
                                                      minHeight As Double,
                                                      workAreas As IEnumerable(Of Rect),
                                                      ByRef normalizedPlacement As MousePerformanceChartWindowPlacement) As Boolean
            normalizedPlacement = Nothing
            If placement Is Nothing OrElse Not placement.HasSavedBounds Then
                Return False
            End If

            If Not IsFinite(placement.Left) OrElse
               Not IsFinite(placement.Top) OrElse
               Not IsFinite(placement.Width) OrElse
               Not IsFinite(placement.Height) Then
                Return False
            End If

            If placement.Width < Math.Max(0.0, minWidth) OrElse
               placement.Height < Math.Max(0.0, minHeight) Then
                Return False
            End If

            Dim bounds = New Rect(placement.Left, placement.Top, placement.Width, placement.Height)
            If bounds.Width <= 0.0 OrElse bounds.Height <= 0.0 Then
                Return False
            End If

            Dim anyVisible = False
            If workAreas IsNot Nothing Then
                For Each workArea In workAreas
                    If workArea.IntersectsWith(bounds) Then
                        anyVisible = True
                        Exit For
                    End If
                Next
            End If

            If Not anyVisible Then
                Return False
            End If

            normalizedPlacement = New MousePerformanceChartWindowPlacement(True,
                                                                           placement.Left,
                                                                           placement.Top,
                                                                           placement.Width,
                                                                           placement.Height,
                                                                           placement.IsMaximized)
            Return True
        End Function

        Private Shared Function IsFinite(value As Double) As Boolean
            Return Not Double.IsNaN(value) AndAlso Not Double.IsInfinity(value)
        End Function
    End Class
End Namespace
