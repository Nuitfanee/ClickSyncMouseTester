using System;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceSummary
{
    private readonly int _eventCount;

    private readonly long _sumX;

    private readonly long _sumY;

    private readonly double _pathCounts;

    private readonly double? _cpi;

    private readonly double? _sumXCm;

    private readonly double? _sumYCm;

    private readonly double? _pathCm;

    private readonly double? _currentVelocityMetersPerSecond;

    private readonly double? _sessionAverageVelocityMetersPerSecond;

    public int EventCount => _eventCount;

    public long SumX => _sumX;

    public long SumY => _sumY;

    public double PathCounts => _pathCounts;

    public double? Cpi => _cpi;

    public double? SumXCm => _sumXCm;

    public double? SumYCm => _sumYCm;

    public double? PathCm => _pathCm;

    public double? CurrentVelocityMetersPerSecond => _currentVelocityMetersPerSecond;

    public double? SessionAverageVelocityMetersPerSecond => _sessionAverageVelocityMetersPerSecond;

    public bool HasDistanceConversion
    {
        get
        {
            double? sumXCm = _sumXCm;
            if (sumXCm.HasValue)
            {
                sumXCm = _sumYCm;
                if (sumXCm.HasValue)
                {
                    sumXCm = _pathCm;
                    return sumXCm.HasValue;
                }
            }
            return false;
        }
    }

    public MousePerformanceSummary(int eventCount, long sumX, long sumY, double pathCounts, double? cpi, double? sumXCm, double? sumYCm, double? pathCm, double? currentVelocityMetersPerSecond, double? sessionAverageVelocityMetersPerSecond)
    {
        _eventCount = Math.Max(0, eventCount);
        _sumX = sumX;
        _sumY = sumY;
        _pathCounts = pathCounts;
        _cpi = cpi;
        _sumXCm = sumXCm;
        _sumYCm = sumYCm;
        _pathCm = pathCm;
        _currentVelocityMetersPerSecond = currentVelocityMetersPerSecond;
        _sessionAverageVelocityMetersPerSecond = sessionAverageVelocityMetersPerSecond;
    }
}





