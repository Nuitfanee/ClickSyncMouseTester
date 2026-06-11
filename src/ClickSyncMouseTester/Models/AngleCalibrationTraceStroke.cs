using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class AngleCalibrationTraceStroke
{
    private readonly IReadOnlyList<AngleCalibrationTracePoint> _points;

    private readonly bool _isCurrent;

    public IReadOnlyList<AngleCalibrationTracePoint> Points => _points;

    public bool IsCurrent => _isCurrent;

    public AngleCalibrationTraceStroke(IReadOnlyList<AngleCalibrationTracePoint> points, bool isCurrent)
    {
        _points = points ?? Array.Empty<AngleCalibrationTracePoint>();
        _isCurrent = isCurrent;
    }
}





