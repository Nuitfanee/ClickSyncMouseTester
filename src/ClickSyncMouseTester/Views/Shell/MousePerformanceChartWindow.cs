using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels.Pages;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Views.Shell;

[SupportedOSPlatform("windows")]
public partial class MousePerformanceChartWindow : Window
{
    private static readonly CornerRadius SquareWindowCornerRadius = new CornerRadius(0.0);

    private const int WM_MOUSEWHEEL = 522;

    private readonly ThemeManager _themeManager;

    private readonly IMousePerformancePreferencesStore _preferencesStore;

    private HwndSource _windowSource;

    private MousePerformanceChartWindowViewModel _viewModel;

    private string _lastViewportResetSignature;
    public event EventHandler ImportComparisonRequested;

    public MousePerformanceChartWindow(MousePerformanceChartWindowViewModel viewModel, IMousePerformancePreferencesStore preferencesStore = null)
    {
        _lastViewportResetSignature = string.Empty;
        InitializeComponent();
        base.DataContext = viewModel;
        AttachViewModel(viewModel);
        if (viewModel != null && ChartControl != null)
        {
            viewModel.UpdateChartRendererAvailability(ChartControl.IsGpuRendererAvailable);
        }
        _preferencesStore = preferencesStore ?? MousePerformancePreferencesStore.Instance;
        _themeManager = ThemeManager.Instance;
        _themeManager.ThemeChanged += OnThemeChanged;
        UpdateWindowControlGlyph();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_windowSource != null)
        {
            _windowSource.AddHook(WndProc);
        }
        UpdateWindowControlGlyph();
        ApplyWindowCornerPreference();
        ApplyWindowChromeTheme();
        ClearSavedChartWindowPlacementPreference();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateWindowControlGlyph();
        ApplyWindowCornerPreference();
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeManager.ThemeChanged -= OnThemeChanged;
        DetachViewModel();
        if (_windowSource != null)
        {
            _windowSource.RemoveHook(WndProc);
            _windowSource = null;
        }
        base.OnClosed(e);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case 36:
                WindowSizingHelper.ApplyWindowBounds(this, hwnd, lParam);
                handled = true;
                return IntPtr.Zero;
            case 522:
                if (TryHandleChartMouseWheel(wParam, lParam))
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
        }
        return IntPtr.Zero;
    }

    private bool TryHandleChartMouseWheel(nint wParam, nint lParam)
    {
        if (ChartControl == null)
        {
            return false;
        }
        int wheelDelta = UnpackSignedHighWord(((IntPtr)wParam).ToInt64());
        if (wheelDelta == 0)
        {
            return false;
        }
        long packedValue = ((IntPtr)lParam).ToInt64();
        Point screenPoint = new Point(UnpackSignedLowWord(packedValue), UnpackSignedHighWord(packedValue));
        bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        return ChartControl.TryHandleWindowMouseWheel(screenPoint, wheelDelta, shiftPressed, controlPressed);
    }

    private static int UnpackSignedLowWord(long packedValue)
    {
        return UnpackSignedWord(packedValue, 0);
    }

    private static int UnpackSignedHighWord(long packedValue)
    {
        return UnpackSignedWord(packedValue, 16);
    }

    private static int UnpackSignedWord(long packedValue, int shiftBits)
    {
        int unsignedWord = (int)((packedValue >> shiftBits) & 0xFFFF);
        if (unsignedWord >= 32768)
        {
            return unsignedWord - 65536;
        }
        return unsignedWord;
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(ApplyWindowChromeTheme));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (base.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
        UpdateWindowControlGlyph();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void UpdateWindowControlGlyph()
    {
        if (MaximizeRestoreGlyph != null)
        {
            MaximizeRestoreGlyph.Text = ((base.WindowState == WindowState.Maximized) ? '\ue923' : '\ue922').ToString();
        }
    }

    private void ApplyWindowChromeTheme()
    {
        bool isDarkTheme = _themeManager.CurrentTheme == AppTheme.Dark;
        Color windowBackgroundColor = ResolveColorResource("WindowBackgroundColor", isDarkTheme ? Colors.Black : Colors.White);
        ApplyNativeBackgroundColor(windowBackgroundColor);
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            Color titleBarColor = ResolveColorResource("GlassTitleBarBackgroundColor", windowBackgroundColor);
            Color titleTextColor = ResolveColorResource("TextStrongColor", isDarkTheme ? Colors.White : Colors.Black);
            Color borderColor = ResolveColorResource("WindowOuterBorderColor", isDarkTheme ? Colors.White : Colors.Black);
            NativeMethods.TrySetImmersiveDarkMode(handle, isDarkTheme);
            NativeMethods.TrySetSystemBackdropType(handle, 2);
            NativeMethods.TrySetWindowColorAttribute(handle, NativeMethods.DWMWA_CAPTION_COLOR, ToColorRef(titleBarColor));
            NativeMethods.TrySetWindowColorAttribute(handle, NativeMethods.DWMWA_TEXT_COLOR, ToColorRef(titleTextColor));
            NativeMethods.TrySetWindowColorAttribute(handle, NativeMethods.DWMWA_BORDER_COLOR, ToColorRef(borderColor));
        }
    }

    private void ApplyNativeBackgroundColor(Color backgroundColor)
    {
        if (_windowSource != null)
        {
            HwndTarget compositionTarget = _windowSource.CompositionTarget;
            if (compositionTarget != null)
            {
                compositionTarget.BackgroundColor = backgroundColor;
            }
        }
    }

    private void ApplyWindowCornerPreference()
    {
        WindowChrome windowChrome = WindowChrome.GetWindowChrome(this);
        if (windowChrome != null)
        {
            windowChrome.CornerRadius = SquareWindowCornerRadius;
        }
        nint handle = new WindowInteropHelper(this).Handle;
        if (!(handle == IntPtr.Zero))
        {
            NativeMethods.TrySetWindowCornerPreference(handle, 1);
        }
    }

    private void ClearSavedChartWindowPlacementPreference()
    {
        _preferencesStore?.SaveChartWindowPlacement(null);
    }

    private Color ResolveColorResource(string resourceKey, Color fallback)
    {
        object resourceValue = TryFindResource(resourceKey);
        if (resourceValue is Color colorValue)
        {
            return colorValue;
        }
        if (resourceValue is SolidColorBrush { Color: var brushColor })
        {
            return brushColor;
        }
        return fallback;
    }

    private void AttachViewModel(MousePerformanceChartWindowViewModel viewModel)
    {
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            DetachViewModel();
            _viewModel = viewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
        _lastViewportResetSignature = string.Empty;
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e != null && !string.IsNullOrWhiteSpace(e.PropertyName) && !string.Equals(e.PropertyName, "RenderFrame", StringComparison.Ordinal))
        {
            return;
        }
        string viewportResetSignature = BuildViewportResetSignature(_viewModel?.RenderFrame);
        if (string.Equals(viewportResetSignature, _lastViewportResetSignature, StringComparison.Ordinal))
        {
            return;
        }
        _lastViewportResetSignature = viewportResetSignature;
        if (string.IsNullOrWhiteSpace(viewportResetSignature))
        {
            return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ChartControl != null)
            {
                ChartControl.ResetViewport();
            }
        }));
    }

    private static string BuildViewportResetSignature(MousePerformanceChartRenderFrame frame)
    {
        if (frame == null || !frame.IsAvailable)
        {
            return string.Empty;
        }
        return string.Join("|", new string[9]
        {
            ((int)frame.PlotType).ToString(CultureInfo.InvariantCulture),
            ((int)frame.TimeBasis).ToString(CultureInfo.InvariantCulture),
            frame.StartIndex.ToString(CultureInfo.InvariantCulture),
            frame.EndIndex.ToString(CultureInfo.InvariantCulture),
            frame.XMinimum.ToString("R", CultureInfo.InvariantCulture),
            frame.XMaximum.ToString("R", CultureInfo.InvariantCulture),
            frame.YMinimum.ToString("R", CultureInfo.InvariantCulture),
            frame.YMaximum.ToString("R", CultureInfo.InvariantCulture),
            frame.HasComparisonDatasets.ToString(CultureInfo.InvariantCulture)
        });
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private void SavePngButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MousePerformanceChartWindowViewModel { CanSavePng: not false }))
        {
            return;
        }
        LocalizationManager instance = LocalizationManager.Instance;
        instance.Initialize();
        SaveFileDialog saveFileDialog = new SaveFileDialog
        {
            Filter = instance.GetString("MousePerformance.Chart.Filter.Png"),
            FilterIndex = 1,
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (saveFileDialog.ShowDialog(this) == true)
        {
            try
            {
                ChartControl.ExportToPng(saveFileDialog.FileName, 1280, 800);
            }
            catch (Exception ex)
            {
                AppAlertDialog appAlertDialog = new AppAlertDialog(instance.GetString("MousePerformance.Chart.Export.GpuError.Title"), ex.Message, instance.GetString("Dialog.Common.Confirm"));
                appAlertDialog.Owner = this;
                appAlertDialog.ShowDialog();
            }
        }
    }

    private void ImportDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MousePerformanceChartWindowViewModel { CanImportComparisonSession: not false })
        {
            ImportComparisonRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MousePerformanceChartWindowViewModel { CanExportBaselineSession: not false } mousePerformanceChartWindowViewModel))
        {
            return;
        }
        MousePerformanceSessionArchive mousePerformanceSessionArchive = mousePerformanceChartWindowViewModel.CreateBaselineExportSession();
        if (mousePerformanceSessionArchive == null || !mousePerformanceSessionArchive.HasData)
        {
            return;
        }
        LocalizationManager instance = LocalizationManager.Instance;
        instance.Initialize();
        SaveFileDialog saveFileDialog = new SaveFileDialog
        {
            Filter = instance.GetString("MousePerformance.Exchange.Filter.Json"),
            FilterIndex = 1,
            AddExtension = true,
            DefaultExt = ".json",
            FileName = MousePerformanceExchangeService.BuildSuggestedFileName(mousePerformanceSessionArchive)
        };
        if (saveFileDialog.ShowDialog(this) == true)
        {
            try
            {
                MousePerformanceExchangeService.ExportSession(mousePerformanceSessionArchive, saveFileDialog.FileName);
            }
            catch (Exception ex)
            {
                AppAlertDialog appAlertDialog = new AppAlertDialog(instance.GetString("MousePerformance.Export.Error.Title"), instance.GetString("MousePerformance.Export.Error.Format", ex.Message), instance.GetString("Dialog.Common.Confirm"));
                appAlertDialog.Owner = this;
                appAlertDialog.ShowDialog();
            }
        }
    }

    private void RangeTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            CommitRangeInputs();
            ChartControl.Focus();
            e.Handled = true;
        }
    }

    private void RangeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitRangeInputs();
    }

    private void CommitRangeInputs()
    {
        if (base.DataContext is MousePerformanceChartWindowViewModel mousePerformanceChartWindowViewModel)
        {
            mousePerformanceChartWindowViewModel.CommitRangeInputs();
        }
    }

    private void ChartControl_VisibleGapCountChanged(object sender, EventArgs e)
    {
        if (base.DataContext is MousePerformanceChartWindowViewModel mousePerformanceChartWindowViewModel && ChartControl != null)
        {
            mousePerformanceChartWindowViewModel.UpdateVisibleGapMetrics(ChartControl.VisibleGapCount, ChartControl.VisibleGapAverageDurationMs);
        }
    }

    private void ChartControl_GpuRendererAvailabilityChanged(object sender, EventArgs e)
    {
        if (base.DataContext is MousePerformanceChartWindowViewModel mousePerformanceChartWindowViewModel && ChartControl != null)
        {
            mousePerformanceChartWindowViewModel.UpdateChartRendererAvailability(ChartControl.IsGpuRendererAvailable);
        }
    }
}








