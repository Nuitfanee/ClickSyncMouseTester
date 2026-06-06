#define TRACE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClickSyncMouseTester.Views.Shell;

internal static class NavigationMotion
{
    internal static Task BeginDoubleAnimationAsync(
        DependencyObject target,
        DependencyProperty property,
        double fromValue,
        double toValue,
        Duration duration,
        IEasingFunction easingFunction,
        TimeSpan beginTime = default,
        Func<bool> shouldComplete = null)
    {
        if (target == null || property == null)
        {
            return Task.CompletedTask;
        }
        ClearAnimation(target, property);
        target.SetCurrentValue(property, fromValue);
        TimeSpan animationDuration = ResolveDuration(duration, 0.0);
        TimeSpan totalDuration = beginTime + animationDuration;
        if (totalDuration <= TimeSpan.Zero || AreClose(fromValue, toValue))
        {
            target.SetCurrentValue(property, toValue);
            return Task.CompletedTask;
        }
        DoubleAnimation doubleAnimation = new DoubleAnimation
        {
            From = fromValue,
            To = toValue,
            BeginTime = beginTime,
            Duration = new Duration(animationDuration),
            FillBehavior = FillBehavior.HoldEnd
        };
        if (easingFunction != null)
        {
            doubleAnimation.EasingFunction = easingFunction;
        }
        try
        {
            Task completionTask = CompleteDoubleAnimationAsync(target, property, doubleAnimation, toValue, totalDuration, shouldComplete);
            BeginAnimation(target, property, doubleAnimation);
            return completionTask;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Navigation double animation failed: {ex.Message}");
            ClearAnimation(target, property);
            target.SetCurrentValue(property, toValue);
            return Task.CompletedTask;
        }
    }

    internal static Task AnimateMenuTextEnterAsync(
        IReadOnlyList<IReadOnlyList<FrameworkElement>> textElementGroups,
        Duration duration,
        IEasingFunction easingFunction,
        double baseDelayMilliseconds,
        double staggerRangeMilliseconds,
        double staggerPower,
        Func<FrameworkElement, TranslateTransform> ensureTextTransform,
        Func<FrameworkElement, double> resolveHiddenOffset,
        Func<FrameworkElement, double> resolveVisibleOpacity,
        Func<bool> shouldComplete = null)
    {
        if (textElementGroups == null || textElementGroups.Count == 0)
        {
            return Task.CompletedTask;
        }
        List<Task> animationTasks = new List<Task>();
        for (int groupIndex = 0; groupIndex < textElementGroups.Count; groupIndex++)
        {
            IReadOnlyList<FrameworkElement> textElements = textElementGroups[groupIndex];
            if (textElements == null || textElements.Count == 0)
            {
                continue;
            }
            TimeSpan beginTime = ResolveMenuItemEnterDelay(groupIndex, textElementGroups.Count, baseDelayMilliseconds, staggerRangeMilliseconds, staggerPower);
            foreach (FrameworkElement textElement in textElements)
            {
                if (textElement == null)
                {
                    continue;
                }
                TranslateTransform textTransform = ensureTextTransform?.Invoke(textElement);
                if (textTransform == null)
                {
                    continue;
                }
                double hiddenOffset = resolveHiddenOffset != null ? resolveHiddenOffset(textElement) : 0.0;
                double visibleOpacity = resolveVisibleOpacity != null ? resolveVisibleOpacity(textElement) : 1.0;
                animationTasks.Add(BeginDoubleAnimationAsync(
                    textElement,
                    UIElement.OpacityProperty,
                    0.0,
                    visibleOpacity,
                    duration,
                    easingFunction,
                    beginTime,
                    shouldComplete));
                animationTasks.Add(BeginDoubleAnimationAsync(
                    textTransform,
                    TranslateTransform.YProperty,
                    hiddenOffset,
                    0.0,
                    duration,
                    easingFunction,
                    beginTime,
                    shouldComplete));
            }
        }
        return animationTasks.Count > 0 ? Task.WhenAll(animationTasks) : Task.CompletedTask;
    }

    internal static TimeSpan ResolveDuration(Duration duration, double fallbackMilliseconds)
    {
        return duration.HasTimeSpan ? duration.TimeSpan : TimeSpan.FromMilliseconds(fallbackMilliseconds);
    }

    internal static TimeSpan ResolveMenuItemEnterDelay(
        int itemIndex,
        int itemCount,
        double baseDelayMilliseconds,
        double staggerRangeMilliseconds,
        double staggerPower)
    {
        if (itemCount <= 1)
        {
            return TimeSpan.FromMilliseconds(Math.Max(0.0, baseDelayMilliseconds));
        }
        int clampedItemIndex = Math.Max(0, Math.Min(itemIndex, itemCount - 1));
        double reversePosition = (double)(itemCount - 1 - clampedItemIndex) / (itemCount - 1);
        double exponent = Math.Max(0.01, staggerPower);
        double easedStagger = 1.0 - Math.Pow(1.0 - reversePosition, exponent);
        return TimeSpan.FromMilliseconds(Math.Max(0.0, baseDelayMilliseconds + staggerRangeMilliseconds * easedStagger));
    }

    internal static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int childIndex = 0; childIndex < childrenCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);
            if (child is T matchingChild)
            {
                yield return matchingChild;
            }
            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    internal static T FindNamedDescendant<T>(DependencyObject root, string elementName) where T : FrameworkElement
    {
        if (root == null || string.IsNullOrWhiteSpace(elementName))
        {
            return null;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int childIndex = 0; childIndex < childrenCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);
            if (child is T matchingElement && string.Equals(matchingElement.Name, elementName, StringComparison.Ordinal))
            {
                return matchingElement;
            }
            T matchingDescendant = FindNamedDescendant<T>(child, elementName);
            if (matchingDescendant != null)
            {
                return matchingDescendant;
            }
        }
        return null;
    }

    private static async Task CompleteDoubleAnimationAsync(
        DependencyObject target,
        DependencyProperty property,
        DoubleAnimation doubleAnimation,
        double toValue,
        TimeSpan totalDuration,
        Func<bool> shouldComplete)
    {
        try
        {
            TaskCompletionSource<object> completionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            void CompleteAnimation()
            {
                doubleAnimation.Completed -= OnCompleted;
                if (shouldComplete != null && !shouldComplete())
                {
                    completionSource.TrySetResult(null);
                    return;
                }
                ClearAnimation(target, property);
                target.SetCurrentValue(property, toValue);
                completionSource.TrySetResult(null);
            }
            void OnCompleted(object sender, EventArgs e)
            {
                CompleteAnimation();
            }
            doubleAnimation.Completed += OnCompleted;
            Task completedTask = await Task.WhenAny(completionSource.Task, Task.Delay(totalDuration + TimeSpan.FromMilliseconds(80.0)));
            if (completedTask != completionSource.Task)
            {
                CompleteAnimation();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Navigation double animation completion failed: {ex.Message}");
            if (shouldComplete == null || shouldComplete())
            {
                ClearAnimation(target, property);
                target.SetCurrentValue(property, toValue);
            }
        }
    }

    private static void BeginAnimation(DependencyObject target, DependencyProperty property, AnimationTimeline animation)
    {
        if (target is UIElement uiElement)
        {
            uiElement.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        else if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static void ClearAnimation(DependencyObject target, DependencyProperty property)
    {
        if (target is UIElement uiElement)
        {
            uiElement.BeginAnimation(property, null);
        }
        else if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, null);
        }
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.001;
    }

}
