using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Services;

internal static class MousePerformanceHistogramPresentationPolicy
{
    private const double UnitBinTolerance = 0.000001;

    public static bool ShouldUseBinCenterTicks(MousePerformancePlotType plotType, IReadOnlyList<MousePerformanceHistogramBin> bins)
    {
        if (plotType != MousePerformancePlotType.DeltaXHistogram && plotType != MousePerformancePlotType.DeltaYHistogram)
        {
            return false;
        }
        if (bins == null || bins.Count == 0)
        {
            return false;
        }

        foreach (MousePerformanceHistogramBin bin in bins)
        {
            double width = bin.MaximumX - bin.MinimumX;
            double center = bin.CenterX;
            if (Math.Abs(width - 1.0) > UnitBinTolerance || Math.Abs(center - Math.Round(center, MidpointRounding.AwayFromZero)) > UnitBinTolerance)
            {
                return false;
            }
        }
        return true;
    }
}
