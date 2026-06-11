using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

internal readonly struct MousePerformanceHistogramDataset
{
    public MousePerformanceChartDatasetSlot DatasetSlot { get; }

    public MousePerformanceSnapshot Snapshot { get; }

    public MousePerformanceHistogramDataset(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceSnapshot snapshot)
    {
        DatasetSlot = datasetSlot;
        Snapshot = snapshot;
    }
}
