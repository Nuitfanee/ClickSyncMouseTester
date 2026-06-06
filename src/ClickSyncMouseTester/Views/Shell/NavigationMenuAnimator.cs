using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace ClickSyncMouseTester.Views.Shell;

internal static class NavigationMenuAnimator
{
    public static void AnimateOpacity(
        UIElement target,
        double targetOpacity,
        Duration duration,
        IEasingFunction easingFunction)
    {
        if (target == null)
        {
            return;
        }

        target.BeginAnimation(UIElement.OpacityProperty, null);
        TimeSpan durationTimeSpan = NavigationMotion.ResolveDuration(duration, 0.0);
        if (durationTimeSpan <= TimeSpan.Zero)
        {
            target.Opacity = targetOpacity;
            return;
        }

        DoubleAnimation animation = new DoubleAnimation
        {
            From = target.Opacity,
            To = targetOpacity,
            Duration = new Duration(durationTimeSpan),
            FillBehavior = FillBehavior.HoldEnd
        };
        if (easingFunction != null)
        {
            animation.EasingFunction = easingFunction;
        }

        target.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
