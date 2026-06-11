namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceChartDatasetSession
{
    public MousePerformanceChartDatasetSlot DatasetSlot { get; }

    public MousePerformanceSessionArchive Session { get; }

    public MousePerformanceChartDatasetSession(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceSessionArchive session)
    {
        DatasetSlot = datasetSlot;
        Session = session;
    }
}
