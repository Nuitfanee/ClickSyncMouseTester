using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

internal struct QueuedRawMousePacket
{
    public static readonly QueuedRawMousePacket Empty = new QueuedRawMousePacket(null, 0);

    private readonly RawMousePacket _packet;

    private readonly int _generation;

    public RawMousePacket Packet => _packet;

    public int Generation => _generation;

    public bool HasPacket => _packet != null;

    public QueuedRawMousePacket(RawMousePacket packet, int generation)
    {
        _packet = packet;
        _generation = generation;
    }
}





