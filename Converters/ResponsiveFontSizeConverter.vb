Imports System.Globalization
Imports System.Windows.Data

Namespace Converters
    Public Class ResponsiveFontSizeConverter
        Implements IMultiValueConverter

        Public Function Convert(values As Object(),
                                targetType As Type,
                                parameter As Object,
                                culture As CultureInfo) As Object Implements IMultiValueConverter.Convert
            Dim actualWidth = ReadDouble(values, 0, 1240.0)
            Dim actualHeight = ReadDouble(values, 1, 940.0)

            Dim baseSize = 214.0
            Dim minSize = 214.0
            Dim maxSize = 292.0
            Dim referenceWidth = 1240.0
            Dim referenceHeight = 940.0

            If parameter IsNot Nothing Then
                Dim tokens = parameter.ToString().Split("|"c)
                If tokens.Length > 0 Then
                    baseSize = ParseDouble(tokens(0), baseSize)
                End If

                If tokens.Length > 1 Then
                    minSize = ParseDouble(tokens(1), minSize)
                End If

                If tokens.Length > 2 Then
                    maxSize = ParseDouble(tokens(2), maxSize)
                End If

                If tokens.Length > 3 Then
                    referenceWidth = ParseDouble(tokens(3), referenceWidth)
                End If

                If tokens.Length > 4 Then
                    referenceHeight = ParseDouble(tokens(4), referenceHeight)
                End If
            End If

            Dim widthScale = actualWidth / Math.Max(referenceWidth, 1.0)
            Dim heightScale = actualHeight / Math.Max(referenceHeight, 1.0)
            Dim scale = Math.Min(widthScale, heightScale)
            Dim fontSize = baseSize * scale

            Return Math.Max(minSize, Math.Min(maxSize, fontSize))
        End Function

        Public Function ConvertBack(value As Object,
                                    targetTypes As Type(),
                                    parameter As Object,
                                    culture As CultureInfo) As Object() Implements IMultiValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

        Private Shared Function ReadDouble(values As Object(), index As Integer, fallback As Double) As Double
            If values Is Nothing OrElse index < 0 OrElse index >= values.Length Then
                Return fallback
            End If

            Return ParseDouble(values(index), fallback)
        End Function

        Private Shared Function ParseDouble(value As Object, fallback As Double) As Double
            If value Is Nothing Then
                Return fallback
            End If

            Dim parsed As Double
            If Double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, parsed) Then
                Return parsed
            End If

            If Double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.CurrentCulture, parsed) Then
                Return parsed
            End If

            Return fallback
        End Function
    End Class
End Namespace
