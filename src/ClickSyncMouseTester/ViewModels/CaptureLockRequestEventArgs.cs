using System;

namespace ClickSyncMouseTester.ViewModels;

public class CaptureLockRequestEventArgs : EventArgs
{
    private readonly CaptureUnlockReason _reason;

    public CaptureUnlockReason Reason => _reason;

    public CaptureLockRequestEventArgs(CaptureUnlockReason reason)
    {
        _reason = reason;
    }
}





