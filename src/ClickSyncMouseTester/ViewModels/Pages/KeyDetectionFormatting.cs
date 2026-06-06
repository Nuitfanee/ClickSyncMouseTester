using System;
using System.Globalization;

namespace ClickSyncMouseTester.ViewModels.Pages;

internal sealed class KeyDetectionFormatting
{
    public static string FormatCount(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatMilliseconds(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "--";
        }
        if (Math.Abs(value.Value) < 10.0)
        {
            return value.Value.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }
        return value.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
    }
}





