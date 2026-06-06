#define TRACE
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
internal sealed class TextRenderingDiagnostics
{
    private TextRenderingDiagnostics()
    {
    }

    public static string BuildSummary(Visual host)
    {
        if (host == null)
        {
            return "WPF text rendering: host unavailable";
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(host);
        int renderTier = RenderCapability.Tier >> 16;
        RenderMode processRenderMode = RenderOptions.ProcessRenderMode;
        string fontSmoothingDescription = "Unavailable";
        bool enabled = false;
        uint smoothingType = 0u;
        uint contrast = 0u;
        uint orientation = 0u;
        if (NativeMethods.TryGetFontSmoothingState(ref enabled, ref smoothingType, ref contrast, ref orientation))
        {
            fontSmoothingDescription = string.Format(CultureInfo.InvariantCulture, "{0}; Type={1}; Contrast={2}; Orientation={3}", enabled ? "On" : "Off", DescribeSmoothingType(smoothingType), contrast, DescribeOrientation(orientation));
        }
        return string.Format(CultureInfo.InvariantCulture, "WPF text rendering: Tier={0}, ProcessRenderMode={1}, DpiScale={2:0.##}x{3:0.##}, PixelsPerDip={4:0.###}, FontSmoothing={5}", renderTier, processRenderMode, dpi.DpiScaleX, dpi.DpiScaleY, dpi.PixelsPerDip, fontSmoothingDescription);
    }

    public static void TraceSnapshot(Visual host)
    {
        Trace.WriteLine(BuildSummary(host));
    }

    private static string DescribeSmoothingType(uint smoothingType)
    {
        return smoothingType switch
        {
            1u => "Standard",
            2u => "ClearType",
            _ => smoothingType.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string DescribeOrientation(uint orientation)
    {
        return orientation switch
        {
            0u => "BGR",
            1u => "RGB",
            _ => orientation.ToString(CultureInfo.InvariantCulture),
        };
    }
}





