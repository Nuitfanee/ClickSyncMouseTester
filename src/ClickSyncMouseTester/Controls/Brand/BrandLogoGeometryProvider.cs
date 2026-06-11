#define TRACE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Controls.Brand;

public sealed class BrandLogoGeometryElement
{
    public BrandLogoGeometryElement(Geometry geometry)
        : this(geometry, geometry?.Bounds ?? Rect.Empty)
    {
    }

    public BrandLogoGeometryElement(Geometry geometry, Rect layoutBounds)
    {
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        Bounds = HasUsableBounds(layoutBounds) ? layoutBounds : geometry.Bounds;
    }

    public Geometry Geometry { get; }

    public Rect Bounds { get; }

    private static bool HasUsableBounds(Rect bounds)
    {
        return !bounds.IsEmpty && bounds.Width > 0.0 && bounds.Height > 0.0;
    }
}

public interface IBrandLogoGeometryProvider
{
    bool CanCreate(BrandLogoDefinition definition);

    IReadOnlyList<BrandLogoGeometryElement> Create(BrandLogoDefinition definition, double pixelsPerDip);
}

public sealed class BrandLogoGeometryProviderRegistry
{
    private readonly List<IBrandLogoGeometryProvider> _providers = new List<IBrandLogoGeometryProvider>();

    private readonly BrandLogoDefinitionResolver _definitionResolver;

    public BrandLogoGeometryProviderRegistry(IEnumerable<IBrandLogoGeometryProvider> providers)
        : this(providers, BrandLogoResourceLookup.TryFindApplicationResource)
    {
    }

    internal BrandLogoGeometryProviderRegistry(IEnumerable<IBrandLogoGeometryProvider> providers, Func<string, object> findResource)
    {
        Func<string, object> resourceLookup = findResource ?? BrandLogoResourceLookup.TryFindApplicationResource;
        _definitionResolver = new BrandLogoDefinitionResolver(resourceLookup);
        if (providers == null)
        {
            return;
        }
        foreach (IBrandLogoGeometryProvider provider in providers)
        {
            if (provider != null)
            {
                _providers.Add(AttachResourceLookup(provider, resourceLookup));
            }
        }
    }

    public IReadOnlyList<BrandLogoGeometryElement> Create(BrandLogoDefinition definition, double pixelsPerDip)
    {
        BrandLogoDefinition resolvedDefinition = _definitionResolver.Resolve(definition);
        if (resolvedDefinition == null)
        {
            return Array.Empty<BrandLogoGeometryElement>();
        }
        foreach (IBrandLogoGeometryProvider provider in _providers)
        {
            if (!provider.CanCreate(resolvedDefinition))
            {
                continue;
            }
            try
            {
                return provider.Create(resolvedDefinition, pixelsPerDip) ?? Array.Empty<BrandLogoGeometryElement>();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Brand logo provider failed for {resolvedDefinition.GetType().Name}: {ex.Message}");
                return Array.Empty<BrandLogoGeometryElement>();
            }
        }
        return Array.Empty<BrandLogoGeometryElement>();
    }

    public static BrandLogoGeometryProviderRegistry CreateDefault()
    {
        Func<string, object> resourceLookup = BrandLogoResourceLookup.TryFindApplicationResource;
        return new BrandLogoGeometryProviderRegistry(new IBrandLogoGeometryProvider[]
        {
            new TextBrandLogoGeometryProvider(),
            new PresetVectorBrandLogoGeometryProvider(resourceLookup)
        }, resourceLookup);
    }

    private static IBrandLogoGeometryProvider AttachResourceLookup(IBrandLogoGeometryProvider provider, Func<string, object> resourceLookup)
    {
        if (provider is PresetVectorBrandLogoGeometryProvider presetProvider)
        {
            return presetProvider.WithResourceLookup(resourceLookup);
        }
        return provider;
    }
}

public sealed class TextBrandLogoGeometryProvider : IBrandLogoGeometryProvider
{
    public bool CanCreate(BrandLogoDefinition definition)
    {
        return definition is TextBrandLogoDefinition;
    }

    public IReadOnlyList<BrandLogoGeometryElement> Create(BrandLogoDefinition definition, double pixelsPerDip)
    {
        TextBrandLogoDefinition textDefinition = (TextBrandLogoDefinition)definition;
        string text = string.IsNullOrWhiteSpace(textDefinition.Text)
            ? BrandLogoDefaults.DefaultText
            : textDefinition.Text.Trim();
        List<BrandLogoGeometryElement> elements = new List<BrandLogoGeometryElement>();
        Typeface primaryTypeface = new Typeface(
            textDefinition.FontFamily,
            textDefinition.FontStyle,
            textDefinition.FontWeight,
            textDefinition.FontStretch);
        Typeface fallbackTypeface = new Typeface(
            textDefinition.FallbackFontFamily,
            textDefinition.FallbackFontStyle,
            textDefinition.FallbackFontWeight,
            textDefinition.FallbackFontStretch);
        double fontSize = Math.Max(1.0, textDefinition.FontSize);
        TextElementEnumerator textElements = StringInfo.GetTextElementEnumerator(text);
        while (textElements.MoveNext())
        {
            string textElement = textElements.GetTextElement();
            if (string.IsNullOrWhiteSpace(textElement))
            {
                continue;
            }
            Geometry geometry = CreateTextElementGeometry(textElement, primaryTypeface, fallbackTypeface, fontSize, pixelsPerDip);
            AddGeometryElement(elements, geometry);
        }
        if (elements.Count == 0)
        {
            AddGeometryElement(elements, CreateFallbackBlockGeometry());
        }
        return elements;
    }

    private static Geometry CreateTextElementGeometry(string text, Typeface primaryTypeface, Typeface fallbackTypeface, double fontSize, double pixelsPerDip)
    {
        try
        {
            return CreateTextElementGeometry(text, primaryTypeface, fontSize, pixelsPerDip);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Brand logo font fallback: failed to build primary glyph for {text}. {ex.Message}");
            try
            {
                return CreateTextElementGeometry(text, fallbackTypeface, fontSize, pixelsPerDip);
            }
            catch (Exception fallbackEx)
            {
                Trace.WriteLine($"Brand logo font fallback failed for {text}. {fallbackEx.Message}");
                return CreateFallbackBlockGeometry();
            }
        }
    }

    private static Geometry CreateTextElementGeometry(string text, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        FormattedText formattedText = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            pixelsPerDip);
        Geometry geometry = formattedText.BuildGeometry(new Point(0.0, 0.0));
        if (HasUsableBounds(geometry))
        {
            FreezeIfPossible(geometry);
            return geometry;
        }
        return CreateFallbackBlockGeometry();
    }

    private static Geometry CreateFallbackBlockGeometry()
    {
        RectangleGeometry geometry = new RectangleGeometry(new Rect(0.0, 0.0, 96.0, 96.0), 12.0, 12.0);
        geometry.Freeze();
        return geometry;
    }

    private static void AddGeometryElement(List<BrandLogoGeometryElement> elements, Geometry geometry)
    {
        if (!HasUsableBounds(geometry))
        {
            return;
        }
        elements.Add(new BrandLogoGeometryElement(geometry));
    }

    private static bool HasUsableBounds(Geometry geometry)
    {
        if (geometry == null)
        {
            return false;
        }
        Rect bounds = geometry.Bounds;
        return !bounds.IsEmpty && bounds.Width > 0.0 && bounds.Height > 0.0;
    }

    private static void FreezeIfPossible(Freezable freezable)
    {
        if (freezable != null && freezable.CanFreeze)
        {
            freezable.Freeze();
        }
    }
}

public sealed class PresetVectorBrandLogoGeometryProvider : IBrandLogoGeometryProvider
{
    private static readonly object CacheLock = new object();
    private static readonly Dictionary<PresetGeometryCacheKey, IReadOnlyList<BrandLogoGeometryElement>> GeometryElementCache = new Dictionary<PresetGeometryCacheKey, IReadOnlyList<BrandLogoGeometryElement>>();

    private readonly Func<string, object> _findResource;

    public PresetVectorBrandLogoGeometryProvider()
        : this(BrandLogoResourceLookup.TryFindApplicationResource)
    {
    }

    internal PresetVectorBrandLogoGeometryProvider(Func<string, object> findResource)
    {
        _findResource = findResource ?? BrandLogoResourceLookup.TryFindApplicationResource;
    }

    public bool CanCreate(BrandLogoDefinition definition)
    {
        return definition is PresetVectorBrandLogoDefinition;
    }

    public IReadOnlyList<BrandLogoGeometryElement> Create(BrandLogoDefinition definition, double pixelsPerDip)
    {
        PresetVectorBrandLogoDefinition presetDefinition = (PresetVectorBrandLogoDefinition)definition;
        if (string.IsNullOrWhiteSpace(presetDefinition.PresetKey))
        {
            return Array.Empty<BrandLogoGeometryElement>();
        }
        object preset = _findResource(presetDefinition.PresetKey);
        if (preset is BrandLogoVectorPreset vectorPreset && vectorPreset.Elements.Count > 0)
        {
            IReadOnlyList<BrandLogoGeometryElement> explicitElements = CreateExplicitGeometryElements(vectorPreset);
            if (explicitElements.Count == 0)
            {
                return Array.Empty<BrandLogoGeometryElement>();
            }
            return explicitElements;
        }

        Geometry geometry = ResolvePresetGeometry(preset, out bool splitGeometryGroup, out int presetRevision);
        if (!HasUsableBounds(geometry))
        {
            return Array.Empty<BrandLogoGeometryElement>();
        }
        bool shouldSplit = presetDefinition.SplitGeometryGroup || splitGeometryGroup;
        PresetGeometryCacheKey cacheKey = new PresetGeometryCacheKey(presetDefinition.PresetKey, presetRevision, shouldSplit);
        lock (CacheLock)
        {
            if (GeometryElementCache.TryGetValue(cacheKey, out IReadOnlyList<BrandLogoGeometryElement> cachedElements))
            {
                return cachedElements;
            }
        }
        IReadOnlyList<BrandLogoGeometryElement> elements;
        if (shouldSplit && geometry is GeometryGroup geometryGroup)
        {
            elements = CreateSplitGeometryElements(geometryGroup);
        }
        else
        {
            Geometry clonedGeometry = geometry.Clone();
            FreezeIfPossible(clonedGeometry);
            elements = new[] { new BrandLogoGeometryElement(clonedGeometry) };
        }
        lock (CacheLock)
        {
            GeometryElementCache[cacheKey] = elements;
        }
        return elements;
    }

    private static IReadOnlyList<BrandLogoGeometryElement> CreateExplicitGeometryElements(BrandLogoVectorPreset vectorPreset)
    {
        List<BrandLogoGeometryElement> elements = new List<BrandLogoGeometryElement>();
        foreach (BrandLogoVectorElement vectorElement in vectorPreset.Elements)
        {
            if (vectorElement?.Geometry == null || !HasUsableBounds(vectorElement.Geometry))
            {
                continue;
            }
            Geometry clonedGeometry = vectorElement.Geometry.Clone();
            FreezeIfPossible(clonedGeometry);
            elements.Add(new BrandLogoGeometryElement(clonedGeometry, vectorElement.LayoutBounds));
        }
        return elements;
    }

    private static IReadOnlyList<BrandLogoGeometryElement> CreateSplitGeometryElements(GeometryGroup geometryGroup)
    {
        List<BrandLogoGeometryElement> elements = new List<BrandLogoGeometryElement>();
        foreach (Geometry childGeometry in geometryGroup.Children)
        {
            if (!HasUsableBounds(childGeometry))
            {
                continue;
            }
            Geometry clonedGeometry = childGeometry.Clone();
            ApplyParentTransform(clonedGeometry, geometryGroup.Transform);
            FreezeIfPossible(clonedGeometry);
            elements.Add(new BrandLogoGeometryElement(clonedGeometry));
        }
        return elements;
    }

    private static void ApplyParentTransform(Geometry geometry, Transform parentTransform)
    {
        if (geometry == null || parentTransform == null || parentTransform == Transform.Identity)
        {
            return;
        }
        if (geometry.Transform == null || geometry.Transform == Transform.Identity)
        {
            geometry.Transform = parentTransform.Clone();
            return;
        }
        TransformGroup transformGroup = new TransformGroup();
        transformGroup.Children.Add(geometry.Transform.Clone());
        transformGroup.Children.Add(parentTransform.Clone());
        geometry.Transform = transformGroup;
    }

    private static Geometry ResolvePresetGeometry(object preset, out bool splitGeometryGroup, out int presetRevision)
    {
        splitGeometryGroup = false;
        presetRevision = 0;
        if (preset is BrandLogoVectorPreset vectorPreset)
        {
            splitGeometryGroup = vectorPreset.SplitGeometryGroup;
            presetRevision = vectorPreset.Revision;
            return vectorPreset.Geometry;
        }
        if (preset is Geometry geometry)
        {
            presetRevision = geometry.GetHashCode();
            return geometry;
        }
        if (preset is string geometryData && !string.IsNullOrWhiteSpace(geometryData))
        {
            try
            {
                presetRevision = geometryData.GetHashCode();
                return Geometry.Parse(geometryData);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Brand logo preset geometry parse failed: {ex.Message}");
            }
        }
        return null;
    }

    internal PresetVectorBrandLogoGeometryProvider WithResourceLookup(Func<string, object> findResource)
    {
        return new PresetVectorBrandLogoGeometryProvider(findResource);
    }

    private static bool HasUsableBounds(Geometry geometry)
    {
        if (geometry == null)
        {
            return false;
        }
        Rect bounds = geometry.Bounds;
        return !bounds.IsEmpty && bounds.Width > 0.0 && bounds.Height > 0.0;
    }

    private static void FreezeIfPossible(Freezable freezable)
    {
        if (freezable != null && freezable.CanFreeze)
        {
            freezable.Freeze();
        }
    }

    private readonly struct PresetGeometryCacheKey : IEquatable<PresetGeometryCacheKey>
    {
        public PresetGeometryCacheKey(string presetKey, int presetRevision, bool splitGeometryGroup)
        {
            PresetKey = presetKey ?? string.Empty;
            PresetRevision = presetRevision;
            SplitGeometryGroup = splitGeometryGroup;
        }

        private string PresetKey { get; }

        private int PresetRevision { get; }

        private bool SplitGeometryGroup { get; }

        public bool Equals(PresetGeometryCacheKey other)
        {
            return string.Equals(PresetKey, other.PresetKey, StringComparison.Ordinal)
                && PresetRevision == other.PresetRevision
                && SplitGeometryGroup == other.SplitGeometryGroup;
        }

        public override bool Equals(object obj)
        {
            return obj is PresetGeometryCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(PresetKey);
                hash = hash * 31 + PresetRevision;
                hash = hash * 31 + SplitGeometryGroup.GetHashCode();
                return hash;
            }
        }
    }
}
