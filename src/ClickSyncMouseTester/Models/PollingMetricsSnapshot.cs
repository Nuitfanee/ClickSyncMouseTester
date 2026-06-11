namespace ClickSyncMouseTester.Models;

public class PollingMetricsSnapshot
{
    private readonly int _currentRate;

    private readonly int _peakRate;

    private readonly double? _emptyPacketPercent;

    private readonly long _droppedPacketCount;

    public int CurrentRate => _currentRate;

    public int PeakRate => _peakRate;

    public double? EmptyPacketPercent => _emptyPacketPercent;

    public long DroppedPacketCount => _droppedPacketCount;

    public PollingMetricsSnapshot(int currentRate, int peakRate, double? emptyPacketPercent, long droppedPacketCount)
    {
        _currentRate = currentRate;
        _peakRate = peakRate;
        _emptyPacketPercent = emptyPacketPercent;
        _droppedPacketCount = droppedPacketCount;
    }
}
