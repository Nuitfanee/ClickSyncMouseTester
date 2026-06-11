#define TRACE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClickSyncMouseTester.Controls.Brand;

namespace ClickSyncMouseTester.Controls;

[SupportedOSPlatform("windows")]
public sealed class NavigationVerticalLogoControl : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(NavigationVerticalLogoControl),
        new FrameworkPropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty LogoBrushProperty = DependencyProperty.Register(
        nameof(LogoBrush),
        typeof(Brush),
        typeof(NavigationVerticalLogoControl),
        new FrameworkPropertyMetadata(CreateFrozenBrush(Color.FromRgb(0x2A, 0x00, 0x47)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LogoDefinitionProperty = DependencyProperty.Register(
        nameof(LogoDefinition),
        typeof(BrandLogoDefinition),
        typeof(NavigationVerticalLogoControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLogoDefinitionChanged));

    private const double FallbackIntroOffsetY = -170.0;
    private const double RepelOffsetRatio = 0.28;
    private const double IntroOffsetRatio = 1.2;
    private const double FallbackIntroBaseDelaySeconds = 0.42;
    private const double FallbackIntroStaggerRangeSeconds = 0.30;
    private const double FallbackIntroStaggerPower = 1.5;
    private const double FallbackInteractionReadySeconds = 1.35;
    private const double FallbackSpringStiffness = 100.0;
    private const double FallbackSpringDamping = 10.0;
    private const double SpringMass = 1.0;
    private const double SpringTolerance = 0.002;

    private static readonly BrandLogoDefinition FallbackLogoDefinition = BrandLogoDefaults.CreateDefaultDefinition();
    private static readonly BrandLogoGeometryProviderRegistry GeometryProviders = BrandLogoGeometryProviderRegistry.CreateDefault();
    private static readonly BrandLogoLayoutEngine LayoutEngine = new BrandLogoLayoutEngine();
    private static readonly BrandLogoLayoutOptions LayoutOptions = new BrandLogoLayoutOptions();

    private readonly List<LogoElementSpring> _elementSprings = new List<LogoElementSpring>();
    private readonly List<MatrixTransform> _elementTransforms = new List<MatrixTransform>();

    private IReadOnlyList<BrandLogoGeometryElement> _geometryElements = Array.Empty<BrandLogoGeometryElement>();
    private BrandLogoLayout _layout = BrandLogoLayout.Empty;
    private BrandLogoDefinition _cachedDefinition;
    private int _cachedDefinitionRevision;
    private Size _layoutContainerSize;
    private bool _isGeometryDirty = true;
    private bool _isLayoutDirty = true;
    private bool _isRendering;
    private bool _hasPointer;
    private bool _isInteractionReady;
    private Point _lastPointer;
    private long _lastRenderTicks;
    private double _geometryPixelsPerDip;
    private Stopwatch _introStopwatch;

    public NavigationVerticalLogoControl()
    {
        Focusable = false;
        ClipToBounds = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseMove += OnLogoMouseMove;
        MouseLeave += OnLogoMouseLeave;
        MouseDown += OnLogoMouseDown;
        MouseUp += OnLogoMouseUp;
        SizeChanged += OnLogoSizeChanged;
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush LogoBrush
    {
        get => (Brush)GetValue(LogoBrushProperty);
        set => SetValue(LogoBrushProperty, value);
    }

    public BrandLogoDefinition LogoDefinition
    {
        get => (BrandLogoDefinition)GetValue(LogoDefinitionProperty);
        set => SetValue(LogoDefinitionProperty, value);
    }

    public void Preheat()
    {
        PreheatLogoGeometry();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0.0 || ActualHeight <= 0.0)
        {
            return;
        }

        EnsureCurrentLogoLayout();
        IReadOnlyList<BrandLogoLayoutElement> layoutElements = _layout.Elements;
        for (int index = 0; index < layoutElements.Count && index < _elementSprings.Count; index++)
        {
            DrawLogoElement(drawingContext, layoutElements[index], index);
        }
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        Point point = hitTestParameters.HitPoint;
        if (IsActive)
        {
            EnsureCurrentLogoLayout();
            if (!_layout.HitBounds.IsEmpty && _layout.HitBounds.Contains(point))
            {
                return new PointHitTestResult(this, point);
            }
        }
        return null;
    }

    private static void OnIsActiveChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        NavigationVerticalLogoControl control = dependencyObject as NavigationVerticalLogoControl;
        if (control == null)
        {
            return;
        }
        if (e.NewValue is bool isActive && isActive)
        {
            control.BeginIntro();
            return;
        }
        control.ReleaseInteraction();
    }

    private static void OnLogoDefinitionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        NavigationVerticalLogoControl control = dependencyObject as NavigationVerticalLogoControl;
        if (control == null)
        {
            return;
        }
        control.InvalidateLogoCache();
        if (control.IsActive)
        {
            control.BeginIntro();
        }
        else
        {
            control.SetRestingState();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PreheatLogoGeometry();
        if (IsActive)
        {
            BeginIntro();
        }
        else
        {
            SetRestingState();
        }
    }

    private void PreheatLogoGeometry()
    {
        EnsureCurrentLogoLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void OnLogoMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsActive || !_isInteractionReady)
        {
            return;
        }
        _hasPointer = true;
        _lastPointer = e.GetPosition(this);
        UpdatePointerTargets();
        EnsureRendering();
    }

    private void OnLogoMouseLeave(object sender, MouseEventArgs e)
    {
        ReleaseInteraction();
    }

    private void OnLogoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive || !_isInteractionReady)
        {
            return;
        }
        _hasPointer = true;
        _lastPointer = e.GetPosition(this);
        UpdatePointerTargets();
        EnsureRendering();
    }

    private void OnLogoMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive || !_isInteractionReady)
        {
            return;
        }
        _lastPointer = e.GetPosition(this);
        UpdatePointerTargets();
    }

    private void OnLogoSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _isLayoutDirty = true;
        if (_hasPointer)
        {
            UpdatePointerTargets();
        }
        InvalidateVisual();
    }

    private void BeginIntro()
    {
        _hasPointer = false;
        _isInteractionReady = false;
        _introStopwatch = Stopwatch.StartNew();
        EnsureCurrentLogoLayout();
        EnsureElementStateCount(_layout.Elements.Count);
        for (int index = 0; index < _elementSprings.Count; index++)
        {
            LogoElementSpring element = _elementSprings[index];
            double introOffsetY = ResolveIntroOffsetY(index);
            element.Reset(0.0, introOffsetY, 0.96);
            element.IntroOffsetY = introOffsetY;
            element.TargetReleaseSeconds = ResolveIntroReleaseSeconds(index, _elementSprings.Count);
        }
        EnsureRendering();
        InvalidateVisual();
    }

    private void SetRestingState()
    {
        _hasPointer = false;
        _isInteractionReady = false;
        _introStopwatch = null;
        EnsureCurrentLogoLayout();
        EnsureElementStateCount(_layout.Elements.Count);
        for (int index = 0; index < _elementSprings.Count; index++)
        {
            LogoElementSpring element = _elementSprings[index];
            element.Reset(0.0, 0.0, 1.0);
            element.IntroOffsetY = ResolveIntroOffsetY(index);
            element.TargetReleaseSeconds = 0.0;
        }
        InvalidateVisual();
    }

    private void ReleaseInteraction()
    {
        _hasPointer = false;
        for (int index = 0; index < _elementSprings.Count; index++)
        {
            _elementSprings[index].TargetX = 0.0;
            _elementSprings[index].TargetY = 0.0;
            _elementSprings[index].TargetScale = 1.0;
        }
        EnsureRendering();
    }

    private void UpdatePointerTargets()
    {
        if (!_hasPointer || ActualWidth <= 0.0 || ActualHeight <= 0.0)
        {
            return;
        }
        EnsureCurrentLogoLayout();
        IReadOnlyList<BrandLogoLayoutElement> layoutElements = _layout.Elements;
        for (int index = 0; index < layoutElements.Count && index < _elementSprings.Count; index++)
        {
            Point center = layoutElements[index].MotionCenter;
            LogoElementSpring element = _elementSprings[index];
            element.TargetX = 0.0;
            element.TargetY = _lastPointer.Y > center.Y ? -ResolveRepelOffsetY(index) : ResolveRepelOffsetY(index);
            element.TargetScale = 1.0;
        }
    }

    private void EnsureCurrentLogoLayout()
    {
        if (ActualWidth <= 0.0 || ActualHeight <= 0.0)
        {
            return;
        }
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        BrandLogoDefinition definition = ResolveLogoDefinition();
        if (_isGeometryDirty
            || !ReferenceEquals(definition, _cachedDefinition)
            || _cachedDefinitionRevision != definition.Revision
            || !AreClose(_geometryPixelsPerDip, pixelsPerDip))
        {
            _geometryElements = GeometryProviders.Create(definition, pixelsPerDip);
            if (_geometryElements.Count == 0 && !ReferenceEquals(definition, FallbackLogoDefinition))
            {
                _geometryElements = GeometryProviders.Create(FallbackLogoDefinition, pixelsPerDip);
            }
            _cachedDefinition = definition;
            _cachedDefinitionRevision = definition.Revision;
            _geometryPixelsPerDip = pixelsPerDip;
            _isGeometryDirty = false;
            _isLayoutDirty = true;
        }

        Size containerSize = new Size(ActualWidth, ActualHeight);
        if (_isLayoutDirty || !AreClose(_layoutContainerSize, containerSize))
        {
            _layout = LayoutEngine.Arrange(_geometryElements, containerSize, LayoutOptions);
            _layoutContainerSize = containerSize;
            _isLayoutDirty = false;
            EnsureElementStateCount(_layout.Elements.Count);
        }
    }

    private BrandLogoDefinition ResolveLogoDefinition()
    {
        BrandLogoDefinition definition = LogoDefinition;
        if (definition != null)
        {
            return definition;
        }
        object resource = TryFindApplicationResource("NavigationBrandLogoDefinition");
        if (resource is BrandLogoDefinition resourceDefinition)
        {
            return resourceDefinition;
        }
        return FallbackLogoDefinition;
    }

    private void EnsureElementStateCount(int elementCount)
    {
        int resolvedElementCount = Math.Max(0, elementCount);
        while (_elementSprings.Count < resolvedElementCount)
        {
            LogoElementSpring element = new LogoElementSpring();
            int elementIndex = _elementSprings.Count;
            if (IsActive && _introStopwatch != null && !_isInteractionReady)
            {
                double introOffsetY = ResolveIntroOffsetY(elementIndex);
                element.Reset(0.0, introOffsetY, 0.96);
                element.IntroOffsetY = introOffsetY;
            }
            else
            {
                element.Reset(0.0, 0.0, 1.0);
                element.IntroOffsetY = ResolveIntroOffsetY(elementIndex);
            }
            _elementSprings.Add(element);
            _elementTransforms.Add(new MatrixTransform());
        }
        while (_elementSprings.Count > resolvedElementCount)
        {
            int lastIndex = _elementSprings.Count - 1;
            _elementSprings.RemoveAt(lastIndex);
            _elementTransforms.RemoveAt(lastIndex);
        }
        UpdateElementReleaseTimes();
    }

    private void UpdateElementReleaseTimes()
    {
        for (int index = 0; index < _elementSprings.Count; index++)
        {
            _elementSprings[index].TargetReleaseSeconds = ResolveIntroReleaseSeconds(index, _elementSprings.Count);
        }
    }

    private void InvalidateLogoCache()
    {
        _isGeometryDirty = true;
        _isLayoutDirty = true;
        _geometryElements = Array.Empty<BrandLogoGeometryElement>();
        _layout = BrandLogoLayout.Empty;
        _cachedDefinition = null;
        _cachedDefinitionRevision = 0;
        _geometryPixelsPerDip = 0.0;
        InvalidateVisual();
    }

    private void EnsureRendering()
    {
        if (_isRendering || !IsLoaded)
        {
            return;
        }
        _isRendering = true;
        _lastRenderTicks = 0L;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_isRendering)
        {
            return;
        }
        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
        _lastRenderTicks = 0L;
    }

    private void OnRendering(object sender, EventArgs e)
    {
        EnsureCurrentLogoLayout();
        long nowTicks = Stopwatch.GetTimestamp();
        double deltaSeconds = _lastRenderTicks > 0L
            ? Math.Min((double)(nowTicks - _lastRenderTicks) / Stopwatch.Frequency, 1.0 / 15.0)
            : 1.0 / 60.0;
        _lastRenderTicks = nowTicks;

        double elapsedSeconds = _introStopwatch != null ? _introStopwatch.Elapsed.TotalSeconds : double.PositiveInfinity;
        if (!_isInteractionReady && elapsedSeconds >= ResolveInteractionReadySeconds())
        {
            _isInteractionReady = true;
            if (_hasPointer)
            {
                UpdatePointerTargets();
            }
        }

        bool anyMoving = false;
        double springStiffness = ResolveSpringStiffness();
        double springDamping = ResolveSpringDamping();
        for (int index = 0; index < _elementSprings.Count; index++)
        {
            LogoElementSpring element = _elementSprings[index];
            if (_introStopwatch != null && elapsedSeconds >= element.TargetReleaseSeconds && element.TargetY <= element.IntroOffsetY * 0.5)
            {
                element.TargetY = 0.0;
                element.TargetScale = 1.0;
            }
            element.Step(deltaSeconds, springStiffness, springDamping);
            anyMoving |= !element.IsSettled;
        }

        InvalidateVisual();
        if (!IsActive && !anyMoving)
        {
            StopRendering();
        }
        else if (IsActive && !_hasPointer && _isInteractionReady && !anyMoving)
        {
            StopRendering();
        }
    }

    private void DrawLogoElement(DrawingContext drawingContext, BrandLogoLayoutElement layoutElement, int index)
    {
        if (layoutElement == null || index < 0 || index >= _elementSprings.Count || index >= _elementTransforms.Count)
        {
            return;
        }
        LogoElementSpring state = _elementSprings[index];
        Point center = layoutElement.MotionCenter;
        Matrix motionMatrix = Matrix.Identity;
        motionMatrix.Translate(-center.X, -center.Y);
        motionMatrix.Scale(state.Scale, state.Scale);
        motionMatrix.Translate(center.X + state.X, center.Y + state.Y);

        Matrix transformMatrix = layoutElement.GeometryToLayoutMatrix;
        transformMatrix.Append(motionMatrix);
        MatrixTransform transform = _elementTransforms[index];
        transform.Matrix = transformMatrix;
        drawingContext.PushTransform(transform);
        drawingContext.DrawGeometry(LogoBrush, null, layoutElement.Geometry);
        drawingContext.Pop();
    }

    private double ResolveIntroOffsetY(int index)
    {
        if (index >= 0 && index < _layout.Elements.Count)
        {
            return -Math.Max(32.0, _layout.Elements[index].SlotBounds.Height * IntroOffsetRatio);
        }
        return FallbackIntroOffsetY;
    }

    private double ResolveRepelOffsetY(int index)
    {
        if (index >= 0 && index < _layout.Elements.Count)
        {
            return Math.Max(6.0, _layout.Elements[index].SlotBounds.Height * RepelOffsetRatio);
        }
        return 40.0;
    }

    private static double ResolveIntroReleaseSeconds(int index, int elementCount)
    {
        int count = Math.Max(1, elementCount);
        if (count <= 1)
        {
            return ResolveBrandMotionDouble("MotionNavigationBrandIntroBaseDelaySeconds", FallbackIntroBaseDelaySeconds);
        }
        int clampedIndex = Math.Max(0, Math.Min(index, count - 1));
        double reversePosition = (double)(count - 1 - clampedIndex) / (count - 1);
        double staggerPower = ResolveBrandMotionDouble("MotionNavigationBrandIntroStaggerPower", FallbackIntroStaggerPower);
        double easedStagger = 1.0 - Math.Pow(1.0 - reversePosition, staggerPower);
        return ResolveBrandMotionDouble("MotionNavigationBrandIntroBaseDelaySeconds", FallbackIntroBaseDelaySeconds)
            + ResolveBrandMotionDouble("MotionNavigationBrandIntroStaggerRangeSeconds", FallbackIntroStaggerRangeSeconds) * easedStagger;
    }

    private static double ResolveInteractionReadySeconds()
    {
        return ResolveBrandMotionDouble("MotionNavigationBrandInteractionReadySeconds", FallbackInteractionReadySeconds);
    }

    private static double ResolveSpringStiffness()
    {
        return ResolveBrandMotionDouble("MotionNavigationBrandSpringStiffness", FallbackSpringStiffness);
    }

    private static double ResolveSpringDamping()
    {
        return ResolveBrandMotionDouble("MotionNavigationBrandSpringDamping", FallbackSpringDamping);
    }

    private static double ResolveBrandMotionDouble(string resourceKey, double fallback)
    {
        object resource = TryFindApplicationResource(resourceKey);
        if (resource is double value)
        {
            return value;
        }
        if (resource is int integer)
        {
            return integer;
        }
        return fallback;
    }

    private static object TryFindApplicationResource(string resourceKey)
    {
        if (System.Windows.Application.Current == null || string.IsNullOrWhiteSpace(resourceKey))
        {
            return null;
        }
        return System.Windows.Application.Current.TryFindResource(resourceKey);
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.001;
    }

    private static bool AreClose(Size left, Size right)
    {
        return AreClose(left.Width, right.Width) && AreClose(left.Height, right.Height);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class LogoElementSpring
    {
        public double X { get; private set; }

        public double Y { get; private set; }

        public double Scale { get; private set; }

        public double TargetX { get; set; }

        public double TargetY { get; set; }

        public double TargetScale { get; set; }

        public double TargetReleaseSeconds { get; set; }

        public double IntroOffsetY { get; set; } = FallbackIntroOffsetY;

        public bool IsSettled =>
            IsClose(X, TargetX)
            && IsClose(Y, TargetY)
            && IsClose(Scale, TargetScale)
            && IsClose(_velocityX, 0.0)
            && IsClose(_velocityY, 0.0)
            && IsClose(_velocityScale, 0.0);

        private double _velocityX;

        private double _velocityY;

        private double _velocityScale;

        public void Reset(double x, double y, double scale)
        {
            X = x;
            Y = y;
            Scale = scale;
            TargetX = x;
            TargetY = y;
            TargetScale = scale;
            _velocityX = 0.0;
            _velocityY = 0.0;
            _velocityScale = 0.0;
        }

        public void Step(double deltaSeconds, double stiffness, double damping)
        {
            X = StepValue(X, TargetX, ref _velocityX, deltaSeconds, stiffness, damping);
            Y = StepValue(Y, TargetY, ref _velocityY, deltaSeconds, stiffness, damping);
            Scale = StepValue(Scale, TargetScale, ref _velocityScale, deltaSeconds, stiffness, damping);
        }

        private static double StepValue(double value, double target, ref double velocity, double deltaSeconds, double stiffness, double damping)
        {
            velocity += ((-stiffness * (value - target)) - damping * velocity) / SpringMass * deltaSeconds;
            value += velocity * deltaSeconds;
            if (Math.Abs(velocity) < SpringTolerance && Math.Abs(value - target) < SpringTolerance)
            {
                value = target;
                velocity = 0.0;
            }
            return value;
        }

        private static bool IsClose(double left, double right)
        {
            return Math.Abs(left - right) < SpringTolerance;
        }
    }
}
