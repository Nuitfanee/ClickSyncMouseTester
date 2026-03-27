Imports System
Imports System.Windows.Media.Animation

Namespace Infrastructure
    Public Class CubicBezierEase
        Implements IEasingFunction

        Private Const NewtonIterations As Integer = 8
        Private Const BinarySubdivisionIterations As Integer = 10
        Private Const Precision As Double = 0.000001

        Public Property X1 As Double
        Public Property Y1 As Double
        Public Property X2 As Double
        Public Property Y2 As Double

        Public Function Ease(normalizedTime As Double) As Double Implements IEasingFunction.Ease
            If normalizedTime <= 0.0 Then
                Return 0.0
            End If

            If normalizedTime >= 1.0 Then
                Return 1.0
            End If

            Dim parameter = SolveCurveX(normalizedTime)
            Return SampleCurveY(parameter)
        End Function

        Private Function SolveCurveX(targetX As Double) As Double
            Dim estimate = targetX

            For iteration = 0 To NewtonIterations - 1
                Dim currentX = SampleCurveX(estimate) - targetX
                If Math.Abs(currentX) < Precision Then
                    Return estimate
                End If

                Dim derivative = SampleCurveDerivativeX(estimate)
                If Math.Abs(derivative) < Precision Then
                    Exit For
                End If

                estimate -= currentX / derivative
            Next

            Dim lower = 0.0
            Dim upper = 1.0
            estimate = targetX

            For iteration = 0 To BinarySubdivisionIterations - 1
                Dim currentX = SampleCurveX(estimate)
                If Math.Abs(currentX - targetX) < Precision Then
                    Exit For
                End If

                If currentX < targetX Then
                    lower = estimate
                Else
                    upper = estimate
                End If

                estimate = (lower + upper) * 0.5
            Next

            Return estimate
        End Function

        Private Function SampleCurveX(parameter As Double) As Double
            Return ((A(X1, X2) * parameter + B(X1, X2)) * parameter + C(X1)) * parameter
        End Function

        Private Function SampleCurveY(parameter As Double) As Double
            Return ((A(Y1, Y2) * parameter + B(Y1, Y2)) * parameter + C(Y1)) * parameter
        End Function

        Private Function SampleCurveDerivativeX(parameter As Double) As Double
            Return (3.0 * A(X1, X2) * parameter + 2.0 * B(X1, X2)) * parameter + C(X1)
        End Function

        Private Shared Function A(first As Double, second As Double) As Double
            Return 1.0 - 3.0 * second + 3.0 * first
        End Function

        Private Shared Function B(first As Double, second As Double) As Double
            Return 3.0 * second - 6.0 * first
        End Function

        Private Shared Function C(first As Double) As Double
            Return 3.0 * first
        End Function
    End Class
End Namespace
