Namespace ViewModels
    Public Class CaptureLockRequestEventArgs
        Inherits EventArgs

        Private ReadOnly _reason As CaptureUnlockReason

        Public Sub New(reason As CaptureUnlockReason)
            _reason = reason
        End Sub

        Public ReadOnly Property Reason As CaptureUnlockReason
            Get
                Return _reason
            End Get
        End Property
    End Class
End Namespace
