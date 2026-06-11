using ClickSyncMouseTester.Models;
using System;
using System.Windows.Media;

namespace ClickSyncMouseTester.Services;

public sealed class MousePerformanceChartColorPalette
{
    private MousePerformanceChartColorPalette()
    {
    }

    public static Color ResolveColor(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceChartSeriesPalette palette)
    {
        return datasetSlot switch
        {
            MousePerformanceChartDatasetSlot.CompareA => palette switch
            {
                MousePerformanceChartSeriesPalette.Secondary => Color.FromRgb(byte.MaxValue, 190, 125),
                MousePerformanceChartSeriesPalette.Accent => Color.FromRgb(197, 106, 18),
                _ => Color.FromRgb(242, 142, 43),
            },
            MousePerformanceChartDatasetSlot.CompareB => palette switch
            {
                MousePerformanceChartSeriesPalette.Secondary => Color.FromRgb(140, 209, 125),
                MousePerformanceChartSeriesPalette.Accent => Color.FromRgb(47, 125, 74),
                _ => Color.FromRgb(89, 161, 79),
            },
            _ => palette switch
            {
                MousePerformanceChartSeriesPalette.Secondary => Color.FromRgb(160, 203, 232),
                MousePerformanceChartSeriesPalette.Accent => Color.FromRgb(47, 93, 138),
                _ => Color.FromRgb(78, 121, 167),
            },
        };
    }

    public static SolidColorBrush CreateBrush(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceChartSeriesPalette palette, double opacity)
    {
        SolidColorBrush brush = new SolidColorBrush(ApplyOpacity(ResolveColor(datasetSlot, palette), opacity));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    public static Pen CreatePen(MousePerformanceChartDatasetSlot datasetSlot, MousePerformanceChartSeriesPalette palette, double opacity, double thickness)
    {
        Pen pen = new Pen(new SolidColorBrush(ApplyOpacity(ResolveColor(datasetSlot, palette), opacity)), Math.Max(0.1, thickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (pen.Brush.CanFreeze)
        {
            pen.Brush.Freeze();
        }
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        return pen;
    }

    private static Color ApplyOpacity(Color color, double opacity)
    {
        double clampedOpacity = Math.Max(0.0, Math.Min(1.0, opacity));
        return Color.FromArgb((byte)Math.Round(clampedOpacity * 255.0), color.R, color.G, color.B);
    }
}





