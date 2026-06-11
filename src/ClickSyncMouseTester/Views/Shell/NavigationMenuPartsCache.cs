using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ClickSyncMouseTester.Views.Shell;

internal sealed class NavigationMenuPartsCache
{
    private readonly ItemsControl _itemsControl;
    private readonly List<Button> _buttons = new List<Button>();
    private readonly Dictionary<Button, NavigationMenuParts> _partsByButton = new Dictionary<Button, NavigationMenuParts>();

    private bool _partsInvalidated = true;

    public NavigationMenuPartsCache(ItemsControl itemsControl)
    {
        _itemsControl = itemsControl;
    }

    public void InvalidateParts()
    {
        _partsInvalidated = true;
    }

    public IReadOnlyList<NavigationMenuParts> Resolve(bool updateLayout)
    {
        if (_itemsControl == null)
        {
            return Array.Empty<NavigationMenuParts>();
        }

        if (updateLayout)
        {
            _itemsControl.UpdateLayout();
        }

        if (!_partsInvalidated && _partsByButton.Count > 0)
        {
            return BuildOrderedParts();
        }

        Rebuild();
        return BuildOrderedParts();
    }

    public NavigationMenuParts Resolve(Button button)
    {
        if (button == null)
        {
            return null;
        }

        if (_partsInvalidated || !_partsByButton.TryGetValue(button, out NavigationMenuParts parts))
        {
            parts = BuildParts(button);
            _partsByButton[button] = parts;
            if (!_buttons.Contains(button))
            {
                _buttons.Add(button);
            }
        }

        return parts;
    }

    private void Rebuild()
    {
        _buttons.Clear();
        _partsByButton.Clear();
        if (_itemsControl == null)
        {
            _partsInvalidated = false;
            return;
        }

        foreach (Button button in NavigationMotion.FindVisualChildren<Button>(_itemsControl))
        {
            _buttons.Add(button);
            _partsByButton[button] = BuildParts(button);
        }

        _buttons.Sort(CompareButtonOrder);
        _partsInvalidated = false;
    }

    private List<NavigationMenuParts> BuildOrderedParts()
    {
        List<NavigationMenuParts> parts = new List<NavigationMenuParts>(_buttons.Count);
        foreach (Button button in _buttons)
        {
            if (button != null && _partsByButton.TryGetValue(button, out NavigationMenuParts itemParts))
            {
                parts.Add(itemParts);
            }
        }

        return parts;
    }

    private int CompareButtonOrder(Button left, Button right)
    {
        object leftData = left?.CommandParameter ?? left?.DataContext;
        object rightData = right?.CommandParameter ?? right?.DataContext;
        int leftIndex = ResolveItemIndex(leftData);
        int rightIndex = ResolveItemIndex(rightData);
        if (leftIndex >= 0 && rightIndex >= 0 && leftIndex != rightIndex)
        {
            return leftIndex.CompareTo(rightIndex);
        }

        if (_itemsControl == null || left == null || right == null)
        {
            return 0;
        }

        Point leftOrigin = left.TranslatePoint(new Point(0.0, 0.0), _itemsControl);
        Point rightOrigin = right.TranslatePoint(new Point(0.0, 0.0), _itemsControl);
        return leftOrigin.Y.CompareTo(rightOrigin.Y);
    }

    private int ResolveItemIndex(object item)
    {
        if (_itemsControl?.Items == null || item == null)
        {
            return -1;
        }

        return _itemsControl.Items.IndexOf(item);
    }

    private NavigationMenuParts BuildParts(Button button)
    {
        if (button == null)
        {
            return null;
        }

        TextBlock indexTextBlock = NavigationMotion.FindNamedDescendant<TextBlock>(button, "MenuIndexTextBlock");
        TextBlock metaTextBlock = NavigationMotion.FindNamedDescendant<TextBlock>(button, "MenuMetaTextBlock");
        TextBlock titleTextBlock = NavigationMotion.FindNamedDescendant<TextBlock>(button, "MenuTitleTextBlock");
        List<FrameworkElement> textElements = new List<FrameworkElement>(3);
        if (indexTextBlock != null)
        {
            textElements.Add(indexTextBlock);
        }

        if (metaTextBlock != null)
        {
            textElements.Add(metaTextBlock);
        }

        if (titleTextBlock != null)
        {
            textElements.Add(titleTextBlock);
        }

        NavigationMenuParts parts = new NavigationMenuParts(
            button,
            NavigationMotion.FindNamedDescendant<FrameworkElement>(button, "MenuItemAnimatedContent"),
            textElements,
            NavigationMotion.FindNamedDescendant<Border>(button, "HoverSurface"));
        button.Unloaded -= OnButtonUnloaded;
        button.Unloaded += OnButtonUnloaded;
        return parts;
    }

    private void OnButtonUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Unloaded -= OnButtonUnloaded;
        }
        InvalidateParts();
    }
}

internal sealed class NavigationMenuParts
{
    public NavigationMenuParts(Button button, FrameworkElement animatedContent, List<FrameworkElement> animatedTextElements, Border hoverSurface)
    {
        Button = button;
        AnimatedContent = animatedContent;
        AnimatedTextElements = animatedTextElements ?? new List<FrameworkElement>();
        HoverSurface = hoverSurface;
    }

    public Button Button { get; }

    public FrameworkElement AnimatedContent { get; }

    public List<FrameworkElement> AnimatedTextElements { get; }

    public Border HoverSurface { get; }
}
