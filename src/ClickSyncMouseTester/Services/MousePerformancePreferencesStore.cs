using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Settings;
using System;

namespace ClickSyncMouseTester.Services;

public sealed class MousePerformancePreferencesStore : IMousePerformancePreferencesStore
{
    private sealed class ApplicationSettingsMousePerformanceSettingsAccessor : IMousePerformanceSettingsAccessor
    {
        public double MousePerformanceLastCpi
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceLastCpi;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceLastCpi = value;
            }
        }

        public int MousePerformanceChartPlotType
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartPlotType;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartPlotType = value;
            }
        }

        public bool MousePerformanceChartShowStem
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartShowStem;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartShowStem = value;
            }
        }

        public bool MousePerformanceChartShowLines
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartShowLines;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartShowLines = value;
            }
        }

        public bool MousePerformanceChartHasSavedBounds
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartHasSavedBounds;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartHasSavedBounds = value;
            }
        }

        public double MousePerformanceChartLeft
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartLeft;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartLeft = value;
            }
        }

        public double MousePerformanceChartTop
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartTop;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartTop = value;
            }
        }

        public double MousePerformanceChartWidth
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartWidth;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartWidth = value;
            }
        }

        public double MousePerformanceChartHeight
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartHeight;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartHeight = value;
            }
        }

        public bool MousePerformanceChartIsMaximized
        {
            get
            {
                return ApplicationSettings.Default.MousePerformanceChartIsMaximized;
            }
            set
            {
                ApplicationSettings.Default.MousePerformanceChartIsMaximized = value;
            }
        }

        public void Save()
        {
            ApplicationSettings.Default.Save();
        }

        void IMousePerformanceSettingsAccessor.Save()
        {
            this.Save();
        }
    }

    private const double DefaultLastCpi = 800.0;

    private static readonly IMousePerformancePreferencesStore _instance = new MousePerformancePreferencesStore();

    private readonly IMousePerformanceSettingsAccessor _settings;

    public static IMousePerformancePreferencesStore Instance => _instance;

    private MousePerformancePreferencesStore()
        : this(new ApplicationSettingsMousePerformanceSettingsAccessor())
    {
    }

    public MousePerformancePreferencesStore(IMousePerformanceSettingsAccessor settingsAccessor)
    {
        _settings = settingsAccessor ?? new ApplicationSettingsMousePerformanceSettingsAccessor();
    }

    public MousePerformancePreferences LoadPreferences()
    {
        MousePerformancePreferences result;
        try
        {
            double? lastCpi = null;
            if (IsFinitePositive(_settings.MousePerformanceLastCpi))
            {
                lastCpi = _settings.MousePerformanceLastCpi;
            }
            MousePerformancePlotType chartPlotType = MousePerformancePlotType.XCountVsTime;
            int mousePerformanceChartPlotType = _settings.MousePerformanceChartPlotType;
            if (Enum.IsDefined(typeof(MousePerformancePlotType), mousePerformanceChartPlotType))
            {
                chartPlotType = (MousePerformancePlotType)mousePerformanceChartPlotType;
            }
            MousePerformanceChartWindowPlacement chartWindowPlacement = new MousePerformanceChartWindowPlacement(_settings.MousePerformanceChartHasSavedBounds, _settings.MousePerformanceChartLeft, _settings.MousePerformanceChartTop, _settings.MousePerformanceChartWidth, _settings.MousePerformanceChartHeight, _settings.MousePerformanceChartIsMaximized);
            result = new MousePerformancePreferences(lastCpi, chartPlotType, _settings.MousePerformanceChartShowStem, _settings.MousePerformanceChartShowLines, chartWindowPlacement);
        }
        catch (Exception)
        {
            result = new MousePerformancePreferences(800.0, MousePerformancePlotType.XCountVsTime, chartShowStem: false, chartShowLines: false, new MousePerformanceChartWindowPlacement(hasSavedBounds: false, 0.0, 0.0, 0.0, 0.0, isMaximized: false));
        }
        return result;
    }

    MousePerformancePreferences IMousePerformancePreferencesStore.LoadPreferences()
    {
        return this.LoadPreferences();
    }

    public void SaveLastCpi(double value)
    {
        if (IsFinitePositive(value))
        {
            try
            {
                _settings.MousePerformanceLastCpi = value;
                _settings.Save();
            }
            catch (Exception)
            {
            }
        }
    }

    void IMousePerformancePreferencesStore.SaveLastCpi(double value)
    {
        this.SaveLastCpi(value);
    }

    public void SaveChartOptions(MousePerformancePlotType plotType, bool showStem, bool showLines)
    {
        try
        {
            _settings.MousePerformanceChartPlotType = (int)plotType;
            _settings.MousePerformanceChartShowStem = showStem;
            _settings.MousePerformanceChartShowLines = showLines;
            _settings.Save();
        }
        catch (Exception)
        {
        }
    }

    void IMousePerformancePreferencesStore.SaveChartOptions(MousePerformancePlotType plotType, bool showStem, bool showLines)
    {
        this.SaveChartOptions(plotType, showStem, showLines);
    }

    public void SaveChartWindowPlacement(MousePerformanceChartWindowPlacement placement)
    {
        try
        {
            if (placement == null || !placement.HasSavedBounds)
            {
                _settings.MousePerformanceChartHasSavedBounds = false;
                _settings.MousePerformanceChartIsMaximized = false;
                _settings.Save();
                return;
            }
            _settings.MousePerformanceChartHasSavedBounds = true;
            _settings.MousePerformanceChartLeft = placement.Left;
            _settings.MousePerformanceChartTop = placement.Top;
            _settings.MousePerformanceChartWidth = placement.Width;
            _settings.MousePerformanceChartHeight = placement.Height;
            _settings.MousePerformanceChartIsMaximized = placement.IsMaximized;
            _settings.Save();
        }
        catch (Exception)
        {
        }
    }

    void IMousePerformancePreferencesStore.SaveChartWindowPlacement(MousePerformanceChartWindowPlacement placement)
    {
        this.SaveChartWindowPlacement(placement);
    }

    private static bool IsFinitePositive(double value)
    {
        if (value > 0.0 && !double.IsNaN(value))
        {
            return !double.IsInfinity(value);
        }
        return false;
    }
}





