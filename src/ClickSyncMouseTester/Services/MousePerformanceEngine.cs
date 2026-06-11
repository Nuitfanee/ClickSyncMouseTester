using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClickSyncMouseTester.Services;

public class MousePerformanceEngine
{
    private readonly MousePerformanceEventBuffer _events;

    private readonly MousePerformanceReportClassifier _reportClassifier;

    private readonly MousePerformanceTimingDiagnostics _timingDiagnostics;

    private readonly List<MousePerformanceSessionSegment> _sessionSegments;

    private readonly MousePerformanceAnalysisOptions _analysisOptions;

    private bool _isCollecting;

    private bool _isFinalized;

    private string _sessionDeviceId;

    private double? _effectiveCpi;

    private bool _canComputeVelocity;

    private long _segmentRawStartTicks;

    private long _segmentLogicalStartTicks;

    private bool _segmentHasAnchor;

    private int _currentSessionSegmentId;

    private long _rawOriginTicks;

    private bool _hasRawOrigin;

    private long _lastLogicalTicks;

    private int _sessionRevision;

    private int _summaryEventCount;

    private long _summarySumX;

    private long _summarySumY;

    private double _summaryPathCounts;

    private int _droppedPacketCount;

    private int _controlReportCount;

    private int _wheelOnlyReportCount;

    private int _emptyReportCount;

    private int _zeroMotionReportCount;

    public bool IsCollecting => _isCollecting;

    public bool IsFinalized => _isFinalized;

    public string SessionDeviceId => _sessionDeviceId;

    public bool HasData => _summaryEventCount > 0;

    public bool CanContinue
    {
        get
        {
            if (_summaryEventCount > 0 && !_isCollecting)
            {
                return !_isFinalized;
            }
            return false;
        }
    }

    public double? EffectiveCpi => _effectiveCpi;

    public bool CanComputeVelocity => _canComputeVelocity;

    public MousePerformanceEngine(MousePerformanceAnalysisOptions analysisOptions = null)
    {
        _events = new MousePerformanceEventBuffer();
        _reportClassifier = new MousePerformanceReportClassifier();
        _timingDiagnostics = new MousePerformanceTimingDiagnostics();
        _sessionSegments = new List<MousePerformanceSessionSegment>();
        _analysisOptions = analysisOptions ?? MousePerformanceAnalysisOptions.Default;
        _effectiveCpi = 800.0;
        _canComputeVelocity = true;
        ResetSession();
    }

    public void ResetSession()
    {
        _events.Clear();
        _timingDiagnostics.Reset();
        _sessionSegments.Clear();
        _isCollecting = false;
        _isFinalized = false;
        _sessionDeviceId = string.Empty;
        _segmentRawStartTicks = 0L;
        _segmentLogicalStartTicks = 0L;
        _segmentHasAnchor = false;
        _currentSessionSegmentId = 0;
        _rawOriginTicks = 0L;
        _hasRawOrigin = false;
        _lastLogicalTicks = 0L;
        _sessionRevision = 0;
        _summaryEventCount = 0;
        _summarySumX = 0L;
        _summarySumY = 0L;
        _summaryPathCounts = 0.0;
        _droppedPacketCount = 0;
        _controlReportCount = 0;
        _wheelOnlyReportCount = 0;
        _emptyReportCount = 0;
        _zeroMotionReportCount = 0;
    }

    public void SetCpiState(double? effectiveCpi, bool canComputeVelocity)
    {
        _effectiveCpi = effectiveCpi;
        _canComputeVelocity = canComputeVelocity && effectiveCpi.HasValue && effectiveCpi.Value > 0.0;
    }

    public void BeginCollecting(string deviceId, long startedAtTicks, bool startFresh)
    {
        if (startFresh)
        {
            ResetSession();
        }
        _sessionDeviceId = deviceId ?? string.Empty;
        _isCollecting = true;
        _isFinalized = false;
        _segmentRawStartTicks = Math.Max(0L, startedAtTicks);
        _segmentLogicalStartTicks = ((_summaryEventCount == 0) ? 0 : _lastLogicalTicks);
        _segmentHasAnchor = _summaryEventCount > 0;
        _currentSessionSegmentId++;
        _sessionSegments.Add(new MousePerformanceSessionSegment(_currentSessionSegmentId, _segmentRawStartTicks));
    }

    public void PauseCollecting()
    {
        _isCollecting = false;
        _segmentHasAnchor = false;
    }

    public void StopCollecting()
    {
        _isCollecting = false;
        _segmentHasAnchor = false;
        _isFinalized = _summaryEventCount > 0;
    }

    public void ReportDroppedPackets(int count)
    {
        if (count > 0)
        {
            _droppedPacketCount += count;
            AdvanceRevision();
        }
    }

    public void PushPacket(RawMousePacket packet)
    {
        if (packet == null || !_isCollecting)
        {
            return;
        }

        MousePerformanceReportClassification classification = _reportClassifier.Classify(packet);
        RecordReportClassification(classification);

        long timingSequence = ResolveTimingSequence(packet);
        long rawCaptureTicks = packet.RawCaptureTicks;
        long logicalTicks = ResolveLogicalTicks(rawCaptureTicks);
        if (!_hasRawOrigin)
        {
            _rawOriginTicks = rawCaptureTicks;
            _hasRawOrigin = true;
        }

        long rawRelativeTicks = Math.Max(0L, rawCaptureTicks - _rawOriginTicks);
        int relativeDeltaX = classification.RelativeDeltaX;
        int relativeDeltaY = classification.RelativeDeltaY;
        double packetDistanceCounts = Math.Sqrt((double)relativeDeltaX * relativeDeltaX + (double)relativeDeltaY * relativeDeltaY);
        _summaryEventCount++;
        _summarySumX += relativeDeltaX;
        _summarySumY += relativeDeltaY;
        _summaryPathCounts += packetDistanceCounts;
        _events.Add(new MousePerformanceEvent(packet.DeltaX, packet.DeltaY, relativeDeltaX, relativeDeltaY, packet.ButtonFlags, packet.RawMouseFlags, packet.ButtonData, packet.ExtraInformation, rawCaptureTicks, rawRelativeTicks, logicalTicks, classification.PacketKind, packet.MovementMode, _summarySumX, _summarySumY, _currentSessionSegmentId, packet.CaptureSequence, timingSequence));
        _timingDiagnostics.RecordReport(_currentSessionSegmentId, timingSequence, rawCaptureTicks, relativeDeltaX, relativeDeltaY, _analysisOptions.CurrentVelocityWindowMs);
        _lastLogicalTicks = logicalTicks;
        AdvanceRevision();
    }

    public MousePerformanceSnapshot CreateSummarySnapshot(MousePerformanceSessionStatus status, bool isLocked, int queueOverflowCount, int queueHighWatermarkCount, int queueCapacity, bool includeDataQuality = true)
    {
        MousePerformanceSummary summary = CreateSummary();
        return new MousePerformanceSnapshot(status, isLocked, _isFinalized, CanContinue, _sessionDeviceId, _effectiveCpi, _canComputeVelocity, summary, Array.Empty<MousePerformanceEvent>(), _sessionRevision, _events.Count, includeDataQuality ? CreateDataQuality(queueOverflowCount, queueHighWatermarkCount, queueCapacity) : null, CreateSessionSegmentsSnapshot());
    }

    public MousePerformanceSnapshot CreateAnalysisSnapshot(MousePerformanceSessionStatus status, bool isLocked, int queueOverflowCount, int queueHighWatermarkCount, int queueCapacity, bool includeDataQuality = true)
    {
        MousePerformanceSummary summary = CreateSummary();
        IReadOnlyList<MousePerformanceEvent> events = _events.CreateReadOnlyView(_events.Count);
        return new MousePerformanceSnapshot(status, isLocked, _isFinalized, CanContinue, _sessionDeviceId, _effectiveCpi, _canComputeVelocity, summary, events, _sessionRevision, _events.Count, includeDataQuality ? CreateDataQuality(queueOverflowCount, queueHighWatermarkCount, queueCapacity) : null, CreateSessionSegmentsSnapshot());
    }

    public MousePerformanceSummary CreateSummary()
    {
        double? cpi = _effectiveCpi.HasValue && _effectiveCpi.Value > 0.0 ? _effectiveCpi : null;
        double? sumXCm = null;
        double? sumYCm = null;
        double? pathCm = null;
        double? currentVelocityMetersPerSecond = null;
        double? sessionAverageVelocityMetersPerSecond = null;
        if (cpi.HasValue)
        {
            sumXCm = Math.Abs((double)_summarySumX / cpi.Value * 2.54);
            sumYCm = Math.Abs((double)_summarySumY / cpi.Value * 2.54);
            pathCm = _summaryPathCounts / cpi.Value * 2.54;
            if (_isCollecting)
            {
                currentVelocityMetersPerSecond = ResolveCurrentVelocityMetersPerSecond(Stopwatch.GetTimestamp());
            }
            else
            {
                sessionAverageVelocityMetersPerSecond = ResolveSessionAverageVelocityMetersPerSecond();
            }
        }
        return new MousePerformanceSummary(_summaryEventCount, _summarySumX, _summarySumY, _summaryPathCounts, cpi, sumXCm, sumYCm, pathCm, currentVelocityMetersPerSecond, sessionAverageVelocityMetersPerSecond);
    }

    public static MousePerformanceChartRenderFrame CreateChartRenderFrame(MousePerformanceSnapshot snapshot, MousePerformancePlotType plotType, int startIndex, int endIndex, bool showStem, bool showLines, MousePerformanceTimeBasis timeBasis, MousePerformanceAnalysisOptions analysisOptions = null, CancellationToken cancellationToken = default(CancellationToken), IReadOnlyList<MousePerformanceChartGapSource> gapSources = null)
    {
        return MousePerformanceChartFrameBuilder.CreateChartRenderFrame(snapshot, plotType, startIndex, endIndex, showStem, showLines, timeBasis, analysisOptions, cancellationToken, gapSources);
    }

    private MousePerformanceDataQuality CreateDataQuality(int queueOverflowCount, int queueHighWatermarkCount, int queueCapacity)
    {
        bool hasPacketLossOrOrderingIssues = _droppedPacketCount > 0 || queueOverflowCount > 0 || _timingDiagnostics.SequenceGapCount > 0 || _timingDiagnostics.ReorderedSequenceCount > 0 || _timingDiagnostics.OutOfOrderTimestampCount > 0;
        bool hasTimingFidelityIssues = hasPacketLossOrOrderingIssues || _timingDiagnostics.SameTickCount > 0;
        MousePerformanceDataQualityLevel qualityLevel = MousePerformanceDataQualityLevel.None;
        if (_summaryEventCount > 0)
        {
            qualityLevel = hasPacketLossOrOrderingIssues ? MousePerformanceDataQualityLevel.Degraded : MousePerformanceDataQualityLevel.Good;
        }
        else if (hasPacketLossOrOrderingIssues)
        {
            qualityLevel = MousePerformanceDataQualityLevel.Degraded;
        }

        return new MousePerformanceDataQuality(_droppedPacketCount, _controlReportCount, _wheelOnlyReportCount, _emptyReportCount, _zeroMotionReportCount, _timingDiagnostics.OutOfOrderTimestampCount, _timingDiagnostics.ZeroIntervalCount, queueOverflowCount, queueHighWatermarkCount, queueCapacity, _timingDiagnostics.SequenceGapCount, _timingDiagnostics.ReorderedSequenceCount, _timingDiagnostics.SameTickCount, !hasTimingFidelityIssues, qualityLevel);
    }

    private IReadOnlyList<MousePerformanceSessionSegment> CreateSessionSegmentsSnapshot()
    {
        if (_sessionSegments.Count == 0)
        {
            return Array.Empty<MousePerformanceSessionSegment>();
        }
        return _sessionSegments.ToArray();
    }

    private double? ResolveCurrentVelocityMetersPerSecond(long nowTicks)
    {
        if (!_canComputeVelocity || !_effectiveCpi.HasValue || _effectiveCpi.Value <= 0.0 || _summaryEventCount <= 0)
        {
            return null;
        }

        return _timingDiagnostics.ResolveCurrentVelocityMetersPerSecond(nowTicks, _effectiveCpi.Value, _analysisOptions.CurrentVelocityWindowMs);
    }

    private double? ResolveSessionAverageVelocityMetersPerSecond()
    {
        if (!_canComputeVelocity || !_effectiveCpi.HasValue || _effectiveCpi.Value <= 0.0 || _summaryEventCount <= 0)
        {
            return null;
        }
        if (_summaryPathCounts <= 0.0)
        {
            return 0.0;
        }

        double elapsedMs = TicksToMilliseconds(_lastLogicalTicks);
        return elapsedMs > 0.0 ? _summaryPathCounts / elapsedMs / _effectiveCpi.Value * 25.4 : 0.0;
    }

    private long ResolveLogicalTicks(long rawTicks)
    {
        if (!_segmentHasAnchor)
        {
            if (_summaryEventCount == 0)
            {
                _segmentRawStartTicks = rawTicks;
                _segmentLogicalStartTicks = 0L;
            }
            _segmentHasAnchor = true;
        }

        long logicalTicks = _segmentLogicalStartTicks + Math.Max(0L, rawTicks - _segmentRawStartTicks);
        if (_summaryEventCount > 0 && logicalTicks < _lastLogicalTicks)
        {
            logicalTicks = _lastLogicalTicks;
        }
        return logicalTicks;
    }

    private static long ResolveTimingSequence(RawMousePacket packet)
    {
        if (packet == null)
        {
            return 0L;
        }
        if (packet.TimingSequence > 0L)
        {
            return packet.TimingSequence;
        }
        return packet.CaptureSequence;
    }

    private void RecordReportClassification(MousePerformanceReportClassification classification)
    {
        if (classification.IsControlReport)
        {
            _controlReportCount++;
        }
        if (classification.IsWheelOnlyReport)
        {
            _wheelOnlyReportCount++;
        }
        if (classification.IsEmptyReport)
        {
            _emptyReportCount++;
        }
        if (classification.IsZeroMotionReport)
        {
            _zeroMotionReportCount++;
        }
    }

    private void AdvanceRevision()
    {
        if (_sessionRevision == int.MaxValue)
        {
            _sessionRevision = 1;
        }
        else
        {
            _sessionRevision++;
        }
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
    }
}
