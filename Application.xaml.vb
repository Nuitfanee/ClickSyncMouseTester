Imports System.Runtime.Versioning
Imports System.Windows
Imports System.Windows.Media
Imports WpfApp1.Services
Imports WpfApp1.ViewModels
Imports WpfApp1.Views.Shell

<SupportedOSPlatform("windows")>
Class Application
    Protected Overrides Sub OnStartup(e As StartupEventArgs)
        MyBase.OnStartup(e)

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default
        ThemeManager.Instance.Initialize()
        LocalizationManager.Instance.Initialize()
        FontManager.Instance.Initialize()

        Dim shellViewModel As New ShellViewModel()
        shellViewModel.OpenNavigationMenu()

        Dim mainWindowInstance As New MainWindow(shellViewModel, True)
        mainWindowInstance.WindowStartupLocation = WindowStartupLocation.Manual
        mainWindowInstance.ShowActivated = False
        mainWindowInstance.ShowInTaskbar = False
        mainWindowInstance.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048.0
        mainWindowInstance.Top = SystemParameters.VirtualScreenTop
        mainWindowInstance.Opacity = 1.0
        MainWindow = mainWindowInstance
        mainWindowInstance.Show()

        Dim startupWindow As New StartupNavigationWindow(shellViewModel, mainWindowInstance)
        startupWindow.Show()
    End Sub
End Class
