namespace ClickSyncMouseTester.Models;

public class RawMouseWheelInput
{
    private readonly int _delta;

    private readonly double _timestampMs;

    public int Delta => _delta;

    public double TimestampMs => _timestampMs;

    public RawMouseWheelInput(int delta, double timestampMs)
    {
        _delta = delta;
        _timestampMs = timestampMs;
    }
}





