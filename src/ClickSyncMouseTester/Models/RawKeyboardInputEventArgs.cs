using System;

namespace ClickSyncMouseTester.Models;

public class RawKeyboardInputEventArgs : EventArgs
{
    private readonly RawKeyboardInput _input;

    public RawKeyboardInput Input => _input;

    public RawKeyboardInputEventArgs(RawKeyboardInput input)
    {
        _input = input;
    }
}





