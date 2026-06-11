using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Controls;

public static class ManualComboBoxSelection
{
    public static readonly DependencyProperty CommitCommandProperty =
        DependencyProperty.RegisterAttached(
            "CommitCommand",
            typeof(ICommand),
            typeof(ManualComboBoxSelection),
            new PropertyMetadata(null, OnCommitCommandChanged));

    private static readonly DependencyProperty IsUserSelectionPendingProperty =
        DependencyProperty.RegisterAttached(
            "IsUserSelectionPending",
            typeof(bool),
            typeof(ManualComboBoxSelection),
            new PropertyMetadata(false));

    public static ICommand GetCommitCommand(DependencyObject element)
    {
        return (ICommand)element.GetValue(CommitCommandProperty);
    }

    public static void SetCommitCommand(DependencyObject element, ICommand value)
    {
        element.SetValue(CommitCommandProperty, value);
    }

    private static bool GetIsUserSelectionPending(DependencyObject element)
    {
        return (bool)element.GetValue(IsUserSelectionPendingProperty);
    }

    private static void SetIsUserSelectionPending(DependencyObject element, bool value)
    {
        element.SetValue(IsUserSelectionPendingProperty, value);
    }

    private static void OnCommitCommandChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox comboBox)
        {
            return;
        }

        if (e.OldValue != null)
        {
            comboBox.DropDownOpened -= OnDropDownOpened;
            comboBox.DropDownClosed -= OnDropDownClosed;
            comboBox.PreviewKeyDown -= OnPreviewKeyDown;
            comboBox.SelectionChanged -= OnSelectionChanged;
        }

        if (e.NewValue != null)
        {
            comboBox.DropDownOpened += OnDropDownOpened;
            comboBox.DropDownClosed += OnDropDownClosed;
            comboBox.PreviewKeyDown += OnPreviewKeyDown;
            comboBox.SelectionChanged += OnSelectionChanged;
        }

        SetIsUserSelectionPending(comboBox, false);
    }

    private static void OnDropDownOpened(object sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            SetIsUserSelectionPending(comboBox, true);
        }
    }

    private static void OnDropDownClosed(object sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            SetIsUserSelectionPending(comboBox, false);
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ComboBox comboBox && IsSelectionKey(e.Key))
        {
            SetIsUserSelectionPending(comboBox, true);
            if (!comboBox.IsDropDownOpen)
            {
                comboBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!comboBox.IsDropDownOpen)
                    {
                        SetIsUserSelectionPending(comboBox, false);
                    }
                }), DispatcherPriority.Background);
            }
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || !IsManualSelection(comboBox))
        {
            return;
        }

        SetIsUserSelectionPending(comboBox, false);
        comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();

        ICommand command = GetCommitCommand(comboBox);
        object parameter = comboBox.SelectedItem;
        if (command != null && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    private static bool IsManualSelection(ComboBox comboBox)
    {
        return GetIsUserSelectionPending(comboBox);
    }

    private static bool IsSelectionKey(Key key)
    {
        return key == Key.Up
            || key == Key.Down
            || key == Key.Left
            || key == Key.Right
            || key == Key.Home
            || key == Key.End
            || key == Key.PageUp
            || key == Key.PageDown
            || key == Key.Return
            || key == Key.Space;
    }
}
