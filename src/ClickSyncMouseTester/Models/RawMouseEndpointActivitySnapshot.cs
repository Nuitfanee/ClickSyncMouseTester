namespace ClickSyncMouseTester.Models;

public sealed class RawMouseEndpointActivitySnapshot
{
    private readonly RawMouseEndpointKind _endpointKind;
    private readonly long _totalPacketCount;
    private readonly long _motionPacketCount;
    private readonly long _emptyPacketCount;
    private readonly long _buttonPacketCount;
    private readonly long _wheelPacketCount;
    private readonly double _firstPacketTimestampMs;
    private readonly double _lastPacketTimestampMs;
    private readonly double _lastMotionTimestampMs;

    public RawMouseEndpointKind EndpointKind => _endpointKind;

    public long TotalPacketCount => _totalPacketCount;

    public long MotionPacketCount => _motionPacketCount;

    public long EmptyPacketCount => _emptyPacketCount;

    public long ButtonPacketCount => _buttonPacketCount;

    public long WheelPacketCount => _wheelPacketCount;

    public double FirstPacketTimestampMs => _firstPacketTimestampMs;

    public double LastPacketTimestampMs => _lastPacketTimestampMs;

    public double LastMotionTimestampMs => _lastMotionTimestampMs;

    public RawMouseEndpointActivitySnapshot(RawMouseEndpointKind endpointKind, long totalPacketCount, long motionPacketCount, long emptyPacketCount, long buttonPacketCount, long wheelPacketCount, double firstPacketTimestampMs, double lastPacketTimestampMs, double lastMotionTimestampMs)
    {
        _endpointKind = endpointKind;
        _totalPacketCount = totalPacketCount;
        _motionPacketCount = motionPacketCount;
        _emptyPacketCount = emptyPacketCount;
        _buttonPacketCount = buttonPacketCount;
        _wheelPacketCount = wheelPacketCount;
        _firstPacketTimestampMs = firstPacketTimestampMs;
        _lastPacketTimestampMs = lastPacketTimestampMs;
        _lastMotionTimestampMs = lastMotionTimestampMs;
    }
}
