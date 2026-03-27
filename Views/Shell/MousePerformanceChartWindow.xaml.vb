Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.Runtime.Versioning
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows.Shell
Imports Microsoft.Win32
Imports WpfApp1.Models
Imports WpfApp1.Services
Imports WpfApp1.ViewModels.Pages

Namespace Views.Shell
    <SupportedOSPlatform("windows")>
    Public Class MousePerformanceChartWindow
        Private Shared ReadOnly SquareWindowCornerRadius As New CornerRadius(0)

        Private ReadOnly _themeManager As ThemeManager
        Private ReadOnly _preferencesStore As IMousePerformancePreferencesStore
        Private _windowSource As HwndSource

        Public Sub New(viewModel As MousePerformanceChartWindowViewModel,
                       Optional preferencesStore As IMousePerformancePreferencesStore = Nothing)
            InitializeComponent()
            DataContext = viewModel
            _preferencesStore = If(preferencesStore, MousePerformancePreferencesStore.Instance)
            _themeManager = ThemeManager.Instance
            AddHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged
            UpdateWindowControlGlyph()
        End Sub

        Protected Overrides Sub OnSourceInitialized(e As EventArgs)
            MyBase.OnSourceInitialized(e)

            _windowSource = TryCast(PresentationSource.FromVisual(Me), HwndSource)
            If _windowSource IsNot Nothing Then
                _windowSource.AddHook(AddressOf WndProc)
            End If

            UpdateWindowControlGlyph()
            ApplyWindowCornerPreference()
            ApplyWindowChromeTheme()
            ApplySavedPlacement()
        End Sub

        Protected Overrides Sub OnStateChanged(e As EventArgs)
            MyBase.OnStateChanged(e)
            UpdateWindowControlGlyph()
            ApplyWindowCornerPreference()
        End Sub

        Protected Overrides Sub OnClosing(e As CancelEventArgs)
            SaveWindowPlacement()
            MyBase.OnClosing(e)
        End Sub

        Protected Overrides Sub OnClosed(e As EventArgs)
            RemoveHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged

            If _windowSource IsNot Nothing Then
                _windowSource.RemoveHook(AddressOf WndProc)
                _windowSource = Nothing
            End If

            MyBase.OnClosed(e)
        End Sub

        Private Function WndProc(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr, ByRef handled As Boolean) As IntPtr
            If msg = NativeMethods.WM_GETMINMAXINFO Then
                WindowSizingHelper.ApplyWindowBounds(Me, hwnd, lParam)
                handled = True
                Return IntPtr.Zero
            End If

            Return IntPtr.Zero
        End Function

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            Dispatcher.BeginInvoke(New Action(AddressOf ApplyWindowChromeTheme))
        End Sub

        Private Sub MinimizeButton_Click(sender As Object, e As RoutedEventArgs)
            SystemCommands.MinimizeWindow(Me)
        End Sub

        Private Sub MaximizeRestoreButton_Click(sender As Object, e As RoutedEventArgs)
            If WindowState = WindowState.Maximized Then
                SystemCommands.RestoreWindow(Me)
            Else
                SystemCommands.MaximizeWindow(Me)
            End If

            UpdateWindowControlGlyph()
        End Sub

        Private Sub CloseButton_Click(sender As Object, e As RoutedEventArgs)
            SystemCommands.CloseWindow(Me)
        End Sub

        Private Sub UpdateWindowControlGlyph()
            If MaximizeRestoreGlyph Is Nothing Then
                Return
            End If

            MaximizeRestoreGlyph.Text = If(WindowState = WindowState.Maximized, ChrW(&HE923), ChrW(&HE922))
        End Sub

        Private Sub ApplyWindowChromeTheme()
            Dim windowHandle = New WindowInteropHelper(Me).Handle
            If windowHandle = IntPtr.Zero Then
                Return
            End If

            Dim isDark = _themeManager.CurrentTheme = AppTheme.Dark
            Dim captionColor = ResolveColorResource("GlassTitleBarBackgroundColor",
                                                    ResolveColorResource("WindowBackgroundColor",
                                                                         If(isDark, Colors.Black, Colors.White)))
            Dim textColor = ResolveColorResource("TextStrongColor", If(isDark, Colors.White, Colors.Black))
            Dim borderColor = ResolveColorResource("WindowOuterBorderColor", If(isDark, Colors.White, Colors.Black))

            NativeMethods.TrySetImmersiveDarkMode(windowHandle, isDark)
            NativeMethods.TrySetSystemBackdropType(windowHandle, NativeMethods.DWMSBT_MAINWINDOW)
            NativeMethods.TrySetWindowColorAttribute(windowHandle, NativeMethods.DWMWA_CAPTION_COLOR, ToColorRef(captionColor))
            NativeMethods.TrySetWindowColorAttribute(windowHandle, NativeMethods.DWMWA_TEXT_COLOR, ToColorRef(textColor))
            NativeMethods.TrySetWindowColorAttribute(windowHandle, NativeMethods.DWMWA_BORDER_COLOR, ToColorRef(borderColor))
        End Sub

        Private Sub ApplyWindowCornerPreference()
            Dim chrome = WindowChrome.GetWindowChrome(Me)
            If chrome IsNot Nothing Then
                chrome.CornerRadius = SquareWindowCornerRadius
            End If

            Dim windowHandle = New WindowInteropHelper(Me).Handle
            If windowHandle = IntPtr.Zero Then
                Return
            End If

            NativeMethods.TrySetWindowCornerPreference(windowHandle, NativeMethods.DWMWCP_DONOTROUND)
        End Sub

        Private Sub ApplySavedPlacement()
            Dim placement = _preferencesStore.LoadPreferences().ChartWindowPlacement
            Dim normalizedPlacement As MousePerformanceChartWindowPlacement = Nothing

            If Not MousePerformanceWindowPlacementHelper.TryNormalizeForRestore(placement,
                                                                                MinWidth,
                                                                                MinHeight,
                                                                                GetWorkingAreasInDip(),
                                                                                normalizedPlacement) Then
                Return
            End If

            WindowStartupLocation = WindowStartupLocation.Manual
            WindowState = WindowState.Normal
            Left = normalizedPlacement.Left
            Top = normalizedPlacement.Top
            Width = normalizedPlacement.Width
            Height = normalizedPlacement.Height

            If normalizedPlacement.IsMaximized Then
                WindowState = WindowState.Maximized
            End If

            UpdateWindowControlGlyph()
        End Sub

        Private Sub SaveWindowPlacement()
            _preferencesStore.SaveChartWindowPlacement(MousePerformanceWindowPlacementHelper.Capture(Me))
        End Sub

        Private Function GetWorkingAreasInDip() As IReadOnlyList(Of Rect)
            Dim dpi = VisualTreeHelper.GetDpi(Me)
            Dim scaleX = If(dpi.DpiScaleX > 0.0, dpi.DpiScaleX, 1.0)
            Dim scaleY = If(dpi.DpiScaleY > 0.0, dpi.DpiScaleY, 1.0)
            Dim workAreas As New List(Of Rect)()

            Dim callback As NativeMethods.MonitorEnumProc =
                Function(hMonitor As IntPtr, hdcMonitor As IntPtr, ByRef monitorRect As NativeMethods.RECT, dwData As IntPtr) As Boolean
                    Dim monitorInfo As New NativeMethods.MONITORINFOEX With {
                        .cbSize = Marshal.SizeOf(GetType(NativeMethods.MONITORINFOEX))
                    }

                    If NativeMethods.GetMonitorInfo(hMonitor, monitorInfo) Then
                        Dim area = monitorInfo.rcWork
                        workAreas.Add(New Rect(area.Left / scaleX,
                                               area.Top / scaleY,
                                               Math.Max(0, area.Right - area.Left) / scaleX,
                                               Math.Max(0, area.Bottom - area.Top) / scaleY))
                    End If

                    Return True
                End Function

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)

            If workAreas.Count = 0 Then
                Dim area = SystemParameters.WorkArea
                workAreas.Add(New Rect(area.Left, area.Top, area.Width, area.Height))
            End If

            Return workAreas
        End Function

        Private Function ResolveColorResource(resourceKey As String, fallback As Color) As Color
            Dim resource = TryFindResource(resourceKey)
            If TypeOf resource Is Color Then
                Return CType(resource, Color)
            End If

            Dim brush = TryCast(resource, SolidColorBrush)
            If brush IsNot Nothing Then
                Return brush.Color
            End If

            Return fallback
        End Function

        Private Shared Function ToColorRef(color As Color) As Integer
            Return CInt(color.R) Or (CInt(color.G) << 8) Or (CInt(color.B) << 16)
        End Function

        Private Sub SavePngButton_Click(sender As Object, e As RoutedEventArgs)
            Dim localization = LocalizationManager.Instance
            localization.Initialize()

            Dim dialog As New SaveFileDialog With {
                .Filter = localization.GetString("MousePerformance.Chart.Filter.Png"),
                .FilterIndex = 1,
                .AddExtension = True,
                .DefaultExt = ".png"
            }

            If dialog.ShowDialog(Me) <> True Then
                Return
            End If

            ChartControl.ExportToPng(dialog.FileName, 1280, 800)
        End Sub

        Private Sub RangeTextBox_PreviewKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then
                Return
            End If

            CommitRangeInputs()
            ChartControl.Focus()
            e.Handled = True
        End Sub

        Private Sub RangeTextBox_LostFocus(sender As Object, e As RoutedEventArgs)
            CommitRangeInputs()
        End Sub

        Private Sub CommitRangeInputs()
            Dim viewModel = TryCast(DataContext, MousePerformanceChartWindowViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            viewModel.CommitRangeInputs()
        End Sub
    End Class
End Namespace
