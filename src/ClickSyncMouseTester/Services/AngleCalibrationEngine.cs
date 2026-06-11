using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

public class AngleCalibrationEngine
{
    private sealed class StrokeBuilder
    {
        public List<MutableTracePoint> Points { get; }

        public double StartTimestampMs { get; set; }

        public double LastTimestampMs { get; set; }

        public double PathLength { get; set; }

        public double NetDx { get; set; }

        public double NetDy { get; set; }

        public int DirectionSign { get; set; }

        public int PacketCount { get; set; }

        public StrokeBuilder(double startX, double startY, double startTimestampMs)
        {
            Points = new List<MutableTracePoint>
            {
                new MutableTracePoint(startX, startY)
            };
            StartTimestampMs = startTimestampMs;
            LastTimestampMs = startTimestampMs;
        }
    }

    private struct BufferedSegment
    {
        public double DeltaX { get; set; }

        public double DeltaY { get; set; }

        public double TimestampMs { get; set; }

        public double EndX { get; set; }

        public double EndY { get; set; }
    }

    private sealed class StrokeFitSample
    {
        public double AngleDegrees { get; set; }

        public double Weight { get; set; }

        public double TimestampMs { get; set; }

        public StrokeFitSample(double angleDegrees, double weight, double timestampMs)
        {
            AngleDegrees = angleDegrees;
            Weight = weight;
            TimestampMs = timestampMs;
        }
    }

    private sealed class EvaluatedStroke
    {
        public int DirectionSign { get; set; }

        public double AngleDegrees { get; set; }

        public double Weight { get; set; }

        public double TimestampMs { get; set; }

        public EvaluatedStroke(int directionSign, double angleDegrees, double weight, double timestampMs)
        {
            DirectionSign = directionSign;
            AngleDegrees = angleDegrees;
            Weight = weight;
            TimestampMs = timestampMs;
        }
    }

    private sealed class SideComputation
    {
        public int AcceptedCount { get; set; }

        public int InlierCount { get; set; }

        public double? MadDegrees { get; set; }

        public double? ResolvedAngleDegrees { get; set; }

        public double TotalInlierWeight { get; set; }
    }

    private sealed class FitComputation
    {
        public SideComputation Left { get; }

        public SideComputation Right { get; }

        public double? CandidateAngleDegrees { get; }

        public FitComputation(SideComputation left, SideComputation right, double? candidateAngleDegrees)
        {
            Left = left;
            Right = right;
            CandidateAngleDegrees = candidateAngleDegrees;
        }
    }

    private sealed class QualityComputation
    {
        public AngleCalibrationQualityLevel Level { get; }

        public AngleCalibrationQualityReason Reason { get; }

        public int Score { get; }

        public QualityComputation(AngleCalibrationQualityLevel level, AngleCalibrationQualityReason reason, int score)
        {
            Level = level;
            Reason = reason;
            Score = score;
        }
    }

    private struct CandidateAngleSample
    {
        public double Value { get; set; }

        public double TimestampMs { get; set; }
    }

    private struct WeightedValue
    {
        public double Value { get; set; }

        public double Weight { get; set; }
    }

    private sealed class MutableTracePoint
    {
        public double X { get; set; }

        public double Y { get; set; }

        public MutableTracePoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    private const double SampleMinimumSegmentLength = 1.5;

    private const double SampleMinimumHorizontalRatio = 0.75;

    private const double StrokeFlipMinimumNetDx = 220.0;

    private const double StrokeFlipMinimumDurationMilliseconds = 90.0;

    private const double StrokeFlipConfirmationDx = 12.0;

    private const double StrokeMinimumNetLength = 220.0;

    private const double StrokeMinimumDurationMilliseconds = 90.0;

    private const double StrokeMaximumDurationMilliseconds = 1500.0;

    private const double StrokeMinimumHorizontalRatio = 0.75;

    private const double StrokeMinimumStraightness = 0.82;

    private const int StrokeMinimumPoints = 6;

    private const double StrokeMinimumResidualRms = 2.0;

    private const double StrokeResidualScale = 0.045;

    private const double StrokeWeightCap = 1200.0;

    private const int SideMaximumAcceptedStrokes = 40;

    private const int SideMinimumInlierCount = 4;

    private const double SideMinimumWeight = 4.0;

    private const double SideOutlierFloorDegrees = 1.2;

    private const double SideOutlierMadScale = 2.5;

    private const int ResultSwipeThreshold = 30;

    private const double StabilityWindowMilliseconds = 1500.0;

    private const int StabilityMaximumSamples = 12;

    private const int MaximumHistoricalStrokes = 40;

    private const int MaximumSnapshotPointsPerStroke = 320;

    private const double TinyEpsilon = 1E-09;

    private readonly List<AngleCalibrationTraceStroke> _historicalStrokes;

    private readonly List<CandidateAngleSample> _candidateAngles;

    private readonly List<StrokeFitSample> _leftStrokeSamples;

    private readonly List<StrokeFitSample> _rightStrokeSamples;

    private readonly List<BufferedSegment> _pendingOppositeSegments;

    private StrokeBuilder _activeStroke;

    private bool _isLocked;

    private double _positionX;

    private double _positionY;

    private int _swipeCount;

    private int _sampleCount;

    private double _oppositeDxAccum;

    private int _lastAcceptedStrokeDirectionSign;

    public AngleCalibrationEngine()
    {
        _historicalStrokes = new List<AngleCalibrationTraceStroke>();
        _candidateAngles = new List<CandidateAngleSample>();
        _leftStrokeSamples = new List<StrokeFitSample>();
        _rightStrokeSamples = new List<StrokeFitSample>();
        _pendingOppositeSegments = new List<BufferedSegment>();
        Reset();
    }

    public void Reset()
    {
        _historicalStrokes.Clear();
        _candidateAngles.Clear();
        _leftStrokeSamples.Clear();
        _rightStrokeSamples.Clear();
        _pendingOppositeSegments.Clear();
        _activeStroke = null;
        _isLocked = false;
        _positionX = 0.0;
        _positionY = 0.0;
        _swipeCount = 0;
        _sampleCount = 0;
        _oppositeDxAccum = 0.0;
        _lastAcceptedStrokeDirectionSign = 0;
    }

    public void SetLocked(bool isLocked, double nowMs = double.NaN)
    {
        if (!isLocked)
        {
            DiscardInProgressStroke();
        }
        _isLocked = isLocked;
    }

    public void PushPacket(RawMousePacket packet)
    {
        if (packet != null && (packet.DeltaX != 0 || packet.DeltaY != 0))
        {
            Ingest(packet.DeltaX, packet.DeltaY, packet.TimestampMs);
        }
    }

    public AngleCalibrationRenderFrame CreateRenderFrame(double nowMs)
    {
        PruneCandidateAngles(nowMs);
        FitComputation fitComputation = ComputeFit();
        bool hasData = HasData();
        double? recommendedAngleDegrees = null;
        if (_swipeCount >= 30 && fitComputation.CandidateAngleDegrees.HasValue)
        {
            recommendedAngleDegrees = ApplyDisplayAngle(fitComputation.CandidateAngleDegrees.Value);
        }
        double? stabilityDegrees = ComputeStability(nowMs);
        QualityComputation qualityComputation = ComputeQuality(hasData, fitComputation);
        IReadOnlyList<AngleCalibrationTraceStroke> traceStrokes = CreateTraceSnapshots();
        AngleCalibrationStatus status = ResolveStatus(hasData, recommendedAngleDegrees.HasValue);
        return new AngleCalibrationRenderFrame(status, _isLocked, hasData, recommendedAngleDegrees, _swipeCount, _sampleCount, stabilityDegrees, traceStrokes, qualityComputation.Level, qualityComputation.Reason, qualityComputation.Score);
    }

    private void Ingest(double deltaX, double deltaY, double timestampMs)
    {
        if (ShouldCountSampleSegment(deltaX, deltaY))
        {
            _sampleCount++;
        }
        double previousPositionX = _positionX;
        double previousPositionY = _positionY;
        _positionX += deltaX;
        _positionY += deltaY;
        if (_activeStroke == null)
        {
            _activeStroke = new StrokeBuilder(previousPositionX, previousPositionY, timestampMs);
        }
        ProcessSegment(new BufferedSegment
        {
            DeltaX = deltaX,
            DeltaY = deltaY,
            TimestampMs = timestampMs,
            EndX = _positionX,
            EndY = _positionY
        });
    }

    private void ProcessSegment(BufferedSegment segment)
    {
        if (_activeStroke == null)
        {
            _activeStroke = new StrokeBuilder(segment.EndX - segment.DeltaX, segment.EndY - segment.DeltaY, segment.TimestampMs);
        }
        int segmentDirectionSign = Math.Sign(segment.DeltaX);
        if (_pendingOppositeSegments.Count > 0)
        {
            if (segmentDirectionSign == 0 || (_activeStroke.DirectionSign != 0 && segmentDirectionSign != _activeStroke.DirectionSign))
            {
                BufferOppositeSegment(segment);
                if (CanSplitStroke(_activeStroke) && _oppositeDxAccum >= StrokeFlipConfirmationDx)
                {
                    CompleteActiveStrokeAndRestart();
                }
                return;
            }
            FlushPendingSegmentsIntoActiveStroke();
        }
        if (_activeStroke.DirectionSign != 0 && segmentDirectionSign != 0 && segmentDirectionSign != _activeStroke.DirectionSign)
        {
            BufferOppositeSegment(segment);
            if (CanSplitStroke(_activeStroke) && _oppositeDxAccum >= StrokeFlipConfirmationDx)
            {
                CompleteActiveStrokeAndRestart();
            }
        }
        else
        {
            AppendSegment(_activeStroke, segment);
        }
    }

    private void BufferOppositeSegment(BufferedSegment segment)
    {
        _pendingOppositeSegments.Add(segment);
        int segmentDirectionSign = Math.Sign(segment.DeltaX);
        if (_activeStroke != null && _activeStroke.DirectionSign != 0 && segmentDirectionSign != 0 && segmentDirectionSign != _activeStroke.DirectionSign)
        {
            _oppositeDxAccum += Math.Abs(segment.DeltaX);
        }
    }

    private void FlushPendingSegmentsIntoActiveStroke()
    {
        if (_activeStroke == null || _pendingOppositeSegments.Count == 0)
        {
            _pendingOppositeSegments.Clear();
            _oppositeDxAccum = 0.0;
            return;
        }
        foreach (BufferedSegment pendingOppositeSegment in _pendingOppositeSegments)
        {
            AppendSegment(_activeStroke, pendingOppositeSegment);
        }
        _pendingOppositeSegments.Clear();
        _oppositeDxAccum = 0.0;
    }

    private void CompleteActiveStrokeAndRestart()
    {
        if (_activeStroke == null)
        {
            return;
        }
        StrokeBuilder activeStroke = _activeStroke;
        ArchiveHistoricalStroke(activeStroke.Points);
        AcceptCompletedStroke(activeStroke);
        double startTimestampMs = ((_pendingOppositeSegments.Count > 0) ? _pendingOppositeSegments[0].TimestampMs : activeStroke.LastTimestampMs);
        MutableTracePoint lastActivePoint = activeStroke.Points[activeStroke.Points.Count - 1];
        _activeStroke = new StrokeBuilder(lastActivePoint.X, lastActivePoint.Y, startTimestampMs);
        foreach (BufferedSegment pendingOppositeSegment in _pendingOppositeSegments)
        {
            AppendSegment(_activeStroke, pendingOppositeSegment);
        }
        _pendingOppositeSegments.Clear();
        _oppositeDxAccum = 0.0;
    }

    private void AcceptCompletedStroke(StrokeBuilder stroke)
    {
        EvaluatedStroke evaluatedStroke = EvaluateStroke(stroke);
        if (evaluatedStroke != null)
        {
            List<StrokeFitSample> strokeSamples = evaluatedStroke.DirectionSign < 0 ? _leftStrokeSamples : _rightStrokeSamples;
            strokeSamples.Add(new StrokeFitSample(evaluatedStroke.AngleDegrees, evaluatedStroke.Weight, evaluatedStroke.TimestampMs));
            while (strokeSamples.Count > SideMaximumAcceptedStrokes)
            {
                strokeSamples.RemoveAt(0);
            }
            if (_lastAcceptedStrokeDirectionSign != 0 && evaluatedStroke.DirectionSign != _lastAcceptedStrokeDirectionSign)
            {
                _swipeCount++;
            }
            _lastAcceptedStrokeDirectionSign = evaluatedStroke.DirectionSign;
            FitComputation fitComputation = ComputeFit();
            if (fitComputation.CandidateAngleDegrees.HasValue)
            {
                _candidateAngles.Add(new CandidateAngleSample
                {
                    Value = ApplyDisplayAngle(fitComputation.CandidateAngleDegrees.Value),
                    TimestampMs = evaluatedStroke.TimestampMs
                });
                PruneCandidateAngles(evaluatedStroke.TimestampMs);
            }
        }
    }

    private static void AppendSegment(StrokeBuilder target, BufferedSegment segment)
    {
        if (target != null)
        {
            double segmentLength = Math.Sqrt(segment.DeltaX * segment.DeltaX + segment.DeltaY * segment.DeltaY);
            if (double.IsNaN(segmentLength) || double.IsInfinity(segmentLength))
            {
                segmentLength = 0.0;
            }
            target.PathLength += segmentLength;
            target.NetDx += segment.DeltaX;
            target.NetDy += segment.DeltaY;
            target.LastTimestampMs = segment.TimestampMs;
            target.PacketCount++;
            int segmentDirectionSign = Math.Sign(segment.DeltaX);
            if (target.DirectionSign == 0 && segmentDirectionSign != 0)
            {
                target.DirectionSign = segmentDirectionSign;
            }
            MutableTracePoint lastPoint = target.Points[target.Points.Count - 1];
            if (lastPoint.X != segment.EndX || lastPoint.Y != segment.EndY)
            {
                target.Points.Add(new MutableTracePoint(segment.EndX, segment.EndY));
            }
        }
    }

    private static bool ShouldCountSampleSegment(double deltaX, double deltaY)
    {
        double segmentLength = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (double.IsNaN(segmentLength) || double.IsInfinity(segmentLength) || segmentLength < SampleMinimumSegmentLength)
        {
            return false;
        }
        double horizontalDistance = Math.Abs(deltaX);
        if (horizontalDistance < TinyEpsilon)
        {
            return false;
        }
        return horizontalDistance / segmentLength >= SampleMinimumHorizontalRatio;
    }

    private static bool CanSplitStroke(StrokeBuilder stroke)
    {
        if (stroke == null)
        {
            return false;
        }
        if (Math.Abs(stroke.NetDx) < StrokeFlipMinimumNetDx)
        {
            return false;
        }
        double strokeDurationMs = stroke.LastTimestampMs - stroke.StartTimestampMs;
        if (double.IsNaN(strokeDurationMs) || double.IsInfinity(strokeDurationMs))
        {
            return false;
        }
        return strokeDurationMs >= StrokeFlipMinimumDurationMilliseconds;
    }

    private EvaluatedStroke EvaluateStroke(StrokeBuilder stroke)
    {
        if (stroke == null || stroke.Points == null || stroke.Points.Count < StrokeMinimumPoints)
        {
            return null;
        }

        int directionSign = Math.Sign(stroke.NetDx);
        if (directionSign == 0)
        {
            return null;
        }

        double durationMs = stroke.LastTimestampMs - stroke.StartTimestampMs;
        if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs < StrokeMinimumDurationMilliseconds || durationMs > StrokeMaximumDurationMilliseconds)
        {
            return null;
        }

        double netLength = Math.Sqrt(stroke.NetDx * stroke.NetDx + stroke.NetDy * stroke.NetDy);
        if (double.IsNaN(netLength) || double.IsInfinity(netLength) || netLength < StrokeMinimumNetLength)
        {
            return null;
        }

        double horizontalTravel = Math.Abs(stroke.NetDx);
        if (horizontalTravel < TinyEpsilon || horizontalTravel / netLength < StrokeMinimumHorizontalRatio)
        {
            return null;
        }

        double strokePathLength = Math.Max(stroke.PathLength, netLength);
        if (strokePathLength < TinyEpsilon)
        {
            return null;
        }

        double straightness = netLength / strokePathLength;
        if (straightness < StrokeMinimumStraightness)
        {
            return null;
        }

        double angleDegrees = 0.0;
        double residualRms = 0.0;
        if (!TryComputeStrokeFit(stroke.Points, stroke.NetDx, stroke.NetDy, ref angleDegrees, ref residualRms))
        {
            return null;
        }

        double residualLimit = Math.Max(StrokeMinimumResidualRms, netLength * StrokeResidualScale);
        if (residualRms > residualLimit)
        {
            return null;
        }

        double sampleWeight = Math.Min(netLength, StrokeWeightCap) * straightness;
        if (double.IsNaN(sampleWeight) || double.IsInfinity(sampleWeight) || sampleWeight <= 0.0)
        {
            return null;
        }

        return new EvaluatedStroke(directionSign, angleDegrees, sampleWeight, stroke.LastTimestampMs);
    }

    private static bool TryComputeStrokeFit(List<MutableTracePoint> points, double netDx, double netDy, ref double angleDegrees, ref double residualRms)
    {
        angleDegrees = 0.0;
        residualRms = 0.0;
        if (points == null || points.Count < 2)
        {
            return false;
        }

        double centroidX = 0.0;
        double centroidY = 0.0;
        foreach (MutableTracePoint point in points)
        {
            centroidX += point.X;
            centroidY += point.Y;
        }
        centroidX /= points.Count;
        centroidY /= points.Count;

        double varianceX = 0.0;
        double varianceY = 0.0;
        double covarianceXY = 0.0;
        foreach (MutableTracePoint point in points)
        {
            double centeredX = point.X - centroidX;
            double centeredY = point.Y - centroidY;
            varianceX += centeredX * centeredX;
            varianceY += centeredY * centeredY;
            covarianceXY += centeredX * centeredY;
        }

        double varianceDifference = varianceX - varianceY;
        double eigenDiscriminant = Math.Sqrt(varianceDifference * varianceDifference + 4.0 * covarianceXY * covarianceXY);
        double directionX = 1.0;
        double directionY = 0.0;
        if (eigenDiscriminant > TinyEpsilon)
        {
            double largestEigenvalue = (varianceX + varianceY + eigenDiscriminant) / 2.0;
            directionX = covarianceXY;
            directionY = largestEigenvalue - varianceX;
            if (Math.Abs(directionX) + Math.Abs(directionY) < TinyEpsilon)
            {
                directionX = largestEigenvalue - varianceY;
                directionY = covarianceXY;
            }
        }

        double directionLength = Math.Sqrt(directionX * directionX + directionY * directionY);
        if (directionLength < TinyEpsilon)
        {
            return false;
        }
        directionX /= directionLength;
        directionY /= directionLength;

        double normalX = -directionY;
        double normalY = directionX;
        double squaredResidualSum = 0.0;
        foreach (MutableTracePoint point in points)
        {
            double centeredX = point.X - centroidX;
            double centeredY = point.Y - centroidY;
            double residual = centeredX * normalX + centeredY * normalY;
            squaredResidualSum += residual * residual;
        }

        residualRms = Math.Sqrt(squaredResidualSum / points.Count);
        double absoluteAngleDegrees = Math.Atan2(Math.Abs(directionY), Math.Abs(directionX)) * 180.0 / Math.PI;
        int correctionSign = ComputeCorrectionSign(netDx, netDy);
        angleDegrees = Clamp(correctionSign * absoluteAngleDegrees, -90.0, 90.0);
        return true;
    }

    private FitComputation ComputeFit()
    {
        SideComputation leftSide = ComputeSide(_leftStrokeSamples);
        SideComputation rightSide = ComputeSide(_rightStrokeSamples);
        double? candidateAngleDegrees = null;
        if (leftSide.ResolvedAngleDegrees.HasValue && rightSide.ResolvedAngleDegrees.HasValue)
        {
            candidateAngleDegrees = Clamp(Wrap180(leftSide.ResolvedAngleDegrees.Value + AngleDifference(rightSide.ResolvedAngleDegrees.Value, leftSide.ResolvedAngleDegrees.Value) / 2.0), -90.0, 90.0);
        }
        return new FitComputation(leftSide, rightSide, candidateAngleDegrees);
    }

    private static SideComputation ComputeSide(List<StrokeFitSample> samples)
    {
        SideComputation sideComputation = new SideComputation
        {
            AcceptedCount = (samples?.Count ?? 0),
            InlierCount = 0,
            MadDegrees = null,
            ResolvedAngleDegrees = null,
            TotalInlierWeight = 0.0
        };
        if (samples == null || samples.Count == 0)
        {
            return sideComputation;
        }
        List<WeightedValue> angleSamples = new List<WeightedValue>(samples.Count);
        foreach (StrokeFitSample sample in samples)
        {
            angleSamples.Add(new WeightedValue
            {
                Value = sample.AngleDegrees,
                Weight = Math.Max(0.0, sample.Weight)
            });
        }
        double? medianAngleDegrees = ComputeWeightedMedian(angleSamples);
        if (!medianAngleDegrees.HasValue)
        {
            return sideComputation;
        }
        List<WeightedValue> deviationSamples = new List<WeightedValue>(samples.Count);
        foreach (StrokeFitSample sample in samples)
        {
            deviationSamples.Add(new WeightedValue
            {
                Value = Math.Abs(AngleDifference(sample.AngleDegrees, medianAngleDegrees.Value)),
                Weight = Math.Max(0.0, sample.Weight)
            });
        }
        double? medianAbsoluteDeviationDegrees = ComputeWeightedMedian(deviationSamples);
        if (medianAbsoluteDeviationDegrees.HasValue)
        {
            sideComputation.MadDegrees = medianAbsoluteDeviationDegrees.Value;
        }
        double outlierThresholdDegrees = Math.Max(SideOutlierFloorDegrees, SideOutlierMadScale * (medianAbsoluteDeviationDegrees.HasValue ? medianAbsoluteDeviationDegrees.Value : 0.0));
        List<StrokeFitSample> inlierSamples = new List<StrokeFitSample>();
        double inlierWeight = 0.0;
        foreach (StrokeFitSample sample in samples)
        {
            if (Math.Abs(AngleDifference(sample.AngleDegrees, medianAngleDegrees.Value)) <= outlierThresholdDegrees)
            {
                inlierSamples.Add(sample);
                inlierWeight += Math.Max(0.0, sample.Weight);
            }
        }
        sideComputation.InlierCount = inlierSamples.Count;
        sideComputation.TotalInlierWeight = inlierWeight;
        if (inlierSamples.Count < SideMinimumInlierCount || inlierWeight < SideMinimumWeight)
        {
            return sideComputation;
        }
        sideComputation.ResolvedAngleDegrees = ComputeWeightedAngleAverage(medianAngleDegrees.Value, inlierSamples);
        return sideComputation;
    }

    private static double? ComputeWeightedMedian(List<WeightedValue> values)
    {
        if (values == null || values.Count == 0)
        {
            return null;
        }
        List<WeightedValue> sortedValues = new List<WeightedValue>(values.Count);
        foreach (WeightedValue value in values)
        {
            sortedValues.Add(value);
        }
        sortedValues.Sort((WeightedValue left, WeightedValue right) => left.Value.CompareTo(right.Value));
        double totalWeight = 0.0;
        foreach (WeightedValue value in sortedValues)
        {
            totalWeight += Math.Max(0.0, value.Weight);
        }
        if (totalWeight <= TinyEpsilon)
        {
            double midpoint = (double)(sortedValues.Count - 1) / 2.0;
            int lowerIndex = (int)Math.Floor(midpoint);
            int upperIndex = (int)Math.Ceiling(midpoint);
            return (sortedValues[lowerIndex].Value + sortedValues[upperIndex].Value) / 2.0;
        }
        double halfWeight = totalWeight / 2.0;
        double cumulativeWeight = 0.0;
        foreach (WeightedValue value in sortedValues)
        {
            cumulativeWeight += Math.Max(0.0, value.Weight);
            if (cumulativeWeight >= halfWeight)
            {
                return value.Value;
            }
        }
        return sortedValues[sortedValues.Count - 1].Value;
    }

    private static double? ComputeWeightedAngleAverage(double center, List<StrokeFitSample> samples)
    {
        if (samples == null || samples.Count == 0)
        {
            return null;
        }
        double totalWeight = 0.0;
        double weightedOffsetSum = 0.0;
        foreach (StrokeFitSample sample in samples)
        {
            double sampleWeight = Math.Max(0.0, sample.Weight);
            totalWeight += sampleWeight;
            weightedOffsetSum += AngleDifference(sample.AngleDegrees, center) * sampleWeight;
        }
        return totalWeight > TinyEpsilon ? Clamp(Wrap180(center + weightedOffsetSum / totalWeight), -90.0, 90.0) : center;
    }

    private double? ComputeStability(double nowMs)
    {
        PruneCandidateAngles(nowMs);
        if (_candidateAngles.Count < 2)
        {
            return null;
        }
        double[] candidateAngleValues = new double[_candidateAngles.Count];
        for (int angleIndex = 0; angleIndex < _candidateAngles.Count; angleIndex++)
        {
            candidateAngleValues[angleIndex] = _candidateAngles[angleIndex].Value;
        }
        return ComputeStandardDeviation(candidateAngleValues);
    }

    private QualityComputation ComputeQuality(bool hasData, FitComputation fit)
    {
        if (!hasData || fit == null)
        {
            return new QualityComputation(AngleCalibrationQualityLevel.None, AngleCalibrationQualityReason.None, 0);
        }
        int leftAcceptedCount = fit.Left.AcceptedCount;
        int rightAcceptedCount = fit.Right.AcceptedCount;
        int leftInlierCount = fit.Left.InlierCount;
        int rightInlierCount = fit.Right.InlierCount;
        double progressScore = Clamp01((double)_swipeCount / ResultSwipeThreshold);
        double acceptedStrokeCount = Math.Max(1.0, leftAcceptedCount + rightAcceptedCount);
        double balanceScore = 1.0 - (double)Math.Abs(leftAcceptedCount - rightAcceptedCount) / acceptedStrokeCount;
        double inlierRatio = (double)(leftInlierCount + rightInlierCount) / acceptedStrokeCount;
        double leftMadDegrees = fit.Left.MadDegrees.HasValue ? fit.Left.MadDegrees.Value : 3.0;
        double rightMadDegrees = fit.Right.MadDegrees.HasValue ? fit.Right.MadDegrees.Value : 3.0;
        double dispersionScore = 1.0 - Clamp01(Math.Max(leftMadDegrees, rightMadDegrees) / 3.0);
        int score = (int)Math.Round(35.0 * progressScore + 20.0 * balanceScore + 25.0 * inlierRatio + 20.0 * dispersionScore);
        score = (int)Math.Round(Clamp(score, 0.0, 100.0));
        AngleCalibrationQualityReason reason = AngleCalibrationQualityReason.Good;
        if (progressScore < 0.3)
        {
            reason = AngleCalibrationQualityReason.InsufficientProgress;
        }
        else if (balanceScore < 0.6)
        {
            reason = AngleCalibrationQualityReason.Imbalance;
        }
        else if (dispersionScore < 0.5)
        {
            reason = AngleCalibrationQualityReason.HighDispersion;
        }
        else if (inlierRatio < 0.6)
        {
            reason = AngleCalibrationQualityReason.TooManyOutliers;
        }
        AngleCalibrationQualityLevel level = AngleCalibrationQualityLevel.Poor;
        if (score >= 85)
        {
            level = AngleCalibrationQualityLevel.Excellent;
        }
        else if (score >= 70)
        {
            level = AngleCalibrationQualityLevel.Good;
        }
        else if (score >= 40)
        {
            level = AngleCalibrationQualityLevel.Fair;
        }
        return new QualityComputation(level, reason, score);
    }

    private void PruneCandidateAngles(double nowMs)
    {
        bool hasValidTimestamp = !double.IsNaN(nowMs) && !double.IsInfinity(nowMs);
        double cutoffTimestampMs = nowMs - StabilityWindowMilliseconds;
        while (_candidateAngles.Count > 0 && ((hasValidTimestamp && _candidateAngles[0].TimestampMs < cutoffTimestampMs) || _candidateAngles.Count > StabilityMaximumSamples))
        {
            _candidateAngles.RemoveAt(0);
        }
    }

    private IReadOnlyList<AngleCalibrationTraceStroke> CreateTraceSnapshots()
    {
        List<AngleCalibrationTraceStroke> traceStrokes = new List<AngleCalibrationTraceStroke>();
        int firstHistoricalStrokeIndex = Math.Max(0, _historicalStrokes.Count - MaximumHistoricalStrokes);
        for (int strokeIndex = firstHistoricalStrokeIndex; strokeIndex < _historicalStrokes.Count; strokeIndex++)
        {
            AngleCalibrationTraceStroke historicalStroke = _historicalStrokes[strokeIndex];
            if (historicalStroke != null)
            {
                traceStrokes.Add(historicalStroke);
            }
        }
        AngleCalibrationTraceStroke currentStroke = CreateStrokeSnapshot(CreateCurrentStrokePoints(), isCurrent: true);
        if (currentStroke != null)
        {
            traceStrokes.Add(currentStroke);
        }
        return traceStrokes;
    }

    private List<MutableTracePoint> CreateCurrentStrokePoints()
    {
        if (_activeStroke == null || _activeStroke.Points == null || _activeStroke.Points.Count == 0)
        {
            return null;
        }
        if (_pendingOppositeSegments.Count == 0)
        {
            return _activeStroke.Points;
        }
        List<MutableTracePoint> currentPoints = new List<MutableTracePoint>(_activeStroke.Points.Count + _pendingOppositeSegments.Count);
        foreach (MutableTracePoint point in _activeStroke.Points)
        {
            currentPoints.Add(new MutableTracePoint(point.X, point.Y));
        }
        foreach (BufferedSegment pendingOppositeSegment in _pendingOppositeSegments)
        {
            MutableTracePoint lastPoint = currentPoints[currentPoints.Count - 1];
            if (lastPoint.X != pendingOppositeSegment.EndX || lastPoint.Y != pendingOppositeSegment.EndY)
            {
                currentPoints.Add(new MutableTracePoint(pendingOppositeSegment.EndX, pendingOppositeSegment.EndY));
            }
        }
        return currentPoints;
    }

    private void ArchiveHistoricalStroke(List<MutableTracePoint> points)
    {
        if (points == null || points.Count < 2)
        {
            return;
        }
        AngleCalibrationTraceStroke archivedStroke = CreateStrokeSnapshot(points, isCurrent: false);
        if (archivedStroke == null)
        {
            return;
        }
        _historicalStrokes.Add(archivedStroke);
        while (_historicalStrokes.Count > MaximumHistoricalStrokes)
        {
            _historicalStrokes.RemoveAt(0);
        }
    }

    private void DiscardInProgressStroke()
    {
        _activeStroke = null;
        _pendingOppositeSegments.Clear();
        _oppositeDxAccum = 0.0;
    }

    private static AngleCalibrationTraceStroke CreateStrokeSnapshot(List<MutableTracePoint> points, bool isCurrent)
    {
        if (points == null || points.Count == 0)
        {
            return null;
        }
        List<AngleCalibrationTracePoint> snapshotPoints = new List<AngleCalibrationTracePoint>();
        if (points.Count <= MaximumSnapshotPointsPerStroke)
        {
            foreach (MutableTracePoint point in points)
            {
                snapshotPoints.Add(new AngleCalibrationTracePoint(point.X, point.Y));
            }
        }
        else
        {
            double sampleStep = (double)(points.Count - 1) / (MaximumSnapshotPointsPerStroke - 1);
            for (int sampleIndex = 0; sampleIndex < MaximumSnapshotPointsPerStroke; sampleIndex++)
            {
                int sourceIndex = (int)Math.Round(sampleIndex * sampleStep);
                sourceIndex = Math.Max(0, Math.Min(points.Count - 1, sourceIndex));
                MutableTracePoint point = points[sourceIndex];
                snapshotPoints.Add(new AngleCalibrationTracePoint(point.X, point.Y));
            }
        }
        return new AngleCalibrationTraceStroke(snapshotPoints, isCurrent);
    }

    private bool HasData()
    {
        if (_historicalStrokes.Count <= 0 && (_activeStroke == null || _activeStroke.Points == null || _activeStroke.Points.Count <= 1) && _pendingOppositeSegments.Count <= 0 && _swipeCount <= 0)
        {
            return _sampleCount > 0;
        }
        return true;
    }

    private AngleCalibrationStatus ResolveStatus(bool hasData, bool hasRecommendedAngle)
    {
        if (!hasData)
        {
            return AngleCalibrationStatus.Empty;
        }
        if (hasRecommendedAngle)
        {
            return AngleCalibrationStatus.ResultReady;
        }
        if (_isLocked)
        {
            return AngleCalibrationStatus.Collecting;
        }
        return AngleCalibrationStatus.Paused;
    }

    private static int ComputeCorrectionSign(double netDx, double netDy)
    {
        if (Math.Abs(netDx) < 1E-06)
        {
            return (!(netDy >= 0.0)) ? 1 : (-1);
        }
        return (netDx * netDy < 0.0) ? 1 : (-1);
    }

    private static double ApplyDisplayAngle(double value)
    {
        return Clamp(Wrap180(value), -90.0, 90.0);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }

    private static double Clamp01(double value)
    {
        return Clamp(value, 0.0, 1.0);
    }

    private static double Wrap180(double value)
    {
        double wrappedValue;
        for (wrappedValue = value; wrappedValue > 180.0; wrappedValue -= 360.0)
        {
        }
        for (; wrappedValue < -180.0; wrappedValue += 360.0)
        {
        }
        return wrappedValue;
    }

    private static double AngleDifference(double a, double b)
    {
        return Wrap180(a - b);
    }

    private static double? ComputeStandardDeviation(double[] values)
    {
        if (values == null || values.Length < 2)
        {
            return null;
        }
        double mean = 0.0;
        foreach (double value in values)
        {
            mean += value;
        }
        mean /= values.Length;
        double squaredDeviationSum = 0.0;
        for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
        {
            double deviation = values[valueIndex] - mean;
            squaredDeviationSum += deviation * deviation;
        }
        return Math.Sqrt(squaredDeviationSum / (values.Length - 1));
    }
}





