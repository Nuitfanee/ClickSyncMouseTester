Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data
Imports System.Windows.Media

Namespace Converters
    Public Class NavigationMenuForegroundConverter
        Implements IMultiValueConverter

        Private Shared ReadOnly FallbackDefaultBrush As Brush = Brushes.Black
        Private Shared ReadOnly FallbackActiveBrush As Brush = Brushes.White
        Private Shared ReadOnly FallbackHoverBrush As Brush = CreateFrozenBrush(Color.FromRgb(&HFF, &H5F, &H0))

        Public Function Convert(values As Object(),
                                targetType As Type,
                                parameter As Object,
                                culture As CultureInfo) As Object Implements IMultiValueConverter.Convert
            Dim currentPageKey = ReadValue(values, 0)
            Dim itemPageKey = ReadValue(values, 1)
            Dim isMouseOver = ReadBoolean(values, 2)

            If currentPageKey IsNot Nothing AndAlso itemPageKey IsNot Nothing AndAlso Equals(currentPageKey, itemPageKey) Then
                Return ResolveBrush("NavigationMenuTextActiveBrush", FallbackActiveBrush)
            End If

            If isMouseOver Then
                Return ResolveBrush("NavigationCurtainBackgroundBrush", FallbackHoverBrush)
            End If

            Return ResolveBrush("NavigationMenuTextBrush", FallbackDefaultBrush)
        End Function

        Public Function ConvertBack(value As Object,
                                    targetTypes As Type(),
                                    parameter As Object,
                                    culture As CultureInfo) As Object() Implements IMultiValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function

        Private Shared Function ReadBoolean(values As Object(), index As Integer) As Boolean
            If values Is Nothing OrElse index < 0 OrElse index >= values.Length Then
                Return False
            End If

            Dim rawValue = ReadValue(values, index)
            If TypeOf rawValue Is Boolean Then
                Return CBool(rawValue)
            End If

            Dim parsedValue As Boolean
            If rawValue IsNot Nothing AndAlso Boolean.TryParse(rawValue.ToString(), parsedValue) Then
                Return parsedValue
            End If

            Return False
        End Function

        Private Shared Function ReadValue(values As Object(), index As Integer) As Object
            If values Is Nothing OrElse index < 0 OrElse index >= values.Length Then
                Return Nothing
            End If

            Return values(index)
        End Function

        Private Shared Function ResolveBrush(resourceKey As String, fallback As Brush) As Brush
            Dim currentApplication As Application = Application.Current
            If currentApplication Is Nothing Then
                Return fallback
            End If

            Dim brush = TryCast(currentApplication.TryFindResource(resourceKey), Brush)
            If brush IsNot Nothing Then
                Return brush
            End If

            Return fallback
        End Function

        Private Shared Function CreateFrozenBrush(color As Color) As Brush
            Dim brush As New SolidColorBrush(color)
            brush.Freeze()
            Return brush
        End Function
    End Class
End Namespace
