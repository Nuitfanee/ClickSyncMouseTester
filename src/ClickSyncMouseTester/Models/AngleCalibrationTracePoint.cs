namespace ClickSyncMouseTester.Models;

public class AngleCalibrationTracePoint
{
    private readonly double _x;

    private readonly double _y;

    public double X => _x;

    public double Y => _y;

    public AngleCalibrationTracePoint(double x, double y)
    {
        _x = x;
        _y = y;
    }
}





