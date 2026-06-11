using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

public class PollingRateEngine
{
    private const double PeakWarmupMilliseconds = 300.0;
    private const double HistoryMilliseconds = 3000.0;
    private const int RenderHistoryCapacity = 4096;
    private const double DefaultRenderHistorySampleMilliseconds = 1000.0 / 120.0;
    private const double SnapTolerance = 0.015;
    private const double LockedHistoryMinimumStepMilliseconds = 0.01;

    private static readonly int[] CommonRates = new int[7] { 125, 250, 500, 1000, 2000, 4000, 8000 };

    private readonly object _syncRoot;
    private readonly ReportRateWindow _reportRateWindow;
    private readonly StabilityMetricsWindow _stabilityMetricsWindow;
    private readonly PollingHistoryPointRingBuffer _renderHistory;

    private double _lastRenderHistorySampleMs;
    private IReadOnlyList<PollingHistoryPoint> _cachedRenderHistoryView;
    private int _cachedRenderHistoryVersion;
    private int _peakRate;
    private double _renderHistorySampleMilliseconds;
    private PollingRateMode _mode;
    private double _peakEligibleAtMs;
    private int _pendingPeakRate;
    private double _renderHistoryPausedStartedRealtimeMs;
    private double _renderHistoryAccumulatedPauseMilliseconds;
    private double _lastLogicalRenderHistorySampleMs;
    private double _latestPacketTimestampMs;

    public PollingRateEngine()
    {
        _syncRoot = new object();
        _reportRateWindow = new ReportRateWindow();
        _stabilityMetricsWindow = new StabilityMetricsWindow();
        _renderHistory = new PollingHistoryPointRingBuffer(RenderHistoryCapacity);
        _cachedRenderHistoryView = Array.Empty<PollingHistoryPoint>();
        _cachedRenderHistoryVersion = -1;
        _renderHistorySampleMilliseconds = DefaultRenderHistorySampleMilliseconds;
        _mode = PollingRateMode.RawPacketRate;
        _peakEligibleAtMs = double.NaN;
        _renderHistoryPausedStartedRealtimeMs = double.NaN;
        _lastLogicalRenderHistorySampleMs = double.NaN;
        _latestPacketTimestampMs = double.NaN;
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _reportRateWindow.Reset();
            _stabilityMetricsWindow.Reset();
            _renderHistory.Clear();
            _lastRenderHistorySampleMs = 0.0;
            _cachedRenderHistoryView = Array.Empty<PollingHistoryPoint>();
            _cachedRenderHistoryVersion = -1;
            _peakRate = 0;
            _peakEligibleAtMs = double.NaN;
            _pendingPeakRate = 0;
            _renderHistoryPausedStartedRealtimeMs = double.NaN;
            _renderHistoryAccumulatedPauseMilliseconds = 0.0;
            _lastLogicalRenderHistorySampleMs = double.NaN;
            _latestPacketTimestampMs = double.NaN;
        }
    }

    public void SetMode(PollingRateMode mode)
    {
        lock (_syncRoot)
        {
            _mode = mode;
            _reportRateWindow.BeginSegment();
            _stabilityMetricsWindow.BeginSegment();
            _latestPacketTimestampMs = double.NaN;
        }
    }

    public void SetRenderHistorySampleInterval(double milliseconds)
    {
        lock (_syncRoot)
        {
            _renderHistorySampleMilliseconds = NormalizeHistorySampleInterval(milliseconds);
        }
    }

    public void BeginActiveSegment(double nowMs)
    {
        lock (_syncRoot)
        {
            if (!IsFinite(nowMs))
            {
                _peakEligibleAtMs = double.NaN;
                return;
            }

            ResumeRenderHistoryClock(nowMs);
            _peakEligibleAtMs = nowMs + PeakWarmupMilliseconds;
            _pendingPeakRate = 0;
            _latestPacketTimestampMs = double.NaN;
            _reportRateWindow.BeginSegment();
            _stabilityMetricsWindow.BeginSegment();
        }
    }

    public void EndActiveSegment(double nowMs = double.NaN)
    {
        lock (_syncRoot)
        {
            StartRenderHistoryPauseClock(nowMs);
            _peakEligibleAtMs = double.NaN;
            _pendingPeakRate = 0;
        }
    }

    public void PushPacket(RawMousePacket packet)
    {
        if (packet == null)
        {
            return;
        }

        lock (_syncRoot)
        {
            double packetTimestampMs = NormalizePacketTimestamp(packet.TimestampMs);
            bool isEmptyPacket = packet.DeltaX == 0 && packet.DeltaY == 0;
            bool isMotionReportMode = _mode == PollingRateMode.MotionReportRate;
            bool isCountedReport = !isMotionReportMode || !isEmptyPacket;

            _stabilityMetricsWindow.PushPacket(packetTimestampMs, isEmptyPacket, isCountedReport);
            if (!isCountedReport)
            {
                return;
            }

            _reportRateWindow.AddReportTimestamp(packetTimestampMs);
        }
    }

    public PollingMetricsSnapshot CreateMetricsSnapshot(double nowMs, long droppedPacketCount)
    {
        lock (_syncRoot)
        {
            PruneByNow(nowMs);

            double? rawRate = _reportRateWindow.ComputeRate();
            int currentRate = 0;
            double? emptyPacketPercent = null;

            if (rawRate.HasValue)
            {
                double currentRawRate = rawRate.Value;
                currentRate = (int)Math.Round(currentRawRate);
                UpdatePeakRate(SnapRate(currentRawRate));
                emptyPacketPercent = _mode == PollingRateMode.MotionReportRate
                    ? _stabilityMetricsWindow.ComputeEmptyPacketPercent()
                    : null;
            }

            return new PollingMetricsSnapshot(currentRate, _peakRate, emptyPacketPercent, droppedPacketCount);
        }
    }

    public PollingChartRenderFrame CreateChartRenderFrame(double nowMs)
    {
        lock (_syncRoot)
        {
            PruneByNow(nowMs);
            double rawCurrentRate = _reportRateWindow.ComputeRate() ?? 0.0;
            SampleRenderHistory(nowMs, rawCurrentRate);
            return new PollingChartRenderFrame(rawCurrentRate, GetRenderHistoryView());
        }
    }

    private double NormalizePacketTimestamp(double packetTimestampMs)
    {
        if (!IsFinite(packetTimestampMs))
        {
            packetTimestampMs = _latestPacketTimestampMs;
        }

        if (IsFinite(_latestPacketTimestampMs) && packetTimestampMs < _latestPacketTimestampMs)
        {
            packetTimestampMs = _latestPacketTimestampMs;
        }

        _latestPacketTimestampMs = packetTimestampMs;
        return packetTimestampMs;
    }

    private void PruneByNow(double nowMs)
    {
        _reportRateWindow.PruneByNow(nowMs);
        _stabilityMetricsWindow.PruneByNow(nowMs);
        _renderHistory.RemoveBefore(GetLogicalHistoryNow(nowMs) - HistoryMilliseconds);
    }

    private void UpdatePeakRate(int candidateRate)
    {
        if (candidateRate <= _peakRate || !CanUpdatePeak())
        {
            _pendingPeakRate = 0;
            return;
        }

        if (_pendingPeakRate > 0)
        {
            int confirmedPeakRate = Math.Min(_pendingPeakRate, candidateRate);
            if (confirmedPeakRate > _peakRate)
            {
                _peakRate = confirmedPeakRate;
            }
        }

        _pendingPeakRate = candidateRate;
    }

    private bool CanUpdatePeak()
    {
        return _reportRateWindow.CanUseForPeak(_peakEligibleAtMs);
    }

    private static int SnapRate(double rate)
    {
        int nearestCommonRate = CommonRates[0];
        double nearestRelativeDifference = double.MaxValue;

        foreach (int commonRate in CommonRates)
        {
            double relativeDifference = Math.Abs(rate - commonRate) / commonRate;
            if (relativeDifference < nearestRelativeDifference)
            {
                nearestRelativeDifference = relativeDifference;
                nearestCommonRate = commonRate;
            }
        }

        return nearestRelativeDifference <= SnapTolerance ? nearestCommonRate : (int)Math.Round(rate);
    }

    private void SampleRenderHistory(double nowMs, double rawCurrentRate)
    {
        if (_lastRenderHistorySampleMs > 0.0 && nowMs - _lastRenderHistorySampleMs < _renderHistorySampleMilliseconds)
        {
            return;
        }

        double logicalHistoryNowMs = GetLogicalHistoryNow(nowMs);
        if (IsFinite(_lastLogicalRenderHistorySampleMs) && logicalHistoryNowMs <= _lastLogicalRenderHistorySampleMs)
        {
            logicalHistoryNowMs = _lastLogicalRenderHistorySampleMs + LockedHistoryMinimumStepMilliseconds;
        }

        _lastRenderHistorySampleMs = nowMs;
        _lastLogicalRenderHistorySampleMs = logicalHistoryNowMs;
        _renderHistory.Add(new PollingHistoryPoint(logicalHistoryNowMs, nowMs, rawCurrentRate));
        _renderHistory.RemoveBefore(logicalHistoryNowMs - HistoryMilliseconds);
    }

    private IReadOnlyList<PollingHistoryPoint> GetRenderHistoryView()
    {
        int version = _renderHistory.Version;
        if (version != _cachedRenderHistoryVersion)
        {
            _cachedRenderHistoryView = _renderHistory.CreateView();
            _cachedRenderHistoryVersion = version;
        }

        return _cachedRenderHistoryView;
    }

    private double GetLogicalHistoryNow(double nowMs)
    {
        double pausedMilliseconds = _renderHistoryAccumulatedPauseMilliseconds;
        if (IsFinite(_renderHistoryPausedStartedRealtimeMs) && IsFinite(nowMs) && nowMs > _renderHistoryPausedStartedRealtimeMs)
        {
            pausedMilliseconds += nowMs - _renderHistoryPausedStartedRealtimeMs;
        }

        return nowMs - pausedMilliseconds;
    }

    private void ResumeRenderHistoryClock(double nowMs)
    {
        if (!IsFinite(_renderHistoryPausedStartedRealtimeMs))
        {
            return;
        }

        if (IsFinite(nowMs) && nowMs > _renderHistoryPausedStartedRealtimeMs)
        {
            _renderHistoryAccumulatedPauseMilliseconds += nowMs - _renderHistoryPausedStartedRealtimeMs;
        }

        _renderHistoryPausedStartedRealtimeMs = double.NaN;
    }

    private void StartRenderHistoryPauseClock(double nowMs)
    {
        if (IsFinite(nowMs) && !IsFinite(_renderHistoryPausedStartedRealtimeMs))
        {
            _renderHistoryPausedStartedRealtimeMs = nowMs;
        }
    }

    private static double NormalizeHistorySampleInterval(double milliseconds)
    {
        if (!IsFinite(milliseconds) || milliseconds <= 0.0)
        {
            return DefaultRenderHistorySampleMilliseconds;
        }

        return Math.Max(1.0, Math.Min(100.0, milliseconds));
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
