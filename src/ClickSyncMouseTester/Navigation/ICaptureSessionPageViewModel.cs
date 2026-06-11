using ClickSyncMouseTester.ViewModels;
using System;

namespace ClickSyncMouseTester.Navigation;

public interface ICaptureSessionPageViewModel : IDisposable
{
    bool IsLocked { get; }

    event EventHandler EnterLockRequested;

    event EventHandler<CaptureLockRequestEventArgs> ExitLockRequested;

    void OnLockEntered();

    void RequestPauseFromView();

    void OnViewUnlockCompleted(CaptureUnlockReason reason);
}





