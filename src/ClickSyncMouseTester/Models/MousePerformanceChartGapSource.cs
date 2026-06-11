using System;
using System.Collections.Generic;
using System.Threading;

namespace ClickSyncMouseTester.Models;

public readonly struct MousePerformanceChartGapInterval
{
    public double StartX { get; }

    public double EndX { get; }

    public double DurationMs { get; }

    public MousePerformanceChartGapInterval(double startX, double endX)
    {
        StartX = Math.Min(startX, endX);
        EndX = Math.Max(startX, endX);
        DurationMs = Math.Abs(endX - startX);
    }
}

public sealed class MousePerformanceChartGapSource
{
    private readonly Func<IReadOnlyList<MousePerformanceChartGapInterval>> _intervalsFactory;
    private readonly object _syncRoot;
    private IReadOnlyList<MousePerformanceChartGapInterval> _intervals;

    public MousePerformanceChartDatasetSlot DatasetSlot { get; }

    public double XOffset { get; }

    public IReadOnlyList<MousePerformanceChartGapInterval> Intervals => ResolveIntervals();

    public MousePerformanceChartGapSource(MousePerformanceChartDatasetSlot datasetSlot, IReadOnlyList<MousePerformanceChartGapInterval> intervals, double xOffset = 0.0)
        : this(datasetSlot, () => intervals ?? Array.Empty<MousePerformanceChartGapInterval>(), xOffset)
    {
        _intervals = intervals ?? Array.Empty<MousePerformanceChartGapInterval>();
    }

    public MousePerformanceChartGapSource(MousePerformanceChartDatasetSlot datasetSlot, Func<IReadOnlyList<MousePerformanceChartGapInterval>> intervalsFactory, double xOffset = 0.0)
    {
        DatasetSlot = datasetSlot;
        _intervalsFactory = intervalsFactory ?? (() => Array.Empty<MousePerformanceChartGapInterval>());
        _syncRoot = new object();
        XOffset = xOffset;
    }

    public MousePerformanceChartGapSource WithDatasetSlot(MousePerformanceChartDatasetSlot datasetSlot)
    {
        if (datasetSlot == DatasetSlot)
        {
            return this;
        }
        return new MousePerformanceChartGapSource(datasetSlot, ResolveIntervals, XOffset);
    }

    public MousePerformanceChartGapSource WithXOffset(double xOffset)
    {
        if (Math.Abs(xOffset - XOffset) < double.Epsilon)
        {
            return this;
        }
        return new MousePerformanceChartGapSource(DatasetSlot, ResolveIntervals, xOffset);
    }

    public MousePerformanceChartGapSource WithDatasetSlotAndXOffset(MousePerformanceChartDatasetSlot datasetSlot, double xOffset)
    {
        if (datasetSlot == DatasetSlot && Math.Abs(xOffset - XOffset) < double.Epsilon)
        {
            return this;
        }
        return new MousePerformanceChartGapSource(datasetSlot, ResolveIntervals, xOffset);
    }

    private IReadOnlyList<MousePerformanceChartGapInterval> ResolveIntervals()
    {
        if (_intervals != null)
        {
            return _intervals;
        }

        object syncRoot = _syncRoot;
        bool lockTaken = false;
        try
        {
            Monitor.Enter(syncRoot, ref lockTaken);
            if (_intervals == null)
            {
                _intervals = _intervalsFactory() ?? Array.Empty<MousePerformanceChartGapInterval>();
            }
            return _intervals;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }
}
