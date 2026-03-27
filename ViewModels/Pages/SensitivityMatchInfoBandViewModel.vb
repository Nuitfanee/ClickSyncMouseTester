Imports System.Windows
Imports WpfApp1.Infrastructure

Namespace ViewModels.Pages
    Public Class SensitivityMatchInfoBandViewModel
        Inherits BindableBase

        Private _labelText As String
        Private _deviceTitle As String
        Private _bindingStateText As String
        Private _metaText As String
        Private _bandHorizontalAlignment As HorizontalAlignment
        Private _bandScaleX As Double
        Private _gapAfterTitle As GridLength
        Private _gapAfterBindingState As GridLength

        Public Property LabelText As String
            Get
                Return _labelText
            End Get
            Set(value As String)
                SetProperty(_labelText, value)
            End Set
        End Property

        Public Property DeviceTitle As String
            Get
                Return _deviceTitle
            End Get
            Set(value As String)
                SetProperty(_deviceTitle, value)
            End Set
        End Property

        Public Property BindingStateText As String
            Get
                Return _bindingStateText
            End Get
            Set(value As String)
                If SetProperty(_bindingStateText, value) Then
                    RefreshLayoutMetrics()
                End If
            End Set
        End Property

        Public Property MetaText As String
            Get
                Return _metaText
            End Get
            Set(value As String)
                If SetProperty(_metaText, value) Then
                    RefreshLayoutMetrics()
                End If
            End Set
        End Property

        Public Property BandScaleX As Double
            Get
                Return _bandScaleX
            End Get
            Set(value As Double)
                If SetProperty(_bandScaleX, value) Then
                    RefreshLayoutMetrics()
                End If
            End Set
        End Property

        Public Property BandHorizontalAlignment As HorizontalAlignment
            Get
                Return _bandHorizontalAlignment
            End Get
            Set(value As HorizontalAlignment)
                SetProperty(_bandHorizontalAlignment, value)
            End Set
        End Property

        Public Property GapAfterTitle As GridLength
            Get
                Return _gapAfterTitle
            End Get
            Private Set(value As GridLength)
                SetProperty(_gapAfterTitle, value)
            End Set
        End Property

        Public Property GapAfterBindingState As GridLength
            Get
                Return _gapAfterBindingState
            End Get
            Private Set(value As GridLength)
                SetProperty(_gapAfterBindingState, value)
            End Set
        End Property

        Private Sub RefreshLayoutMetrics()
            Dim hasBindingState = Not String.IsNullOrWhiteSpace(_bindingStateText)
            Dim hasMeta = Not String.IsNullOrWhiteSpace(_metaText)

            GapAfterTitle = If(hasBindingState, New GridLength(16), New GridLength(0))
            GapAfterBindingState = If(hasMeta, New GridLength(16), New GridLength(0))
        End Sub
    End Class
End Namespace
