using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.ViewModels.Pages;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;

namespace ClickSyncMouseTester.Views.Pages;

[SupportedOSPlatform("windows")]
public partial class AngleCalibrationPage : UserControl, ICaptureSurfaceHost
{
    public FrameworkElement CaptureLockSurface => CaptureSurfaceButton;
    public AngleCalibrationPage()
    {
        InitializeComponent();
    }

    private void AngleCalibrationPage_Loaded(object sender, RoutedEventArgs e)
    {
        SyncViewModelState();
    }

    private void AngleCalibrationPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is AngleCalibrationPageViewModel angleCalibrationPageViewModel)
        {
            angleCalibrationPageViewModel.SetPageActive(isActive: false);
        }
    }

    private void AngleCalibrationPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncViewModelState();
    }

    private void AngleCalibrationPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AngleCalibrationPageViewModel angleCalibrationPageViewModel)
        {
            angleCalibrationPageViewModel.SetPageActive(isActive: false);
        }
        SyncViewModelState();
    }

    private void SyncViewModelState()
    {
        if (base.DataContext is AngleCalibrationPageViewModel angleCalibrationPageViewModel)
        {
            angleCalibrationPageViewModel.SetPageActive(base.IsLoaded && base.IsVisible);
        }
    }
}







