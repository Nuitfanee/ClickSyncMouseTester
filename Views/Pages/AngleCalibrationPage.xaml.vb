Imports System.Runtime.Versioning
Imports WpfApp1.Navigation
Imports WpfApp1.ViewModels.Pages

Namespace Views.Pages
    <SupportedOSPlatform("windows")>
    Partial Public Class AngleCalibrationPage
        Implements ICaptureSurfaceHost

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub AngleCalibrationPage_Loaded(sender As Object, e As RoutedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub AngleCalibrationPage_Unloaded(sender As Object, e As RoutedEventArgs)
            Dim viewModel = TryCast(DataContext, AngleCalibrationPageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetPageActive(False)
            End If
        End Sub

        Private Sub AngleCalibrationPage_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub AngleCalibrationPage_DataContextChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            Dim previousViewModel = TryCast(e.OldValue, AngleCalibrationPageViewModel)
            If previousViewModel IsNot Nothing Then
                previousViewModel.SetPageActive(False)
            End If

            SyncViewModelState()
        End Sub

        Private Sub SyncViewModelState()
            Dim viewModel = TryCast(DataContext, AngleCalibrationPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            viewModel.SetPageActive(IsLoaded AndAlso IsVisible)
        End Sub

        Public ReadOnly Property CaptureLockSurface As FrameworkElement Implements ICaptureSurfaceHost.CaptureLockSurface
            Get
                Return CaptureSurfaceButton
            End Get
        End Property
    End Class
End Namespace
