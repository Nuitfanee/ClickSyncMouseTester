using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.ViewModels;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;

namespace ClickSyncMouseTester.Views.Pages;

[SupportedOSPlatform("windows")]
public partial class PollingWorkspacePage : UserControl, ICaptureSurfaceHost
{
    public FrameworkElement CaptureLockSurface => CaptureSurface;
    public PollingWorkspacePage()
    {
        InitializeComponent();
    }

    private void PollingWorkspacePage_Loaded(object sender, RoutedEventArgs e)
    {
        SyncViewModelState();
    }

    private void PollingWorkspacePage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            mainWindowViewModel.SetPageActive(isActive: false);
        }
    }

    private void PollingWorkspacePage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncViewModelState();
    }

    private void PollingWorkspacePage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainWindowViewModel mainWindowViewModel)
        {
            mainWindowViewModel.SetPageActive(isActive: false);
        }
        SyncViewModelState();
    }

    private void SyncViewModelState()
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            mainWindowViewModel.SetPageActive(base.IsLoaded && base.IsVisible);
        }
    }
}







