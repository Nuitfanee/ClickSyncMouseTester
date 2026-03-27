Imports System.ComponentModel
Imports System.Runtime.Versioning
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports WpfApp1.Navigation
Imports WpfApp1.ViewModels.Pages
Imports WpfApp1.Views.Shell

Namespace Views.Pages
    <SupportedOSPlatform("windows")>
    Partial Public Class MousePerformancePage
        Implements ICaptureSurfaceHost

        Private _trackedViewModel As MousePerformancePageViewModel
        Private _chartWindow As MousePerformanceChartWindow
        Private _chartWindowViewModel As MousePerformanceChartWindowViewModel

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub MousePerformancePage_Loaded(sender As Object, e As RoutedEventArgs)
            AttachViewModelHandlers(TryCast(DataContext, MousePerformancePageViewModel))
            SyncViewModelState()
        End Sub

        Private Sub MousePerformancePage_Unloaded(sender As Object, e As RoutedEventArgs)
            Dim viewModel = TryCast(DataContext, MousePerformancePageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetPageActive(False)
                viewModel.SetChartWindowAttached(False)
            End If

            DetachViewModelHandlers()
            CloseChartWindow()
        End Sub

        Private Sub MousePerformancePage_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub MousePerformancePage_DataContextChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            DetachViewModelHandlers()

            Dim previousViewModel = TryCast(e.OldValue, MousePerformancePageViewModel)
            If previousViewModel IsNot Nothing Then
                previousViewModel.SetPageActive(False)
                previousViewModel.SetChartWindowAttached(False)
            End If

            CloseChartWindow()
            AttachViewModelHandlers(TryCast(e.NewValue, MousePerformancePageViewModel))
            SyncViewModelState()
        End Sub

        Private Sub CpiTextBox_PreviewKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then
                Return
            End If

            CommitCpiTextBox()
            CaptureSurface.Focus()
            e.Handled = True
        End Sub

        Private Sub CpiTextBox_LostFocus(sender As Object, e As RoutedEventArgs)
            CommitCpiTextBox()
        End Sub

        Private Sub CommitCpiTextBox()
            Dim viewModel = TryCast(DataContext, MousePerformancePageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            Dim bindingExpression = CpiTextBox.GetBindingExpression(TextBox.TextProperty)
            If bindingExpression IsNot Nothing Then
                bindingExpression.UpdateSource()
            End If

            viewModel.CommitCpiInput()
        End Sub

        Private Sub AttachViewModelHandlers(viewModel As MousePerformancePageViewModel)
            If ReferenceEquals(_trackedViewModel, viewModel) Then
                Return
            End If

            DetachViewModelHandlers()
            _trackedViewModel = viewModel

            If _trackedViewModel IsNot Nothing Then
                AddHandler _trackedViewModel.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
        End Sub

        Private Sub DetachViewModelHandlers()
            If _trackedViewModel Is Nothing Then
                Return
            End If

            RemoveHandler _trackedViewModel.PropertyChanged, AddressOf OnViewModelPropertyChanged
            _trackedViewModel = Nothing
        End Sub

        Private Sub SyncViewModelState()
            Dim viewModel = TryCast(DataContext, MousePerformancePageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            viewModel.SetPageActive(IsLoaded AndAlso IsVisible)
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If e Is Nothing OrElse String.IsNullOrWhiteSpace(e.PropertyName) Then
                Return
            End If

            If String.Equals(e.PropertyName, NameOf(MousePerformancePageViewModel.PlotOpenRequestVersion), StringComparison.Ordinal) Then
                EnsureChartWindow()
                Return
            End If

            If String.Equals(e.PropertyName, NameOf(MousePerformancePageViewModel.ChartWindowCloseRequestVersion), StringComparison.Ordinal) Then
                CloseChartWindow()
                Return
            End If

            If String.Equals(e.PropertyName, NameOf(MousePerformancePageViewModel.LatestChartSnapshot), StringComparison.Ordinal) Then
                PushChartSnapshot()
            End If
        End Sub

        Private Sub EnsureChartWindow()
            Dim viewModel = TryCast(DataContext, MousePerformancePageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            If _chartWindow IsNot Nothing Then
                viewModel.SetChartWindowAttached(True)
                PushChartSnapshot()
                RestoreAndActivateChartWindow()
                Return
            End If

            _chartWindowViewModel = New MousePerformanceChartWindowViewModel(viewModel.PreferencesStore)
            _chartWindow = New MousePerformanceChartWindow(_chartWindowViewModel, viewModel.PreferencesStore) With {
                .Owner = Window.GetWindow(Me)
            }

            AddHandler _chartWindow.Closed, AddressOf ChartWindow_Closed
            viewModel.SetChartWindowAttached(True)
            PushChartSnapshot()
            _chartWindow.Show()
            RestoreAndActivateChartWindow()
        End Sub

        Private Sub RestoreAndActivateChartWindow()
            If _chartWindow Is Nothing Then
                Return
            End If

            If _chartWindow.WindowState = WindowState.Minimized Then
                SystemCommands.RestoreWindow(_chartWindow)
            End If

            If Not _chartWindow.IsVisible Then
                _chartWindow.Show()
            End If

            _chartWindow.Activate()
            _chartWindow.Focus()
        End Sub

        Private Sub PushChartSnapshot()
            Dim viewModel = TryCast(DataContext, MousePerformancePageViewModel)
            If viewModel Is Nothing OrElse _chartWindowViewModel Is Nothing Then
                Return
            End If

            _chartWindowViewModel.UpdateSnapshot(viewModel.LatestChartSnapshot)
        End Sub

        Private Sub CloseChartWindow()
            If _chartWindow Is Nothing Then
                If _chartWindowViewModel IsNot Nothing Then
                    _chartWindowViewModel.Dispose()
                    _chartWindowViewModel = Nothing
                End If

                Return
            End If

            RemoveHandler _chartWindow.Closed, AddressOf ChartWindow_Closed
            _chartWindow.Close()
            _chartWindow = Nothing

            If _chartWindowViewModel IsNot Nothing Then
                _chartWindowViewModel.Dispose()
                _chartWindowViewModel = Nothing
            End If
        End Sub

        Private Sub ChartWindow_Closed(sender As Object, e As EventArgs)
            If _chartWindow IsNot Nothing Then
                RemoveHandler _chartWindow.Closed, AddressOf ChartWindow_Closed
            End If

            _chartWindow = Nothing
            If _chartWindowViewModel IsNot Nothing Then
                _chartWindowViewModel.Dispose()
                _chartWindowViewModel = Nothing
            End If

            Dim viewModel = TryCast(DataContext, MousePerformancePageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetChartWindowAttached(False)
            End If
        End Sub

        Public ReadOnly Property CaptureLockSurface As FrameworkElement Implements ICaptureSurfaceHost.CaptureLockSurface
            Get
                Return If(TryCast(CaptureLockZone, FrameworkElement), CaptureSurface)
            End Get
        End Property
    End Class
End Namespace
