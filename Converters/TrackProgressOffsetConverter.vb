Imports System.Globalization
Imports System.Windows.Data

Namespace Converters
    Public Class TrackProgressOffsetConverter
        Implements IMultiValueConverter

        Public Function Convert(values As Object(),
                                targetType As Type,
                                parameter As Object,
                                culture As CultureInfo) As Object Implements IMultiValueConverter.Convert
            Dim progressValue = ReadDouble(values, 0, 0.0)
            Dim trackWidth = ReadDouble(values, 1, 0.0)
            Dim reservedWidth = ParseDouble(parameter, 0.0)

            Dim normalizedProgress = Math.Max(0.0, Math.Min(100.0, progressValue))
            Dim availableTravel = Math.Max(0.0, trackWidth - Math.Max(0.0, reservedWidth))

            Return availableTravel * (normalizedProgress / 100.0)
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

            Dim parsedValue As Double
            If Double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, parsedValue) Then
                Return parsedValue
            End If

            If Double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.CurrentCulture, parsedValue) Then
                Return parsedValue
            End If

            Return fallback
        End Function
    End Class
End Namespace
