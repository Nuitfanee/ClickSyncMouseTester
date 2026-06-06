using System;

namespace ClickSyncMouseTester.Models;

public class RawMouseButtonInputEventArgs : EventArgs
{
    private readonly RawMouseButtonInput _input;

    public RawMouseButtonInput Input => _input;

    public RawMouseButtonInputEventArgs(RawMouseButtonInput input)
    {
        _input = input;
    }
}





