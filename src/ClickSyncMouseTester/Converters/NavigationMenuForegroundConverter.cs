using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ClickSyncMouseTester.Converters;

public class NavigationMenuForegroundConverter : IMultiValueConverter
{
    private static readonly Brush FallbackDefaultBrush = Brushes.Black;

    private static readonly Brush FallbackActiveBrush = Brushes.White;

    private static readonly Brush FallbackHoverBrush = CreateFrozenBrush(Color.FromRgb(byte.MaxValue, 95, 0));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        object currentPageKey = ReadValue(values, 0);
        object buttonPageKey = ReadValue(values, 1);
        bool isHovered = ReadBoolean(values, 2);
        if (currentPageKey != null && buttonPageKey != null && object.Equals(currentPageKey, buttonPageKey))
        {
            return ResolveBrush("NavigationMenuTextActiveBrush", FallbackActiveBrush);
        }
        if (isHovered)
        {
            return ResolveBrush("NavigationCurtainBackgroundBrush", FallbackHoverBrush);
        }
        return ResolveBrush("NavigationMenuTextBrush", FallbackDefaultBrush);
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

    private static bool ReadBoolean(object[] values, int index)
    {
        if (values == null || index < 0 || index >= values.Length)
        {
            return false;
        }
        object rawValue = ReadValue(values, index);
        if (rawValue is bool boolValue)
        {
            return boolValue;
        }
        if (rawValue != null && bool.TryParse(rawValue.ToString(), out var result))
        {
            return result;
        }
        return false;
    }

    private static object ReadValue(object[] values, int index)
    {
        if (values == null || index < 0 || index >= values.Length)
        {
            return null;
        }
        return values[index];
    }

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        Application application = (Application)System.Windows.Application.Current;
        if (application == null)
        {
            return fallback;
        }
        if (application.TryFindResource(resourceKey) is Brush result)
        {
            return result;
        }
        return fallback;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }
}





