using System;

namespace ClickSyncMouseTester.Models;

public class RawMousePacketEventArgs : EventArgs
{
    private readonly RawMousePacket _packet;

    private readonly int _sessionGeneration;

    public RawMousePacket Packet => _packet;

    public int SessionGeneration => _sessionGeneration;

    public RawMousePacketEventArgs(RawMousePacket packet, int sessionGeneration = 0)
    {
        _packet = packet;
        _sessionGeneration = sessionGeneration;
    }
}





