using ClickSyncMouseTester.Models;
using System;

namespace ClickSyncMouseTester.Services;

public interface IRawMouseReportSource
{
    event EventHandler<RawMousePacketEventArgs> MousePacketCaptured;
}
