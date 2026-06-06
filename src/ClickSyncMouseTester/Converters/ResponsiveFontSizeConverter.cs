using System;
using System.Globalization;
using System.Windows.Data;

namespace ClickSyncMouseTester.Converters;

public class ResponsiveFontSizeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double currentWidth = ReadDouble(values, 0, 1240.0);
        double currentHeight = ReadDouble(values, 1, 940.0);
        double baseFontSize = 214.0;
        double minimumFontSize = 214.0;
        double maximumFontSize = 292.0;
        double referenceWidth = 1240.0;
        double referenceHeight = 940.0;
        if (parameter != null)
        {
            string[] parameterParts = parameter.ToString().Split('|');
            if (parameterParts.Length > 0)
            {
                baseFontSize = ParseDouble(parameterParts[0], baseFontSize);
            }
            if (parameterParts.Length > 1)
            {
                minimumFontSize = ParseDouble(parameterParts[1], minimumFontSize);
            }
            if (parameterParts.Length > 2)
            {
                maximumFontSize = ParseDouble(parameterParts[2], maximumFontSize);
            }
            if (parameterParts.Length > 3)
            {
                referenceWidth = ParseDouble(parameterParts[3], referenceWidth);
            }
            if (parameterParts.Length > 4)
            {
                referenceHeight = ParseDouble(parameterParts[4], referenceHeight);
            }
        }
        double widthScale = currentWidth / Math.Max(referenceWidth, 1.0);
        double heightScale = currentHeight / Math.Max(referenceHeight, 1.0);
        double responsiveScale = Math.Min(widthScale, heightScale);
        double responsiveFontSize = baseFontSize * responsiveScale;
        return Math.Max(minimumFontSize, Math.Min(maximumFontSize, responsiveFontSize));
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





