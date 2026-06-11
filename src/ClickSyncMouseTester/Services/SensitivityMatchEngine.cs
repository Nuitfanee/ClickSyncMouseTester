using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClickSyncMouseTester.Services;

internal class SensitivityMatchEngine
{
    private sealed class ParticipantAccumulator
    {
        public double PathLength { get; set; }

        public double NetDx { get; set; }

        public double NetDy { get; set; }

        public int PacketCount { get; set; }

        public double? FirstPacketTimeMs { get; set; }

        public double? LastPacketTimeMs { get; set; }

        public List<RawMousePacket> Packets { get; }

        public ParticipantAccumulator()
        {
            Packets = new List<RawMousePacket>();
        }
    }

    internal sealed class RollingScaleSample
    {
        public double TimestampMs { get; set; }

        public double Scale { get; set; }

        public double TailEnergyRatio { get; set; }
    }

    private sealed class RoundAccumulator
    {
        public int RoundIndex { get; set; }

        public double SourceGoalCounts { get; set; }

        public double TargetGoalCounts { get; set; }

        public double StartedMs { get; set; }

        public ParticipantAccumulator Source { get; set; }

        public ParticipantAccumulator Target { get; set; }

        public SensitivityMatchRoundStage Stage { get; set; }

        public double? StabilizingStartedMs { get; set; }

        public List<RollingScaleSample> RollingSamples { get; }

        public RoundAccumulator()
        {
            Source = new ParticipantAccumulator();
            Target = new ParticipantAccumulator();
            RollingSamples = new List<RollingScaleSample>();
        }
    }

    private sealed class TimeWindow
    {
        public double StartMs { get; set; }

        public double End { get; set; }

        public double DurationMilliseconds => Math.Max(0.0, End - StartMs);
    }

    internal sealed class MotionProjection
    {
        public double TimestampMs { get; set; }

        public double SignedAxisComponent { get; set; }

        public double AxisMagnitude { get; set; }

        public double SegmentLength { get; set; }
    }

    private sealed class MotionBin
    {
        public double AxisMagnitude { get; set; }

        public double SignedAxisComponent { get; set; }
    }

    internal sealed class LagAlignmentResult
    {
        public double TimeOffsetMs { get; set; }

        public double Correlation { get; set; }

        public double OppositeDirectionRatio { get; set; }
    }

    private sealed class NormalizedDistanceBin
    {
        public double AxisMagnitude { get; set; }

        public double SignedAxisComponent { get; set; }
    }

    private sealed class VectorBin
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Magnitude => Math.Sqrt(X * X + Y * Y);
    }

    private sealed class SimilarityFitResult
    {
        public double RotationDegrees { get; set; }

        public double NormalizedResidual { get; set; }
    }

    private sealed class PacketWindowAnalysisOptions
    {
        public double TrimFraction { get; set; }

        public int BinCount { get; set; }

        public int MinimumValidBins { get; set; }

        public double MinimumSourceBinMagnitude { get; set; }

        public double MinimumTargetBinMagnitude { get; set; }

        public double MinimumRequiredOverlapRatio { get; set; }

        public double MinimumRequiredAxisPurity { get; set; }

        public double MaximumAllowedOppositeRatio { get; set; }

        public double MaximumAllowedSimilarityRotationDegrees { get; set; }

        public double MaximumAllowedSimilarityResidualRatio { get; set; }

        public double MinimumRequiredLagCorrelation { get; set; }
    }

    private sealed class RobustDispersionResult
    {
        public double MedianScale { get; set; }

        public double NormalizedDispersion { get; set; }
    }

    private sealed class PacketWindowAnalysis
    {
        public double Scale { get; set; }

        public double OverlapRatio { get; set; }

        public double SourceAxisPurity { get; set; }

        public double TargetAxisPurity { get; set; }

        public double DirectionDeltaDegrees { get; set; }

        public double OppositeDirectionRatio { get; set; }

        public double WeightedRatioCv { get; set; }

        public double NormalizedDispersion { get; set; }

        public double SimilarityResidualRatio { get; set; }

        public int ValidBinCount { get; set; }

        public double LagCorrelation { get; set; }

        public double TimeOffsetMs { get; set; }

        public double SourceAxisX { get; set; }

        public double SourceAxisY { get; set; }

        public double TargetAxisX { get; set; }

        public double TargetAxisY { get; set; }

        public double TailEnergyRatio { get; set; }

        public SensitivityMatchRoundFailureReason ValidationFailureReason { get; set; }
    }

    private sealed class RoundAnalysisResult
    {
        public SensitivityMatchRoundFailureReason FailureReason { get; set; }

        public double Scale { get; set; }

        public double SourceAxisPurity { get; set; }

        public double TargetAxisPurity { get; set; }

        public double DirectionDeltaDegrees { get; set; }

        public double OverlapRatio { get; set; }
    }


    private const double InchesPerRound = 8.0;

    private const double MinimumRoundDurationMilliseconds = 400.0;

    private const double MaximumRoundDurationMilliseconds = 8000.0;

    private const int MinimumPacketCount = 30;

    private const double WarmupIgnoreDurationMilliseconds = 200.0;

    private const double MinimumOverlapRatio = 0.75;

    private const double MinimumAxisPurity = 0.85;

    private const double MaximumSimilarityRotationDegrees = 35.0;

    private const double MaximumSimilarityResidualRatio = 0.18;

    private const double MaximumOppositeDirectionRatio = 0.15;

    private const double StableWindowTrimFraction = 0.1;

    private const double DominantAxisTrimFraction = 0.2;

    private const int StableWindowBinCount = 12;

    private const int MinimumStableValidBins = 6;

    private const double MaximumStableNormalizedDispersion = 0.035;

    private const double FormalMinimumBinMagnitudeBase = 28.0;

    private const double FormalMinimumBinMagnitudeGoalFraction = 0.009;

    private const double StabilizingMaximumDurationMilliseconds = 250.0;

    private const double StabilizingRollingWindowMilliseconds = 150.0;

    private const double StabilizingRequiredStableDurationMilliseconds = 120.0;

    private const double StabilizingMaximumScaleDrift = 0.003;

    private const int StabilizingRollingBinCount = 6;

    private const int MinimumRollingValidBins = 3;

    private const double TailEnergyWindowMilliseconds = 80.0;

    private const double MaximumTailEnergyRatio = 0.18;

    private const double LagSearchMaximumOffsetMilliseconds = 24.0;

    private const double LagSearchStepMilliseconds = 2.0;

    private const double LagCorrelationBinSizeMilliseconds = 8.0;

    private const double MinimumLagCorrelation = 0.7;

    private const double MinimumRollingLagCorrelation = 0.45;

    private const double LagBoundaryContinuationDelta = 0.01;

    private const double TinyEpsilon = 1E-07;

    private readonly List<SensitivityMatchRoundResult> _completedRounds;

    private RoundAccumulator _activeRound;

    private int _sourceDpi;

    private int _targetCurrentDpi;

    private SensitivityMatchRoundFailureReason _lastRoundFailureReason;

    private int _lastRoundFailureIndex;

    private bool _resultsExpired;

    public IReadOnlyList<SensitivityMatchRoundResult> CompletedRounds => _completedRounds.ToArray();

    public bool HasActiveRound => _activeRound != null;

    public SensitivityMatchRoundFailureReason LastRoundFailureReason => _lastRoundFailureReason;

    public int LastRoundFailureIndex => _lastRoundFailureIndex;

    public bool ResultsExpired => _resultsExpired;

    public SensitivityMatchEngine()
    {
        _completedRounds = new List<SensitivityMatchRoundResult>();
    }

    public void Reset()
    {
        _completedRounds.Clear();
        _activeRound = null;
        _sourceDpi = 0;
        _targetCurrentDpi = 0;
        _lastRoundFailureReason = SensitivityMatchRoundFailureReason.None;
        _lastRoundFailureIndex = 0;
        _resultsExpired = false;
    }

    public void ResetMeasurements()
    {
        _completedRounds.Clear();
        _activeRound = null;
        _lastRoundFailureReason = SensitivityMatchRoundFailureReason.None;
        _lastRoundFailureIndex = 0;
        _resultsExpired = false;
    }

    public bool StartRound(int sourceDpi, int targetCurrentDpi, double startedMs)
    {
        if (_activeRound != null || _completedRounds.Count >= 3)
        {
            return false;
        }
        if (sourceDpi <= 0 || targetCurrentDpi <= 0)
        {
            return false;
        }
        if (_sourceDpi != sourceDpi || _targetCurrentDpi != targetCurrentDpi)
        {
            _sourceDpi = sourceDpi;
            _targetCurrentDpi = targetCurrentDpi;
            ResetMeasurements();
        }
        _lastRoundFailureReason = SensitivityMatchRoundFailureReason.None;
        _lastRoundFailureIndex = 0;
        _activeRound = new RoundAccumulator
        {
            RoundIndex = _completedRounds.Count + 1,
            SourceGoalCounts = (double)sourceDpi * 8.0,
            TargetGoalCounts = (double)targetCurrentDpi * 8.0,
            StartedMs = ResolveValidTimestamp(startedMs),
            Stage = SensitivityMatchRoundStage.Collecting
        };
        return true;
    }

    public void CancelActiveRound()
    {
        _activeRound = null;
    }

    public void InvalidateResults()
    {
        if (_completedRounds.Count != 0)
        {
            _resultsExpired = true;
        }
    }

    public void PushPacket(SensitivityMatchBindingSlot slot, RawMousePacket packet)
    {
        if (_activeRound == null || packet == null)
        {
            return;
        }
        if (IsWarmupPacket(_activeRound, packet))
        {
            return;
        }
        AppendPacket((slot == SensitivityMatchBindingSlot.SourceMouse) ? _activeRound.Source : _activeRound.Target, packet);
        if (_activeRound != null)
        {
            if (_activeRound.Stage == SensitivityMatchRoundStage.Collecting && HasReachedMinimumDistance(_activeRound))
            {
                BeginStabilizing(packet.TimestampMs);
            }
            if (_activeRound != null && _activeRound.Stage == SensitivityMatchRoundStage.Stabilizing)
            {
                AdvanceStabilizing(packet.TimestampMs);
            }
        }
    }

    public void Update(double nowMs)
    {
        if (_activeRound != null)
        {
            if (ComputeDurationMilliseconds(_activeRound.Source, _activeRound.Target, nowMs) > 8000.0)
            {
                FailCurrentRound(SensitivityMatchRoundFailureReason.Timeout);
            }
            else if (_activeRound.Stage == SensitivityMatchRoundStage.Stabilizing)
            {
                AdvanceStabilizing(nowMs);
            }
        }
    }

    public SensitivityMatchCurrentRoundState CreateCurrentRoundState()
    {
        if (_activeRound == null)
        {
            return null;
        }

        double sourceProgress = ComputeProgress(_activeRound.Source.PathLength, _activeRound.SourceGoalCounts);
        double targetProgress = ComputeProgress(_activeRound.Target.PathLength, _activeRound.TargetGoalCounts);
        double averageProgress = (sourceProgress + targetProgress) / 2.0;
        return new SensitivityMatchCurrentRoundState(
            _activeRound.RoundIndex,
            _activeRound.Stage,
            _activeRound.Source.PathLength,
            _activeRound.Target.PathLength,
            _activeRound.SourceGoalCounts,
            _activeRound.TargetGoalCounts,
            sourceProgress,
            targetProgress,
            averageProgress,
            ComputeDurationMilliseconds(_activeRound.Source, _activeRound.Target),
            _activeRound.Source.PacketCount,
            _activeRound.Target.PacketCount);
    }

    public double? GetFinalScale()
    {
        if (_completedRounds.Count != 3)
        {
            return null;
        }

        double[] sortedScales = _completedRounds
            .Select(round => round.Scale)
            .OrderBy(scale => scale)
            .ToArray();
        return sortedScales.Length == 3 ? sortedScales[1] : null;
    }

    public int? GetFinalRecommendedTargetDpi()
    {
        double? finalScale = GetFinalScale();
        return (finalScale.HasValue && _targetCurrentDpi > 0) ? (int)Math.Round(_targetCurrentDpi * finalScale.Value) : null;
    }

    public double? GetConsistencyPercent()
    {
        double? finalScale = GetFinalScale();
        if (!finalScale.HasValue || finalScale.Value <= TinyEpsilon || _completedRounds.Count != 3)
        {
            return null;
        }

        double maximumPercentDeviation = 0.0;
        foreach (SensitivityMatchRoundResult completedRound in _completedRounds)
        {
            double percentDeviation = Math.Abs(completedRound.Scale - finalScale.Value) / finalScale.Value * 100.0;
            maximumPercentDeviation = Math.Max(maximumPercentDeviation, percentDeviation);
        }
        return maximumPercentDeviation;
    }

    public SensitivityMatchConsistencyLevel GetConsistencyLevel()
    {
        double? consistencyPercent = GetConsistencyPercent();
        if (!consistencyPercent.HasValue)
        {
            return SensitivityMatchConsistencyLevel.None;
        }
        if (consistencyPercent.Value <= 1.0)
        {
            return SensitivityMatchConsistencyLevel.Excellent;
        }
        if (consistencyPercent.Value <= 2.5)
        {
            return SensitivityMatchConsistencyLevel.Good;
        }
        if (consistencyPercent.Value <= 5.0)
        {
            return SensitivityMatchConsistencyLevel.Fair;
        }
        return SensitivityMatchConsistencyLevel.Poor;
    }

    private static void AppendPacket(ParticipantAccumulator participant, RawMousePacket packet)
    {
        if (participant == null || packet == null)
        {
            return;
        }

        double segmentLength = ComputeSegmentLength(packet);
        participant.PathLength += segmentLength;
        participant.NetDx += packet.DeltaX;
        participant.NetDy += packet.DeltaY;
        participant.PacketCount++;
        participant.Packets.Add(packet);
        if (!participant.FirstPacketTimeMs.HasValue)
        {
            participant.FirstPacketTimeMs = packet.TimestampMs;
        }
        participant.LastPacketTimeMs = packet.TimestampMs;
    }

    private static double ComputeProgress(double pathLength, double goalCounts)
    {
        if (goalCounts <= 1E-07)
        {
            return 0.0;
        }
        return Math.Max(0.0, Math.Min(1.0, pathLength / goalCounts));
    }

    private static double ComputeDurationMilliseconds(ParticipantAccumulator source, ParticipantAccumulator target, double fallbackNowMs = double.NaN)
    {
        double firstPacketTimeMs = double.PositiveInfinity;
        double lastObservedTimeMs = double.NegativeInfinity;
        if (source != null && source.FirstPacketTimeMs.HasValue)
        {
            firstPacketTimeMs = Math.Min(firstPacketTimeMs, source.FirstPacketTimeMs.Value);
        }
        if (target != null && target.FirstPacketTimeMs.HasValue)
        {
            firstPacketTimeMs = Math.Min(firstPacketTimeMs, target.FirstPacketTimeMs.Value);
        }
        if (source != null && source.LastPacketTimeMs.HasValue)
        {
            lastObservedTimeMs = Math.Max(lastObservedTimeMs, source.LastPacketTimeMs.Value);
        }
        if (target != null && target.LastPacketTimeMs.HasValue)
        {
            lastObservedTimeMs = Math.Max(lastObservedTimeMs, target.LastPacketTimeMs.Value);
        }
        if (!double.IsNaN(fallbackNowMs) && !double.IsInfinity(fallbackNowMs))
        {
            lastObservedTimeMs = Math.Max(lastObservedTimeMs, fallbackNowMs);
        }
        if (double.IsPositiveInfinity(firstPacketTimeMs) || double.IsNegativeInfinity(lastObservedTimeMs))
        {
            return 0.0;
        }
        return Math.Max(0.0, lastObservedTimeMs - firstPacketTimeMs);
    }

    private static bool HasReachedMinimumDistance(RoundAccumulator round)
    {
        if (round == null)
        {
            return false;
        }
        return round.Source.PathLength >= round.SourceGoalCounts && round.Target.PathLength >= round.TargetGoalCounts;
    }

    private static bool IsWarmupPacket(RoundAccumulator round, RawMousePacket packet)
    {
        if (round == null || packet == null)
        {
            return false;
        }

        return packet.TimestampMs < round.StartedMs + WarmupIgnoreDurationMilliseconds;
    }

    private void BeginStabilizing(double nowMs)
    {
        if (_activeRound != null && _activeRound.Stage != SensitivityMatchRoundStage.Stabilizing)
        {
            _activeRound.Stage = SensitivityMatchRoundStage.Stabilizing;
            _activeRound.StabilizingStartedMs = ResolveLatestObservedTime(_activeRound, nowMs);
            _activeRound.RollingSamples.Clear();
        }
    }

    private void AdvanceStabilizing(double nowMs)
    {
        if (_activeRound == null || _activeRound.Stage != SensitivityMatchRoundStage.Stabilizing)
        {
            return;
        }

        AddRollingScaleSample(nowMs);
        if (HasStableRollingScale(nowMs))
        {
            CompleteActiveRound(nowMs);
            return;
        }

        double stabilizingStartedMs = _activeRound.StabilizingStartedMs ?? nowMs;
        if (nowMs - stabilizingStartedMs >= StabilizingMaximumDurationMilliseconds)
        {
            CompleteActiveRound(nowMs);
        }
    }

    private void AddRollingScaleSample(double nowMs)
    {
        if (_activeRound == null)
        {
            return;
        }

        PacketWindowAnalysis rollingAnalysis = TryEstimateRollingAnalysis(_activeRound, nowMs);
        if (rollingAnalysis == null)
        {
            PurgeRollingSamples(_activeRound, nowMs);
            return;
        }

        PurgeRollingSamples(_activeRound, nowMs);
        int lastSampleIndex = _activeRound.RollingSamples.Count - 1;
        if (lastSampleIndex >= 0 && Math.Abs(_activeRound.RollingSamples[lastSampleIndex].TimestampMs - nowMs) <= TinyEpsilon)
        {
            _activeRound.RollingSamples[lastSampleIndex].Scale = rollingAnalysis.Scale;
            _activeRound.RollingSamples[lastSampleIndex].TailEnergyRatio = rollingAnalysis.TailEnergyRatio;
            return;
        }

        _activeRound.RollingSamples.Add(new RollingScaleSample
        {
            TimestampMs = nowMs,
            Scale = rollingAnalysis.Scale,
            TailEnergyRatio = rollingAnalysis.TailEnergyRatio
        });
    }

    private static void PurgeRollingSamples(RoundAccumulator round, double nowMs)
    {
        if (round == null)
        {
            return;
        }

        double retentionStartMs = nowMs - StabilizingMaximumDurationMilliseconds - StabilizingRollingWindowMilliseconds;
        round.RollingSamples.RemoveAll(sample => sample == null || sample.TimestampMs < retentionStartMs);
    }

    private bool HasStableRollingScale(double nowMs)
    {
        if (_activeRound == null || _activeRound.RollingSamples.Count < 2)
        {
            return false;
        }

        double windowStartMs = nowMs - StabilizingRequiredStableDurationMilliseconds;
        RollingScaleSample[] recentSamples = _activeRound.RollingSamples
            .Where(sample => sample != null && sample.TimestampMs >= windowStartMs)
            .OrderBy(sample => sample.TimestampMs)
            .ToArray();
        if (recentSamples.Length < 2)
        {
            return false;
        }
        if (nowMs - recentSamples[0].TimestampMs < StabilizingRequiredStableDurationMilliseconds - 5.0)
        {
            return false;
        }

        double medianScale = ComputeMedian(recentSamples.Select(sample => sample.Scale));
        if (medianScale <= TinyEpsilon)
        {
            return false;
        }

        double maximumRelativeDrift = 0.0;
        foreach (RollingScaleSample sample in recentSamples)
        {
            maximumRelativeDrift = Math.Max(maximumRelativeDrift, Math.Abs(sample.Scale - medianScale) / medianScale);
        }
        if (maximumRelativeDrift > StabilizingMaximumScaleDrift)
        {
            return false;
        }

        return recentSamples[^1].TailEnergyRatio <= MaximumTailEnergyRatio;
    }

    private PacketWindowAnalysis TryEstimateRollingAnalysis(RoundAccumulator round, double nowMs)
    {
        if (round == null)
        {
            return null;
        }

        double windowStartMs = nowMs - StabilizingRollingWindowMilliseconds;
        List<RawMousePacket> sourcePackets = FilterNonZeroPackets(round.Source.Packets, windowStartMs, nowMs);
        List<RawMousePacket> targetPackets = FilterNonZeroPackets(round.Target.Packets, windowStartMs, nowMs);
        if (sourcePackets.Count == 0 || targetPackets.Count == 0)
        {
            return null;
        }

        PacketWindowAnalysis rollingAnalysis = AnalyzePacketWindow(
            sourcePackets,
            targetPackets,
            CreateRollingAnalysisOptions(round));
        if (rollingAnalysis == null)
        {
            return null;
        }

        rollingAnalysis.TailEnergyRatio = EstimateTailEnergy(
            round,
            rollingAnalysis.SourceAxisX,
            rollingAnalysis.SourceAxisY,
            rollingAnalysis.TargetAxisX,
            rollingAnalysis.TargetAxisY,
            rollingAnalysis.TimeOffsetMs,
            nowMs);
        return rollingAnalysis;
    }

    private static PacketWindowAnalysisOptions CreateRollingAnalysisOptions(RoundAccumulator round)
    {
        return new PacketWindowAnalysisOptions
        {
            TrimFraction = 0.0,
            BinCount = StabilizingRollingBinCount,
            MinimumValidBins = MinimumRollingValidBins,
            MinimumSourceBinMagnitude = Math.Max(8.0, round.SourceGoalCounts * 0.002),
            MinimumTargetBinMagnitude = Math.Max(8.0, round.TargetGoalCounts * 0.002),
            MinimumRequiredOverlapRatio = 0.35,
            MinimumRequiredAxisPurity = 0.0,
            MaximumAllowedOppositeRatio = 1.0,
            MaximumAllowedSimilarityRotationDegrees = 180.0,
            MaximumAllowedSimilarityResidualRatio = double.PositiveInfinity,
            MinimumRequiredLagCorrelation = MinimumRollingLagCorrelation
        };
    }

    private static PacketWindowAnalysisOptions CreateFormalAnalysisOptions(RoundAccumulator round)
    {
        return new PacketWindowAnalysisOptions
        {
            TrimFraction = StableWindowTrimFraction,
            BinCount = StableWindowBinCount,
            MinimumValidBins = MinimumStableValidBins,
            MinimumSourceBinMagnitude = Math.Max(FormalMinimumBinMagnitudeBase, round.SourceGoalCounts * FormalMinimumBinMagnitudeGoalFraction),
            MinimumTargetBinMagnitude = Math.Max(FormalMinimumBinMagnitudeBase, round.TargetGoalCounts * FormalMinimumBinMagnitudeGoalFraction),
            MinimumRequiredOverlapRatio = MinimumOverlapRatio,
            MinimumRequiredAxisPurity = MinimumAxisPurity,
            MaximumAllowedOppositeRatio = MaximumOppositeDirectionRatio,
            MaximumAllowedSimilarityRotationDegrees = MaximumSimilarityRotationDegrees,
            MaximumAllowedSimilarityResidualRatio = MaximumSimilarityResidualRatio,
            MinimumRequiredLagCorrelation = MinimumLagCorrelation
        };
    }

    private void CompleteActiveRound(double completionTimeMs = double.NaN)
    {
        if (_activeRound == null)
        {
            return;
        }

        double effectiveCompletionTimeMs = ResolveLatestObservedTime(_activeRound, completionTimeMs);
        RoundAnalysisResult analysis = EvaluateCurrentRound(effectiveCompletionTimeMs);
        if (analysis.FailureReason != SensitivityMatchRoundFailureReason.None)
        {
            FailCurrentRound(analysis.FailureReason);
            return;
        }

        int recommendedTargetDpi = (int)Math.Round(_targetCurrentDpi * analysis.Scale);
        _completedRounds.Add(new SensitivityMatchRoundResult(
            _activeRound.RoundIndex,
            analysis.Scale,
            recommendedTargetDpi,
            _activeRound.Source.PathLength,
            _activeRound.Target.PathLength,
            _activeRound.SourceGoalCounts,
            _activeRound.TargetGoalCounts,
            ComputeDurationMilliseconds(_activeRound.Source, _activeRound.Target, effectiveCompletionTimeMs),
            _activeRound.Source.PacketCount,
            _activeRound.Target.PacketCount,
            analysis.SourceAxisPurity,
            analysis.TargetAxisPurity,
            analysis.DirectionDeltaDegrees,
            analysis.OverlapRatio));
        _activeRound = null;
        _lastRoundFailureReason = SensitivityMatchRoundFailureReason.None;
        _lastRoundFailureIndex = 0;
        _resultsExpired = false;
    }

    private RoundAnalysisResult EvaluateCurrentRound(double completionTimeMs)
    {
        RoundAnalysisResult result = new RoundAnalysisResult
        {
            FailureReason = SensitivityMatchRoundFailureReason.None
        };
        if (_activeRound == null)
        {
            return result;
        }

        double durationMs = ComputeDurationMilliseconds(_activeRound.Source, _activeRound.Target, completionTimeMs);
        if (durationMs > MaximumRoundDurationMilliseconds)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.Timeout;
            return result;
        }
        if (durationMs < MinimumRoundDurationMilliseconds)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.TooFast;
            return result;
        }

        List<RawMousePacket> sourcePackets = FilterNonZeroPackets(_activeRound.Source.Packets);
        List<RawMousePacket> targetPackets = FilterNonZeroPackets(_activeRound.Target.Packets);
        if (sourcePackets.Count < MinimumPacketCount || targetPackets.Count < MinimumPacketCount)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.InsufficientPackets;
            return result;
        }

        PacketWindowAnalysis analysis = AnalyzePacketWindow(
            sourcePackets,
            targetPackets,
            CreateFormalAnalysisOptions(_activeRound));
        if (analysis == null)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.Unsynchronized;
            return result;
        }

        result.Scale = analysis.Scale;
        result.SourceAxisPurity = analysis.SourceAxisPurity;
        result.TargetAxisPurity = analysis.TargetAxisPurity;
        result.DirectionDeltaDegrees = analysis.DirectionDeltaDegrees;
        result.OverlapRatio = analysis.OverlapRatio;
        if (analysis.SourceAxisPurity < MinimumAxisPurity || analysis.TargetAxisPurity < MinimumAxisPurity)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.ExcessiveCurvature;
            return result;
        }
        if (analysis.ValidationFailureReason != SensitivityMatchRoundFailureReason.None)
        {
            result.FailureReason = analysis.ValidationFailureReason;
            return result;
        }
        if (analysis.DirectionDeltaDegrees > MaximumSimilarityRotationDegrees || analysis.OppositeDirectionRatio > MaximumOppositeDirectionRatio)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.DirectionMismatch;
            return result;
        }
        if (analysis.SimilarityResidualRatio > MaximumSimilarityResidualRatio)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.PathShapeMismatch;
            return result;
        }
        if (analysis.OverlapRatio < MinimumOverlapRatio || analysis.ValidBinCount < MinimumStableValidBins)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.Unsynchronized;
            return result;
        }
        if (analysis.NormalizedDispersion > MaximumStableNormalizedDispersion)
        {
            result.FailureReason = SensitivityMatchRoundFailureReason.Unsynchronized;
            return result;
        }

        return result;
    }

    private static PacketWindowAnalysis AnalyzePacketWindow(IReadOnlyList<RawMousePacket> sourcePackets, IReadOnlyList<RawMousePacket> targetPackets, PacketWindowAnalysisOptions options)
    {
        if (sourcePackets == null || targetPackets == null || options == null || sourcePackets.Count == 0 || targetPackets.Count == 0)
        {
            return null;
        }

        TimeWindow rawOverlapWindow = GetOverlapWindow(sourcePackets, targetPackets);
        if (rawOverlapWindow == null)
        {
            return null;
        }

        double initialSourceAxisX = 0.0;
        double initialSourceAxisY = 0.0;
        double initialTargetAxisX = 0.0;
        double initialTargetAxisY = 0.0;
        if (!TryBuildTrimmedParticipantAxis(sourcePackets, rawOverlapWindow, ref initialSourceAxisX, ref initialSourceAxisY)
            || !TryBuildTrimmedParticipantAxis(targetPackets, rawOverlapWindow, ref initialTargetAxisX, ref initialTargetAxisY))
        {
            return null;
        }

        AlignAxesToSameDirection(initialSourceAxisX, initialSourceAxisY, ref initialTargetAxisX, ref initialTargetAxisY);
        List<MotionProjection> sourceInitialProjection = ProjectPackets(sourcePackets, double.NegativeInfinity, double.PositiveInfinity, initialSourceAxisX, initialSourceAxisY);
        List<MotionProjection> targetInitialProjection = ProjectPackets(targetPackets, double.NegativeInfinity, double.PositiveInfinity, initialTargetAxisX, initialTargetAxisY);
        LagAlignmentResult lagAlignment = TryEstimateBestLagOffset(sourceInitialProjection, targetInitialProjection, options.MinimumRequiredLagCorrelation);
        if (lagAlignment == null)
        {
            return null;
        }

        List<RawMousePacket> lagAdjustedTargetPackets = ShiftPackets(targetPackets, lagAlignment.TimeOffsetMs);
        TimeWindow alignedOverlapWindow = GetOverlapWindow(sourcePackets, lagAdjustedTargetPackets);
        if (alignedOverlapWindow == null)
        {
            return null;
        }

        double overlapRatio = ComputeOverlapRatio(sourcePackets, lagAdjustedTargetPackets, alignedOverlapWindow);
        if (overlapRatio < options.MinimumRequiredOverlapRatio)
        {
            return null;
        }

        double sourceAxisX = 0.0;
        double sourceAxisY = 0.0;
        double targetAxisX = 0.0;
        double targetAxisY = 0.0;
        if (!TryBuildTrimmedParticipantAxis(sourcePackets, alignedOverlapWindow, ref sourceAxisX, ref sourceAxisY)
            || !TryBuildTrimmedParticipantAxis(lagAdjustedTargetPackets, alignedOverlapWindow, ref targetAxisX, ref targetAxisY))
        {
            return null;
        }

        AlignAxesToSameDirection(sourceAxisX, sourceAxisY, ref targetAxisX, ref targetAxisY);
        List<MotionProjection> sourceProjection = ProjectPackets(sourcePackets, alignedOverlapWindow.StartMs, alignedOverlapWindow.End, sourceAxisX, sourceAxisY);
        List<MotionProjection> targetProjection = ProjectPackets(lagAdjustedTargetPackets, alignedOverlapWindow.StartMs, alignedOverlapWindow.End, targetAxisX, targetAxisY);
        if (sourceProjection.Count == 0 || targetProjection.Count == 0)
        {
            return null;
        }

        double sourceAxisPurity = ComputeAxisPurity(sourceProjection);
        double targetAxisPurity = ComputeAxisPurity(targetProjection);
        double axisDeltaDegrees = ComputeAngleBetweenVectorsDegrees(sourceAxisX, sourceAxisY, targetAxisX, targetAxisY);
        if (sourceAxisPurity < options.MinimumRequiredAxisPurity || targetAxisPurity < options.MinimumRequiredAxisPurity)
        {
            return new PacketWindowAnalysis
            {
                OverlapRatio = overlapRatio,
                SourceAxisPurity = sourceAxisPurity,
                TargetAxisPurity = targetAxisPurity,
                DirectionDeltaDegrees = axisDeltaDegrees,
                OppositeDirectionRatio = 1.0,
                WeightedRatioCv = double.PositiveInfinity,
                NormalizedDispersion = double.PositiveInfinity,
                SimilarityResidualRatio = double.PositiveInfinity,
                ValidBinCount = 0,
                LagCorrelation = lagAlignment.Correlation,
                TimeOffsetMs = lagAlignment.TimeOffsetMs,
                SourceAxisX = sourceAxisX,
                SourceAxisY = sourceAxisY,
                TargetAxisX = targetAxisX,
                TargetAxisY = targetAxisY
            };
        }

        TimeWindow stableWindow = TrimWindow(alignedOverlapWindow, options.TrimFraction);
        if (stableWindow == null)
        {
            return null;
        }

        double oppositeDirectionRatio = ComputeOppositeDirectionRatio(sourceProjection, targetProjection, stableWindow.StartMs, stableWindow.End, options.BinCount);
        SimilarityFitResult similarityFit = TryFitSimilarityTransform(
            sourcePackets,
            lagAdjustedTargetPackets,
            stableWindow.StartMs,
            stableWindow.End,
            options.BinCount,
            options.MinimumValidBins,
            options.MinimumSourceBinMagnitude,
            options.MinimumTargetBinMagnitude);
        double similarityResidualRatio = similarityFit?.NormalizedResidual ?? double.PositiveInfinity;
        double directionDeltaDegrees = similarityFit == null ? axisDeltaDegrees : Math.Abs(similarityFit.RotationDegrees);
        SensitivityMatchRoundFailureReason validationFailureReason = ResolveSimilarityValidationFailure(similarityFit, directionDeltaDegrees, similarityResidualRatio, oppositeDirectionRatio, options);
        if (validationFailureReason != SensitivityMatchRoundFailureReason.None)
        {
            return new PacketWindowAnalysis
            {
                OverlapRatio = overlapRatio,
                SourceAxisPurity = sourceAxisPurity,
                TargetAxisPurity = targetAxisPurity,
                DirectionDeltaDegrees = directionDeltaDegrees,
                OppositeDirectionRatio = oppositeDirectionRatio,
                WeightedRatioCv = double.PositiveInfinity,
                NormalizedDispersion = double.PositiveInfinity,
                SimilarityResidualRatio = similarityResidualRatio,
                ValidBinCount = 0,
                LagCorrelation = lagAlignment.Correlation,
                TimeOffsetMs = lagAlignment.TimeOffsetMs,
                SourceAxisX = sourceAxisX,
                SourceAxisY = sourceAxisY,
                TargetAxisX = targetAxisX,
                TargetAxisY = targetAxisY,
                ValidationFailureReason = validationFailureReason
            };
        }

        List<NormalizedDistanceBin> sourceBins = null;
        List<NormalizedDistanceBin> targetBins = null;
        if (!TryBuildNormalizedDistanceBins(sourceProjection, targetProjection, stableWindow.StartMs, stableWindow.End, options.BinCount, ref sourceBins, ref targetBins))
        {
            return null;
        }

        List<double> scaleRatios = new List<double>();
        List<double> ratioWeights = new List<double>();
        for (int binIndex = 0; binIndex < options.BinCount; binIndex++)
        {
            NormalizedDistanceBin sourceBin = sourceBins[binIndex];
            NormalizedDistanceBin targetBin = targetBins[binIndex];
            if (sourceBin == null || targetBin == null || sourceBin.AxisMagnitude < options.MinimumSourceBinMagnitude || targetBin.AxisMagnitude < options.MinimumTargetBinMagnitude)
            {
                continue;
            }

            double sharedMagnitude = Math.Min(sourceBin.AxisMagnitude, targetBin.AxisMagnitude);
            if (sharedMagnitude <= TinyEpsilon || targetBin.AxisMagnitude <= TinyEpsilon)
            {
                continue;
            }

            scaleRatios.Add(sourceBin.AxisMagnitude / targetBin.AxisMagnitude);
            ratioWeights.Add(sharedMagnitude);
        }

        double weightedRatioCv = ComputeWeightedCoefficientOfVariation(scaleRatios, ratioWeights);
        RobustDispersionResult robustDispersion = ComputeWeightedMadNormalizedDispersion(scaleRatios, ratioWeights);
        if (scaleRatios.Count < options.MinimumValidBins)
        {
            return CreatePacketWindowAnalysis(0.0, overlapRatio, sourceAxisPurity, targetAxisPurity, directionDeltaDegrees, oppositeDirectionRatio, weightedRatioCv, robustDispersion.NormalizedDispersion, similarityResidualRatio, scaleRatios.Count, lagAlignment, sourceAxisX, sourceAxisY, targetAxisX, targetAxisY);
        }

        return CreatePacketWindowAnalysis(robustDispersion.MedianScale, overlapRatio, sourceAxisPurity, targetAxisPurity, directionDeltaDegrees, oppositeDirectionRatio, weightedRatioCv, robustDispersion.NormalizedDispersion, similarityResidualRatio, scaleRatios.Count, lagAlignment, sourceAxisX, sourceAxisY, targetAxisX, targetAxisY);
    }

    private static SensitivityMatchRoundFailureReason ResolveSimilarityValidationFailure(SimilarityFitResult similarityFit, double directionDeltaDegrees, double similarityResidualRatio, double oppositeDirectionRatio, PacketWindowAnalysisOptions options)
    {
        if (options == null)
        {
            return SensitivityMatchRoundFailureReason.Unsynchronized;
        }
        if (similarityFit == null)
        {
            return SensitivityMatchRoundFailureReason.Unsynchronized;
        }
        if (directionDeltaDegrees > options.MaximumAllowedSimilarityRotationDegrees || oppositeDirectionRatio > options.MaximumAllowedOppositeRatio)
        {
            return SensitivityMatchRoundFailureReason.DirectionMismatch;
        }
        if (similarityResidualRatio > options.MaximumAllowedSimilarityResidualRatio)
        {
            return SensitivityMatchRoundFailureReason.PathShapeMismatch;
        }

        return SensitivityMatchRoundFailureReason.None;
    }

    private static PacketWindowAnalysis CreatePacketWindowAnalysis(double scale, double overlapRatio, double sourceAxisPurity, double targetAxisPurity, double directionDeltaDegrees, double oppositeDirectionRatio, double weightedRatioCv, double normalizedDispersion, double similarityResidualRatio, int validBinCount, LagAlignmentResult lagAlignment, double sourceAxisX, double sourceAxisY, double targetAxisX, double targetAxisY)
    {
        return new PacketWindowAnalysis
        {
            Scale = scale,
            OverlapRatio = overlapRatio,
            SourceAxisPurity = sourceAxisPurity,
            TargetAxisPurity = targetAxisPurity,
            DirectionDeltaDegrees = directionDeltaDegrees,
            OppositeDirectionRatio = oppositeDirectionRatio,
            WeightedRatioCv = weightedRatioCv,
            NormalizedDispersion = normalizedDispersion,
            SimilarityResidualRatio = similarityResidualRatio,
            ValidBinCount = validBinCount,
            LagCorrelation = lagAlignment?.Correlation ?? 0.0,
            TimeOffsetMs = lagAlignment?.TimeOffsetMs ?? 0.0,
            SourceAxisX = sourceAxisX,
            SourceAxisY = sourceAxisY,
            TargetAxisX = targetAxisX,
            TargetAxisY = targetAxisY
        };
    }

    private static List<RawMousePacket> FilterNonZeroPackets(IReadOnlyList<RawMousePacket> packets, double minimumTimestampMs = double.NegativeInfinity, double maximumTimestampMs = double.PositiveInfinity)
    {
        List<RawMousePacket> movingPackets = new List<RawMousePacket>();
        if (packets == null)
        {
            return movingPackets;
        }

        foreach (RawMousePacket packet in packets)
        {
            if (packet != null
                && packet.TimestampMs >= minimumTimestampMs
                && packet.TimestampMs <= maximumTimestampMs
                && (packet.DeltaX != 0 || packet.DeltaY != 0))
            {
                movingPackets.Add(packet);
            }
        }

        return movingPackets;
    }

    private static List<RawMousePacket> ShiftPackets(IReadOnlyList<RawMousePacket> packets, double timeOffsetMs)
    {
        List<RawMousePacket> shiftedPackets = new List<RawMousePacket>();
        if (packets == null)
        {
            return shiftedPackets;
        }

        long tickOffset = (long)Math.Round(timeOffsetMs * 1000.0);
        foreach (RawMousePacket packet in packets)
        {
            if (packet != null)
            {
                shiftedPackets.Add(new RawMousePacket(
                    packet.DeviceId,
                    packet.TimestampTicks + tickOffset,
                    packet.TimestampMs + timeOffsetMs,
                    packet.CaptureSequence,
                    packet.DeltaX,
                    packet.DeltaY,
                    packet.ButtonFlags,
                    0,
                    0,
                    0u,
                    packet.TimingSequence));
            }
        }

        return shiftedPackets;
    }

    private static List<MotionProjection> ShiftProjectedPackets(IReadOnlyList<MotionProjection> projectedPackets, double timeOffsetMs)
    {
        List<MotionProjection> shiftedProjections = new List<MotionProjection>();
        if (projectedPackets == null)
        {
            return shiftedProjections;
        }

        foreach (MotionProjection projectedPacket in projectedPackets)
        {
            if (projectedPacket != null)
            {
                shiftedProjections.Add(new MotionProjection
                {
                    TimestampMs = projectedPacket.TimestampMs + timeOffsetMs,
                    SignedAxisComponent = projectedPacket.SignedAxisComponent,
                    AxisMagnitude = projectedPacket.AxisMagnitude,
                    SegmentLength = projectedPacket.SegmentLength
                });
            }
        }

        return shiftedProjections;
    }

    private static TimeWindow GetOverlapWindow(IReadOnlyList<RawMousePacket> sourcePackets, IReadOnlyList<RawMousePacket> targetPackets)
    {
        if (sourcePackets == null || targetPackets == null || sourcePackets.Count == 0 || targetPackets.Count == 0)
        {
            return null;
        }

        return BuildOverlapWindow(
            sourcePackets[0].TimestampMs,
            sourcePackets[sourcePackets.Count - 1].TimestampMs,
            targetPackets[0].TimestampMs,
            targetPackets[targetPackets.Count - 1].TimestampMs);
    }

    private static TimeWindow GetOverlapWindow(IReadOnlyList<MotionProjection> sourceProjected, IReadOnlyList<MotionProjection> targetProjected)
    {
        if (sourceProjected == null || targetProjected == null || sourceProjected.Count == 0 || targetProjected.Count == 0)
        {
            return null;
        }

        return BuildOverlapWindow(
            sourceProjected[0].TimestampMs,
            sourceProjected[sourceProjected.Count - 1].TimestampMs,
            targetProjected[0].TimestampMs,
            targetProjected[targetProjected.Count - 1].TimestampMs);
    }

    private static TimeWindow BuildOverlapWindow(double sourceStartMs, double sourceEndMs, double targetStartMs, double targetEndMs)
    {
        double overlapStartMs = Math.Max(sourceStartMs, targetStartMs);
        double overlapEndMs = Math.Min(sourceEndMs, targetEndMs);
        if (overlapEndMs <= overlapStartMs)
        {
            return null;
        }

        return new TimeWindow
        {
            StartMs = overlapStartMs,
            End = overlapEndMs
        };
    }

    private static double ComputeOverlapRatio(IReadOnlyList<RawMousePacket> sourcePackets, IReadOnlyList<RawMousePacket> targetPackets, TimeWindow overlapWindow)
    {
        if (sourcePackets == null || targetPackets == null || overlapWindow == null)
        {
            return 0.0;
        }

        double combinedStartMs = Math.Min(sourcePackets[0].TimestampMs, targetPackets[0].TimestampMs);
        double combinedEndMs = Math.Max(sourcePackets[sourcePackets.Count - 1].TimestampMs, targetPackets[targetPackets.Count - 1].TimestampMs);
        double combinedDurationMs = Math.Max(0.0, combinedEndMs - combinedStartMs);
        if (combinedDurationMs <= TinyEpsilon)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(1.0, overlapWindow.DurationMilliseconds / combinedDurationMs));
    }

    private static bool TryBuildTrimmedParticipantAxis(IReadOnlyList<RawMousePacket> packets, TimeWindow overlapWindow, ref double axisX, ref double axisY)
    {
        axisX = 0.0;
        axisY = 0.0;
        if (overlapWindow == null)
        {
            return false;
        }

        TimeWindow trimmedWindow = TrimWindow(overlapWindow, DominantAxisTrimFraction);
        if (trimmedWindow != null && TryBuildParticipantAxisForWindow(packets, trimmedWindow, ref axisX, ref axisY))
        {
            return true;
        }

        return TryBuildParticipantAxisForWindow(packets, overlapWindow, ref axisX, ref axisY);
    }

    private static bool TryBuildParticipantAxisForWindow(IReadOnlyList<RawMousePacket> packets, TimeWindow window, ref double axisX, ref double axisY)
    {
        if (window == null)
        {
            return false;
        }

        return TryBuildPrincipalAxis(packets, window.StartMs, window.End, ref axisX, ref axisY);
    }

    private static void AlignAxesToSameDirection(double sourceAxisX, double sourceAxisY, ref double targetAxisX, ref double targetAxisY)
    {
        double dotProduct = sourceAxisX * targetAxisX + sourceAxisY * targetAxisY;
        if (dotProduct < 0.0)
        {
            targetAxisX = -targetAxisX;
            targetAxisY = -targetAxisY;
        }
    }

    private static bool TryBuildPrincipalAxis(IReadOnlyList<RawMousePacket> packets, double startMs, double endMs, ref double axisX, ref double axisY)
    {
        axisX = 0.0;
        axisY = 0.0;
        if (packets == null)
        {
            return false;
        }

        double xx = 0.0;
        double xy = 0.0;
        double yy = 0.0;
        double netX = 0.0;
        double netY = 0.0;
        foreach (RawMousePacket packet in packets)
        {
            if (packet == null || packet.TimestampMs < startMs || packet.TimestampMs > endMs)
            {
                continue;
            }

            double deltaX = packet.DeltaX;
            double deltaY = packet.DeltaY;
            xx += deltaX * deltaX;
            xy += deltaX * deltaY;
            yy += deltaY * deltaY;
            netX += deltaX;
            netY += deltaY;
        }

        if (xx + yy <= TinyEpsilon)
        {
            return false;
        }

        double angleRadians = 0.5 * Math.Atan2(2.0 * xy, xx - yy);
        axisX = Math.Cos(angleRadians);
        axisY = Math.Sin(angleRadians);
        if (axisX * netX + axisY * netY < 0.0)
        {
            axisX = -axisX;
            axisY = -axisY;
        }

        return true;
    }

    private static List<MotionProjection> ProjectPackets(IReadOnlyList<RawMousePacket> packets, double startMs, double endMs, double axisX, double axisY)
    {
        List<MotionProjection> projectedPackets = new List<MotionProjection>();
        if (packets == null)
        {
            return projectedPackets;
        }

        foreach (RawMousePacket packet in packets)
        {
            if (packet != null && packet.TimestampMs >= startMs && packet.TimestampMs <= endMs)
            {
                double signedAxisComponent = packet.DeltaX * axisX + packet.DeltaY * axisY;
                double segmentLength = ComputeSegmentLength(packet);
                projectedPackets.Add(new MotionProjection
                {
                    TimestampMs = packet.TimestampMs,
                    SignedAxisComponent = signedAxisComponent,
                    AxisMagnitude = Math.Abs(signedAxisComponent),
                    SegmentLength = segmentLength
                });
            }
        }

        return projectedPackets;
    }

    private static double ComputeAxisPurity(IReadOnlyList<MotionProjection> projectedPackets)
    {
        if (projectedPackets == null || projectedPackets.Count == 0)
        {
            return 0.0;
        }

        double axisMagnitudeTotal = 0.0;
        double segmentLengthTotal = 0.0;
        foreach (MotionProjection projectedPacket in projectedPackets)
        {
            if (projectedPacket != null)
            {
                axisMagnitudeTotal += projectedPacket.AxisMagnitude;
                segmentLengthTotal += projectedPacket.SegmentLength;
            }
        }

        if (segmentLengthTotal <= TinyEpsilon)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(1.0, axisMagnitudeTotal / segmentLengthTotal));
    }

    private static LagAlignmentResult TryEstimateBestLagOffset(IReadOnlyList<MotionProjection> sourceProjected, IReadOnlyList<MotionProjection> targetProjected, double minimumRequiredCorrelation)
    {
        if (sourceProjected == null || targetProjected == null || sourceProjected.Count == 0 || targetProjected.Count == 0)
        {
            return null;
        }

        LagAlignmentResult bestAlignment = null;
        List<LagAlignmentResult> candidates = new List<LagAlignmentResult>();
        for (double offsetMs = -LagSearchMaximumOffsetMilliseconds; offsetMs <= LagSearchMaximumOffsetMilliseconds + TinyEpsilon; offsetMs += LagSearchStepMilliseconds)
        {
            List<MotionProjection> shiftedTargetProjection = ShiftProjectedPackets(targetProjected, offsetMs);
            TimeWindow overlapWindow = GetOverlapWindow(sourceProjected, shiftedTargetProjection);
            if (overlapWindow == null)
            {
                continue;
            }

            int binCount = DetermineLagCorrelationBinCount(overlapWindow);
            if (binCount <= 0)
            {
                continue;
            }

            List<MotionBin> sourceBins = BuildTimeBins(sourceProjected, overlapWindow.StartMs, overlapWindow.End, binCount);
            List<MotionBin> targetBins = BuildTimeBins(shiftedTargetProjection, overlapWindow.StartMs, overlapWindow.End, binCount);
            double correlation = ComputeSignedAxisCorrelation(sourceBins, targetBins);
            double oppositeDirectionRatio = ComputeOppositeDirectionRatio(sourceBins, targetBins);
            LagAlignmentResult candidate = new LagAlignmentResult
            {
                TimeOffsetMs = offsetMs,
                Correlation = correlation,
                OppositeDirectionRatio = oppositeDirectionRatio
            };
            candidates.Add(candidate);

            if (IsBetterLagAlignment(candidate, bestAlignment))
            {
                bestAlignment = candidate;
            }
        }

        if (bestAlignment == null || bestAlignment.Correlation < minimumRequiredCorrelation)
        {
            return null;
        }

        if (IsUnreliableBoundaryLagAlignment(bestAlignment, candidates))
        {
            return null;
        }

        return bestAlignment;
    }

    private static bool IsBetterLagAlignment(LagAlignmentResult candidate, LagAlignmentResult currentBest)
    {
        if (candidate == null)
        {
            return false;
        }
        if (currentBest == null)
        {
            return true;
        }
        if (candidate.Correlation > currentBest.Correlation + TinyEpsilon)
        {
            return true;
        }
        if (Math.Abs(candidate.Correlation - currentBest.Correlation) > TinyEpsilon)
        {
            return false;
        }
        if (candidate.OppositeDirectionRatio < currentBest.OppositeDirectionRatio - TinyEpsilon)
        {
            return true;
        }
        if (Math.Abs(candidate.OppositeDirectionRatio - currentBest.OppositeDirectionRatio) > TinyEpsilon)
        {
            return false;
        }

        return Math.Abs(candidate.TimeOffsetMs) < Math.Abs(currentBest.TimeOffsetMs);
    }

    private static bool IsUnreliableBoundaryLagAlignment(LagAlignmentResult bestAlignment, IReadOnlyList<LagAlignmentResult> candidates)
    {
        if (bestAlignment == null || candidates == null)
        {
            return false;
        }
        if (Math.Abs(Math.Abs(bestAlignment.TimeOffsetMs) - LagSearchMaximumOffsetMilliseconds) > TinyEpsilon)
        {
            return false;
        }

        double neighborOffsetMs = bestAlignment.TimeOffsetMs - Math.Sign(bestAlignment.TimeOffsetMs) * LagSearchStepMilliseconds;
        LagAlignmentResult neighbor = candidates.FirstOrDefault(candidate => Math.Abs(candidate.TimeOffsetMs - neighborOffsetMs) <= TinyEpsilon);
        return neighbor != null && bestAlignment.Correlation > neighbor.Correlation + LagBoundaryContinuationDelta;
    }

    private static int DetermineLagCorrelationBinCount(TimeWindow overlapWindow)
    {
        if (overlapWindow == null || overlapWindow.DurationMilliseconds <= TinyEpsilon)
        {
            return 0;
        }

        int requiredBinCount = (int)Math.Ceiling(overlapWindow.DurationMilliseconds / LagCorrelationBinSizeMilliseconds);
        return Math.Max(8, Math.Min(96, requiredBinCount));
    }

    private static double ComputeOppositeDirectionRatio(IReadOnlyList<MotionProjection> sourceProjected, IReadOnlyList<MotionProjection> targetProjected, double startMs, double endMs, int binCount)
    {
        if (sourceProjected == null || targetProjected == null || binCount <= 0)
        {
            return 1.0;
        }
        List<MotionBin> sourceBins = BuildTimeBins(sourceProjected, startMs, endMs, binCount);
        List<MotionBin> targetBins = BuildTimeBins(targetProjected, startMs, endMs, binCount);
        return ComputeOppositeDirectionRatio(sourceBins, targetBins);
    }

    private static double ComputeOppositeDirectionRatio(IReadOnlyList<MotionBin> sourceBins, IReadOnlyList<MotionBin> targetBins)
    {
        if (sourceBins == null || targetBins == null)
        {
            return 1.0;
        }

        double sharedMagnitudeTotal = 0.0;
        double oppositeDirectionMagnitude = 0.0;
        int comparedBinCount = Math.Min(sourceBins.Count, targetBins.Count);
        for (int binIndex = 0; binIndex < comparedBinCount; binIndex++)
        {
            MotionBin sourceBin = sourceBins[binIndex];
            MotionBin targetBin = targetBins[binIndex];
            if (sourceBin == null || targetBin == null || sourceBin.AxisMagnitude <= TinyEpsilon || targetBin.AxisMagnitude <= TinyEpsilon)
            {
                continue;
            }

            double sharedMagnitude = Math.Min(sourceBin.AxisMagnitude, targetBin.AxisMagnitude);
            if (sharedMagnitude <= TinyEpsilon)
            {
                continue;
            }

            sharedMagnitudeTotal += sharedMagnitude;
            if (Math.Sign(sourceBin.SignedAxisComponent) != Math.Sign(targetBin.SignedAxisComponent))
            {
                oppositeDirectionMagnitude += sharedMagnitude;
            }
        }

        if (sharedMagnitudeTotal <= TinyEpsilon)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(1.0, oppositeDirectionMagnitude / sharedMagnitudeTotal));
    }

    private static double ComputeSignedAxisCorrelation(IReadOnlyList<MotionBin> sourceBins, IReadOnlyList<MotionBin> targetBins)
    {
        if (sourceBins == null || targetBins == null || sourceBins.Count == 0 || targetBins.Count == 0)
        {
            return 0.0;
        }

        double dotProduct = 0.0;
        double sourceEnergy = 0.0;
        double targetEnergy = 0.0;
        int comparedBinCount = Math.Min(sourceBins.Count, targetBins.Count);
        for (int binIndex = 0; binIndex < comparedBinCount; binIndex++)
        {
            double sourceValue = sourceBins[binIndex]?.SignedAxisComponent ?? 0.0;
            double targetValue = targetBins[binIndex]?.SignedAxisComponent ?? 0.0;
            dotProduct += sourceValue * targetValue;
            sourceEnergy += sourceValue * sourceValue;
            targetEnergy += targetValue * targetValue;
        }

        if (sourceEnergy <= TinyEpsilon || targetEnergy <= TinyEpsilon)
        {
            return 0.0;
        }

        double correlation = dotProduct / (Math.Sqrt(sourceEnergy) * Math.Sqrt(targetEnergy));
        return Math.Max(-1.0, Math.Min(1.0, correlation));
    }

    private static TimeWindow TrimWindow(TimeWindow window, double trimFraction)
    {
        if (window == null)
        {
            return null;
        }
        if (trimFraction <= TinyEpsilon)
        {
            return new TimeWindow
            {
                StartMs = window.StartMs,
                End = window.End
            };
        }

        double trimDurationMs = window.DurationMilliseconds * trimFraction;
        double trimmedStartMs = window.StartMs + trimDurationMs;
        double trimmedEndMs = window.End - trimDurationMs;
        if (trimmedEndMs <= trimmedStartMs)
        {
            return null;
        }

        return new TimeWindow
        {
            StartMs = trimmedStartMs,
            End = trimmedEndMs
        };
    }

    private static List<MotionBin> BuildTimeBins(IReadOnlyList<MotionProjection> projectedPackets, double startMs, double endMs, int binCount)
    {
        List<MotionBin> bins = new List<MotionBin>();
        if (binCount <= 0)
        {
            return bins;
        }

        for (int binIndex = 0; binIndex < binCount; binIndex++)
        {
            bins.Add(new MotionBin());
        }

        double durationMs = Math.Max(0.0, endMs - startMs);
        if (projectedPackets == null || durationMs <= TinyEpsilon)
        {
            return bins;
        }

        foreach (MotionProjection projectedPacket in projectedPackets)
        {
            if (projectedPacket == null || projectedPacket.TimestampMs < startMs || projectedPacket.TimestampMs > endMs)
            {
                continue;
            }

            int binIndex = (int)Math.Floor((projectedPacket.TimestampMs - startMs) / durationMs * binCount);
            if (binIndex < 0)
            {
                binIndex = 0;
            }
            else if (binIndex >= binCount)
            {
                binIndex = binCount - 1;
            }

            bins[binIndex].AxisMagnitude += projectedPacket.AxisMagnitude;
            bins[binIndex].SignedAxisComponent += projectedPacket.SignedAxisComponent;
        }

        return bins;
    }

    private static bool TryBuildNormalizedDistanceBins(IReadOnlyList<MotionProjection> sourceProjected, IReadOnlyList<MotionProjection> targetProjected, double startMs, double endMs, int binCount, ref List<NormalizedDistanceBin> sourceBins, ref List<NormalizedDistanceBin> targetBins)
    {
        sourceBins = CreateNormalizedDistanceBins(binCount);
        targetBins = CreateNormalizedDistanceBins(binCount);
        if (binCount <= 0 || sourceProjected == null || targetProjected == null)
        {
            return false;
        }

        int oversampledBinCount = Math.Max(binCount * 4, binCount);
        List<MotionBin> sourceOversampledBins = BuildTimeBins(sourceProjected, startMs, endMs, oversampledBinCount);
        List<MotionBin> targetOversampledBins = BuildTimeBins(targetProjected, startMs, endMs, oversampledBinCount);
        if (sourceOversampledBins.Count != oversampledBinCount || targetOversampledBins.Count != oversampledBinCount)
        {
            return false;
        }

        double sourceTotalMagnitude = sourceOversampledBins.Sum(bin => bin?.AxisMagnitude ?? 0.0);
        double targetTotalMagnitude = targetOversampledBins.Sum(bin => bin?.AxisMagnitude ?? 0.0);
        if (sourceTotalMagnitude <= TinyEpsilon || targetTotalMagnitude <= TinyEpsilon)
        {
            return false;
        }

        double sourceNormalizedDistance = 0.0;
        double targetNormalizedDistance = 0.0;
        for (int oversampledBinIndex = 0; oversampledBinIndex < oversampledBinCount; oversampledBinIndex++)
        {
            MotionBin sourceBin = sourceOversampledBins[oversampledBinIndex];
            MotionBin targetBin = targetOversampledBins[oversampledBinIndex];
            double sourceMagnitude = sourceBin?.AxisMagnitude ?? 0.0;
            double targetMagnitude = targetBin?.AxisMagnitude ?? 0.0;
            if (sourceMagnitude <= TinyEpsilon && targetMagnitude <= TinyEpsilon)
            {
                continue;
            }

            double normalizedStart = (sourceNormalizedDistance + targetNormalizedDistance) / 2.0;
            sourceNormalizedDistance = Math.Min(1.0, sourceNormalizedDistance + sourceMagnitude / sourceTotalMagnitude);
            targetNormalizedDistance = Math.Min(1.0, targetNormalizedDistance + targetMagnitude / targetTotalMagnitude);
            double normalizedEnd = (sourceNormalizedDistance + targetNormalizedDistance) / 2.0;

            if (sourceMagnitude > TinyEpsilon)
            {
                DistributeProjectedSegmentToBins(sourceBins, normalizedStart, normalizedEnd, sourceMagnitude, sourceBin.SignedAxisComponent);
            }
            if (targetMagnitude > TinyEpsilon)
            {
                DistributeProjectedSegmentToBins(targetBins, normalizedStart, normalizedEnd, targetMagnitude, targetBin.SignedAxisComponent);
            }
        }

        return true;
    }

    private static SimilarityFitResult TryFitSimilarityTransform(IReadOnlyList<RawMousePacket> sourcePackets, IReadOnlyList<RawMousePacket> targetPackets, double startMs, double endMs, int binCount, int minimumValidBins, double minimumSourceBinMagnitude, double minimumTargetBinMagnitude)
    {
        if (binCount <= 0 || sourcePackets == null || targetPackets == null)
        {
            return null;
        }

        List<VectorBin> sourceBins = BuildVectorTimeBins(sourcePackets, startMs, endMs, binCount);
        List<VectorBin> targetBins = BuildVectorTimeBins(targetPackets, startMs, endMs, binCount);
        if (sourceBins.Count != binCount || targetBins.Count != binCount)
        {
            return null;
        }

        double dotTotal = 0.0;
        double crossTotal = 0.0;
        double targetEnergy = 0.0;
        double sourceEnergy = 0.0;
        int validBinCount = 0;
        for (int binIndex = 0; binIndex < binCount; binIndex++)
        {
            VectorBin sourceBin = sourceBins[binIndex];
            VectorBin targetBin = targetBins[binIndex];
            if (sourceBin == null || targetBin == null || sourceBin.Magnitude < minimumSourceBinMagnitude || targetBin.Magnitude < minimumTargetBinMagnitude)
            {
                continue;
            }

            dotTotal += sourceBin.X * targetBin.X + sourceBin.Y * targetBin.Y;
            crossTotal += targetBin.X * sourceBin.Y - targetBin.Y * sourceBin.X;
            targetEnergy += targetBin.X * targetBin.X + targetBin.Y * targetBin.Y;
            sourceEnergy += sourceBin.X * sourceBin.X + sourceBin.Y * sourceBin.Y;
            validBinCount++;
        }

        if (validBinCount < minimumValidBins || targetEnergy <= TinyEpsilon || sourceEnergy <= TinyEpsilon)
        {
            return null;
        }

        double transformA = dotTotal / targetEnergy;
        double transformB = crossTotal / targetEnergy;
        double scale = Math.Sqrt(transformA * transformA + transformB * transformB);
        if (scale <= TinyEpsilon || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return null;
        }

        double residualEnergy = 0.0;
        double comparedSourceEnergy = 0.0;
        for (int binIndex = 0; binIndex < binCount; binIndex++)
        {
            VectorBin sourceBin = sourceBins[binIndex];
            VectorBin targetBin = targetBins[binIndex];
            if (sourceBin == null || targetBin == null || sourceBin.Magnitude < minimumSourceBinMagnitude || targetBin.Magnitude < minimumTargetBinMagnitude)
            {
                continue;
            }

            double fittedX = transformA * targetBin.X - transformB * targetBin.Y;
            double fittedY = transformB * targetBin.X + transformA * targetBin.Y;
            double residualX = sourceBin.X - fittedX;
            double residualY = sourceBin.Y - fittedY;
            residualEnergy += residualX * residualX + residualY * residualY;
            comparedSourceEnergy += sourceBin.X * sourceBin.X + sourceBin.Y * sourceBin.Y;
        }

        if (comparedSourceEnergy <= TinyEpsilon)
        {
            return null;
        }

        return new SimilarityFitResult
        {
            RotationDegrees = Math.Atan2(transformB, transformA) * 180.0 / Math.PI,
            NormalizedResidual = Math.Sqrt(residualEnergy / comparedSourceEnergy)
        };
    }

    private static List<VectorBin> BuildVectorTimeBins(IReadOnlyList<RawMousePacket> packets, double startMs, double endMs, int binCount)
    {
        List<VectorBin> bins = new List<VectorBin>();
        if (binCount <= 0)
        {
            return bins;
        }

        for (int binIndex = 0; binIndex < binCount; binIndex++)
        {
            bins.Add(new VectorBin());
        }

        double durationMs = Math.Max(0.0, endMs - startMs);
        if (packets == null || durationMs <= TinyEpsilon)
        {
            return bins;
        }

        foreach (RawMousePacket packet in packets)
        {
            if (packet == null || packet.TimestampMs < startMs || packet.TimestampMs > endMs)
            {
                continue;
            }

            int binIndex = (int)Math.Floor((packet.TimestampMs - startMs) / durationMs * binCount);
            if (binIndex < 0)
            {
                binIndex = 0;
            }
            else if (binIndex >= binCount)
            {
                binIndex = binCount - 1;
            }

            bins[binIndex].X += packet.DeltaX;
            bins[binIndex].Y += packet.DeltaY;
        }

        return bins;
    }

    private static List<NormalizedDistanceBin> CreateNormalizedDistanceBins(int binCount)
    {
        List<NormalizedDistanceBin> bins = new List<NormalizedDistanceBin>();
        if (binCount <= 0)
        {
            return bins;
        }

        for (int binIndex = 0; binIndex < binCount; binIndex++)
        {
            bins.Add(new NormalizedDistanceBin());
        }

        return bins;
    }

    private static void DistributeProjectedSegmentToBins(IList<NormalizedDistanceBin> bins, double normalizedStart, double normalizedEnd, double axisMagnitude, double signedAxisComponent)
    {
        if (bins == null || bins.Count == 0)
        {
            return;
        }

        double clippedStart = Math.Max(0.0, Math.Min(1.0, normalizedStart));
        double clippedEnd = Math.Max(0.0, Math.Min(1.0, normalizedEnd));
        if (clippedEnd <= clippedStart || axisMagnitude <= TinyEpsilon)
        {
            return;
        }

        double clippedLength = clippedEnd - clippedStart;
        int firstBinIndex = Math.Max(0, (int)Math.Floor(clippedStart * bins.Count));
        int lastBinIndex = Math.Min(bins.Count - 1, (int)Math.Floor(Math.Max(clippedStart, clippedEnd - TinyEpsilon) * bins.Count));
        for (int binIndex = firstBinIndex; binIndex <= lastBinIndex; binIndex++)
        {
            double binStart = (double)binIndex / bins.Count;
            double binEnd = (double)(binIndex + 1) / bins.Count;
            double overlapStart = Math.Max(clippedStart, binStart);
            double overlapLength = Math.Min(clippedEnd, binEnd) - overlapStart;
            if (overlapLength <= TinyEpsilon)
            {
                continue;
            }

            double overlapFraction = overlapLength / clippedLength;
            bins[binIndex].AxisMagnitude += axisMagnitude * overlapFraction;
            bins[binIndex].SignedAxisComponent += signedAxisComponent * overlapFraction;
        }
    }

    private static double ComputeWeightedMedian(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        if (values == null || weights == null || values.Count == 0 || values.Count != weights.Count)
        {
            return 0.0;
        }

        List<(double Value, double Weight)> weightedValues = new List<(double Value, double Weight)>();
        for (int index = 0; index < values.Count; index++)
        {
            if (weights[index] > TinyEpsilon)
            {
                weightedValues.Add((values[index], weights[index]));
            }
        }

        if (weightedValues.Count == 0)
        {
            return 0.0;
        }

        (double Value, double Weight)[] orderedValues = weightedValues.OrderBy(entry => entry.Value).ToArray();
        double halfTotalWeight = orderedValues.Sum(entry => entry.Weight) / 2.0;
        double cumulativeWeight = 0.0;
        foreach ((double value, double weight) in orderedValues)
        {
            cumulativeWeight += weight;
            if (cumulativeWeight >= halfTotalWeight)
            {
                return value;
            }
        }

        return orderedValues[orderedValues.Length - 1].Value;
    }

    private static RobustDispersionResult ComputeWeightedMadNormalizedDispersion(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        RobustDispersionResult dispersion = new RobustDispersionResult
        {
            MedianScale = 0.0,
            NormalizedDispersion = double.PositiveInfinity
        };
        if (values == null || weights == null || values.Count == 0 || values.Count != weights.Count)
        {
            return dispersion;
        }

        double medianScale = ComputeWeightedMedian(values, weights);
        if (Math.Abs(medianScale) <= TinyEpsilon)
        {
            return dispersion;
        }

        List<double> absoluteDeviations = new List<double>();
        for (int index = 0; index < values.Count; index++)
        {
            absoluteDeviations.Add(Math.Abs(values[index] - medianScale));
        }

        dispersion.MedianScale = medianScale;
        dispersion.NormalizedDispersion = ComputeWeightedMedian(absoluteDeviations, weights) / Math.Abs(medianScale);
        return dispersion;
    }

    private static double ComputeWeightedCoefficientOfVariation(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        if (values == null || weights == null || values.Count == 0 || values.Count != weights.Count)
        {
            return double.PositiveInfinity;
        }

        double totalWeight = 0.0;
        double weightedValueTotal = 0.0;
        for (int index = 0; index < values.Count; index++)
        {
            double weight = Math.Max(0.0, weights[index]);
            totalWeight += weight;
            weightedValueTotal += values[index] * weight;
        }
        if (totalWeight <= TinyEpsilon)
        {
            return double.PositiveInfinity;
        }

        double weightedMean = weightedValueTotal / totalWeight;
        if (Math.Abs(weightedMean) <= TinyEpsilon)
        {
            return double.PositiveInfinity;
        }

        double weightedVariance = 0.0;
        for (int index = 0; index < values.Count; index++)
        {
            double weight = Math.Max(0.0, weights[index]);
            double deviation = values[index] - weightedMean;
            weightedVariance += weight * deviation * deviation;
        }
        weightedVariance /= totalWeight;
        return Math.Sqrt(weightedVariance) / Math.Abs(weightedMean);
    }

    private static double EstimateTailEnergy(RoundAccumulator round, double sourceAxisX, double sourceAxisY, double targetAxisX, double targetAxisY, double targetTimeOffsetMs, double nowMs)
    {
        if (round == null)
        {
            return 1.0;
        }

        double windowStartMs = round.StabilizingStartedMs.HasValue ? round.StabilizingStartedMs.Value : nowMs;
        if (nowMs - windowStartMs < TailEnergyWindowMilliseconds)
        {
            return 1.0;
        }

        List<MotionProjection> sourceProjection = ProjectPackets(FilterNonZeroPackets(round.Source.Packets), windowStartMs, nowMs, sourceAxisX, sourceAxisY);
        List<MotionProjection> targetProjection = ProjectPackets(ShiftPackets(FilterNonZeroPackets(round.Target.Packets), targetTimeOffsetMs), windowStartMs, nowMs, targetAxisX, targetAxisY);
        if (sourceProjection.Count == 0 || targetProjection.Count == 0)
        {
            return 1.0;
        }

        double sourceTailEnergyRatio = EstimateParticipantTailEnergyRatio(sourceProjection, windowStartMs, nowMs);
        double targetTailEnergyRatio = EstimateParticipantTailEnergyRatio(targetProjection, windowStartMs, nowMs);
        return Math.Max(sourceTailEnergyRatio, targetTailEnergyRatio);
    }

    private static double EstimateParticipantTailEnergyRatio(IReadOnlyList<MotionProjection> projectedPackets, double windowStartMs, double nowMs)
    {
        if (projectedPackets == null || projectedPackets.Count == 0)
        {
            return 1.0;
        }
        if (nowMs - windowStartMs < TailEnergyWindowMilliseconds)
        {
            return 1.0;
        }

        double tailWindowStartMs = nowMs - TailEnergyWindowMilliseconds;
        double currentTailIntensity = ComputeAxisIntensity(projectedPackets, tailWindowStartMs, nowMs);
        double maximumHistoricalIntensity = 0.0;
        IOrderedEnumerable<double> evaluationTimes = projectedPackets
            .Where(packet => packet != null && packet.TimestampMs >= windowStartMs)
            .Select(packet => packet.TimestampMs)
            .Concat(new[] { nowMs })
            .Distinct()
            .OrderBy(timestamp => timestamp);

        foreach (double evaluationTimeMs in evaluationTimes)
        {
            if (evaluationTimeMs < windowStartMs + TailEnergyWindowMilliseconds)
            {
                continue;
            }

            double historicalWindowStartMs = evaluationTimeMs - TailEnergyWindowMilliseconds;
            maximumHistoricalIntensity = Math.Max(maximumHistoricalIntensity, ComputeAxisIntensity(projectedPackets, historicalWindowStartMs, evaluationTimeMs));
        }

        if (maximumHistoricalIntensity <= TinyEpsilon)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(1.0, currentTailIntensity / maximumHistoricalIntensity));
    }

    private static double ComputeAxisIntensity(IReadOnlyList<MotionProjection> projectedPackets, double windowStartMs, double windowEndMs)
    {
        if (projectedPackets == null)
        {
            return 0.0;
        }

        double windowDurationMs = Math.Max(TinyEpsilon, windowEndMs - windowStartMs);
        double axisMagnitudeTotal = 0.0;
        foreach (MotionProjection projectedPacket in projectedPackets)
        {
            if (projectedPacket != null && projectedPacket.TimestampMs >= windowStartMs && projectedPacket.TimestampMs <= windowEndMs)
            {
                axisMagnitudeTotal += projectedPacket.AxisMagnitude;
            }
        }

        return axisMagnitudeTotal / windowDurationMs;
    }

    private static double ComputeAngleBetweenVectorsDegrees(double sourceX, double sourceY, double targetX, double targetY)
    {
        double sourceMagnitude = Math.Sqrt(sourceX * sourceX + sourceY * sourceY);
        double targetMagnitude = Math.Sqrt(targetX * targetX + targetY * targetY);
        if (sourceMagnitude <= TinyEpsilon || targetMagnitude <= TinyEpsilon)
        {
            return 180.0;
        }

        double cosine = (sourceX * targetX + sourceY * targetY) / (sourceMagnitude * targetMagnitude);
        cosine = Math.Max(-1.0, Math.Min(1.0, cosine));
        return Math.Acos(cosine) * 180.0 / Math.PI;
    }

    private static double ComputeSegmentLength(RawMousePacket packet)
    {
        if (packet == null)
        {
            return 0.0;
        }

        double deltaX = packet.DeltaX;
        double deltaY = packet.DeltaY;
        double segmentLength = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (double.IsNaN(segmentLength) || double.IsInfinity(segmentLength))
        {
            return 0.0;
        }

        return Math.Max(0.0, segmentLength);
    }

    private static double ResolveLatestObservedTime(RoundAccumulator round, double fallbackTimeMs)
    {
        double latestObservedTimeMs = double.NegativeInfinity;
        if (round != null)
        {
            if (round.Source != null && round.Source.LastPacketTimeMs.HasValue)
            {
                latestObservedTimeMs = Math.Max(latestObservedTimeMs, round.Source.LastPacketTimeMs.Value);
            }
            if (round.Target != null && round.Target.LastPacketTimeMs.HasValue)
            {
                latestObservedTimeMs = Math.Max(latestObservedTimeMs, round.Target.LastPacketTimeMs.Value);
            }
        }

        if (!double.IsNaN(fallbackTimeMs) && !double.IsInfinity(fallbackTimeMs))
        {
            latestObservedTimeMs = Math.Max(latestObservedTimeMs, fallbackTimeMs);
        }
        if (double.IsNegativeInfinity(latestObservedTimeMs))
        {
            return 0.0;
        }

        return latestObservedTimeMs;
    }

    private static double ResolveValidTimestamp(double timestampMs)
    {
        if (double.IsNaN(timestampMs) || double.IsInfinity(timestampMs))
        {
            return 0.0;
        }

        return timestampMs;
    }

    private static double ComputeMedian(IEnumerable<double> values)
    {
        if (values == null)
        {
            return 0.0;
        }

        double[] sortedValues = values.OrderBy(value => value).ToArray();
        if (sortedValues.Length == 0)
        {
            return 0.0;
        }

        int middleIndex = sortedValues.Length / 2;
        if (sortedValues.Length % 2 == 1)
        {
            return sortedValues[middleIndex];
        }

        return (sortedValues[middleIndex - 1] + sortedValues[middleIndex]) / 2.0;
    }

    private void FailCurrentRound(SensitivityMatchRoundFailureReason reason)
    {
        if (_activeRound != null)
        {
            _lastRoundFailureReason = reason;
            _lastRoundFailureIndex = _activeRound.RoundIndex;
            _activeRound = null;
        }
    }
}








