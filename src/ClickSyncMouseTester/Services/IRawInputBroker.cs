using System;

namespace ClickSyncMouseTester.Services;

public interface IRawInputBroker : IRawMouseReportSource, IRawMouseControlInputSource, IRawKeyboardInputSource, IRawInputDeviceCatalog, IDisposable
{
}
