namespace ClickSyncMouseTester.Models;

public class PollingHistoryPoint
{
    private readonly double _timestampMs;

    private readonly double _realtimeTimestampMs;

    private readonly double _rate;

    public double TimestampMs => _timestampMs;

    public double RealtimeTimestampMs => _realtimeTimestampMs;

    public double Rate => _rate;

    public PollingHistoryPoint(double timestampMs, double realtimeTimestampMs, double rate)
    {
        _timestampMs = timestampMs;
        _realtimeTimestampMs = realtimeTimestampMs;
        _rate = rate;
    }
}





