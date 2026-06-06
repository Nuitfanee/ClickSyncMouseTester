using ClickSyncMouseTester.Navigation;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClickSyncMouseTester.Converters;

public class NavigationMenuIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return string.Empty;
        }
        AppPageKey result;
        if (value is AppPageKey)
        {
            result = (AppPageKey)value;
        }
        else if (!Enum.TryParse<AppPageKey>(value.ToString(), ignoreCase: true, out result))
        {
            return string.Empty;
        }
        return result switch
        {
            AppPageKey.PollingDashboard => "01.",
            AppPageKey.SensitivityMatching => "02.",
            AppPageKey.MousePerformance => "03.",
            AppPageKey.AngleCalibration => "04.",
            AppPageKey.KeyDetection => "05.",
            _ => string.Empty,
        };
    }

    object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return this.Convert(value, targetType, parameter, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return this.ConvertBack(value, targetType, parameter, culture);
    }
}





