using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClickSyncMouseTester.Services;

namespace ClickSyncMouseTester.Views.Shell;

internal static class NavigationCurtainAnimator
{
    private static readonly ConditionalWeakTable<FrameworkElement, CurtainRevealParts> CurtainRevealPartsBySurface = new ConditionalWeakTable<FrameworkElement, CurtainRevealParts>();

    public static Task AnimateAsync(
        FrameworkElement curtainSurface,
        double ownerHeight,
        bool isOpening,
        Duration duration,
        IEasingFunction easingFunction,
        Func<bool> shouldComplete = null)
    {
        if (curtainSurface == null)
        {
            return Task.CompletedTask;
        }

        Window ownerWindow = Window.GetWindow(curtainSurface);
        IDisposable performanceScope = UiPerformanceProbe.BeginStage(isOpening ? "NavigationMotion.Curtain.Open" : "NavigationMotion.Curtain.Close", ownerWindow);
        double curtainHeight = Math.Max(curtainSurface.ActualHeight, Math.Max(ownerHeight, 1.0));
        CurtainRevealParts revealParts = ResolveCurtainRevealParts(curtainSurface);
        TranslateTransform revealTransform = revealParts.RevealTransform;
        TranslateTransform contentTransform = revealParts.ContentTransform;
        double currentVisibleHeight = ResolveVisibleHeightFromOffset(curtainHeight, revealTransform.Y);
        if (isOpening && currentVisibleHeight >= curtainHeight)
        {
            currentVisibleHeight = 0.0;
        }

        double startVisibleHeight = isOpening ? currentVisibleHeight : currentVisibleHeight > 0.0 ? currentVisibleHeight : curtainHeight;
        double targetVisibleHeight = isOpening ? curtainHeight : 0.0;
        double startOffsetY = ResolveRevealOffset(curtainHeight, startVisibleHeight);
        double targetOffsetY = ResolveRevealOffset(curtainHeight, targetVisibleHeight);
        curtainSurface.Visibility = Visibility.Visible;
        curtainSurface.IsHitTestVisible = isOpening;
        curtainSurface.Clip = null;
        curtainSurface.ClipToBounds = true;
        Task animationTask = NavigationMotion.BeginDoubleAnimationAsync(
            revealTransform,
            TranslateTransform.YProperty,
            startOffsetY,
            targetOffsetY,
            duration,
            easingFunction,
            default,
            shouldComplete);
        Task contentAnimationTask = NavigationMotion.BeginDoubleAnimationAsync(
            contentTransform,
            TranslateTransform.YProperty,
            -startOffsetY,
            -targetOffsetY,
            duration,
            easingFunction,
            default,
            shouldComplete);
        return CompleteWithScopeAsync(Task.WhenAll(animationTask, contentAnimationTask), performanceScope);
    }

    public static void SetReveal(FrameworkElement curtainSurface, double visibleHeight)
    {
        if (curtainSurface == null)
        {
            return;
        }

        double curtainHeight = Math.Max(curtainSurface.ActualHeight, 0.0);
        CurtainRevealParts revealParts = ResolveCurtainRevealParts(curtainSurface);
        TranslateTransform revealTransform = revealParts.RevealTransform;
        TranslateTransform contentTransform = revealParts.ContentTransform;
        double revealOffset = ResolveRevealOffset(curtainHeight, visibleHeight);
        revealTransform.BeginAnimation(TranslateTransform.YProperty, null);
        contentTransform.BeginAnimation(TranslateTransform.YProperty, null);
        revealTransform.Y = revealOffset;
        contentTransform.Y = -revealOffset;
        curtainSurface.Clip = null;
        curtainSurface.ClipToBounds = true;
    }

    private static CurtainRevealParts ResolveCurtainRevealParts(FrameworkElement curtainSurface)
    {
        return CurtainRevealPartsBySurface.GetValue(curtainSurface, BuildCurtainRevealParts);
    }

    private static CurtainRevealParts BuildCurtainRevealParts(FrameworkElement curtainSurface)
    {
        FrameworkElement revealHost = ResolveCurtainRevealHost(curtainSurface);
        FrameworkElement revealContent = ResolveCurtainRevealContent(revealHost);
        return new CurtainRevealParts(
            EnsureRevealTransform(revealHost),
            ReferenceEquals(revealHost, revealContent) ? new TranslateTransform() : EnsureRevealTransform(revealContent));
    }

    private static FrameworkElement ResolveCurtainRevealHost(FrameworkElement curtainSurface)
    {
        FrameworkElement revealHost = NavigationMotion.FindNamedDescendant<FrameworkElement>(curtainSurface, "NavigationCurtainRevealHost");
        return revealHost ?? curtainSurface;
    }

    private static FrameworkElement ResolveCurtainRevealContent(FrameworkElement revealHost)
    {
        FrameworkElement revealContent = NavigationMotion.FindNamedDescendant<FrameworkElement>(revealHost, "NavigationCurtainContent");
        return revealContent ?? revealHost;
    }

    private static TranslateTransform EnsureRevealTransform(FrameworkElement revealHost)
    {
        if (revealHost == null)
        {
            return new TranslateTransform();
        }

        if (revealHost.RenderTransform is TranslateTransform translateTransform && !translateTransform.IsFrozen)
        {
            return translateTransform;
        }

        double offsetX = 0.0;
        double offsetY = 0.0;
        if (revealHost.RenderTransform is TranslateTransform existingTranslateTransform)
        {
            offsetX = existingTranslateTransform.X;
            offsetY = existingTranslateTransform.Y;
        }

        translateTransform = new TranslateTransform(offsetX, offsetY);
        revealHost.RenderTransform = translateTransform;
        return translateTransform;
    }

    private static double ResolveRevealOffset(double curtainHeight, double visibleHeight)
    {
        curtainHeight = Math.Max(curtainHeight, 0.0);
        if (curtainHeight <= 0.0)
        {
            return 0.0;
        }

        double clampedVisibleHeight = Math.Max(0.0, Math.Min(visibleHeight, curtainHeight));
        return clampedVisibleHeight - curtainHeight;
    }

    private static double ResolveVisibleHeightFromOffset(double curtainHeight, double offsetY)
    {
        curtainHeight = Math.Max(curtainHeight, 0.0);
        if (curtainHeight <= 0.0)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(curtainHeight + offsetY, curtainHeight));
    }

    private static async Task CompleteWithScopeAsync(Task task, IDisposable scope)
    {
        try
        {
            await task;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private sealed class CurtainRevealParts
    {
        public CurtainRevealParts(TranslateTransform revealTransform, TranslateTransform contentTransform)
        {
            RevealTransform = revealTransform;
            ContentTransform = contentTransform;
        }

        public TranslateTransform RevealTransform { get; }

        public TranslateTransform ContentTransform { get; }
    }
}
