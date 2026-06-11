using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

public interface IMousePerformancePreferencesStore
{
    MousePerformancePreferences LoadPreferences();

    void SaveLastCpi(double value);

    void SaveChartOptions(MousePerformancePlotType plotType, bool showStem, bool showLines);

    void SaveChartWindowPlacement(MousePerformanceChartWindowPlacement placement);
}





