using System;

namespace ClickSyncMouseTester.Models;

public class SensitivityMatchRoundResult
{
    private readonly int _roundIndex;

    private readonly double _scale;

    private readonly int _recommendedTargetDpi;

    private readonly double _sourcePathLength;

    private readonly double _targetPathLength;

    private readonly double _sourceGoalCounts;

    private readonly double _targetGoalCounts;

    private readonly double _durationMilliseconds;

    private readonly int _sourcePacketCount;

    private readonly int _targetPacketCount;

    private readonly double _sourceStraightness;

    private readonly double _targetStraightness;

    private readonly double _directionDeltaDegrees;

    private readonly double _overlapRatio;

    public int RoundIndex => _roundIndex;

    public double Scale => _scale;

    public int RecommendedTargetDpi => _recommendedTargetDpi;

    public double SourcePathLength => _sourcePathLength;

    public double TargetPathLength => _targetPathLength;

    public double SourceGoalCounts => _sourceGoalCounts;

    public double TargetGoalCounts => _targetGoalCounts;

    public double DurationMilliseconds => _durationMilliseconds;

    public int SourcePacketCount => _sourcePacketCount;

    public int TargetPacketCount => _targetPacketCount;

    public double SourceStraightness => _sourceStraightness;

    public double TargetStraightness => _targetStraightness;

    public double DirectionDeltaDegrees => _directionDeltaDegrees;

    public double OverlapRatio => _overlapRatio;

    internal SensitivityMatchRoundResult(int roundIndex, double scale, int recommendedTargetDpi, double sourcePathLength, double targetPathLength, double sourceGoalCounts, double targetGoalCounts, double durationMilliseconds, int sourcePacketCount, int targetPacketCount, double sourceStraightness, double targetStraightness, double directionDeltaDegrees, double overlapRatio)
    {
        _roundIndex = Math.Max(1, roundIndex);
        _scale = Math.Max(0.0, scale);
        _recommendedTargetDpi = Math.Max(0, recommendedTargetDpi);
        _sourcePathLength = Math.Max(0.0, sourcePathLength);
        _targetPathLength = Math.Max(0.0, targetPathLength);
        _sourceGoalCounts = Math.Max(0.0, sourceGoalCounts);
        _targetGoalCounts = Math.Max(0.0, targetGoalCounts);
        _durationMilliseconds = Math.Max(0.0, durationMilliseconds);
        _sourcePacketCount = Math.Max(0, sourcePacketCount);
        _targetPacketCount = Math.Max(0, targetPacketCount);
        _sourceStraightness = Math.Max(0.0, Math.Min(1.0, sourceStraightness));
        _targetStraightness = Math.Max(0.0, Math.Min(1.0, targetStraightness));
        _directionDeltaDegrees = Math.Max(0.0, directionDeltaDegrees);
        _overlapRatio = Math.Max(0.0, Math.Min(1.0, overlapRatio));
    }
}





