Imports WpfApp1.ViewModels

Namespace Navigation
    Public Interface ICaptureSessionPageViewModel
        Inherits IDisposable

        Event EnterLockRequested As EventHandler
        Event ExitLockRequested As EventHandler(Of CaptureLockRequestEventArgs)

        ReadOnly Property IsLocked As Boolean

        Sub OnLockEntered()
        Sub RequestPauseFromView()
        Sub OnViewUnlockCompleted(reason As CaptureUnlockReason)
    End Interface
End Namespace
