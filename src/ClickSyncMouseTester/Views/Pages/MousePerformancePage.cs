using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels.Pages;
using ClickSyncMouseTester.Views.Shell;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace ClickSyncMouseTester.Views.Pages;

[SupportedOSPlatform("windows")]
public partial class MousePerformancePage : UserControl, ICaptureSurfaceHost, IComponentConnector
{
    private MousePerformancePageViewModel _trackedViewModel;

    private MousePerformanceChartWindow _chartWindow;

    private MousePerformanceChartWindowViewModel _chartWindowViewModel;

    public FrameworkElement CaptureLockSurface => CaptureLockZone ?? CaptureSurface;

    public MousePerformancePage()
    {
        InitializeComponent();
    }

    private void MousePerformancePage_Loaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelHandlers(base.DataContext as MousePerformancePageViewModel);
        SyncViewModelState();
    }

    private void MousePerformancePage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            mousePerformancePageViewModel.SetPageActive(isActive: false);
            mousePerformancePageViewModel.SetChartWindowAttached(isAttached: false);
        }
        DetachViewModelHandlers();
        CloseChartWindow();
    }

    private void MousePerformancePage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncViewModelState();
    }

    private void MousePerformancePage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModelHandlers();
        if (e.OldValue is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            mousePerformancePageViewModel.SetPageActive(isActive: false);
            mousePerformancePageViewModel.SetChartWindowAttached(isAttached: false);
        }
        CloseChartWindow();
        AttachViewModelHandlers(e.NewValue as MousePerformancePageViewModel);
        SyncViewModelState();
    }

    private void CpiTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            CommitCpiTextBox();
            CaptureSurface.Focus();
            e.Handled = true;
        }
    }

    private void CpiTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitCpiTextBox();
    }

    private void CommitCpiTextBox()
    {
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            CpiTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            mousePerformancePageViewModel.CommitCpiInput();
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ImportSessionIntoCurrentGroup(Window.GetWindow(this));
    }

    private void DeleteImportedDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            mousePerformancePageViewModel.DeleteImportedSessions();
            if (_chartWindowViewModel != null)
            {
                PushChartSessionGroup();
            }
        }
    }

    private void AttachViewModelHandlers(MousePerformancePageViewModel viewModel)
    {
        if (!ReferenceEquals(_trackedViewModel, viewModel))
        {
            DetachViewModelHandlers();
            _trackedViewModel = viewModel;
            if (_trackedViewModel != null)
            {
                _trackedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }
    }

    private void DetachViewModelHandlers()
    {
        if (_trackedViewModel != null)
        {
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedViewModel = null;
        }
    }

    private void SyncViewModelState()
    {
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            mousePerformancePageViewModel.SetPageActive(base.IsLoaded && base.IsVisible);
        }
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }
        if (string.Equals(e.PropertyName, "PlotOpenRequestVersion", StringComparison.Ordinal))
        {
            EnsureChartWindow();
        }
        else if (string.Equals(e.PropertyName, "ChartWindowCloseRequestVersion", StringComparison.Ordinal))
        {
            CloseChartWindow();
        }
        else if (string.Equals(e.PropertyName, "IsLocked", StringComparison.Ordinal))
        {
            if (_chartWindowViewModel != null)
            {
                PushChartSessionGroup();
            }
        }
        else if (string.Equals(e.PropertyName, "LatestChartSnapshot", StringComparison.Ordinal))
        {
            PushChartSessionGroup();
        }
    }

    private void EnsureChartWindow()
    {
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            if (_chartWindow != null)
            {
                mousePerformancePageViewModel.SetChartWindowAttached(isAttached: true);
                PushChartSessionGroup();
                RestoreAndActivateChartWindow();
                return;
            }
            _chartWindowViewModel = new MousePerformanceChartWindowViewModel(mousePerformancePageViewModel.PreferencesStore);
            _chartWindow = new MousePerformanceChartWindow(_chartWindowViewModel, mousePerformancePageViewModel.PreferencesStore)
            {
                Owner = Window.GetWindow(this)
            };
            _chartWindow.Closed += ChartWindow_Closed;
            _chartWindow.ImportComparisonRequested += ChartWindow_ImportComparisonRequested;
            mousePerformancePageViewModel.SetChartWindowAttached(isAttached: true);
            PushChartSessionGroup();
            _chartWindow.Show();
            RestoreAndActivateChartWindow();
        }
    }

    private void RestoreAndActivateChartWindow()
    {
        if (_chartWindow != null)
        {
            if (_chartWindow.WindowState == WindowState.Minimized)
            {
                SystemCommands.RestoreWindow(_chartWindow);
            }
            if (!_chartWindow.IsVisible)
            {
                _chartWindow.Show();
            }
            _chartWindow.Activate();
            _chartWindow.Focus();
        }
    }

    private void PushChartSessionGroup()
    {
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel && _chartWindowViewModel != null)
        {
            _chartWindowViewModel.UpdateSessionGroup(mousePerformancePageViewModel.CreateCurrentChartBaselineSession(), mousePerformancePageViewModel.CreateCurrentChartComparisonSessions());
        }
    }

    private void ImportSessionIntoCurrentGroup(Window ownerWindow)
    {
        if (!(base.DataContext is MousePerformancePageViewModel { IsLocked: false } mousePerformancePageViewModel))
        {
            return;
        }
        LocalizationManager instance = LocalizationManager.Instance;
        instance.Initialize();
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Filter = instance.GetString("MousePerformance.Exchange.Filter.Json"),
            FilterIndex = 1,
            CheckFileExists = true,
            Multiselect = false
        };
        if (openFileDialog.ShowDialog(ownerWindow) != true)
        {
            return;
        }
        try
        {
            MousePerformanceSessionArchive session = MousePerformanceExchangeService.ImportSession(openFileDialog.FileName);
            if (mousePerformancePageViewModel.HasCurrentSessionData)
            {
                if (mousePerformancePageViewModel.ContainsEquivalentSession(session))
                {
                    ShowAlertDialog(ownerWindow, instance.GetString("MousePerformance.Chart.Import.Duplicate.Title"), instance.GetString("MousePerformance.Chart.Import.Duplicate.Message"), instance.GetString("Dialog.Common.Confirm"));
                    return;
                }
                bool replaceOldest = false;
                if (mousePerformancePageViewModel.ImportedComparisonSessionCount >= 2)
                {
                    if (!ShowConfirmDialog(ownerWindow, instance.GetString("MousePerformance.Chart.Import.CompareLimit.Title"), instance.GetString("MousePerformance.Chart.Import.CompareLimit.Message"), instance.GetString("MousePerformance.Chart.Import.CompareLimit.Confirm"), instance.GetString("Dialog.Common.Cancel")))
                    {
                        return;
                    }
                    replaceOldest = true;
                }
                mousePerformancePageViewModel.AddImportedComparisonSession(session, replaceOldest);
            }
            else
            {
                mousePerformancePageViewModel.ReplaceWithImportedSession(session);
            }
            PushChartSessionGroup();
        }
        catch (Exception ex)
        {
            ShowAlertDialog(ownerWindow, instance.GetString("MousePerformance.Import.Error.Title"), instance.GetString("MousePerformance.Import.Error.Format", ex.Message), instance.GetString("Dialog.Common.Confirm"));
        }
    }

    private static void ShowAlertDialog(Window ownerWindow, string title, string message, string confirmText)
    {
        AppAlertDialog appAlertDialog = new AppAlertDialog(title, message, confirmText);
        if (ownerWindow != null)
        {
            appAlertDialog.Owner = ownerWindow;
        }
        appAlertDialog.ShowDialog();
    }

    private static bool ShowConfirmDialog(Window ownerWindow, string title, string message, string confirmText, string secondaryText)
    {
        AppAlertDialog appAlertDialog = new AppAlertDialog(title, message, confirmText, secondaryText);
        if (ownerWindow != null)
        {
            appAlertDialog.Owner = ownerWindow;
        }
        bool? dialogResult = appAlertDialog.ShowDialog();
        return dialogResult == true;
    }

    private void CloseChartWindow()
    {
        if (_chartWindow == null)
        {
            if (_chartWindowViewModel != null)
            {
                _chartWindowViewModel.Dispose();
                _chartWindowViewModel = null;
            }
            return;
        }
        _chartWindow.Closed -= ChartWindow_Closed;
        _chartWindow.ImportComparisonRequested -= ChartWindow_ImportComparisonRequested;
        _chartWindow.Close();
        _chartWindow = null;
        if (_chartWindowViewModel != null)
        {
            _chartWindowViewModel.Dispose();
            _chartWindowViewModel = null;
        }
    }

    private void ChartWindow_Closed(object sender, EventArgs e)
    {
        if (_chartWindow != null)
        {
            _chartWindow.Closed -= ChartWindow_Closed;
            _chartWindow.ImportComparisonRequested -= ChartWindow_ImportComparisonRequested;
        }
        _chartWindow = null;
        if (_chartWindowViewModel != null)
        {
            _chartWindowViewModel.Dispose();
            _chartWindowViewModel = null;
        }
        if (base.DataContext is MousePerformancePageViewModel mousePerformancePageViewModel)
        {
            mousePerformancePageViewModel.SetChartWindowAttached(isAttached: false);
        }
    }

    private void ChartWindow_ImportComparisonRequested(object sender, EventArgs e)
    {
        ImportSessionIntoCurrentGroup(_chartWindow);
    }
}





