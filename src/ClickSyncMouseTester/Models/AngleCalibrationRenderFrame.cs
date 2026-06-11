using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class AngleCalibrationRenderFrame
{
    private readonly AngleCalibrationStatus _status;

    private readonly bool _isLocked;

    private readonly bool _hasData;

    private readonly double? _recommendedAngleDegrees;

    private readonly int _swipeCount;

    private readonly int _sampleCount;

    private readonly double? _stabilityDegrees;

    private readonly IReadOnlyList<AngleCalibrationTraceStroke> _traceStrokes;

    private readonly AngleCalibrationQualityLevel _qualityLevel;

    private readonly AngleCalibrationQualityReason _qualityReason;

    private readonly int _qualityScore;

    public AngleCalibrationStatus Status => _status;

    public bool IsLocked => _isLocked;

    public bool HasData => _hasData;

    public double? RecommendedAngleDegrees => _recommendedAngleDegrees;

    public bool HasRecommendedAngle
    {
        get
        {
            double? recommendedAngleDegrees = _recommendedAngleDegrees;
            return recommendedAngleDegrees.HasValue;
        }
    }

    public int SwipeCount => _swipeCount;

    public int SampleCount => _sampleCount;

    public double? StabilityDegrees => _stabilityDegrees;

    public IReadOnlyList<AngleCalibrationTraceStroke> TraceStrokes => _traceStrokes;

    internal AngleCalibrationQualityLevel QualityLevel => _qualityLevel;

    internal AngleCalibrationQualityReason QualityReason => _qualityReason;

    internal int QualityScore => _qualityScore;

    internal AngleCalibrationRenderFrame(AngleCalibrationStatus status, bool isLocked, bool hasData, double? recommendedAngleDegrees, int swipeCount, int sampleCount, double? stabilityDegrees, IReadOnlyList<AngleCalibrationTraceStroke> traceStrokes, AngleCalibrationQualityLevel qualityLevel, AngleCalibrationQualityReason qualityReason, int qualityScore)
    {
        _status = status;
        _isLocked = isLocked;
        _hasData = hasData;
        _recommendedAngleDegrees = recommendedAngleDegrees;
        _swipeCount = swipeCount;
        _sampleCount = sampleCount;
        _stabilityDegrees = stabilityDegrees;
        _traceStrokes = traceStrokes ?? Array.Empty<AngleCalibrationTraceStroke>();
        _qualityLevel = qualityLevel;
        _qualityReason = qualityReason;
        _qualityScore = Math.Max(0, Math.Min(100, qualityScore));
    }
}





