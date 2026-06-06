using System.Configuration;

namespace ClickSyncMouseTester.Settings;

internal sealed class ApplicationSettings : ApplicationSettingsBase
{
    private static readonly ApplicationSettings _default = (ApplicationSettings)Synchronized(new ApplicationSettings());

    public static ApplicationSettings Default => _default;

    [UserScopedSetting]
    [DefaultSettingValue("Dark")]
    public string ThemeName
    {
        get => (string)this[nameof(ThemeName)];
        set => this[nameof(ThemeName)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("800")]
    public double MousePerformanceLastCpi
    {
        get => (double)this[nameof(MousePerformanceLastCpi)];
        set => this[nameof(MousePerformanceLastCpi)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("0")]
    public int MousePerformanceChartPlotType
    {
        get => (int)this[nameof(MousePerformanceChartPlotType)];
        set => this[nameof(MousePerformanceChartPlotType)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool MousePerformanceChartShowStem
    {
        get => (bool)this[nameof(MousePerformanceChartShowStem)];
        set => this[nameof(MousePerformanceChartShowStem)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool MousePerformanceChartShowLines
    {
        get => (bool)this[nameof(MousePerformanceChartShowLines)];
        set => this[nameof(MousePerformanceChartShowLines)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool MousePerformanceChartHasSavedBounds
    {
        get => (bool)this[nameof(MousePerformanceChartHasSavedBounds)];
        set => this[nameof(MousePerformanceChartHasSavedBounds)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("0")]
    public double MousePerformanceChartLeft
    {
        get => (double)this[nameof(MousePerformanceChartLeft)];
        set => this[nameof(MousePerformanceChartLeft)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("0")]
    public double MousePerformanceChartTop
    {
        get => (double)this[nameof(MousePerformanceChartTop)];
        set => this[nameof(MousePerformanceChartTop)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("1400")]
    public double MousePerformanceChartWidth
    {
        get => (double)this[nameof(MousePerformanceChartWidth)];
        set => this[nameof(MousePerformanceChartWidth)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("1040")]
    public double MousePerformanceChartHeight
    {
        get => (double)this[nameof(MousePerformanceChartHeight)];
        set => this[nameof(MousePerformanceChartHeight)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool MousePerformanceChartIsMaximized
    {
        get => (bool)this[nameof(MousePerformanceChartIsMaximized)];
        set => this[nameof(MousePerformanceChartIsMaximized)] = value;
    }
}
