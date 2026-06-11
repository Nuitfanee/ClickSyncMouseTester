using System;

namespace ClickSyncMouseTester.Models;

public class RawMouseWheelInputEventArgs : EventArgs
{
    private readonly RawMouseWheelInput _input;

    public RawMouseWheelInput Input => _input;

    public RawMouseWheelInputEventArgs(RawMouseWheelInput input)
    {
        _input = input;
    }
}





