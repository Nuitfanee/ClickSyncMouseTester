using System;

namespace ClickSyncMouseTester.Models;

public class SensitivityMatchCurrentRoundState
{
    private readonly int _roundIndex;

    private readonly SensitivityMatchRoundStage _stage;

    private readonly double _sourcePathLength;

    private readonly double _targetPathLength;

    private readonly double _sourceGoalCounts;

    private readonly double _targetGoalCounts;

    private readonly double _sourceProgress;

    private readonly double _targetProgress;

    private readonly double _overallProgress;

    private readonly double _durationMilliseconds;

    private readonly int _sourcePacketCount;

    private readonly int _targetPacketCount;

    public int RoundIndex => _roundIndex;

    public SensitivityMatchRoundStage Stage => _stage;

    public double SourcePathLength => _sourcePathLength;

    public double TargetPathLength => _targetPathLength;

    public double SourceGoalCounts => _sourceGoalCounts;

    public double TargetGoalCounts => _targetGoalCounts;

    public double SourceProgress => _sourceProgress;

    public double TargetProgress => _targetProgress;

    public double OverallProgress => _overallProgress;

    public double DurationMilliseconds => _durationMilliseconds;

    public int SourcePacketCount => _sourcePacketCount;

    public int TargetPacketCount => _targetPacketCount;

    internal SensitivityMatchCurrentRoundState(int roundIndex, SensitivityMatchRoundStage stage, double sourcePathLength, double targetPathLength, double sourceGoalCounts, double targetGoalCounts, double sourceProgress, double targetProgress, double overallProgress, double durationMilliseconds, int sourcePacketCount, int targetPacketCount)
    {
        _roundIndex = Math.Max(1, roundIndex);
        _stage = stage;
        _sourcePathLength = Math.Max(0.0, sourcePathLength);
        _targetPathLength = Math.Max(0.0, targetPathLength);
        _sourceGoalCounts = Math.Max(0.0, sourceGoalCounts);
        _targetGoalCounts = Math.Max(0.0, targetGoalCounts);
        _sourceProgress = Math.Max(0.0, Math.Min(1.0, sourceProgress));
        _targetProgress = Math.Max(0.0, Math.Min(1.0, targetProgress));
        _overallProgress = Math.Max(0.0, Math.Min(1.0, overallProgress));
        _durationMilliseconds = Math.Max(0.0, durationMilliseconds);
        _sourcePacketCount = Math.Max(0, sourcePacketCount);
        _targetPacketCount = Math.Max(0, targetPacketCount);
    }
}





