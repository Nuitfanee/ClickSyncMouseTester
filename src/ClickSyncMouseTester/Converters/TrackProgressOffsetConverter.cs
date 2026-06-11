using System;
using System.Globalization;
using System.Windows.Data;

namespace ClickSyncMouseTester.Converters;

public class TrackProgressOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double progressPercent = ReadDouble(values, 0, 0.0);
        double trackLength = ReadDouble(values, 1, 0.0);
        double reservedOffset = ParseDouble(parameter, 0.0);
        double clampedProgressPercent = Math.Max(0.0, Math.Min(100.0, progressPercent));
        return Math.Max(0.0, trackLength - Math.Max(0.0, reservedOffset)) * (clampedProgressPercent / 100.0);
    }

    object IMultiValueConverter.Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return this.Convert(values, targetType, parameter, culture);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    object[] IMultiValueConverter.ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return this.ConvertBack(value, targetTypes, parameter, culture);
    }

    private static double ReadDouble(object[] values, int index, double fallback)
    {
        if (values == null || index < 0 || index >= values.Length)
        {
            return fallback;
        }
        return ParseDouble(values[index], fallback);
    }

    private static double ParseDouble(object value, double fallback)
    {
        if (value == null)
        {
            return fallback;
        }
        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }
        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }
        return fallback;
    }
}





