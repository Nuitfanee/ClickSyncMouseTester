using ClickSyncMouseTester.ChartGpu;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels.Pages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using Vortice.D3DCompiler;

namespace ClickSyncMouseTester.Tests;

internal static class Program
{
    private const int TestDpi = 100;
    private const double StartedMs = 1000.0;
    private const double FirstMeasuredPacketMs = 1250.0;
    private const double PacketIntervalMs = 20.0;

    [SupportedOSPlatform("windows")]
    private static int Main()
    {
        (string Name, Action Run)[] tests =
        {
            ("Warmup packets are ignored", WarmupPacketsAreIgnored),
            ("Rotated target axis keeps stable scale", RotatedTargetAxisKeepsStableScale),
            ("Shape mismatch reports shape failure", ShapeMismatchReportsShapeFailure),
            ("Mouse performance keeps raw zero and control reports", MousePerformanceKeepsRawZeroAndControlReports),
            ("Mouse performance keeps absolute reports out of relative motion", MousePerformanceKeepsAbsoluteReportsOutOfRelativeMotion),
            ("Mouse performance uses device timing sequence for gap diagnostics", MousePerformanceUsesDeviceTimingSequenceForGapDiagnostics),
            ("Mouse performance flags timing sequence gaps inside a segment", MousePerformanceFlagsTimingSequenceGapsInsideSegment),
            ("Mouse performance ignores timing sequence gaps across paused segments", MousePerformanceIgnoresTimingSequenceGapsAcrossPausedSegments),
            ("Mouse performance export preserves timing scale", MousePerformanceExportPreservesTimingScale),
            ("Mouse performance export preserves raw report fields", MousePerformanceExportPreservesRawReportFields),
            ("Mouse performance summary snapshot avoids packet pump lock contention", MousePerformanceSummarySnapshotAvoidsPacketPumpLockContention),
            ("Generic HID mouse devices sort after named devices", GenericHidMouseDevicesSortAfterNamedDevices),
            ("Keyboard-named raw mouse endpoints stay hidden", KeyboardNamedRawMouseEndpointsStayHidden),
            ("Unnamed zero-button raw mouse endpoints stay hidden until motion capable", UnnamedZeroButtonRawMouseEndpointsStayHiddenUntilMotionCapable),
            ("Button-bearing unknown raw mouse endpoints remain selectable before motion", ButtonBearingUnknownRawMouseEndpointsRemainSelectableBeforeMotion),
            ("Motion-capable devices sort before unknown candidates", MotionCapableDevicesSortBeforeUnknownCandidates),
            ("Button-bearing unknown raw mouse endpoints hide after motion device is known", ButtonBearingUnknownRawMouseEndpointsHideAfterMotionDeviceIsKnown),
            ("Generic HID unknown candidates sort after named unknown candidates", GenericHidUnknownCandidatesSortAfterNamedUnknownCandidates),
            ("Initial device selection uses first listed device", InitialDeviceSelectionUsesFirstListedDevice),
            ("Device refresh preserves previous motion endpoint selection", DeviceRefreshPreservesPreviousMotionEndpointSelection),
            ("Mouse performance chart presentation policy matches plot semantics", MousePerformanceChartPresentationPolicyMatchesPlotSemantics),
            ("Interval chart cache preserves scatter-only presentation", IntervalChartCachePreservesScatterOnlyPresentation),
            ("Frequency chart cache preserves continuous estimate presentation", FrequencyChartCachePreservesContinuousEstimatePresentation),
            ("Velocity chart cache preserves continuous estimate presentation", VelocityChartCachePreservesContinuousEstimatePresentation),
            ("Velocity trends preserve report anchors", VelocityTrendsPreserveReportAnchors),
            ("Velocity trends smooth isolated report spikes", VelocityTrendsSmoothIsolatedReportSpikes),
            ("Path speed chart uses path length", PathSpeedChartUsesPathLength),
            ("Distribution statistics expose extended percentiles", DistributionStatisticsExposeExtendedPercentiles),
            ("Interval histogram preserves full sample count", IntervalHistogramPreservesFullSampleCount),
            ("Delta histograms preserve zero and magnitude samples", DeltaHistogramsPreserveZeroAndMagnitudeSamples),
            ("Delta axis histograms use integer bins", DeltaAxisHistogramsUseIntegerBins),
            ("Single histogram cache preserves bins", SingleHistogramCachePreservesBins),
            ("Comparison histogram uses shared bin layout", ComparisonHistogramUsesSharedBinLayout),
            ("Comparison delta histogram axis keeps data ticks", ComparisonDeltaHistogramAxisKeepsDataTicks),
            ("Histogram viewport and axis use full bin semantics", HistogramViewportAndAxisUseFullBinSemantics),
            ("Histogram pan viewport matches visible drag semantics", HistogramPanViewportMatchesVisibleDragSemantics),
            ("Histogram zero bar value labels are hidden", HistogramZeroBarValueLabelsAreHidden),
            ("Histogram bar label budget is shared per series", HistogramBarLabelBudgetIsSharedPerSeries),
            ("Plot display order keeps path speed after velocity plots", PlotDisplayOrderKeepsPathSpeedAfterVelocityPlots),
            ("Sum chart cache preserves locked connected presentation", SumChartCachePreservesLockedConnectedPresentation),
            ("Gap sources are shared across cached plot frames", GapSourcesAreSharedAcrossCachedPlotFrames),
            ("Comparison gap sources keep dataset slots and raw-capture alignment", ComparisonGapSourcesKeepDatasetSlotsAndRawCaptureAlignment),
            ("Count chart cache presentation matches direct frame", CountChartCachePresentationMatchesDirectFrame),
            ("Count chart cache reuses data points across presentation toggles", CountChartCacheReusesDataPointsAcrossPresentationToggles),
            ("Chart x-axis labels include visible zero", ChartXAxisLabelsIncludeVisibleZero),
            ("Chart minor axis labels include subdivision values", ChartMinorAxisLabelsIncludeSubdivisionValues),
            ("Chart axis labels use at most two decimals", ChartAxisLabelsUseAtMostTwoDecimals),
            ("Chart duplicate formatted labels are suppressed", ChartDuplicateFormattedLabelsAreSuppressed),
            ("Chart zero axes use highlighted grid lines", ChartZeroAxesUseHighlightedGridLines),
            ("GPU data-coordinate shaders compile", GpuDataCoordinateShadersCompile),
            ("GPU data chunks use relative coordinates", GpuDataChunksUseRelativeCoordinates),
            ("GPU histogram chunks apply x offset once", GpuHistogramChunksApplyXOffsetOnce)
        };

        List<string> failures = new List<string>();
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.Message}");
                Console.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        if (failures.Count == 0)
        {
            return 0;
        }

        Console.Error.WriteLine("Sensitivity match regression failures:");
        foreach (string failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        return 1;
    }

    private static void WarmupPacketsAreIgnored()
    {
        SensitivityMatchEngine engine = CreateStartedEngine();
        long sequence = 1;

        PushPair(engine, StartedMs + 100.0, 60, 0, 60, 0, ref sequence);
        SensitivityMatchCurrentRoundState state = RequireState(engine);
        Equal(0, state.SourcePacketCount, "source packet count before warmup ended");
        Equal(0, state.TargetPacketCount, "target packet count before warmup ended");
        Near(0.0, state.OverallProgress, 0.0001, "overall progress before warmup ended");

        PushPair(engine, StartedMs + 200.0, 60, 0, 60, 0, ref sequence);
        state = RequireState(engine);
        Equal(1, state.SourcePacketCount, "source packet count after warmup boundary");
        Equal(1, state.TargetPacketCount, "target packet count after warmup boundary");
    }

    private static void RotatedTargetAxisKeepsStableScale()
    {
        SensitivityMatchEngine engine = CreateStartedEngine();
        long sequence = 1;
        for (int index = 0; index < 40; index++)
        {
            double timestampMs = FirstMeasuredPacketMs + index * PacketIntervalMs;
            PushPair(engine, timestampMs, 25, 0, 20, 4, ref sequence);
        }

        engine.Update(FirstMeasuredPacketMs + 40 * PacketIntervalMs + 300.0);
        SensitivityMatchRoundResult result = RequireCompletedRound(engine);
        double expectedScale = 25.0 / Math.Sqrt(20.0 * 20.0 + 4.0 * 4.0);
        Near(expectedScale, result.Scale, 0.05, "scale for rotated target axis");
        LessThan(result.DirectionDeltaDegrees, 20.0, "rotation estimate");
    }

    private static void ShapeMismatchReportsShapeFailure()
    {
        SensitivityMatchEngine engine = CreateStartedEngine();
        long sequence = 1;
        for (int index = 0; index < 48; index++)
        {
            int targetDeltaX = index < 24 ? 20 : 40;
            double timestampMs = FirstMeasuredPacketMs + index * PacketIntervalMs;
            PushPair(engine, timestampMs, 30, 0, targetDeltaX, 0, ref sequence);
        }

        engine.Update(FirstMeasuredPacketMs + 48 * PacketIntervalMs + 300.0);
        Equal(SensitivityMatchRoundFailureReason.PathShapeMismatch, engine.LastRoundFailureReason, "shape mismatch failure reason");
    }

    private static void MousePerformanceKeepsRawZeroAndControlReports()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"raw-reports-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 1L, 0, 0, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 2L, 0, 0, buttonFlags: 1, timingSequence: 2L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(3.0), 3.0, 3L, 4, -2, timingSequence: 3L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        Equal(3, snapshot.EventCount, "raw report event count");
        Equal(MousePerformancePacketKind.Empty, snapshot.Events[0].PacketKind, "empty report kind");
        Equal(MousePerformancePacketKind.ButtonOnly, snapshot.Events[1].PacketKind, "button report kind");
        Equal(MousePerformancePacketKind.Motion, snapshot.Events[2].PacketKind, "motion report kind");
        Equal(2, snapshot.DataQuality.ZeroMotionReportCount, "zero-motion report count");
        Equal(1, snapshot.DataQuality.EmptyReportCount, "empty report count");
        Equal(1, snapshot.DataQuality.ControlReportCount, "control report count");
    }

    private static void MousePerformanceExportPreservesTimingScale()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("export-timing");
        string filePath = Path.Combine(Path.GetTempPath(), $"mouse-performance-{Guid.NewGuid():N}.json");
        try
        {
            MousePerformanceExchangeService.ExportSession(session, filePath);
            MousePerformanceSessionArchive importedSession = MousePerformanceExchangeService.ImportSession(filePath);
            Equal(session.Snapshot.EventCount, importedSession.Snapshot.EventCount, "export/import event count");
            Near(session.Snapshot.Events[1].RawTimeMs, importedSession.Snapshot.Events[1].RawTimeMs, 0.000001, "export/import raw time scale");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private static void MousePerformanceUsesDeviceTimingSequenceForGapDiagnostics()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"timing-sequence-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 10L, 1, 0, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 12L, 1, 0, timingSequence: 2L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        Equal(0, snapshot.DataQuality.SequenceGapCount, "device timing sequence gap count");
        Equal(MousePerformanceDataQualityLevel.Good, snapshot.DataQuality.QualityLevel, "device timing sequence quality");
        Equal(1L, snapshot.Events[0].TimingSequence, "first timing sequence");
        Equal(2L, snapshot.Events[1].TimingSequence, "second timing sequence");
    }

    private static void MousePerformanceFlagsTimingSequenceGapsInsideSegment()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"timing-gap-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 1L, 1, 0, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 3L, 1, 0, timingSequence: 3L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        Equal(1, snapshot.DataQuality.SequenceGapCount, "same-segment timing sequence gap count");
        Equal(MousePerformanceDataQualityLevel.Degraded, snapshot.DataQuality.QualityLevel, "same-segment timing sequence quality");
    }

    private static void MousePerformanceIgnoresTimingSequenceGapsAcrossPausedSegments()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"timing-segment-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 1L, 1, 0, timingSequence: 1L));
        engine.PauseCollecting();
        engine.BeginCollecting(deviceId, MillisecondsToStopwatchTicks(20.0), startFresh: false);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(21.0), 21.0, 10L, 1, 0, timingSequence: 10L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(22.0), 22.0, 11L, 1, 0, timingSequence: 11L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        Equal(0, snapshot.DataQuality.SequenceGapCount, "cross-segment timing sequence gap count");
        Equal(MousePerformanceDataQualityLevel.Good, snapshot.DataQuality.QualityLevel, "cross-segment timing sequence quality");
    }

    private static void MousePerformanceKeepsAbsoluteReportsOutOfRelativeMotion()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"absolute-reports-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 1L, 10, -5, rawMouseFlags: 1, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 2L, 4, -2, timingSequence: 2L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        Equal(2, snapshot.EventCount, "absolute report event count");
        Equal(RawMouseMovementMode.Absolute, snapshot.Events[0].MovementMode, "absolute report movement mode");
        Equal(10, snapshot.Events[0].RawDeltaX, "absolute raw delta x");
        Equal(-5, snapshot.Events[0].RawDeltaY, "absolute raw delta y");
        Equal(0, snapshot.Events[0].DeltaX, "absolute analysis delta x");
        Equal(0, snapshot.Events[0].DeltaY, "absolute analysis delta y");
        Equal(0, snapshot.DataQuality.ZeroMotionReportCount, "absolute raw zero-motion report count");
        Equal(4L, snapshot.Summary.SumX, "relative summary x");
        Equal(-2L, snapshot.Summary.SumY, "relative summary y");
        Near(Math.Sqrt(20.0), snapshot.Summary.PathCounts, 0.000001, "relative path count");
    }

    private static void MousePerformanceExportPreservesRawReportFields()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"raw-export-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 1L, 100, 200, rawMouseFlags: 1, buttonData: 42, extraInformation: 1234u, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 2L, 3, -4, buttonFlags: 1, timingSequence: 2L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        MousePerformanceSessionArchive session = new MousePerformanceSessionArchive(MousePerformanceSessionSourceMode.Imported, new MousePerformanceSessionMetadata("raw-export", deviceId, null, null, 0, isVirtual: true, string.Empty), snapshot);
        string filePath = Path.Combine(Path.GetTempPath(), $"mouse-performance-raw-{Guid.NewGuid():N}.json");
        try
        {
            MousePerformanceExchangeService.ExportSession(session, filePath);
            MousePerformanceSessionArchive importedSession = MousePerformanceExchangeService.ImportSession(filePath);
            Equal(2, importedSession.Snapshot.EventCount, "raw export/import event count");
            Equal(RawMouseMovementMode.Absolute, importedSession.Snapshot.Events[0].MovementMode, "raw export/import movement mode");
            Equal(100, importedSession.Snapshot.Events[0].RawDeltaX, "raw export/import raw delta x");
            Equal(200, importedSession.Snapshot.Events[0].RawDeltaY, "raw export/import raw delta y");
            Equal(0, importedSession.Snapshot.Events[0].DeltaX, "raw export/import analysis delta x");
            Equal((ushort)1, importedSession.Snapshot.Events[0].RawMouseFlags, "raw export/import raw mouse flags");
            Equal((ushort)42, importedSession.Snapshot.Events[0].ButtonData, "raw export/import button data");
            Equal(1234u, importedSession.Snapshot.Events[0].ExtraInformation, "raw export/import extra information");
            Equal(MousePerformancePacketKind.MotionWithButton, importedSession.Snapshot.Events[1].PacketKind, "raw export/import packet kind");
            Equal(snapshot.Events[0].TimingSequence, importedSession.Snapshot.Events[0].TimingSequence, "raw export/import timing sequence");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MousePerformanceSummarySnapshotAvoidsPacketPumpLockContention()
    {
        FakeRawInputSource rawInputSource = new FakeRawInputSource("cached-summary");
        using MousePerformanceCaptureService captureService = new MousePerformanceCaptureService(rawInputSource, rawInputSource, packetBufferCapacity: 16, packetConsumerDelayMilliseconds: 0);
        if (!captureService.BeginSession(rawInputSource.DeviceId, startFresh: true))
        {
            throw new InvalidOperationException("mouse performance capture session did not start");
        }

        rawInputSource.RaiseMousePacket(new RawMousePacket(rawInputSource.DeviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 1L, 1, 0, timingSequence: 1L));
        WaitUntil(() => captureService.CaptureSummarySnapshot().EventCount == 1, 1000, "initial summary snapshot");

        object syncRoot = typeof(MousePerformanceCaptureService).GetField("_syncRoot", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(captureService);
        if (syncRoot == null)
        {
            throw new InvalidOperationException("capture service sync root was not found");
        }

        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            MousePerformanceSnapshot cachedSnapshot = captureService.CaptureSummarySnapshot();
            Equal(1, cachedSnapshot.EventCount, "cached summary event count under lock contention");
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }

        captureService.StopSession();
    }

    private static void GenericHidMouseDevicesSortAfterNamedDevices()
    {
        RawMouseDeviceInfo namedDevice = new RawMouseDeviceInfo("rawinput://mouse/named", "Razer Viper", 0x1532, 0x0098, 5, isVirtual: false);
        RawMouseDeviceInfo genericDevice = new RawMouseDeviceInfo("rawinput://mouse/generic", "HID Mouse", 0x1234, 0x0001, 5, isVirtual: false);
        RawMouseDeviceInfo genericDeviceWithVidPid = new RawMouseDeviceInfo("rawinput://mouse/generic-vid-pid", "HID Mouse (VID_1234/PID_0002)", 0x1234, 0x0002, 5, isVirtual: false);
        Dictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots = new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [namedDevice.DeviceId] = CreateMotionCapableActivitySnapshot(),
            [genericDevice.DeviceId] = CreateMotionCapableActivitySnapshot(),
            [genericDeviceWithVidPid.DeviceId] = CreateMotionCapableActivitySnapshot()
        };

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { genericDevice, genericDeviceWithVidPid, namedDevice },
            activitySnapshots);

        Equal(3, selectableDevices.Count, "selectable device count");
        Equal(namedDevice.DeviceId, selectableDevices[0].DeviceId, "first selectable device");
        Equal(true, selectableDevices[1].SelectionDisplayName.StartsWith("HID Mouse", StringComparison.OrdinalIgnoreCase), "second selectable device is generic HID");
        Equal(true, selectableDevices[2].SelectionDisplayName.StartsWith("HID Mouse", StringComparison.OrdinalIgnoreCase), "third selectable device is generic HID");
    }

    private static void KeyboardNamedRawMouseEndpointsStayHidden()
    {
        RawMouseDeviceInfo mouseDevice = new RawMouseDeviceInfo("rawinput://mouse/beast", "WLMOUSE BEAST G 8K RECEIVER", 0x35A4, 0x0198, 5, isVirtual: false);
        RawMouseDeviceInfo keyboardNamedEndpoint = new RawMouseDeviceInfo("rawinput://mouse/neon75", "VGN Neon75 Keyboard", 0x33FA, 0x0101, 0, isVirtual: false);
        Dictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots = new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [mouseDevice.DeviceId] = CreateMotionCapableActivitySnapshot(),
            [keyboardNamedEndpoint.DeviceId] = CreateMotionCapableActivitySnapshot()
        };

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { keyboardNamedEndpoint, mouseDevice },
            activitySnapshots);

        Equal(1, selectableDevices.Count, "selectable device count");
        Equal(mouseDevice.DeviceId, selectableDevices[0].DeviceId, "only selectable device");
    }

    private static void UnnamedZeroButtonRawMouseEndpointsStayHiddenUntilMotionCapable()
    {
        RawMouseDeviceInfo mouseDevice = new RawMouseDeviceInfo("rawinput://mouse/beast", "WLMOUSE BEAST G 8K RECEIVER", 0x35A4, 0x0198, 5, isVirtual: false);
        RawMouseDeviceInfo unnamedKeyboardEndpoint = new RawMouseDeviceInfo("rawinput://mouse/neon75-control", "VGN Neon75", 0x33FA, 0x0101, 0, isVirtual: false);
        Dictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots = new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [mouseDevice.DeviceId] = CreateMotionCapableActivitySnapshot()
        };

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { unnamedKeyboardEndpoint, mouseDevice },
            activitySnapshots);

        Equal(1, selectableDevices.Count, "selectable device count");
        Equal(mouseDevice.DeviceId, selectableDevices[0].DeviceId, "only selectable device");
    }

    private static void ButtonBearingUnknownRawMouseEndpointsRemainSelectableBeforeMotion()
    {
        RawMouseDeviceInfo unknownCandidate = new RawMouseDeviceInfo("rawinput://mouse/unknown-candidate", "Named Candidate", 0x5678, 0x0002, 5, isVirtual: false);

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { unknownCandidate },
            new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase));

        Equal(1, selectableDevices.Count, "selectable device count");
        Equal(unknownCandidate.DeviceId, selectableDevices[0].DeviceId, "selectable device");
    }

    private static void MotionCapableDevicesSortBeforeUnknownCandidates()
    {
        RawMouseDeviceInfo motionDevice = new RawMouseDeviceInfo("rawinput://mouse/motion", "Motion Mouse", 0x1234, 0x0001, 5, isVirtual: false);
        RawMouseDeviceInfo unknownCandidate = new RawMouseDeviceInfo("rawinput://mouse/unknown-candidate", "Named Candidate", 0x5678, 0x0002, 5, isVirtual: false);

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { unknownCandidate, motionDevice },
            new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase));

        Equal(2, selectableDevices.Count, "selectable device count");
        Equal(motionDevice.DeviceId, selectableDevices[0].DeviceId, "first selectable device");
        Equal(unknownCandidate.DeviceId, selectableDevices[1].DeviceId, "second selectable device");
    }

    private static void ButtonBearingUnknownRawMouseEndpointsHideAfterMotionDeviceIsKnown()
    {
        RawMouseDeviceInfo motionDevice = new RawMouseDeviceInfo("rawinput://mouse/motion", "Motion Mouse", 0x1234, 0x0001, 5, isVirtual: false);
        RawMouseDeviceInfo unknownCandidate = new RawMouseDeviceInfo("rawinput://mouse/unknown-candidate", "Named Candidate", 0x5678, 0x0002, 5, isVirtual: false);
        Dictionary<string, RawMouseEndpointActivitySnapshot> activitySnapshots = new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [motionDevice.DeviceId] = CreateMotionCapableActivitySnapshot()
        };

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { unknownCandidate, motionDevice },
            activitySnapshots);

        Equal(1, selectableDevices.Count, "selectable device count");
        Equal(motionDevice.DeviceId, selectableDevices[0].DeviceId, "first selectable device");
    }

    private static void GenericHidUnknownCandidatesSortAfterNamedUnknownCandidates()
    {
        RawMouseDeviceInfo namedUnknownCandidate = new RawMouseDeviceInfo("rawinput://mouse/named-candidate", "Named Candidate", 0x1234, 0x0001, 5, isVirtual: false);
        RawMouseDeviceInfo genericUnknownCandidate = new RawMouseDeviceInfo("rawinput://mouse/generic-candidate", "HID Mouse", 0x5678, 0x0002, 5, isVirtual: false);

        IReadOnlyList<RawMouseDeviceInfo> selectableDevices = new RawMouseDeviceCatalog().CreateSelectableDevices(
            new[] { genericUnknownCandidate, namedUnknownCandidate },
            new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase));

        Equal(2, selectableDevices.Count, "selectable device count");
        Equal(namedUnknownCandidate.DeviceId, selectableDevices[0].DeviceId, "first selectable device");
        Equal(genericUnknownCandidate.DeviceId, selectableDevices[1].DeviceId, "second selectable device");
    }

    private static void InitialDeviceSelectionUsesFirstListedDevice()
    {
        RawMouseDeviceInfo mouseDevice = new RawMouseDeviceInfo("rawinput://mouse/beast", "WLMOUSE BEAST G 8K RECEIVER", 0x35A4, 0x0198, 5, isVirtual: false)
            .WithEndpointMetadata(RawMouseEndpointKind.MotionCapable, isVisibleByDefault: true, isRecommended: true, endpointToken: string.Empty);
        RawMouseDeviceInfo unknownEndpoint = new RawMouseDeviceInfo("rawinput://mouse/unknown-control", "VGN Neon75", 0x33FA, 0x0101, 0, isVirtual: false)
            .WithEndpointMetadata(RawMouseEndpointKind.Unknown, isVisibleByDefault: true, isRecommended: true, endpointToken: string.Empty);

        RawMouseDeviceInfo selectedDevice = RawMouseDeviceSelectionPolicy.ResolveInitialSelection(new[] { unknownEndpoint, mouseDevice });

        Equal(unknownEndpoint.DeviceId, selectedDevice?.DeviceId, "automatic selected device");
    }

    private static void DeviceRefreshPreservesPreviousMotionEndpointSelection()
    {
        RawMouseDeviceInfo firstMouseDevice = new RawMouseDeviceInfo("rawinput://mouse/beast", "WLMOUSE BEAST G 8K RECEIVER", 0x35A4, 0x0198, 5, isVirtual: false)
            .WithEndpointMetadata(RawMouseEndpointKind.MotionCapable, isVisibleByDefault: true, isRecommended: true, endpointToken: string.Empty);
        RawMouseDeviceInfo secondMouseDevice = new RawMouseDeviceInfo("rawinput://mouse/other", "Other Mouse", 0x1234, 0x0002, 5, isVirtual: false)
            .WithEndpointMetadata(RawMouseEndpointKind.MotionCapable, isVisibleByDefault: true, isRecommended: true, endpointToken: string.Empty);

        RawMouseDeviceInfo selectedDevice = RawMouseDeviceSelectionPolicy.ResolveSelectionAfterRefresh(
            new[] { firstMouseDevice, secondMouseDevice },
            secondMouseDevice.DeviceId);

        Equal(secondMouseDevice.DeviceId, selectedDevice?.DeviceId, "refreshed selected device");
    }

    private static void MousePerformanceChartPresentationPolicyMatchesPlotSemantics()
    {
        AssertPresentationPolicy(MousePerformancePlotType.XCountVsTime, canToggleStem: true, canToggleLines: true, MousePerformanceLinePresentationMode.RawConnectedToggle, MousePerformanceStemPresentationMode.RawImpulseToggle);
        AssertPresentationPolicy(MousePerformancePlotType.YCountVsTime, canToggleStem: true, canToggleLines: true, MousePerformanceLinePresentationMode.RawConnectedToggle, MousePerformanceStemPresentationMode.RawImpulseToggle);
        AssertPresentationPolicy(MousePerformancePlotType.XYCountVsTime, canToggleStem: true, canToggleLines: true, MousePerformanceLinePresentationMode.RawConnectedToggle, MousePerformanceStemPresentationMode.RawImpulseToggle);

        AssertPresentationPolicy(MousePerformancePlotType.IntervalVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.ScatterOnlyLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.FrequencyVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.ContinuousEstimateLocked, MousePerformanceStemPresentationMode.Unavailable);

        AssertPresentationPolicy(MousePerformancePlotType.XVelocityVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.ContinuousEstimateLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.YVelocityVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.ContinuousEstimateLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.XYVelocityVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.ContinuousEstimateLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.PathSpeedVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.ContinuousEstimateLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.IntervalHistogram, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.DistributionLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.DeltaXHistogram, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.DistributionLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.DeltaYHistogram, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.DistributionLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.DeltaMagnitudeHistogram, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.DistributionLocked, MousePerformanceStemPresentationMode.Unavailable);

        AssertPresentationPolicy(MousePerformancePlotType.XSumVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.RawConnectedLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.YSumVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.RawConnectedLocked, MousePerformanceStemPresentationMode.Unavailable);
        AssertPresentationPolicy(MousePerformancePlotType.XYSumVsTime, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.RawConnectedLocked, MousePerformanceStemPresentationMode.Unavailable);

        AssertPresentationPolicy(MousePerformancePlotType.XVsY, canToggleStem: false, canToggleLines: false, MousePerformanceLinePresentationMode.TrajectoryLocked, MousePerformanceStemPresentationMode.Unavailable);
    }

    private static void IntervalChartCachePreservesScatterOnlyPresentation()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("timing-cache");
        int endIndex = session.Snapshot.EventCount - 1;
        BuildCachedFrame(session, MousePerformancePlotType.IntervalVsTime, endIndex, showStem: false, showLines: false);

        MousePerformanceChartRenderFrame cachedFrame = BuildCachedFrame(session, MousePerformancePlotType.IntervalVsTime, endIndex, showStem: true, showLines: true);
        MousePerformanceChartRenderFrame directFrame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.IntervalVsTime, 0, endIndex, showStem: true, showLines: true, MousePerformanceTimeBasis.RawCapture);

        AssertFrameSeriesShapeEqual(directFrame, cachedFrame, "interval cache frame");
        Equal(false, directFrame.ShowStem, "interval direct stem state");
        Equal(false, directFrame.ShowLines, "interval direct line toggle state");
        Equal(false, cachedFrame.ShowStem, "interval cached stem state");
        Equal(false, cachedFrame.ShowLines, "interval cached line toggle state");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary), "interval cached scatter series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent), "interval cached raw line series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary), "interval cached trend line series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary), "interval cached stem series count");
    }

    private static void FrequencyChartCachePreservesContinuousEstimatePresentation()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("frequency-continuous-cache");
        int endIndex = session.Snapshot.EventCount - 1;
        BuildCachedFrame(session, MousePerformancePlotType.FrequencyVsTime, endIndex, showStem: false, showLines: false);

        MousePerformanceChartRenderFrame cachedFrame = BuildCachedFrame(session, MousePerformancePlotType.FrequencyVsTime, endIndex, showStem: true, showLines: true);
        MousePerformanceChartRenderFrame directFrame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.FrequencyVsTime, 0, endIndex, showStem: true, showLines: true, MousePerformanceTimeBasis.RawCapture);

        AssertFrameSeriesShapeEqual(directFrame, cachedFrame, "frequency continuous cache frame");
        Equal(false, directFrame.ShowStem, "frequency direct stem state");
        Equal(false, directFrame.ShowLines, "frequency direct line toggle state");
        Equal(false, cachedFrame.ShowStem, "frequency cached stem state");
        Equal(false, cachedFrame.ShowLines, "frequency cached line toggle state");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary), "frequency cached estimate line series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary), "frequency cached scatter series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary), "frequency cached stem series count");
        MousePerformanceChartSeries estimateSeries = FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary);
        Equal(MousePerformanceSampleBasis.ReportTiming, estimateSeries.SampleBasis, "frequency estimate sample basis");
        Equal(session.Snapshot.EventCount - 1, estimateSeries.Points.Count, "frequency estimate point count");
        Equal(1, cachedFrame.GapSources.Count, "frequency cached gap source count");
        Equal(session.Snapshot.EventCount - 1, cachedFrame.GapSources[0].Intervals.Count, "frequency cached report gap interval count");
    }

    private static void VelocityChartCachePreservesContinuousEstimatePresentation()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("velocity-continuous-cache");
        int endIndex = session.Snapshot.EventCount - 1;
        BuildCachedFrame(session, MousePerformancePlotType.XYVelocityVsTime, endIndex, showStem: false, showLines: false);

        MousePerformanceChartRenderFrame cachedFrame = BuildCachedFrame(session, MousePerformancePlotType.XYVelocityVsTime, endIndex, showStem: true, showLines: true);
        MousePerformanceChartRenderFrame directFrame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.XYVelocityVsTime, 0, endIndex, showStem: true, showLines: true, MousePerformanceTimeBasis.RawCapture);

        AssertFrameSeriesShapeEqual(directFrame, cachedFrame, "velocity continuous cache frame");
        Equal(false, directFrame.ShowStem, "velocity direct stem state");
        Equal(false, directFrame.ShowLines, "velocity direct line toggle state");
        Equal(false, cachedFrame.ShowStem, "velocity cached stem state");
        Equal(false, cachedFrame.ShowLines, "velocity cached line toggle state");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary), "velocity cached primary estimate line series count");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary), "velocity cached secondary estimate line series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary), "velocity cached primary scatter series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Secondary), "velocity cached secondary scatter series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary), "velocity cached primary stem series count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Secondary), "velocity cached secondary stem series count");
        Equal(MousePerformanceSampleBasis.TrendEstimate, FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary).SampleBasis, "velocity primary estimate sample basis");
        Equal(MousePerformanceSampleBasis.TrendEstimate, FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary).SampleBasis, "velocity secondary estimate sample basis");
        Equal(1, cachedFrame.GapSources.Count, "velocity cached gap source count");
        Equal(session.Snapshot.EventCount - 1, cachedFrame.GapSources[0].Intervals.Count, "velocity cached report gap interval count");
    }

    private static void SumChartCachePreservesLockedConnectedPresentation()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("sum-locked-cache");
        int endIndex = session.Snapshot.EventCount - 1;
        BuildCachedFrame(session, MousePerformancePlotType.XYSumVsTime, endIndex, showStem: false, showLines: false);

        MousePerformanceChartRenderFrame cachedFrame = BuildCachedFrame(session, MousePerformancePlotType.XYSumVsTime, endIndex, showStem: true, showLines: true);
        MousePerformanceChartRenderFrame directFrame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.XYSumVsTime, 0, endIndex, showStem: true, showLines: true, MousePerformanceTimeBasis.RawCapture);

        AssertFrameSeriesShapeEqual(directFrame, cachedFrame, "sum locked cache frame");
        Equal(false, directFrame.ShowStem, "sum direct stem state");
        Equal(false, directFrame.ShowLines, "sum direct line toggle state");
        Equal(false, cachedFrame.ShowStem, "sum cached stem state");
        Equal(false, cachedFrame.ShowLines, "sum cached line toggle state");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent), "sum cached primary connected line count");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary), "sum cached secondary connected line count");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary), "sum cached primary scatter count");
        Equal(1, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Secondary), "sum cached secondary scatter count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary), "sum cached primary stem count");
        Equal(0, CountSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Secondary), "sum cached secondary stem count");
        Equal(MousePerformanceSampleBasis.CumulativeMotion, FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent).SampleBasis, "sum primary line sample basis");
        Equal(MousePerformanceSampleBasis.CumulativeMotion, FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Secondary).SampleBasis, "sum secondary line sample basis");
    }

    private static void VelocityTrendsPreserveReportAnchors()
    {
        MousePerformanceSnapshot snapshot = CreateVelocitySpikeSnapshot("velocity-anchor");
        MousePerformanceChartRenderFrame xFrame = MousePerformanceEngine.CreateChartRenderFrame(snapshot, MousePerformancePlotType.XVelocityVsTime, 0, snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartRenderFrame pathFrame = MousePerformanceEngine.CreateChartRenderFrame(snapshot, MousePerformancePlotType.PathSpeedVsTime, 0, snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartSeries xSeries = FindSeries(xFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary);
        MousePerformanceChartSeries pathSeries = FindSeries(pathFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary);

        Equal(snapshot.EventCount - 1, xSeries.Points.Count, "x velocity report-anchor point count");
        Equal(snapshot.EventCount - 1, pathSeries.Points.Count, "path speed report-anchor point count");
        for (int pointIndex = 0; pointIndex < xSeries.Points.Count; pointIndex++)
        {
            Near(snapshot.Events[pointIndex + 1].RawTimeMs, xSeries.Points[pointIndex].X, 0.000001, $"x velocity report anchor {pointIndex}");
            Near(snapshot.Events[pointIndex + 1].RawTimeMs, pathSeries.Points[pointIndex].X, 0.000001, $"path speed report anchor {pointIndex}");
        }
    }

    private static void VelocityTrendsSmoothIsolatedReportSpikes()
    {
        MousePerformanceSnapshot snapshot = CreateVelocitySpikeSnapshot("velocity-spike-smoothing");
        MousePerformanceSessionAnalysisIndex index = new MousePerformanceSessionAnalysisIndex(snapshot.Events);
        MousePerformanceAnalysisOptions options = new MousePerformanceAnalysisOptions(
            trendWindowMs: 1.0,
            minimumTrendSamples: 3,
            currentVelocityWindowMs: 30.0,
            timingSeriesRecommendedWindowMs: 75.0,
            timingSeriesRecommendedMinimumSamples: 7,
            timingSeriesTrimRatio: 0.1,
            timingSeriesEmaTimeConstantMs: 60.0);

        IReadOnlyList<MousePerformanceChartPoint> xTrend = MousePerformanceSeriesBuilder.BuildVelocityTrend(index, 0, snapshot.EventCount - 1, snapshot.EffectiveCpi, MousePerformancePlotType.XVelocityVsTime, options, MousePerformanceTimeBasis.RawCapture);
        IReadOnlyList<MousePerformanceChartPoint> pathTrend = MousePerformanceSeriesBuilder.BuildPathSpeedTrend(index, 0, snapshot.EventCount - 1, snapshot.EffectiveCpi, options, MousePerformanceTimeBasis.RawCapture);

        Equal(snapshot.EventCount - 1, xTrend.Count, "smoothed x velocity point count");
        Equal(snapshot.EventCount - 1, pathTrend.Count, "smoothed path speed point count");
        LessThan(MaxAbsoluteY(xTrend), 6.0, "smoothed x velocity isolated spike amplitude");
        LessThan(MaxAbsoluteY(pathTrend), 6.0, "smoothed path speed isolated spike amplitude");
        GreaterThan(MaxAbsoluteY(pathTrend), 0.0, "path speed remains non-zero after smoothing");
    }

    private static void PathSpeedChartUsesPathLength()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        engine.SetCpiState(25.4, canComputeVelocity: true);
        string deviceId = $"path-speed-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(0.0), 0.0, 1L, 0, 0, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(10.0), 10.0, 2L, 3, 4, timingSequence: 2L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(20.0), 20.0, 3L, -3, -4, timingSequence: 3L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        MousePerformanceChartRenderFrame frame = MousePerformanceEngine.CreateChartRenderFrame(snapshot, MousePerformancePlotType.PathSpeedVsTime, 0, snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartSeries series = FindSeries(frame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Primary);

        Equal(2, series.Points.Count, "path speed point count");
        GreaterThan(series.Points[1].Y, 0.35, "path speed uses summed path length instead of net displacement");
    }

    private static void DistributionStatisticsExposeExtendedPercentiles()
    {
        double[] samples = new double[1000];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = index + 1;
        }

        MousePerformanceDistributionStatisticsSummary statistics = MousePerformanceDistributionCalculator.Compute(samples);
        Equal(1000, statistics.SampleCount, "distribution sample count");
        Near(500.5, statistics.P50Value, 0.000001, "distribution p50");
        Near(950.05, statistics.P95Value, 0.000001, "distribution p95");
        Near(990.01, statistics.P99Value, 0.000001, "distribution p99");
        Near(999.001, statistics.P999Value.Value, 0.000001, "distribution p999");
        Near(Math.Sqrt((1000.0 * 1000.0 - 1.0) / 12.0), statistics.StandardDeviationValue, 0.000001, "distribution standard deviation");

        MousePerformanceDistributionStatisticsSummary smallStatistics = MousePerformanceDistributionCalculator.Compute(new[] { 1.0, 2.0, 3.0 });
        Equal(false, smallStatistics.P999Value.HasValue, "small sample p999 unavailable");

        MousePerformanceDistributionStatisticsSummary signedStatistics = MousePerformanceDistributionCalculator.Compute(new[] { -4.0, -2.0, 0.0, 2.0 });
        Near(-1.0, signedStatistics.AverageValue, 0.000001, "signed distribution average");
        Near(-1.0, signedStatistics.P50Value, 0.000001, "signed distribution p50");
    }

    private static void IntervalHistogramPreservesFullSampleCount()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("interval-histogram");
        MousePerformanceChartRenderFrame frame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.IntervalHistogram, 0, session.Snapshot.EventCount - 1, showStem: true, showLines: true, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartSeries series = FindSeries(frame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary);

        int totalCount = 0;
        foreach (MousePerformanceHistogramBin bin in series.HistogramBins)
        {
            totalCount += bin.Count;
        }
        Equal(session.Snapshot.EventCount - 1, totalCount, "interval histogram full sample count");
        Equal(0, frame.GapSources.Count, "interval histogram gap overlay sources disabled");
        Equal(MousePerformanceSampleBasis.ReportTiming, series.SampleBasis, "interval histogram sample basis");
    }

    private static void DeltaHistogramsPreserveZeroAndMagnitudeSamples()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"delta-histogram-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(0.0), 0.0, 1L, 0, 0, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 2L, 3, 4, timingSequence: 2L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 3L, -3, -4, timingSequence: 3L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        MousePerformanceChartRenderFrame xFrame = MousePerformanceEngine.CreateChartRenderFrame(snapshot, MousePerformancePlotType.DeltaXHistogram, 0, snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartRenderFrame magnitudeFrame = MousePerformanceEngine.CreateChartRenderFrame(snapshot, MousePerformancePlotType.DeltaMagnitudeHistogram, 0, snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);

        Equal(snapshot.EventCount, SumHistogramCounts(FindSeries(xFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary)), "delta x histogram full sample count");
        Equal(snapshot.EventCount, SumHistogramCounts(FindSeries(magnitudeFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary)), "delta magnitude histogram full sample count");
        Equal(MousePerformanceSampleBasis.RawReport, FindSeries(magnitudeFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary).SampleBasis, "delta magnitude histogram sample basis");
    }

    private static void DeltaAxisHistogramsUseIntegerBins()
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string deviceId = $"delta-integer-histogram-{Guid.NewGuid():N}";
        engine.BeginCollecting(deviceId, 0L, startFresh: true);
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(0.0), 0.0, 1L, -2, 0, timingSequence: 1L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(1.0), 1.0, 2L, -1, 0, timingSequence: 2L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(2.0), 2.0, 3L, 0, 0, timingSequence: 3L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(3.0), 3.0, 4L, 1, 0, timingSequence: 4L));
        engine.PushPacket(new RawMousePacket(deviceId, MillisecondsToStopwatchTicks(4.0), 4.0, 5L, 2, 0, timingSequence: 5L));
        engine.StopCollecting();

        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        MousePerformanceChartRenderFrame frame = MousePerformanceEngine.CreateChartRenderFrame(snapshot, MousePerformancePlotType.DeltaXHistogram, 0, snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartSeries series = FindSeries(frame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary);
        Equal(5, series.HistogramBins.Count, "delta x integer bin count");
        for (int binIndex = 0; binIndex < series.HistogramBins.Count; binIndex++)
        {
            MousePerformanceHistogramBin bin = series.HistogramBins[binIndex];
            Near(-2 + binIndex, bin.CenterX, 0.000001, $"delta x bin {binIndex} integer center");
            Near(1.0, bin.MaximumX - bin.MinimumX, 0.000001, $"delta x bin {binIndex} width");
        }

        object viewport = InvokeChartControlStatic<object>("BuildAutomaticViewport", frame);
        IReadOnlyList<double> ticks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveXAxisLabelTicks", viewport, frame);
        ContainsNear(-2.0, ticks, 0.000001, "delta x-axis integer tick");
        ContainsNear(0.0, ticks, 0.000001, "delta x-axis zero tick");
        ContainsNear(2.0, ticks, 0.000001, "delta x-axis positive integer tick");
    }

    private static void ComparisonHistogramUsesSharedBinLayout()
    {
        MousePerformanceSessionArchive baseline = CreateMousePerformanceSession("histogram-compare-baseline", deltaScale: 1);
        MousePerformanceSessionArchive comparison = CreateMousePerformanceSession("histogram-compare-a", deltaScale: 4);
        int endIndex = Math.Min(baseline.Snapshot.EventCount, comparison.Snapshot.EventCount) - 1;
        MousePerformanceChartRenderFrame baselineFrame = MousePerformanceEngine.CreateChartRenderFrame(baseline.Snapshot, MousePerformancePlotType.DeltaMagnitudeHistogram, 0, endIndex, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartRenderFrame comparisonFrame = MousePerformanceEngine.CreateChartRenderFrame(comparison.Snapshot, MousePerformancePlotType.DeltaMagnitudeHistogram, 0, endIndex, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);

        object baselineEntry = CreateNestedViewModelInstance("DatasetSessionEntry", MousePerformanceChartDatasetSlot.Baseline, baseline);
        object comparisonEntry = CreateNestedViewModelInstance("DatasetSessionEntry", MousePerformanceChartDatasetSlot.CompareA, comparison);
        Array visibleEntries = Array.CreateInstance(baselineEntry.GetType(), 2);
        visibleEntries.SetValue(baselineEntry, 0);
        visibleEntries.SetValue(comparisonEntry, 1);

        object request = CreateNestedViewModelInstance("RenderRequestState", MousePerformancePlotType.DeltaMagnitudeHistogram, MousePerformanceTimeBasis.RawCapture, 0, endIndex, false, false, 2, visibleEntries);
        object baselineFrameEntry = CreateNestedViewModelInstance("DatasetRenderFrameEntry", MousePerformanceChartDatasetSlot.Baseline, baselineFrame);
        object comparisonFrameEntry = CreateNestedViewModelInstance("DatasetRenderFrameEntry", MousePerformanceChartDatasetSlot.CompareA, comparisonFrame);
        Array availableFrames = Array.CreateInstance(baselineFrameEntry.GetType(), 2);
        availableFrames.SetValue(baselineFrameEntry, 0);
        availableFrames.SetValue(comparisonFrameEntry, 1);

        MousePerformanceChartRenderFrame mergedFrame = (MousePerformanceChartRenderFrame)InvokeChartWindowViewModelStatic("CreateMergedHistogramRenderFrame", request, availableFrames);
        Equal(true, mergedFrame.IsAvailable, "comparison histogram availability");
        Equal(2, mergedFrame.Series.Count, "comparison histogram series count");
        Equal(0, mergedFrame.GapSources.Count, "comparison histogram gap sources");
        AssertHistogramBinLayoutEqual(mergedFrame.Series[0].HistogramBins, mergedFrame.Series[1].HistogramBins, "comparison histogram shared bin layout");
        Equal(true, Math.Abs(mergedFrame.Series[0].XOffset - mergedFrame.Series[1].XOffset) > 0.000001, "comparison histogram grouped x offset");
        Equal(true, mergedFrame.Series[0].GroupScale < 1.0, "comparison histogram grouped scale");
        Equal(SumHistogramCounts(FindSeries(baselineFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary)), SumHistogramCounts(mergedFrame.Series[0]), "baseline merged histogram sample count");
        Equal(SumHistogramCounts(FindSeries(comparisonFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary)), SumHistogramCounts(mergedFrame.Series[1]), "comparison merged histogram sample count");
    }

    private static void ComparisonDeltaHistogramAxisKeepsDataTicks()
    {
        MousePerformanceSessionArchive baseline = CreateMousePerformanceSession("histogram-axis-baseline", deltaScale: 1);
        MousePerformanceSessionArchive comparison = CreateMousePerformanceSession("histogram-axis-compare", deltaScale: 1);
        int endIndex = Math.Min(baseline.Snapshot.EventCount, comparison.Snapshot.EventCount) - 1;

        MousePerformanceChartRenderFrame mergedFrame = MousePerformanceChartFrameBuilder.CreateComparisonHistogramRenderFrame(
            new[]
            {
                new MousePerformanceHistogramDataset(MousePerformanceChartDatasetSlot.Baseline, baseline.Snapshot),
                new MousePerformanceHistogramDataset(MousePerformanceChartDatasetSlot.CompareA, comparison.Snapshot)
            },
            MousePerformancePlotType.DeltaXHistogram,
            0,
            endIndex,
            showStem: false,
            showLines: false,
            MousePerformanceTimeBasis.RawCapture);

        Equal(true, Math.Abs(mergedFrame.Series[0].XOffset) > 0.000001, "comparison delta histogram has grouped visual offset");
        object viewport = InvokeChartControlStatic<object>("BuildAutomaticViewport", mergedFrame);
        IReadOnlyList<double> ticks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveXAxisLabelTicks", viewport, mergedFrame);
        ContainsNear(-1.0, ticks, 0.000001, "comparison delta x-axis negative integer tick");
        ContainsNear(2.0, ticks, 0.000001, "comparison delta x-axis positive integer tick");
        foreach (double tick in ticks)
        {
            Near(Math.Round(tick, MidpointRounding.AwayFromZero), tick, 0.000001, "comparison delta x-axis tick remains integer");
        }
    }

    private static void SingleHistogramCachePreservesBins()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("single-histogram-cache");
        int endIndex = session.Snapshot.EventCount - 1;
        MousePerformanceChartRenderFrame directFrame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.DeltaXHistogram, 0, endIndex, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartRenderFrame cachedFrame = BuildCachedFrame(session, MousePerformancePlotType.DeltaXHistogram, endIndex, showStem: false, showLines: false);
        MousePerformanceChartSeries directSeries = FindSeries(directFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary);
        MousePerformanceChartSeries cachedSeries = FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary);

        AssertHistogramBinLayoutEqual(directSeries.HistogramBins, cachedSeries.HistogramBins, "single histogram cache bin layout");
        Equal(SumHistogramCounts(directSeries), SumHistogramCounts(cachedSeries), "single histogram cache sample count");
        Near(0.0, cachedSeries.XOffset, 0.000001, "single histogram cache x offset");
        Near(1.0, cachedSeries.GroupScale, 0.000001, "single histogram cache group scale");
    }

    private static void HistogramViewportAndAxisUseFullBinSemantics()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("histogram-axis");
        MousePerformanceChartRenderFrame frame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.IntervalHistogram, 0, session.Snapshot.EventCount - 1, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        object viewport = InvokeChartControlStatic<object>("BuildAutomaticViewport", frame);
        Near(frame.XMinimum, ReadStructDouble(viewport, "XMinimum"), 0.000001, "histogram automatic viewport x minimum");
        Near(frame.XMaximum, ReadStructDouble(viewport, "XMaximum"), 0.000001, "histogram automatic viewport x maximum");
        Near(frame.YMinimum, ReadStructDouble(viewport, "YMinimum"), 0.000001, "histogram automatic viewport y minimum");
        Near(frame.YMaximum, ReadStructDouble(viewport, "YMaximum"), 0.000001, "histogram automatic viewport y maximum");

        IReadOnlyList<double> ticks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveXAxisLabelTicks", viewport, frame);
        MousePerformanceChartSeries series = FindSeries(frame, MousePerformanceChartSeriesKind.Histogram, MousePerformanceChartSeriesPalette.Primary);
        MousePerformanceHistogramBin firstBin = series.HistogramBins[0];
        MousePerformanceHistogramBin lastBin = series.HistogramBins[series.HistogramBins.Count - 1];
        ContainsNear(firstBin.MinimumX, ticks, 0.000001, "histogram x-axis first bin boundary");
        ContainsNear(lastBin.MaximumX, ticks, 0.000001, "histogram x-axis last bin boundary");
    }

    private static void HistogramPanViewportMatchesVisibleDragSemantics()
    {
        MousePerformanceChartRenderFrame frame = new MousePerformanceChartRenderFrame(
            MousePerformancePlotType.IntervalHistogram,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            isAvailable: true,
            string.Empty,
            0,
            0,
            showStem: false,
            showLines: false,
            MousePerformanceTimeBasis.RawCapture,
            0.0,
            100.0,
            0.0,
            100.0,
            Array.Empty<MousePerformanceChartSeries>());
        object startViewport = CreateChartViewport(10.0, 60.0, 20.0, 70.0);

        object pannedViewport = InvokeChartControlStatic<object>("BuildPannedViewport", frame, startViewport, 50.0, 50.0, 150.0, 90.0, 400.0, 200.0);

        LessThan(ReadStructDouble(pannedViewport, "XMinimum"), ReadStructDouble(startViewport, "XMinimum"), "dragging right moves histogram viewport left");
        GreaterThan(ReadStructDouble(pannedViewport, "YMinimum"), ReadStructDouble(startViewport, "YMinimum"), "dragging down moves histogram viewport up in value space");

        object clampedViewport = InvokeChartControlStatic<object>("BuildPannedViewport", frame, startViewport, 50.0, 50.0, 5000.0, 5000.0, 400.0, 200.0);
        GreaterThanOrEqual(ReadStructDouble(clampedViewport, "XMinimum"), frame.XMinimum, "panned histogram viewport clamps x minimum");
        GreaterThanOrEqual(ReadStructDouble(clampedViewport, "YMinimum"), frame.YMinimum, "panned histogram viewport clamps y minimum");
    }

    private static void HistogramZeroBarValueLabelsAreHidden()
    {
        Equal(false, InvokeChartControlStatic<bool>("CanDisplayHistogramBarValueLabel", 0.0), "zero histogram bar label hidden");
        Equal(false, InvokeChartControlStatic<bool>("CanDisplayHistogramBarValueLabel", 0.0000001), "near-zero histogram bar label hidden");
        Equal(true, InvokeChartControlStatic<bool>("CanDisplayHistogramBarValueLabel", 1.0), "positive histogram bar label shown");
    }

    private static void HistogramBarLabelBudgetIsSharedPerSeries()
    {
        Equal(96, InvokeChartControlStatic<int>("ResolveHistogramBarValueLabelLimitPerSeries", 1), "single histogram label budget");
        Equal(48, InvokeChartControlStatic<int>("ResolveHistogramBarValueLabelLimitPerSeries", 2), "two-series histogram label budget");
        Equal(24, InvokeChartControlStatic<int>("ResolveHistogramBarValueLabelLimitPerSeries", 4), "four-series histogram label budget");
    }

    private static void PlotDisplayOrderKeepsPathSpeedAfterVelocityPlots()
    {
        IReadOnlyList<MousePerformancePlotType> order = MousePerformancePlotTraits.ResolvePlotDisplayOrder();
        int xyVelocityIndex = IndexOfPlotType(order, MousePerformancePlotType.XYVelocityVsTime);
        int pathSpeedIndex = IndexOfPlotType(order, MousePerformancePlotType.PathSpeedVsTime);
        int xSumIndex = IndexOfPlotType(order, MousePerformancePlotType.XSumVsTime);

        Equal(xyVelocityIndex + 1, pathSpeedIndex, "path speed order after velocity plots");
        Equal(pathSpeedIndex + 1, xSumIndex, "sum plot order after path speed");
    }

    private static void GapSourcesAreSharedAcrossCachedPlotFrames()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("shared-gap-source-cache");
        int endIndex = session.Snapshot.EventCount - 1;

        MousePerformanceChartRenderFrame frequencyFrame = BuildCachedFrame(session, MousePerformancePlotType.FrequencyVsTime, endIndex, showStem: false, showLines: false);
        MousePerformanceChartRenderFrame velocityFrame = BuildCachedFrame(session, MousePerformancePlotType.XVelocityVsTime, endIndex, showStem: false, showLines: false);
        MousePerformanceChartRenderFrame sumFrame = BuildCachedFrame(session, MousePerformancePlotType.XSumVsTime, endIndex, showStem: false, showLines: false);

        Equal(1, frequencyFrame.GapSources.Count, "frequency gap source count");
        Equal(1, velocityFrame.GapSources.Count, "velocity gap source count");
        Equal(1, sumFrame.GapSources.Count, "sum gap source count");
        SameReference(frequencyFrame.GapSources[0], velocityFrame.GapSources[0], "frequency/velocity cached gap source");
        SameReference(frequencyFrame.GapSources[0], sumFrame.GapSources[0], "frequency/sum cached gap source");
        Equal(session.Snapshot.EventCount - 1, frequencyFrame.GapSources[0].Intervals.Count, "shared report gap interval count");
    }

    private static void ComparisonGapSourcesKeepDatasetSlotsAndRawCaptureAlignment()
    {
        MousePerformanceSessionArchive baseline = CreateMousePerformanceSession("gap-compare-baseline");
        MousePerformanceSessionArchive comparison = CreateMousePerformanceSession("gap-compare-a");
        int endIndex = baseline.Snapshot.EventCount - 1;
        MousePerformanceChartRenderFrame baselineFrame = MousePerformanceEngine.CreateChartRenderFrame(baseline.Snapshot, MousePerformancePlotType.FrequencyVsTime, 0, endIndex, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        MousePerformanceChartRenderFrame comparisonFrame = MousePerformanceEngine.CreateChartRenderFrame(comparison.Snapshot, MousePerformancePlotType.FrequencyVsTime, 0, endIndex, showStem: false, showLines: false, MousePerformanceTimeBasis.RawCapture);
        double baselineMinimumX = Convert.ToDouble(InvokeChartWindowViewModelStatic("ResolveFrameSeriesMinimumX", baselineFrame));
        double comparisonMinimumX = Convert.ToDouble(InvokeChartWindowViewModelStatic("ResolveFrameSeriesMinimumX", comparisonFrame));
        MousePerformanceChartRenderFrame alignedBaselineFrame = (MousePerformanceChartRenderFrame)InvokeChartWindowViewModelStatic("TranslateFrameAlongX", baselineFrame, -baselineMinimumX);
        MousePerformanceChartRenderFrame alignedComparisonFrame = (MousePerformanceChartRenderFrame)InvokeChartWindowViewModelStatic("TranslateFrameAlongX", comparisonFrame, -comparisonMinimumX);

        List<MousePerformanceChartGapSource> mergedGapSources = new List<MousePerformanceChartGapSource>();
        InvokeChartWindowViewModelStatic("AppendDatasetGapSources", mergedGapSources, alignedBaselineFrame, MousePerformanceChartDatasetSlot.Baseline);
        InvokeChartWindowViewModelStatic("AppendDatasetGapSources", mergedGapSources, alignedComparisonFrame, MousePerformanceChartDatasetSlot.CompareA);

        Equal(2, mergedGapSources.Count, "comparison merged gap source count");
        MousePerformanceChartGapSource baselineGapSource = FindGapSource(mergedGapSources, MousePerformanceChartDatasetSlot.Baseline);
        MousePerformanceChartGapSource comparisonGapSource = FindGapSource(mergedGapSources, MousePerformanceChartDatasetSlot.CompareA);
        Equal(MousePerformanceChartDatasetSlot.Baseline, baselineGapSource.DatasetSlot, "baseline gap source slot");
        Equal(MousePerformanceChartDatasetSlot.CompareA, comparisonGapSource.DatasetSlot, "comparison gap source slot");
        Equal(baseline.Snapshot.EventCount - 1, baselineGapSource.Intervals.Count, "baseline gap interval count");
        Equal(comparison.Snapshot.EventCount - 1, comparisonGapSource.Intervals.Count, "comparison gap interval count");
        Near(baselineGapSource.Intervals[0].StartX + baselineGapSource.XOffset, comparisonGapSource.Intervals[0].StartX + comparisonGapSource.XOffset, 0.000001, "comparison first gap start after alignment");
        Near(baselineGapSource.Intervals[0].EndX + baselineGapSource.XOffset, comparisonGapSource.Intervals[0].EndX + comparisonGapSource.XOffset, 0.000001, "comparison first gap end after alignment");
    }

    private static void CountChartCachePresentationMatchesDirectFrame()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("count-cache");
        int endIndex = session.Snapshot.EventCount - 1;
        BuildCachedFrame(session, MousePerformancePlotType.XCountVsTime, endIndex, showStem: false, showLines: false);

        MousePerformanceChartRenderFrame cachedFrame = BuildCachedFrame(session, MousePerformancePlotType.XCountVsTime, endIndex, showStem: true, showLines: true);
        MousePerformanceChartRenderFrame directFrame = MousePerformanceEngine.CreateChartRenderFrame(session.Snapshot, MousePerformancePlotType.XCountVsTime, 0, endIndex, showStem: true, showLines: true, MousePerformanceTimeBasis.RawCapture);

        AssertFrameSeriesShapeEqual(directFrame, cachedFrame, "count cache frame");
        MousePerformanceChartSeries scatterSeries = FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
        MousePerformanceChartSeries lineSeries = FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Line, MousePerformanceChartSeriesPalette.Accent);
        MousePerformanceChartSeries stemSeries = FindSeries(cachedFrame, MousePerformanceChartSeriesKind.Stem, MousePerformanceChartSeriesPalette.Primary);
        SameReference(scatterSeries.Points, lineSeries.Points, "count cached scatter/line point list");
        SameReference(scatterSeries.Points, stemSeries.Points, "count cached scatter/stem point list");
    }

    private static void CountChartCacheReusesDataPointsAcrossPresentationToggles()
    {
        MousePerformanceSessionArchive session = CreateMousePerformanceSession("count-cache-reuse");
        int endIndex = session.Snapshot.EventCount - 1;

        MousePerformanceChartRenderFrame trendFrame = BuildCachedFrame(session, MousePerformancePlotType.XCountVsTime, endIndex, showStem: false, showLines: false);
        MousePerformanceChartRenderFrame lineFrame = BuildCachedFrame(session, MousePerformancePlotType.XCountVsTime, endIndex, showStem: true, showLines: true);

        MousePerformanceChartSeries trendScatterSeries = FindSeries(trendFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
        MousePerformanceChartSeries lineScatterSeries = FindSeries(lineFrame, MousePerformanceChartSeriesKind.Scatter, MousePerformanceChartSeriesPalette.Primary);
        SameReference(trendScatterSeries.Points, lineScatterSeries.Points, "cached data scatter point list across presentation toggles");
        Equal(MousePerformanceSampleBasis.RawReport, lineScatterSeries.SampleBasis, "count scatter sample basis");
    }

    private static void GpuDataCoordinateShadersCompile()
    {
        Assembly chartGpuAssembly = typeof(ClickSyncMouseTester.ChartGpu.GpuSeriesSubmission).Assembly;
        Type rendererType = chartGpuAssembly.GetType("ClickSyncMouseTester.ChartGpu.ChartGpuRenderer", throwOnError: true);
        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        ShaderFlags shaderFlags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        foreach (string fieldName in new[] { "DataLineShaderSource", "DataCircleShaderSource" })
        {
            string source = (string)rendererType.GetField(fieldName, flags).GetValue(null);
            _ = Compiler.Compile(source, "VSMain", fieldName, "vs_4_0", shaderFlags).ToArray();
            _ = Compiler.Compile(source, "PSMain", fieldName, "ps_4_0", shaderFlags).ToArray();
        }
    }

    private static void ChartXAxisLabelsIncludeVisibleZero()
    {
        object viewport = CreateChartViewport(-12.0, 37.0, -4.0, 8.0);
        IReadOnlyList<double> ticks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveXAxisLabelTicks", viewport);
        ContainsNear(0.0, ticks, 0.000001, "x-axis zero label tick");
    }

    private static void ChartMinorAxisLabelsIncludeSubdivisionValues()
    {
        object viewport = CreateChartViewport(0.0, 50.0, 0.0, 50.0);
        IReadOnlyList<double> xAxisTicks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveXAxisLabelTicks", viewport);
        IReadOnlyList<double> minorXAxisTicks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveMinorXAxisLabelTicks", viewport, xAxisTicks);
        IReadOnlyList<double> yAxisTicks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveYAxisTicks", viewport);
        IReadOnlyList<double> minorYAxisTicks = InvokeChartControlStatic<IReadOnlyList<double>>("ResolveMinorYAxisTicks", viewport, yAxisTicks);

        ContainsNear(2.0, minorXAxisTicks, 0.000001, "x-axis minor label tick");
        ContainsNear(8.0, minorXAxisTicks, 0.000001, "x-axis minor label tick before major");
        ContainsNear(2.0, minorYAxisTicks, 0.000001, "y-axis minor label tick");
        ContainsNear(8.0, minorYAxisTicks, 0.000001, "y-axis minor label tick before major");
    }

    private static void ChartAxisLabelsUseAtMostTwoDecimals()
    {
        Equal("10.12", InvokeChartControlStatic<string>("FormatAxisValue", 10.123), "axis label rounds to two decimals");
        Equal("10.1", InvokeChartControlStatic<string>("FormatAxisValue", 10.1), "axis label trims trailing zero");
        Equal("10", InvokeChartControlStatic<string>("FormatAxisValue", 10.004), "axis label omits insignificant decimals");
        Equal("0", InvokeChartControlStatic<string>("FormatAxisValue", 0.0), "axis zero label");
    }

    private static void ChartDuplicateFormattedLabelsAreSuppressed()
    {
        HashSet<string> occupiedLabels = new HashSet<string>(StringComparer.Ordinal);
        string firstLabel = InvokeChartControlStatic<string>("FormatAxisValue", 10.001);
        string duplicateLabel = InvokeChartControlStatic<string>("FormatAxisValue", 10.004);
        Equal(firstLabel, duplicateLabel, "rounded duplicate axis labels");
        Equal(true, InvokeChartControlStatic<bool>("TryReserveAxisLabelText", occupiedLabels, firstLabel), "first rounded label is reserved");
        Equal(false, InvokeChartControlStatic<bool>("TryReserveAxisLabelText", occupiedLabels, duplicateLabel), "duplicate rounded label is skipped");
    }

    private static void ChartZeroAxesUseHighlightedGridLines()
    {
        List<GpuGridLine> gridLines = new List<GpuGridLine>();
        object viewport = CreateChartViewport(-10.0, 30.0, -20.0, 20.0);
        System.Windows.Rect plotArea = new System.Windows.Rect(0.0, 0.0, 400.0, 200.0);

        InvokeChartControlStatic(
            "AddVisibleZeroAxisGridLines",
            gridLines,
            plotArea,
            viewport,
            true,
            System.Windows.Media.Colors.White);

        GpuGridLine verticalZeroAxis = FindGridLine(gridLines, isVertical: true);
        GpuGridLine horizontalZeroAxis = FindGridLine(gridLines, isVertical: false);
        Near(100.0, verticalZeroAxis.PositionPixels, 0.001, "vertical zero-axis position");
        Near(100.0, horizontalZeroAxis.PositionPixels, 0.001, "horizontal zero-axis position");
        Equal(true, verticalZeroAxis.ThicknessPixels > 1f, "vertical zero-axis is highlighted");
        Equal(true, horizontalZeroAxis.ThicknessPixels > 1f, "horizontal zero-axis is highlighted");
    }

    private static void GpuDataChunksUseRelativeCoordinates()
    {
        MousePerformanceChartPoint[] points =
        {
            new MousePerformanceChartPoint(1_000_000_000_000.0, 500_000_000.0),
            new MousePerformanceChartPoint(1_000_000_000_001.25, 500_000_000.5),
            new MousePerformanceChartPoint(1_000_000_000_002.5, 500_000_001.0)
        };

        GpuPointChunk[] pointChunks = InvokeChartControlStatic<GpuPointChunk[]>("BuildDataPointChunks", points, 0.0);
        Equal(1, pointChunks.Length, "relative point chunk count");
        Near(points[0].X, pointChunks[0].OriginX, 0.000001, "relative point chunk origin x");
        Near(0.0, pointChunks[0].Points[0].X, 0.000001, "relative point first local x");
        Near(1.25, pointChunks[0].Points[1].X, 0.000001, "relative point second local x");

        GpuSegmentChunk[] lineChunks = InvokeChartControlStatic<GpuSegmentChunk[]>("BuildDataLineSegmentChunks", points, 0.0, null);
        Equal(1, lineChunks.Length, "relative line chunk count");
        Near(points[0].X, lineChunks[0].OriginX, 0.000001, "relative line chunk origin x");
        Near(0.0, lineChunks[0].Segments[0].X0, 0.000001, "relative line first local x0");
        Near(1.25, lineChunks[0].Segments[0].X1, 0.000001, "relative line first local x1");
    }

    private static void GpuHistogramChunksApplyXOffsetOnce()
    {
        MousePerformanceHistogramBin[] bins =
        {
            new MousePerformanceHistogramBin(0.0, 10.0, 50.0, 5)
        };
        const double xOffset = 20.0;
        GpuHistogramBinChunk[] chunks = InvokeChartControlStatic<GpuHistogramBinChunk[]>("BuildHistogramBinChunks", bins, xOffset, 1.0);
        Equal(1, chunks.Length, "histogram chunk count");
        Near(20.0, chunks[0].OriginX, 0.000001, "histogram chunk absorbs series x offset");

        GpuSeriesSubmission submission = new GpuSeriesSubmission
        {
            Kind = GpuSeriesKind.Histogram,
            XOffset = xOffset,
            Color = System.Windows.Media.Colors.White,
            HistogramBinChunks = chunks
        };
        GpuPlotSceneFrame scene = new GpuPlotSceneFrame
        {
            Viewport = new GpuViewportState
            {
                XMinimum = 0.0,
                XMaximum = 100.0,
                YMinimum = 0.0,
                YMaximum = 100.0
            },
            ScreenYAxisPositiveDown = false
        };

        object vertices = InvokeChartGpuRendererStatic("BuildHistogramVertices", submission, scene, 100, 100);
        float firstX = ReadListStructFloat(vertices, 0, "X");
        float firstPixelX = (firstX + 1.0f) * 50.0f;
        Near(20.8, firstPixelX, 0.0001, "histogram renderer does not apply series x offset twice");
    }

    private static object CreateChartViewport(double xMinimum, double xMaximum, double yMinimum, double yMaximum)
    {
        Type viewportType = typeof(ClickSyncMouseTester.Controls.MousePerformanceChartControl).GetNestedType("ChartViewport", BindingFlags.NonPublic);
        if (viewportType == null)
        {
            throw new InvalidOperationException("missing ChartViewport");
        }
        return Activator.CreateInstance(viewportType, xMinimum, xMaximum, yMinimum, yMaximum);
    }

    private static T InvokeChartControlStatic<T>(string methodName, params object[] args)
    {
        Type controlType = typeof(ClickSyncMouseTester.Controls.MousePerformanceChartControl);
        MethodInfo method = ResolveStaticMethod(controlType, methodName, args);
        if (method == null)
        {
            throw new InvalidOperationException($"missing {methodName}");
        }
        return (T)method.Invoke(null, args);
    }

    private static void InvokeChartControlStatic(string methodName, params object[] args)
    {
        Type controlType = typeof(ClickSyncMouseTester.Controls.MousePerformanceChartControl);
        MethodInfo method = ResolveStaticMethod(controlType, methodName, args);
        if (method == null)
        {
            throw new InvalidOperationException($"missing {methodName}");
        }
        method.Invoke(null, args);
    }

    private static object InvokeChartGpuRendererStatic(string methodName, params object[] args)
    {
        Assembly chartGpuAssembly = typeof(ClickSyncMouseTester.ChartGpu.GpuSeriesSubmission).Assembly;
        Type rendererType = chartGpuAssembly.GetType("ClickSyncMouseTester.ChartGpu.ChartGpuRenderer", throwOnError: true);
        MethodInfo method = ResolveStaticMethod(rendererType, methodName, args);
        if (method == null)
        {
            throw new InvalidOperationException($"missing {methodName}");
        }
        return method.Invoke(null, args);
    }

    private static object InvokeChartWindowViewModelStatic(string methodName, params object[] args)
    {
        Type viewModelType = typeof(MousePerformanceChartWindowViewModel);
        MethodInfo method = ResolveStaticMethod(viewModelType, methodName, args);
        if (method == null)
        {
            throw new InvalidOperationException($"missing {methodName}");
        }
        return method.Invoke(null, args);
    }

    private static MethodInfo ResolveStaticMethod(Type ownerType, string methodName, object[] args)
    {
        foreach (MethodInfo method in ownerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != (args?.Length ?? 0))
            {
                continue;
            }
            bool matches = true;
            for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                object arg = args[parameterIndex];
                if (arg != null && !parameters[parameterIndex].ParameterType.IsInstanceOfType(arg))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                return method;
            }
        }
        return null;
    }

    private static object CreateNestedViewModelInstance(string typeName, params object[] args)
    {
        Type viewModelType = typeof(MousePerformanceChartWindowViewModel);
        Type nestedType = viewModelType.GetNestedType(typeName, BindingFlags.NonPublic);
        if (nestedType == null)
        {
            throw new InvalidOperationException($"missing nested type {typeName}");
        }
        return Activator.CreateInstance(nestedType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, args, culture: null);
    }

    private static float ReadListStructFloat(object list, int index, string fieldName)
    {
        if (list == null)
        {
            throw new InvalidOperationException("missing list");
        }
        object value = list.GetType().GetProperty("Item").GetValue(list, new object[] { index });
        FieldInfo field = value.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            throw new InvalidOperationException($"missing field {fieldName}");
        }
        return Convert.ToSingle(field.GetValue(value));
    }

    private static double ReadStructDouble(object value, string fieldName)
    {
        if (value == null)
        {
            throw new InvalidOperationException("missing struct value");
        }
        PropertyInfo property = value.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null)
        {
            return Convert.ToDouble(property.GetValue(value));
        }
        FieldInfo field = value.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            throw new InvalidOperationException($"missing field {fieldName}");
        }
        return Convert.ToDouble(field.GetValue(value));
    }

    private static int IndexOfPlotType(IReadOnlyList<MousePerformancePlotType> plotTypes, MousePerformancePlotType plotType)
    {
        for (int index = 0; index < plotTypes.Count; index++)
        {
            if (plotTypes[index] == plotType)
            {
                return index;
            }
        }
        throw new InvalidOperationException($"missing plot type {plotType}");
    }

    private static SensitivityMatchEngine CreateStartedEngine()
    {
        SensitivityMatchEngine engine = new SensitivityMatchEngine();
        if (!engine.StartRound(TestDpi, TestDpi, StartedMs))
        {
            throw new InvalidOperationException("round did not start");
        }

        return engine;
    }

    private static void PushPair(SensitivityMatchEngine engine, double timestampMs, int sourceDeltaX, int sourceDeltaY, int targetDeltaX, int targetDeltaY, ref long sequence)
    {
        engine.PushPacket(SensitivityMatchBindingSlot.SourceMouse, CreatePacket("source", timestampMs, sequence++, sourceDeltaX, sourceDeltaY));
        engine.PushPacket(SensitivityMatchBindingSlot.TargetMouse, CreatePacket("target", timestampMs, sequence++, targetDeltaX, targetDeltaY));
    }

    private static RawMousePacket CreatePacket(string deviceId, double timestampMs, long sequence, int deltaX, int deltaY)
    {
        long ticks = (long)Math.Round(timestampMs * 10000.0);
        return new RawMousePacket(deviceId, ticks, timestampMs, sequence, deltaX, deltaY, timingSequence: sequence);
    }

    private static MousePerformanceSnapshot CreateVelocitySpikeSnapshot(string label)
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        engine.SetCpiState(25.4, canComputeVelocity: true);
        string uniqueId = $"{label}-{Guid.NewGuid():N}";
        engine.BeginCollecting(uniqueId, 0L, startFresh: true);
        for (int packetIndex = 0; packetIndex < 24; packetIndex++)
        {
            int deltaX = packetIndex == 12 ? 100 : 1;
            double timestampMs = packetIndex;
            long sequence = packetIndex + 1L;
            engine.PushPacket(new RawMousePacket(uniqueId, MillisecondsToStopwatchTicks(timestampMs), timestampMs, sequence, deltaX, 0, timingSequence: sequence));
        }

        engine.StopCollecting();
        return engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
    }

    private static RawMouseEndpointActivitySnapshot CreateMotionCapableActivitySnapshot()
    {
        return new RawMouseEndpointActivitySnapshot(RawMouseEndpointKind.MotionCapable, 1L, 1L, 0L, 0L, 0L, 0.0, 1.0, 1.0);
    }

    private static MousePerformanceSessionArchive CreateMousePerformanceSession(string label, int deltaScale = 1)
    {
        MousePerformanceEngine engine = new MousePerformanceEngine();
        string uniqueId = $"{label}-{Guid.NewGuid():N}";
        engine.BeginCollecting(uniqueId, 0L, startFresh: true);
        for (int packetIndex = 0; packetIndex < 32; packetIndex++)
        {
            double timestampMs = packetIndex + 1.0;
            long sequence = packetIndex + 1L;
            int deltaX = (packetIndex % 2 == 0 ? 2 : -1) * deltaScale;
            int deltaY = (packetIndex % 3 == 0 ? 1 : -1) * deltaScale;
            engine.PushPacket(new RawMousePacket(uniqueId, MillisecondsToStopwatchTicks(timestampMs), timestampMs, sequence, deltaX, deltaY, timingSequence: sequence));
        }

        engine.StopCollecting();
        MousePerformanceSnapshot snapshot = engine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, queueOverflowCount: 0, queueHighWatermarkCount: 0, queueCapacity: 1024);
        MousePerformanceSessionMetadata metadata = new MousePerformanceSessionMetadata(label, uniqueId, null, null, 0, isVirtual: true, Guid.NewGuid().ToString("N"));
        return new MousePerformanceSessionArchive(MousePerformanceSessionSourceMode.Imported, metadata, snapshot);
    }

    private static MousePerformanceChartRenderFrame BuildCachedFrame(MousePerformanceSessionArchive session, MousePerformancePlotType plotType, int endIndex, bool showStem, bool showLines)
    {
        return MousePerformanceChartAnalysisCache.Instance.GetOrBuildFrameAsync(session, plotType, 0, endIndex, showStem, showLines, MousePerformanceTimeBasis.RawCapture, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static long MillisecondsToStopwatchTicks(double milliseconds)
    {
        return (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
    }

    private static SensitivityMatchCurrentRoundState RequireState(SensitivityMatchEngine engine)
    {
        return engine.CreateCurrentRoundState() ?? throw new InvalidOperationException("round is not active");
    }

    private static SensitivityMatchRoundResult RequireCompletedRound(SensitivityMatchEngine engine)
    {
        if (engine.CompletedRounds.Count != 1)
        {
            throw new InvalidOperationException($"expected 1 completed round, got {engine.CompletedRounds.Count}; last failure was {engine.LastRoundFailureReason}");
        }

        return engine.CompletedRounds[0];
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
        }
    }

    private static void AssertPresentationPolicy(MousePerformancePlotType plotType, bool canToggleStem, bool canToggleLines, MousePerformanceLinePresentationMode lineMode, MousePerformanceStemPresentationMode stemMode)
    {
        MousePerformancePlotPresentationPolicy policy = MousePerformancePlotPresentationPolicy.Resolve(plotType);
        Equal(canToggleStem, policy.CanToggleStem, $"{plotType} stem toggle");
        Equal(canToggleLines, policy.CanToggleLines, $"{plotType} line toggle");
        Equal(lineMode, policy.LineMode, $"{plotType} line presentation mode");
        Equal(stemMode, policy.StemMode, $"{plotType} stem presentation mode");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{label}: expected {expected:0.###}, got {actual:0.###}");
        }
    }

    private static void ContainsNear(double expected, IReadOnlyList<double> values, double tolerance, string label)
    {
        foreach (double value in values)
        {
            if (Math.Abs(expected - value) <= tolerance)
            {
                return;
            }
        }
        throw new InvalidOperationException($"{label}: expected list to contain {expected:0.###}");
    }

    private static GpuGridLine FindGridLine(IReadOnlyList<GpuGridLine> gridLines, bool isVertical)
    {
        foreach (GpuGridLine gridLine in gridLines)
        {
            if (gridLine.IsVertical == isVertical)
            {
                return gridLine;
            }
        }
        throw new InvalidOperationException($"missing {(isVertical ? "vertical" : "horizontal")} grid line");
    }

    private static void LessThan(double actual, double upperBound, string label)
    {
        if (!(actual < upperBound))
        {
            throw new InvalidOperationException($"{label}: expected less than {upperBound:0.###}, got {actual:0.###}");
        }
    }

    private static void GreaterThan(double actual, double lowerBound, string label)
    {
        if (!(actual > lowerBound))
        {
            throw new InvalidOperationException($"{label}: expected greater than {lowerBound:0.###}, got {actual:0.###}");
        }
    }

    private static void GreaterThanOrEqual(double actual, double lowerBound, string label)
    {
        if (!(actual >= lowerBound))
        {
            throw new InvalidOperationException($"{label}: expected greater than or equal to {lowerBound:0.###}, got {actual:0.###}");
        }
    }

    private static double MaxAbsoluteY(IReadOnlyList<MousePerformanceChartPoint> points)
    {
        double maximum = 0.0;
        if (points == null)
        {
            return maximum;
        }

        foreach (MousePerformanceChartPoint point in points)
        {
            maximum = Math.Max(maximum, Math.Abs(point.Y));
        }
        return maximum;
    }

    private static void AssertFrameSeriesShapeEqual(MousePerformanceChartRenderFrame expected, MousePerformanceChartRenderFrame actual, string label)
    {
        Equal(expected.IsAvailable, actual.IsAvailable, $"{label} availability");
        Equal(expected.Series.Count, actual.Series.Count, $"{label} series count");
        Equal(expected.GapSources.Count, actual.GapSources.Count, $"{label} gap source count");
        for (int sourceIndex = 0; sourceIndex < expected.GapSources.Count; sourceIndex++)
        {
            MousePerformanceChartGapSource expectedSource = expected.GapSources[sourceIndex];
            MousePerformanceChartGapSource actualSource = actual.GapSources[sourceIndex];
            Equal(expectedSource.DatasetSlot, actualSource.DatasetSlot, $"{label} gap source {sourceIndex} dataset slot");
            Near(expectedSource.XOffset, actualSource.XOffset, 0.000001, $"{label} gap source {sourceIndex} x offset");
            Equal(expectedSource.Intervals.Count, actualSource.Intervals.Count, $"{label} gap source {sourceIndex} interval count");
            for (int intervalIndex = 0; intervalIndex < expectedSource.Intervals.Count; intervalIndex++)
            {
                Near(expectedSource.Intervals[intervalIndex].StartX, actualSource.Intervals[intervalIndex].StartX, 0.000001, $"{label} gap source {sourceIndex} interval {intervalIndex} start");
                Near(expectedSource.Intervals[intervalIndex].EndX, actualSource.Intervals[intervalIndex].EndX, 0.000001, $"{label} gap source {sourceIndex} interval {intervalIndex} end");
            }
        }
        for (int seriesIndex = 0; seriesIndex < expected.Series.Count; seriesIndex++)
        {
            MousePerformanceChartSeries expectedSeries = expected.Series[seriesIndex];
            MousePerformanceChartSeries actualSeries = actual.Series[seriesIndex];
            Equal(expectedSeries.Kind, actualSeries.Kind, $"{label} series {seriesIndex} kind");
            Equal(expectedSeries.Palette, actualSeries.Palette, $"{label} series {seriesIndex} palette");
            Equal(expectedSeries.Points.Count, actualSeries.Points.Count, $"{label} series {seriesIndex} point count");
            for (int pointIndex = 0; pointIndex < expectedSeries.Points.Count; pointIndex++)
            {
                Near(expectedSeries.Points[pointIndex].X, actualSeries.Points[pointIndex].X, 0.000001, $"{label} series {seriesIndex} point {pointIndex} x");
                Near(expectedSeries.Points[pointIndex].Y, actualSeries.Points[pointIndex].Y, 0.000001, $"{label} series {seriesIndex} point {pointIndex} y");
            }
        }
    }

    private static int CountSeries(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeriesKind kind, MousePerformanceChartSeriesPalette palette)
    {
        int count = 0;
        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series.Kind == kind && series.Palette == palette)
            {
                count++;
            }
        }
        return count;
    }

    private static MousePerformanceChartSeries FindSeries(MousePerformanceChartRenderFrame frame, MousePerformanceChartSeriesKind kind, MousePerformanceChartSeriesPalette palette)
    {
        foreach (MousePerformanceChartSeries series in frame.Series)
        {
            if (series.Kind == kind && series.Palette == palette)
            {
                return series;
            }
        }

        throw new InvalidOperationException($"missing {kind}/{palette} series");
    }

    private static int SumHistogramCounts(MousePerformanceChartSeries series)
    {
        int totalCount = 0;
        if (series?.HistogramBins == null)
        {
            return totalCount;
        }
        foreach (MousePerformanceHistogramBin bin in series.HistogramBins)
        {
            totalCount += bin.Count;
        }
        return totalCount;
    }

    private static void AssertHistogramBinLayoutEqual(IReadOnlyList<MousePerformanceHistogramBin> expected, IReadOnlyList<MousePerformanceHistogramBin> actual, string label)
    {
        Equal(expected?.Count ?? 0, actual?.Count ?? 0, $"{label} bin count");
        if (expected == null || actual == null)
        {
            return;
        }
        for (int binIndex = 0; binIndex < expected.Count; binIndex++)
        {
            Near(expected[binIndex].MinimumX, actual[binIndex].MinimumX, 0.000001, $"{label} bin {binIndex} minimum");
            Near(expected[binIndex].MaximumX, actual[binIndex].MaximumX, 0.000001, $"{label} bin {binIndex} maximum");
        }
    }

    private static MousePerformanceChartGapSource FindGapSource(MousePerformanceChartRenderFrame frame, MousePerformanceChartDatasetSlot datasetSlot)
    {
        return FindGapSource(frame.GapSources, datasetSlot);
    }

    private static MousePerformanceChartGapSource FindGapSource(IReadOnlyList<MousePerformanceChartGapSource> gapSources, MousePerformanceChartDatasetSlot datasetSlot)
    {
        foreach (MousePerformanceChartGapSource gapSource in gapSources)
        {
            if (gapSource.DatasetSlot == datasetSlot)
            {
                return gapSource;
            }
        }

        throw new InvalidOperationException($"missing {datasetSlot} gap source");
    }

    private static void SameReference(object expected, object actual, string label)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected same reference");
        }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMilliseconds, string label)
    {
        long deadlineTick = Environment.TickCount64 + Math.Max(0, timeoutMilliseconds);
        while (Environment.TickCount64 < deadlineTick)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(1);
        }

        if (!condition())
        {
            throw new InvalidOperationException($"{label}: condition was not met within {timeoutMilliseconds} ms");
        }
    }

    private sealed class FakeRawInputSource : IRawMouseReportSource, IRawInputDeviceCatalog
    {
        private readonly RawMouseDeviceInfo _device;

        private readonly IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> _activitySnapshots;

        public string DeviceId => _device.DeviceId;

        public event EventHandler<RawMousePacketEventArgs> MousePacketCaptured;

        public event EventHandler MouseDevicesChanged
        {
            add { }
            remove { }
        }

        public FakeRawInputSource(string deviceId)
        {
            _device = new RawMouseDeviceInfo(deviceId, "Fake Mouse", null, null, 5, isVirtual: false);
            _activitySnapshots = new Dictionary<string, RawMouseEndpointActivitySnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [deviceId] = new RawMouseEndpointActivitySnapshot(RawMouseEndpointKind.MotionCapable, 1L, 1L, 0L, 0L, 0L, 0.0, 1.0, 1.0)
            };
        }

        public IReadOnlyList<RawMouseDeviceInfo> GetMouseDevices()
        {
            return new[] { _device };
        }

        public IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> GetMouseEndpointActivitySnapshots()
        {
            return _activitySnapshots;
        }

        public void RequestMouseDevicesRefresh(bool force = false)
        {
        }

        public void RaiseMousePacket(RawMousePacket packet)
        {
            MousePacketCaptured?.Invoke(this, new RawMousePacketEventArgs(packet));
        }
    }
}
