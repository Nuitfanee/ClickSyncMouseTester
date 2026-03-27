Imports WpfApp1.Infrastructure

Namespace ViewModels.Pages
    Public Class SensitivityMatchRoundCardViewModel
        Inherits BindableBase

        Private _title As String
        Private _statusText As String
        Private _valueText As String
        Private _detailText As String
        Private _isCurrent As Boolean
        Private _isCompleted As Boolean
        Private _isFailed As Boolean
        Private _showProgressTrack As Boolean
        Private _sourceTrackProgressValue As Double
        Private _targetTrackProgressValue As Double
        Private _trackProgressValue As Double
        Private _trackCaptionText As String

        Public Property Title As String
            Get
                Return _title
            End Get
            Set(value As String)
                SetProperty(_title, value)
            End Set
        End Property

        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Public Property ValueText As String
            Get
                Return _valueText
            End Get
            Set(value As String)
                SetProperty(_valueText, value)
            End Set
        End Property

        Public Property DetailText As String
            Get
                Return _detailText
            End Get
            Set(value As String)
                SetProperty(_detailText, value)
            End Set
        End Property

        Public Property IsCurrent As Boolean
            Get
                Return _isCurrent
            End Get
            Set(value As Boolean)
                SetProperty(_isCurrent, value)
            End Set
        End Property

        Public Property IsCompleted As Boolean
            Get
                Return _isCompleted
            End Get
            Set(value As Boolean)
                SetProperty(_isCompleted, value)
            End Set
        End Property

        Public Property IsFailed As Boolean
            Get
                Return _isFailed
            End Get
            Set(value As Boolean)
                SetProperty(_isFailed, value)
            End Set
        End Property

        Public Property ShowProgressTrack As Boolean
            Get
                Return _showProgressTrack
            End Get
            Set(value As Boolean)
                SetProperty(_showProgressTrack, value)
            End Set
        End Property

        Public Property SourceTrackProgressValue As Double
            Get
                Return _sourceTrackProgressValue
            End Get
            Set(value As Double)
                SetProperty(_sourceTrackProgressValue, value)
            End Set
        End Property

        Public Property TargetTrackProgressValue As Double
            Get
                Return _targetTrackProgressValue
            End Get
            Set(value As Double)
                SetProperty(_targetTrackProgressValue, value)
            End Set
        End Property

        Public Property TrackProgressValue As Double
            Get
                Return _trackProgressValue
            End Get
            Set(value As Double)
                SetProperty(_trackProgressValue, value)
            End Set
        End Property

        Public Property TrackCaptionText As String
            Get
                Return _trackCaptionText
            End Get
            Set(value As String)
                SetProperty(_trackCaptionText, value)
            End Set
        End Property
    End Class
End Namespace
