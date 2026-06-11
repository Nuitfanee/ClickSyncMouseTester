using ClickSyncMouseTester.Models;
using System;

namespace ClickSyncMouseTester.Services;

public interface IRawMouseControlInputSource
{
    event EventHandler<RawMouseButtonInputEventArgs> MouseButtonInput;

    event EventHandler<RawMouseWheelInputEventArgs> MouseWheelInput;
}
