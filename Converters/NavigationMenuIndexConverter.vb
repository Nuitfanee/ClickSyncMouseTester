Imports System.Globalization
Imports System.Windows.Data
Imports WpfApp1.Navigation

Namespace Converters
    Public Class NavigationMenuIndexConverter
        Implements IValueConverter

        Public Function Convert(value As Object,
                                targetType As Type,
                                parameter As Object,
                                culture As CultureInfo) As Object Implements IValueConverter.Convert
            If value Is Nothing Then
                Return String.Empty
            End If

            Dim pageKey As AppPageKey
            If TypeOf value Is AppPageKey Then
                pageKey = DirectCast(value, AppPageKey)
            ElseIf Not [Enum].TryParse(value.ToString(), True, pageKey) Then
                Return String.Empty
            End If

            Select Case pageKey
                Case AppPageKey.PollingDashboard
                    Return "01."
                Case AppPageKey.SensitivityMatching
                    Return "02."
                Case AppPageKey.MousePerformance
                    Return "03."
                Case AppPageKey.AngleCalibration
                    Return "04."
                Case AppPageKey.KeyDetection
                    Return "05."
                Case Else
                    Return String.Empty
            End Select
        End Function

        Public Function ConvertBack(value As Object,
                                    targetType As Type,
                                    parameter As Object,
                                    culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class
End Namespace
