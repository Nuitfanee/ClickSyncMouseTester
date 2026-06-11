using ClickSyncMouseTester.ViewModels.Pages;
using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClickSyncMouseTester.Views.Pages;

[SupportedOSPlatform("windows")]
public partial class KeyDetectionPage : UserControl
{
    private Window _hostWindow;

    private int _activeTextEntryCount;

    public KeyDetectionPage()
    {
        InitializeComponent();
    }

    private void KeyDetectionPage_Loaded(object sender, RoutedEventArgs e)
    {
        AttachWindowHandlers();
        SyncViewModelState();
    }

    private void KeyDetectionPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            keyDetectionPageViewModel.SetPageActive(isActive: false);
            keyDetectionPageViewModel.SetWindowActive(isActive: false);
            keyDetectionPageViewModel.SetTextEntryActive(isActive: false);
        }
        _activeTextEntryCount = 0;
        DetachWindowHandlers();
    }

    private void KeyDetectionPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncViewModelState();
    }

    private void KeyDetectionPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncViewModelState();
    }

    private void AttachWindowHandlers()
    {
        Window window = Window.GetWindow(this);
        if (!ReferenceEquals(_hostWindow, window))
        {
            DetachWindowHandlers();
            _hostWindow = window;
            if (_hostWindow != null)
            {
                _hostWindow.Activated += HostWindow_Activated;
                _hostWindow.Deactivated += HostWindow_Deactivated;
            }
        }
    }

    private void DetachWindowHandlers()
    {
        if (_hostWindow != null)
        {
            _hostWindow.Activated -= HostWindow_Activated;
            _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow = null;
        }
    }

    private void HostWindow_Activated(object sender, EventArgs e)
    {
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            keyDetectionPageViewModel.SetWindowActive(isActive: true);
        }
    }

    private void HostWindow_Deactivated(object sender, EventArgs e)
    {
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            keyDetectionPageViewModel.SetWindowActive(isActive: false);
        }
    }

    private void SyncViewModelState()
    {
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            AttachWindowHandlers();
            keyDetectionPageViewModel.SetPageActive(base.IsLoaded && base.IsVisible);
            keyDetectionPageViewModel.SetWindowActive(_hostWindow == null || _hostWindow.IsActive);
            keyDetectionPageViewModel.SetTextEntryActive(_activeTextEntryCount > 0);
        }
    }

    private void RootScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void RootScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!(Keyboard.FocusedElement is TextBox textBox) || !IsThresholdTextBox(textBox))
        {
            return;
        }
        object originalSource = e.OriginalSource;
        DependencyObject sourceElement = originalSource as DependencyObject;
        if (sourceElement != null && FindAncestorOrSelf<TextBox>(sourceElement) == null)
        {
            CommitThresholdTextBox(textBox);
            if (FindAncestorOrSelf<Button>(sourceElement) == null)
            {
                FocusThresholdFallbackSurface();
            }
        }
    }

    private void ThresholdTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            CommitThresholdTextBox(sender as TextBox);
            FocusThresholdFallbackSurface();
            e.Handled = true;
        }
    }

    private void ThresholdTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitThresholdTextBox(sender as TextBox);
    }

    private void ThresholdTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _activeTextEntryCount++;
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            keyDetectionPageViewModel.SetTextEntryActive(isActive: true);
        }
    }

    private void ThresholdTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _activeTextEntryCount = Math.Max(0, _activeTextEntryCount - 1);
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            keyDetectionPageViewModel.SetTextEntryActive(_activeTextEntryCount > 0);
        }
    }

    private void CommitThresholdTextBox(TextBox textBox)
    {
        if (textBox == null)
        {
            return;
        }
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (base.DataContext is KeyDetectionPageViewModel keyDetectionPageViewModel)
        {
            if (ReferenceEquals(textBox, MouseThresholdTextBox))
            {
                keyDetectionPageViewModel.CommitMouseDoubleClickThresholdInput();
            }
            else if (ReferenceEquals(textBox, CustomKeyThresholdTextBox))
            {
                keyDetectionPageViewModel.CommitKeyDoubleClickThresholdInput();
            }
        }
    }

    private void FocusThresholdFallbackSurface()
    {
        RootScrollViewer.Focus();
        Keyboard.Focus(RootScrollViewer);
    }

    private static bool IsThresholdTextBox(TextBox textBox)
    {
        if (textBox == null)
        {
            return false;
        }
        return string.Equals(textBox.Name, "MouseThresholdTextBox", StringComparison.Ordinal) || string.Equals(textBox.Name, "CustomKeyThresholdTextBox", StringComparison.Ordinal);
    }

    private static T FindAncestorOrSelf<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject currentElement = source; currentElement != null; currentElement = GetVisualOrContentParent(currentElement))
        {
            if (currentElement is T result)
            {
                return result;
            }
        }
        return null;
    }

    private static DependencyObject GetVisualOrContentParent(DependencyObject element)
    {
        if (element is Visual visual)
        {
            return VisualTreeHelper.GetParent(visual);
        }

        if (element is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        return null;
    }

}






