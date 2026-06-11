using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Controls.Brand;

public sealed class BrandLogoLayoutOptions
{
    public double ReferenceWidth { get; set; } = 180.0;

    public double ReferenceElementHeight { get; set; } = 140.0;

    public double ReferenceElementGap { get; set; } = 12.0;

    public double LayoutWidthFill { get; set; } = 0.9;

    public double LayoutHeightFill { get; set; } = 0.98;

    public double MotionSafeTop { get; set; } = 44.0;

    public double MotionSafeBottom { get; set; } = 68.0;

    public double RestingDownShift { get; set; } = 10.0;

    public double MinimumGap { get; set; } = 2.0;

    public double HitPadding { get; set; } = 10.0;
}

public sealed class BrandLogoLayoutElement
{
    public BrandLogoLayoutElement(Geometry geometry, Rect slotBounds, Rect inkBounds, Matrix geometryToLayoutMatrix)
    {
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        SlotBounds = slotBounds;
        InkBounds = inkBounds;
        GeometryToLayoutMatrix = geometryToLayoutMatrix;
    }

    public Geometry Geometry { get; }

    public Rect SlotBounds { get; }

    public Rect InkBounds { get; }

    public Matrix GeometryToLayoutMatrix { get; }

    public Point MotionCenter => new Point(SlotBounds.X + SlotBounds.Width / 2.0, SlotBounds.Y + SlotBounds.Height / 2.0);
}

public sealed class BrandLogoLayout
{
    public BrandLogoLayout(IReadOnlyList<BrandLogoLayoutElement> elements, Rect contentBounds, Rect hitBounds)
    {
        Elements = elements ?? Array.Empty<BrandLogoLayoutElement>();
        ContentBounds = contentBounds;
        HitBounds = hitBounds;
    }

    public IReadOnlyList<BrandLogoLayoutElement> Elements { get; }

    public Rect ContentBounds { get; }

    public Rect HitBounds { get; }

    public bool IsEmpty => Elements.Count == 0;

    public static BrandLogoLayout Empty { get; } = new BrandLogoLayout(Array.Empty<BrandLogoLayoutElement>(), Rect.Empty, Rect.Empty);
}

public sealed class BrandLogoLayoutEngine
{
    public BrandLogoLayout Arrange(
        IReadOnlyList<BrandLogoGeometryElement> geometryElements,
        Size containerSize,
        BrandLogoLayoutOptions options)
    {
        if (geometryElements == null || geometryElements.Count == 0 || containerSize.Width <= 0.0 || containerSize.Height <= 0.0)
        {
            return BrandLogoLayout.Empty;
        }
        BrandLogoLayoutOptions resolvedOptions = options ?? new BrandLogoLayoutOptions();
        Rect availableRect = ResolveAvailableRect(containerSize, geometryElements.Count, resolvedOptions);
        if (availableRect.Width <= 0.0 || availableRect.Height <= 0.0)
        {
            return BrandLogoLayout.Empty;
        }

        double gapRatio = ResolveGapRatio(resolvedOptions);
        int gapCount = Math.Max(0, geometryElements.Count - 1);
        double slotHeight = availableRect.Height / Math.Max(1.0, geometryElements.Count + gapCount * gapRatio);
        double desiredGap = gapCount > 0 ? Math.Max(0.0, slotHeight * gapRatio) : 0.0;
        double preferredGap = Math.Max(0.0, resolvedOptions.MinimumGap);
        double maximumFittingGap = gapCount > 0
            ? Math.Max(0.0, (availableRect.Height - geometryElements.Count) / gapCount)
            : 0.0;
        double gap = gapCount > 0 ? Math.Min(Math.Max(desiredGap, preferredGap), maximumFittingGap) : 0.0;
        slotHeight = Math.Max(0.01, (availableRect.Height - gap * gapCount) / geometryElements.Count);

        List<BrandLogoLayoutElement> layoutElements = new List<BrandLogoLayoutElement>(geometryElements.Count);
        for (int index = 0; index < geometryElements.Count; index++)
        {
            BrandLogoGeometryElement geometryElement = geometryElements[index];
            if (!HasUsableBounds(geometryElement.Bounds))
            {
                continue;
            }
            Rect slotBounds = new Rect(
                availableRect.X,
                availableRect.Y + index * (slotHeight + gap),
                availableRect.Width,
                slotHeight);
            BrandLogoLayoutElement layoutElement = ArrangeElement(geometryElement, slotBounds);
            layoutElements.Add(layoutElement);
        }

        if (layoutElements.Count == 0)
        {
            return BrandLogoLayout.Empty;
        }
        Rect contentBounds = BuildUnionBounds(layoutElements, useSlotBounds: true);
        Rect hitBounds = contentBounds;
        hitBounds.Inflate(Math.Max(0.0, resolvedOptions.HitPadding), Math.Max(0.0, resolvedOptions.HitPadding));
        hitBounds.Intersect(new Rect(0.0, 0.0, containerSize.Width, containerSize.Height));
        return new BrandLogoLayout(layoutElements, contentBounds, hitBounds);
    }

    private static BrandLogoLayoutElement ArrangeElement(BrandLogoGeometryElement geometryElement, Rect slotBounds)
    {
        Rect sourceBounds = geometryElement.Bounds;
        double scale = Math.Min(slotBounds.Width / sourceBounds.Width, slotBounds.Height / sourceBounds.Height);
        scale = Math.Max(0.01, scale);
        double inkWidth = sourceBounds.Width * scale;
        double inkHeight = sourceBounds.Height * scale;
        Rect inkBounds = new Rect(
            slotBounds.X + (slotBounds.Width - inkWidth) / 2.0,
            slotBounds.Y + (slotBounds.Height - inkHeight) / 2.0,
            inkWidth,
            inkHeight);
        Matrix matrix = Matrix.Identity;
        matrix.Translate(-sourceBounds.X, -sourceBounds.Y);
        matrix.Scale(scale, scale);
        matrix.Translate(inkBounds.X, inkBounds.Y);
        return new BrandLogoLayoutElement(geometryElement.Geometry, slotBounds, inkBounds, matrix);
    }

    private static Rect ResolveAvailableRect(Size containerSize, int elementCount, BrandLogoLayoutOptions options)
    {
        double width = Math.Max(1.0, containerSize.Width);
        double height = Math.Max(1.0, containerSize.Height);
        int count = Math.Max(1, elementCount);
        double referenceWidth = Math.Max(1.0, options.ReferenceWidth);
        double referenceElementHeight = Math.Max(1.0, options.ReferenceElementHeight);
        double referenceElementGap = Math.Max(0.0, options.ReferenceElementGap);
        double referenceContentHeight = count * referenceElementHeight + Math.Max(0, count - 1) * referenceElementGap;
        double reservedReferenceHeight = referenceContentHeight + Math.Max(0.0, options.MotionSafeTop) + Math.Max(0.0, options.MotionSafeBottom);
        double fillWidth = ClampFill(options.LayoutWidthFill);
        double fillHeight = ClampFill(options.LayoutHeightFill);
        double safetyScale = Math.Min(width / referenceWidth * fillWidth, height / Math.Max(1.0, reservedReferenceHeight) * fillHeight);
        safetyScale = Math.Max(0.01, safetyScale);
        double topSafety = Math.Max(0.0, options.MotionSafeTop) * safetyScale;
        double bottomSafety = Math.Max(0.0, options.MotionSafeBottom) * safetyScale;
        if (topSafety + bottomSafety > height * 0.6)
        {
            double compression = height * 0.6 / (topSafety + bottomSafety);
            topSafety *= compression;
            bottomSafety *= compression;
        }

        double availableWidth = Math.Max(1.0, width * fillWidth);
        double availableHeight = Math.Max(1.0, (height - topSafety - bottomSafety) * fillHeight);
        double x = (width - availableWidth) / 2.0;
        double centeredY = topSafety + (height - topSafety - bottomSafety - availableHeight) / 2.0;
        double shiftedY = centeredY + options.RestingDownShift * safetyScale;
        double maxY = Math.Max(topSafety, height - bottomSafety - availableHeight);
        double y = Math.Max(topSafety, Math.Min(shiftedY, maxY));
        return new Rect(x, y, availableWidth, availableHeight);
    }

    private static double ResolveGapRatio(BrandLogoLayoutOptions options)
    {
        return Math.Max(0.0, options.ReferenceElementGap) / Math.Max(1.0, options.ReferenceElementHeight);
    }

    private static double ClampFill(double fill)
    {
        if (double.IsNaN(fill) || double.IsInfinity(fill))
        {
            return 1.0;
        }
        return Math.Max(0.01, Math.Min(1.0, fill));
    }

    private static Rect BuildUnionBounds(IReadOnlyList<BrandLogoLayoutElement> elements, bool useSlotBounds)
    {
        Rect bounds = Rect.Empty;
        foreach (BrandLogoLayoutElement element in elements)
        {
            Rect elementBounds = useSlotBounds ? element.SlotBounds : element.InkBounds;
            if (bounds.IsEmpty)
            {
                bounds = elementBounds;
            }
            else
            {
                bounds.Union(elementBounds);
            }
        }
        return bounds;
    }

    private static bool HasUsableBounds(Rect bounds)
    {
        return !bounds.IsEmpty && bounds.Width > 0.0 && bounds.Height > 0.0;
    }
}
