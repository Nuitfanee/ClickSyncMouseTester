using ClickSyncMouseTester.Models;
using System;

namespace ClickSyncMouseTester.Services;

public interface IRawKeyboardInputSource
{
    event EventHandler<RawKeyboardInputEventArgs> KeyboardInput;
}
