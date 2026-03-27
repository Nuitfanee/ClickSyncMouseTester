Imports System.Runtime.Versioning
Imports WpfApp1.Navigation
Imports WpfApp1.ViewModels

Namespace Views.Pages
    <SupportedOSPlatform("windows")>
    Partial Public Class PollingWorkspacePage
        Implements ICaptureSurfaceHost

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub PollingWorkspacePage_Loaded(sender As Object, e As RoutedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub PollingWorkspacePage_Unloaded(sender As Object, e As RoutedEventArgs)
            Dim viewModel = TryCast(DataContext, MainWindowViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetPageActive(False)
            End If
        End Sub

        Private Sub PollingWorkspacePage_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub PollingWorkspacePage_DataContextChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            Dim previousViewModel = TryCast(e.OldValue, MainWindowViewModel)
            If previousViewModel IsNot Nothing Then
                previousViewModel.SetPageActive(False)
            End If

            SyncViewModelState()
        End Sub

        Private Sub SyncViewModelState()
            Dim viewModel = TryCast(DataContext, MainWindowViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            viewModel.SetPageActive(IsLoaded AndAlso IsVisible)
        End Sub

        Public ReadOnly Property CaptureLockSurface As FrameworkElement Implements ICaptureSurfaceHost.CaptureLockSurface
            Get
                Return CaptureSurface
            End Get
        End Property
    End Class
End Namespace
