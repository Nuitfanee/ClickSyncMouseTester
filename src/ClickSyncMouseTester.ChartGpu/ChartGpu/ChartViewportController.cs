using System;

namespace ClickSyncMouseTester.ChartGpu;

internal static class ChartViewportController
{
    private const int GridLineCount = 5;

    private const double AutomaticHorizontalGridThresholdMs = 2.0;

    private const double ZoomStepFactor = 1.18;

    private const double MinimumViewportSpan = 0.0001;

    public static GpuViewportState BuildPannedViewport(GpuPlotSceneFrame scene, GpuViewportState panStartViewport, double startX, double startY, double currentX, double currentY, double width, double height)
    {
        if (scene == null)
        {
            return panStartViewport?.Clone() ?? new GpuViewportState();
        }
        double num = currentX - startX;
        double num2 = currentY - startY;
        double num3 = panStartViewport.XMaximum - panStartViewport.XMinimum;
        double num4 = panStartViewport.YMaximum - panStartViewport.YMinimum;
        double num5 = ((width <= 0.0) ? 0.0 : ((0.0 - num) * num3 / width));
        double num6 = (scene.ScreenYAxisPositiveDown ? (-1.0) : 1.0);
        double num7 = ((height <= 0.0) ? 0.0 : (num6 * num2 * num4 / height));
        GpuViewportState viewport = new GpuViewportState
        {
            XMinimum = panStartViewport.XMinimum + num5,
            XMaximum = panStartViewport.XMaximum + num5,
            YMinimum = panStartViewport.YMinimum + num7,
            YMaximum = panStartViewport.YMaximum + num7
        };
        return ClampViewport(scene.DataBounds, viewport);
    }

    public static GpuViewportState Zoom(GpuPlotSceneFrame scene, double positionX, double positionY, double width, double height, int wheelDelta, bool shiftPressed, bool controlPressed)
    {
        if (scene == null)
        {
            return new GpuViewportState();
        }
        GpuViewportState gpuViewportState = scene.Viewport?.Clone() ?? scene.DataBounds?.Clone() ?? new GpuViewportState();
        bool zoomX = TrueByDefault();
        bool zoomY = TrueByDefault();
        double minimumNextXSpan = double.NaN;
        double maximumNextXSpan = double.NaN;
        double minimumNextYSpan = double.NaN;
        double maximumNextYSpan = double.NaN;
        if (shiftPressed && !controlPressed)
        {
            zoomY = false;
        }
        else if (controlPressed && !shiftPressed)
        {
            zoomX = false;
        }
        else if (!shiftPressed && !controlPressed && scene.EnableAutomaticWheelZoom)
        {
            ConfigureAutomaticWheelZoom(scene, gpuViewportState, wheelDelta > 0, ref zoomX, ref zoomY, ref minimumNextXSpan, ref maximumNextXSpan, ref minimumNextYSpan, ref maximumNextYSpan);
        }
        GpuViewportState gpuViewportState2 = scene.DataBounds?.Clone() ?? gpuViewportState.Clone();
        double num = ((wheelDelta > 0) ? 0.8474576271186441 : 1.18);
        GpuViewportState gpuViewportState3 = gpuViewportState.Clone();
        if (zoomX)
        {
            double num2 = ScreenToDataX(gpuViewportState, positionX, width);
            double num3 = Math.Max(val2: (gpuViewportState.XMaximum - gpuViewportState.XMinimum) * num, val1: GetMinimumViewportSpan(gpuViewportState2.XMaximum - gpuViewportState2.XMinimum));
            if (!double.IsNaN(minimumNextXSpan))
            {
                num3 = Math.Max(minimumNextXSpan, num3);
            }
            if (!double.IsNaN(maximumNextXSpan))
            {
                num3 = Math.Min(maximumNextXSpan, num3);
            }
            double num4 = ((width <= 0.0) ? 0.0 : (positionX / width));
            double num5 = (gpuViewportState3.XMinimum = num2 - num4 * num3);
            gpuViewportState3.XMaximum = num5 + num3;
        }
        if (zoomY)
        {
            double num7 = ScreenToDataY(gpuViewportState, positionY, height, scene.ScreenYAxisPositiveDown);
            double num8 = Math.Max(val2: (gpuViewportState.YMaximum - gpuViewportState.YMinimum) * num, val1: GetMinimumViewportSpan(gpuViewportState2.YMaximum - gpuViewportState2.YMinimum));
            if (!double.IsNaN(maximumNextYSpan))
            {
                num8 = Math.Min(maximumNextYSpan, num8);
            }
            if (!double.IsNaN(minimumNextYSpan))
            {
                num8 = Math.Max(minimumNextYSpan, num8);
            }
            double num9 = ((height <= 0.0) ? 0.0 : ((!scene.ScreenYAxisPositiveDown) ? ((height - positionY) / height) : (positionY / height)));
            double num10 = (gpuViewportState3.YMinimum = num7 - num9 * num8);
            gpuViewportState3.YMaximum = num10 + num8;
        }
        return ClampViewport(gpuViewportState2, gpuViewportState3);
    }

    public static GpuViewportState Reset(GpuPlotSceneFrame scene)
    {
        return scene?.DefaultViewport?.Clone() ?? scene?.DataBounds?.Clone() ?? new GpuViewportState();
    }

    private static bool TrueByDefault()
    {
        return true;
    }

    private static void ConfigureAutomaticWheelZoom(GpuPlotSceneFrame scene, GpuViewportState viewport, bool zoomIn, ref bool zoomX, ref bool zoomY, ref double minimumNextXSpan, ref double maximumNextXSpan, ref double minimumNextYSpan, ref double maximumNextYSpan)
    {
        if (scene == null)
        {
            return;
        }
        GpuViewportState gpuViewportState = scene.DefaultViewport ?? scene.DataBounds ?? viewport;
        GpuViewportState viewport2 = scene.DataBounds ?? gpuViewportState;
        double viewportWidth = GetViewportWidth(viewport);
        double viewportHeight = GetViewportHeight(viewport);
        double viewportWidth2 = GetViewportWidth(gpuViewportState);
        double viewportHeight2 = GetViewportHeight(gpuViewportState);
        double viewportWidth3 = GetViewportWidth(viewport2);
        double viewportHeight3 = GetViewportHeight(viewport2);
        if (zoomIn)
        {
            if (viewportWidth > viewportWidth2 && !AreClose(viewportWidth, viewportWidth2))
            {
                zoomX = true;
                zoomY = false;
                minimumNextXSpan = viewportWidth2;
                return;
            }
            if (viewportHeight > viewportHeight2 && !AreClose(viewportHeight, viewportHeight2))
            {
                zoomX = false;
                zoomY = true;
                minimumNextYSpan = viewportHeight2;
                return;
            }
            double horizontalGridStep = GetHorizontalGridStep(viewport);
            if (horizontalGridStep > 2.0 && !AreClose(horizontalGridStep, 2.0))
            {
                zoomX = true;
                zoomY = false;
                minimumNextXSpan = 10.0;
            }
            else
            {
                zoomX = false;
                zoomY = true;
            }
        }
        else if (viewportHeight < viewportHeight2 && !AreClose(viewportHeight, viewportHeight2))
        {
            zoomX = false;
            zoomY = true;
            maximumNextYSpan = viewportHeight2;
        }
        else if (viewportWidth < viewportWidth2 && !AreClose(viewportWidth, viewportWidth2))
        {
            zoomX = true;
            zoomY = false;
            maximumNextXSpan = viewportWidth2;
        }
        else if (viewportHeight < viewportHeight3 && !AreClose(viewportHeight, viewportHeight3))
        {
            zoomX = false;
            zoomY = true;
            maximumNextYSpan = viewportHeight3;
        }
        else if (viewportWidth < viewportWidth3 && !AreClose(viewportWidth, viewportWidth3))
        {
            zoomX = true;
            zoomY = false;
            maximumNextXSpan = viewportWidth3;
        }
        else
        {
            zoomX = true;
            zoomY = false;
        }
    }

    private static GpuViewportState ClampViewport(GpuViewportState bounds, GpuViewportState viewport)
    {
        if (bounds == null)
        {
            return viewport?.Clone() ?? new GpuViewportState();
        }
        double num = Math.Max(0.0001, bounds.XMaximum - bounds.XMinimum);
        double num2 = Math.Max(0.0001, bounds.YMaximum - bounds.YMinimum);
        double num3 = Math.Max(GetMinimumViewportSpan(num), Math.Min(viewport.XMaximum - viewport.XMinimum, num));
        double num4 = Math.Max(GetMinimumViewportSpan(num2), Math.Min(viewport.YMaximum - viewport.YMinimum, num2));
        double num5 = bounds.XMinimum;
        if (num3 < num)
        {
            num5 = Math.Max(bounds.XMinimum, Math.Min(viewport.XMinimum, bounds.XMaximum - num3));
        }
        double num6 = bounds.YMinimum;
        if (num4 < num2)
        {
            num6 = Math.Max(bounds.YMinimum, Math.Min(viewport.YMinimum, bounds.YMaximum - num4));
        }
        return new GpuViewportState
        {
            XMinimum = num5,
            XMaximum = num5 + num3,
            YMinimum = num6,
            YMaximum = num6 + num4
        };
    }

    private static double ScreenToDataX(GpuViewportState viewport, double screenX, double width)
    {
        if (viewport == null || width <= 0.0)
        {
            return 0.0;
        }
        return viewport.XMinimum + (viewport.XMaximum - viewport.XMinimum) * screenX / width;
    }

    private static double ScreenToDataY(GpuViewportState viewport, double screenY, double height, bool screenYAxisPositiveDown)
    {
        if (viewport == null || height <= 0.0)
        {
            return 0.0;
        }
        if (screenYAxisPositiveDown)
        {
            return viewport.YMinimum + (viewport.YMaximum - viewport.YMinimum) * screenY / height;
        }
        return viewport.YMinimum + (viewport.YMaximum - viewport.YMinimum) * (height - screenY) / height;
    }

    private static double GetHorizontalGridStep(GpuViewportState viewport)
    {
        if (viewport == null)
        {
            return 0.0;
        }
        return (viewport.XMaximum - viewport.XMinimum) / 5.0;
    }

    private static double GetViewportWidth(GpuViewportState viewport)
    {
        if (viewport == null)
        {
            return 0.0;
        }
        return Math.Max(0.0, viewport.XMaximum - viewport.XMinimum);
    }

    private static double GetViewportHeight(GpuViewportState viewport)
    {
        if (viewport == null)
        {
            return 0.0;
        }
        return Math.Max(0.0, viewport.YMaximum - viewport.YMinimum);
    }

    private static double GetMinimumViewportSpan(double fullSpan)
    {
        return Math.Max(0.0001, Math.Abs(fullSpan) * 0.0005);
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) <= 1E-06;
    }
}


