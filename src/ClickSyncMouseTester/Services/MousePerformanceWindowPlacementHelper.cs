using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Windows;

namespace ClickSyncMouseTester.Services;

public sealed class MousePerformanceWindowPlacementHelper
{
    private MousePerformanceWindowPlacementHelper()
    {
    }

    public static MousePerformanceChartWindowPlacement Capture(Window window)
    {
        if (window == null)
        {
            return new MousePerformanceChartWindowPlacement(hasSavedBounds: false, 0.0, 0.0, 0.0, 0.0, isMaximized: false);
        }
        Rect restoreBounds = window.RestoreBounds;
        double width = ((restoreBounds.Width > 0.0) ? restoreBounds.Width : ((window.ActualWidth > 0.0) ? window.ActualWidth : window.Width));
        double height = ((restoreBounds.Height > 0.0) ? restoreBounds.Height : ((window.ActualHeight > 0.0) ? window.ActualHeight : window.Height));
        double left = (double.IsNaN(restoreBounds.Left) ? window.Left : restoreBounds.Left);
        double top = (double.IsNaN(restoreBounds.Top) ? window.Top : restoreBounds.Top);
        return new MousePerformanceChartWindowPlacement(hasSavedBounds: true, left, top, width, height, window.WindowState == WindowState.Maximized);
    }

    public static bool TryNormalizeForRestore(MousePerformanceChartWindowPlacement placement, double minWidth, double minHeight, IEnumerable<Rect> workAreas, ref MousePerformanceChartWindowPlacement normalizedPlacement)
    {
        normalizedPlacement = null;
        if (placement == null || !placement.HasSavedBounds)
        {
            return false;
        }
        if (!IsFinite(placement.Left) || !IsFinite(placement.Top) || !IsFinite(placement.Width) || !IsFinite(placement.Height))
        {
            return false;
        }
        if (placement.Width < Math.Max(0.0, minWidth) || placement.Height < Math.Max(0.0, minHeight))
        {
            return false;
        }
        Rect requestedBounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (requestedBounds.Width <= 0.0 || requestedBounds.Height <= 0.0)
        {
            return false;
        }
        bool intersectsAnyWorkArea = false;
        if (workAreas != null)
        {
            foreach (Rect workArea in workAreas)
            {
                Rect workAreaBounds = workArea;
                if (workAreaBounds.IntersectsWith(requestedBounds))
                {
                    intersectsAnyWorkArea = true;
                    break;
                }
            }
        }
        if (!intersectsAnyWorkArea)
        {
            return false;
        }
        normalizedPlacement = new MousePerformanceChartWindowPlacement(hasSavedBounds: true, placement.Left, placement.Top, placement.Width, placement.Height, placement.IsMaximized);
        return true;
    }

    private static bool IsFinite(double value)
    {
        if (!double.IsNaN(value))
        {
            return !double.IsInfinity(value);
        }
        return false;
    }
}





