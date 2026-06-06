namespace ClickSyncMouseTester.Services;

public interface IMousePerformanceSettingsAccessor
{
    double MousePerformanceLastCpi { get; set; }

    int MousePerformanceChartPlotType { get; set; }

    bool MousePerformanceChartShowStem { get; set; }

    bool MousePerformanceChartShowLines { get; set; }

    bool MousePerformanceChartHasSavedBounds { get; set; }

    double MousePerformanceChartLeft { get; set; }

    double MousePerformanceChartTop { get; set; }

    double MousePerformanceChartWidth { get; set; }

    double MousePerformanceChartHeight { get; set; }

    bool MousePerformanceChartIsMaximized { get; set; }

    void Save();
}





