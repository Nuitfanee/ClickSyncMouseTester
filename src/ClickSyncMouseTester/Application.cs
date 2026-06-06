using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels;
using ClickSyncMouseTester.Views.Shell;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClickSyncMouseTester;

[SupportedOSPlatform("windows")]
public partial class Application : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        using (UiPerformanceProbe.BeginStage("Application.OnStartup"))
        {
            UiPerformanceProbe.StartStartupToFirstFrame();
            base.OnStartup(e);
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            ThemeManager.Instance.Initialize();
            LocalizationManager.Instance.Initialize();
            FontManager.Instance.Initialize();
            ShellViewModel shellViewModel = new ShellViewModel();
            shellViewModel.OpenNavigationMenu();
            MainWindow mainWindow = new MainWindow(shellViewModel, usesStartupMenuHandoff: true);
            mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            mainWindow.ShowActivated = false;
            mainWindow.ShowInTaskbar = false;
            mainWindow.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048.0;
            mainWindow.Top = SystemParameters.VirtualScreenTop;
            mainWindow.Opacity = 0.0;
            mainWindow.IsHitTestVisible = false;
            base.MainWindow = mainWindow;
            new StartupNavigationWindow(shellViewModel, mainWindow).Show();
        }
    }
}





