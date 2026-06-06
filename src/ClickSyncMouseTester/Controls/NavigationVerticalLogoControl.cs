#define TRACE
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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

    private const int LetterCount = 4;
    private const double DesignWidth = 180.0;
    private const double LetterHeight = 140.0;
    private const double LetterGap = 12.0;
    private const double DesignHeight = LetterCount * LetterHeight + (LetterCount - 1) * LetterGap;
    private const double LetterFontSize = 220.0;
    private const double LetterMaxWidth = 188.0;
    private const double LetterMaxHeight = 148.0;
    private const double LayoutWidthFill = 0.9;
    private const double LayoutHeightFill = 0.98;
    private const double MotionSafeTop = 44.0;
    private const double MotionSafeBottom = 68.0;
    private const double RestingDownShift = 10.0;
    private const double IntroOffsetY = -170.0;
    private const double RepelOffsetY = 40.0;
    private const double LogoHitPadding = 10.0;
    private const double FallbackIntroBaseDelaySeconds = 0.42;
    private const double FallbackIntroStaggerRangeSeconds = 0.30;
    private const double FallbackIntroStaggerPower = 1.5;
    private const double FallbackInteractionReadySeconds = 1.35;
    private const double FallbackSpringStiffness = 100.0;
    private const double FallbackSpringDamping = 10.0;
    private const double SpringMass = 1.0;
    private const double SpringTolerance = 0.002;

    private static readonly Uri ApplicationFontBaseUri = new Uri("pack://application:,,,/");
    private static readonly string[] LetterTexts = { "N", "U", "I", "T" };
    private static readonly Typeface LogoTypeface = new Typeface(
        new FontFamily(ApplicationFontBaseUri, "./Assets/Fonts/Bundled/moderne-3d-schwabacher/#Moderne 3D Schwabacher"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);
    private static readonly Typeface FallbackLogoTypeface = new Typeface(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    private readonly LetterSpring[] _letters = new LetterSpring[LetterCount];
    private readonly Geometry[] _letterGeometries = new Geometry[LetterCount];
    private readonly MatrixTransform _layoutOffsetTransform = new MatrixTransform();
    private readonly MatrixTransform _layoutScaleTransform = new MatrixTransform();
    private readonly MatrixTransform[] _letterMotionTransforms = new MatrixTransform[LetterCount];
    private readonly MatrixTransform[] _letterBaseTransforms = new MatrixTransform[LetterCount];

    private bool _isRendering;
    private bool _hasPointer;
    private bool _isInteractionReady;
    private Point _lastPointer;
    private long _lastRenderTicks;
    private double _letterGeometryPixelsPerDip;
    private Stopwatch _introStopwatch;

    public NavigationVerticalLogoControl()
    {
        Focusable = false;
        ClipToBounds = true;
        for (int index = 0; index < _letters.Length; index++)
        {
            _letters[index] = new LetterSpring();
            _letterMotionTransforms[index] = new MatrixTransform();
            _letterBaseTransforms[index] = new MatrixTransform();
        }
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0.0 || ActualHeight <= 0.0)
        {
            return;
        }

        Rect layoutRect = ResolveLogoLayoutRect();
        EnsureLetterGeometries(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _layoutOffsetTransform.Matrix = new Matrix(1.0, 0.0, 0.0, 1.0, layoutRect.X, layoutRect.Y);
        _layoutScaleTransform.Matrix = new Matrix(layoutRect.Width / DesignWidth, 0.0, 0.0, layoutRect.Height / DesignHeight, 0.0, 0.0);
        drawingContext.PushTransform(_layoutOffsetTransform);
        drawingContext.PushTransform(_layoutScaleTransform);
        for (int index = 0; index < _letters.Length; index++)
        {
            DrawLetter(drawingContext, index);
        }
        drawingContext.Pop();
        drawingContext.Pop();
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        Point point = hitTestParameters.HitPoint;
        if (IsActive && ResolveLogoHitRect().Contains(point))
        {
            return new PointHitTestResult(this, point);
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsActive)
        {
            BeginIntro();
        }
        else
        {
            SetRestingState();
        }
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
        for (int index = 0; index < _letters.Length; index++)
        {
            LetterSpring letter = _letters[index];
            letter.Reset(0.0, IntroOffsetY, 0.96);
            letter.TargetX = 0.0;
            letter.TargetY = IntroOffsetY;
            letter.TargetScale = 0.96;
            letter.TargetReleaseSeconds = ResolveIntroReleaseSeconds(index);
        }
        EnsureRendering();
        InvalidateVisual();
    }

    private void SetRestingState()
    {
        _hasPointer = false;
        _isInteractionReady = false;
        _introStopwatch = null;
        for (int index = 0; index < _letters.Length; index++)
        {
            LetterSpring letter = _letters[index];
            letter.Reset(0.0, 0.0, 1.0);
            letter.TargetReleaseSeconds = 0.0;
        }
        InvalidateVisual();
    }

    private void ReleaseInteraction()
    {
        _hasPointer = false;
        for (int index = 0; index < _letters.Length; index++)
        {
            _letters[index].TargetX = 0.0;
            _letters[index].TargetY = 0.0;
            _letters[index].TargetScale = 1.0;
        }
        EnsureRendering();
    }

    private void UpdatePointerTargets()
    {
        if (!_hasPointer || ActualWidth <= 0.0 || ActualHeight <= 0.0)
        {
            return;
        }
        Rect layoutRect = ResolveLogoLayoutRect();
        if (layoutRect.Width <= 0.0 || layoutRect.Height <= 0.0)
        {
            return;
        }
        double designPointerY = (_lastPointer.Y - layoutRect.Y) / (layoutRect.Height / DesignHeight);
        for (int index = 0; index < _letters.Length; index++)
        {
            double centerY = ResolveLetterBaseY(index) + LetterHeight / 2.0;
            LetterSpring letter = _letters[index];
            letter.TargetX = 0.0;
            letter.TargetY = designPointerY > centerY ? -RepelOffsetY : RepelOffsetY;
            letter.TargetScale = 1.0;
        }
    }

    private Rect ResolveLogoLayoutRect()
    {
        double width = Math.Max(ActualWidth, 1.0);
        double height = Math.Max(ActualHeight, 1.0);
        double reservedDesignHeight = DesignHeight + MotionSafeTop + MotionSafeBottom;
        double scale = Math.Min(width / DesignWidth * LayoutWidthFill, height / reservedDesignHeight * LayoutHeightFill);
        scale = Math.Max(scale, 0.01);
        double logoWidth = DesignWidth * scale;
        double logoHeight = DesignHeight * scale;
        double reservedHeight = reservedDesignHeight * scale;
        double topSafety = MotionSafeTop * scale;
        double bottomSafety = MotionSafeBottom * scale;
        double shiftedY = (height - reservedHeight) / 2.0 + topSafety + RestingDownShift * scale;
        double maxY = Math.Max(0.0, height - bottomSafety - logoHeight);
        return new Rect(
            (width - logoWidth) / 2.0,
            Math.Max(topSafety, Math.Min(shiftedY, maxY)),
            logoWidth,
            logoHeight);
    }

    private Rect ResolveLogoHitRect()
    {
        if (ActualWidth <= 0.0 || ActualHeight <= 0.0)
        {
            return Rect.Empty;
        }
        Rect layoutRect = ResolveLogoLayoutRect();
        double scale = layoutRect.Width / DesignWidth;
        double padding = LogoHitPadding * Math.Max(scale, 0.01);
        layoutRect.Inflate(padding, padding);
        layoutRect.Intersect(new Rect(0.0, 0.0, ActualWidth, ActualHeight));
        return layoutRect;
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
        for (int index = 0; index < _letters.Length; index++)
        {
            LetterSpring letter = _letters[index];
            if (_introStopwatch != null && elapsedSeconds >= letter.TargetReleaseSeconds && letter.TargetY <= IntroOffsetY * 0.5)
            {
                letter.TargetY = 0.0;
                letter.TargetScale = 1.0;
            }
            letter.Step(deltaSeconds, springStiffness, springDamping);
            anyMoving |= !letter.IsSettled;
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

    private void DrawLetter(DrawingContext drawingContext, int index)
    {
        LetterSpring state = _letters[index];
        double baseY = ResolveLetterBaseY(index);
        double centerX = DesignWidth / 2.0;
        double centerY = baseY + LetterHeight / 2.0;

        Matrix motionMatrix = Matrix.Identity;
        motionMatrix.Translate(-centerX, -centerY);
        motionMatrix.Scale(state.Scale, state.Scale);
        motionMatrix.Translate(centerX + state.X, centerY + state.Y);
        _letterMotionTransforms[index].Matrix = motionMatrix;
        drawingContext.PushTransform(_letterMotionTransforms[index]);
        Geometry letterGeometry = _letterGeometries[index];
        if (letterGeometry != null)
        {
            _letterBaseTransforms[index].Matrix = new Matrix(1.0, 0.0, 0.0, 1.0, 0.0, baseY);
            drawingContext.PushTransform(_letterBaseTransforms[index]);
            drawingContext.DrawGeometry(LogoBrush, null, letterGeometry);
            drawingContext.Pop();
        }
        drawingContext.Pop();
    }

    private void EnsureLetterGeometries(double pixelsPerDip)
    {
        if (_letterGeometries[0] != null && Math.Abs(_letterGeometryPixelsPerDip - pixelsPerDip) < 0.001)
        {
            return;
        }
        _letterGeometryPixelsPerDip = pixelsPerDip;
        for (int index = 0; index < LetterCount; index++)
        {
            _letterGeometries[index] = CreateLetterGeometry(LetterTexts[index], pixelsPerDip);
        }
    }

    private static double ResolveLetterBaseY(int index)
    {
        return index * (LetterHeight + LetterGap);
    }

    private static double ResolveIntroReleaseSeconds(int index)
    {
        int clampedIndex = Math.Max(0, Math.Min(index, LetterCount - 1));
        double reversePosition = (double)(LetterCount - 1 - clampedIndex) / (LetterCount - 1);
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

    private static Geometry CreateLetterGeometry(string text, double pixelsPerDip)
    {
        try
        {
            return CreateLetterGeometry(text, LogoTypeface, pixelsPerDip);
        }
        catch (Exception ex)
        {
            string message = $"Navigation logo font fallback: failed to build Moderne 3D Schwabacher glyph for {text}. {ex.Message}";
            Trace.WriteLine(message);
            Debug.Fail(message);
            return CreateLetterGeometry(text, FallbackLogoTypeface, pixelsPerDip);
        }
    }

    private static Geometry CreateLetterGeometry(string text, Typeface typeface, double pixelsPerDip)
    {
        FormattedText formattedText = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            LetterFontSize,
            Brushes.Black,
            pixelsPerDip);
        Geometry rawGeometry = formattedText.BuildGeometry(new Point(0.0, 0.0));
        Rect bounds = rawGeometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return CreateFallbackBlockGeometry();
        }

        double scale = Math.Min(LetterMaxWidth / bounds.Width, LetterMaxHeight / bounds.Height);
        double scaledWidth = bounds.Width * scale;
        double scaledHeight = bounds.Height * scale;
        TransformGroup transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform((DesignWidth - scaledWidth) / 2.0, (LetterHeight - scaledHeight) / 2.0));
        if (transform.CanFreeze)
        {
            transform.Freeze();
        }

        Geometry transformedGeometry = rawGeometry.Clone();
        transformedGeometry.Transform = transform;
        if (transformedGeometry.CanFreeze)
        {
            transformedGeometry.Freeze();
        }
        return transformedGeometry;
    }

    private static Geometry CreateFallbackBlockGeometry()
    {
        RectangleGeometry geometry = new RectangleGeometry(new Rect(18.0, 12.0, DesignWidth - 36.0, LetterHeight - 24.0), 18.0, 18.0);
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class LetterSpring
    {
        public double X { get; private set; }

        public double Y { get; private set; }

        public double Scale { get; private set; }

        public double TargetX { get; set; }

        public double TargetY { get; set; }

        public double TargetScale { get; set; }

        public double TargetReleaseSeconds { get; set; }

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
