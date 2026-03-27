Imports System.Globalization

Namespace ViewModels.Pages
    Friend Module KeyDetectionFormatting
        Public Function FormatCount(value As Integer) As String
            Return value.ToString(CultureInfo.InvariantCulture)
        End Function

        Public Function FormatMilliseconds(value As Nullable(Of Double)) As String
            If Not value.HasValue OrElse Double.IsNaN(value.Value) OrElse Double.IsInfinity(value.Value) Then
                Return "--"
            End If

            If Math.Abs(value.Value) < 10.0 Then
                Return value.Value.ToString("0.00", CultureInfo.InvariantCulture) & " ms"
            End If

            Return value.Value.ToString("0.0", CultureInfo.InvariantCulture) & " ms"
        End Function
    End Module
End Namespace
