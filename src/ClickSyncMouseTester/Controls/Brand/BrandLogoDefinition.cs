using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Controls.Brand;

public abstract class BrandLogoDefinition
{
    private int _revision;

    public int Revision => _revision;

    protected void MarkChanged()
    {
        unchecked
        {
            _revision++;
        }
    }
}

public sealed class TextBrandLogoDefinition : BrandLogoDefinition
{
    private string _text = BrandLogoDefaults.DefaultText;

    private FontFamily _fontFamily = BrandLogoDefaults.CreateDefaultFontFamily();

    private FontStyle _fontStyle = FontStyles.Normal;

    private FontWeight _fontWeight = FontWeights.Normal;

    private FontStretch _fontStretch = FontStretches.Normal;

    private FontFamily _fallbackFontFamily = new FontFamily("Segoe UI");

    private FontStyle _fallbackFontStyle = FontStyles.Normal;

    private FontWeight _fallbackFontWeight = FontWeights.Bold;

    private FontStretch _fallbackFontStretch = FontStretches.Normal;

    private double _fontSize = BrandLogoDefaults.DefaultGlyphFontSize;

    public string Text
    {
        get => _text;
        set => SetValue(ref _text, value);
    }

    public FontFamily FontFamily
    {
        get => _fontFamily;
        set => SetValue(ref _fontFamily, value);
    }

    public FontStyle FontStyle
    {
        get => _fontStyle;
        set => SetValue(ref _fontStyle, value);
    }

    public FontWeight FontWeight
    {
        get => _fontWeight;
        set => SetValue(ref _fontWeight, value);
    }

    public FontStretch FontStretch
    {
        get => _fontStretch;
        set => SetValue(ref _fontStretch, value);
    }

    public FontFamily FallbackFontFamily
    {
        get => _fallbackFontFamily;
        set => SetValue(ref _fallbackFontFamily, value);
    }

    public FontStyle FallbackFontStyle
    {
        get => _fallbackFontStyle;
        set => SetValue(ref _fallbackFontStyle, value);
    }

    public FontWeight FallbackFontWeight
    {
        get => _fallbackFontWeight;
        set => SetValue(ref _fallbackFontWeight, value);
    }

    public FontStretch FallbackFontStretch
    {
        get => _fallbackFontStretch;
        set => SetValue(ref _fallbackFontStretch, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetValue(ref _fontSize, value);
    }

    private void SetValue<T>(ref T field, T value)
    {
        if (Equals(field, value))
        {
            return;
        }
        field = value;
        MarkChanged();
    }
}

public sealed class SelectedBrandLogoDefinition : BrandLogoDefinition
{
    private string _selectedKey = string.Empty;

    private string _resourceKeyPrefix = BrandLogoDefaults.DefaultResourceKeyPrefix;

    private string _fallbackKey = string.Empty;

    public string SelectedKey
    {
        get => _selectedKey;
        set => SetValue(ref _selectedKey, value);
    }

    public string ResourceKeyPrefix
    {
        get => _resourceKeyPrefix;
        set => SetValue(ref _resourceKeyPrefix, value);
    }

    public string FallbackKey
    {
        get => _fallbackKey;
        set => SetValue(ref _fallbackKey, value);
    }

    private void SetValue<T>(ref T field, T value)
    {
        if (Equals(field, value))
        {
            return;
        }
        field = value;
        MarkChanged();
    }
}

public sealed class PresetVectorBrandLogoDefinition : BrandLogoDefinition
{
    private string _presetKey = string.Empty;

    private bool _splitGeometryGroup;

    public string PresetKey
    {
        get => _presetKey;
        set => SetValue(ref _presetKey, value);
    }

    public bool SplitGeometryGroup
    {
        get => _splitGeometryGroup;
        set => SetValue(ref _splitGeometryGroup, value);
    }

    private void SetValue<T>(ref T field, T value)
    {
        if (Equals(field, value))
        {
            return;
        }
        field = value;
        MarkChanged();
    }
}

public sealed class BrandLogoVectorPreset : BrandLogoDefinition
{
    private readonly Collection<BrandLogoVectorElement> _elements = new Collection<BrandLogoVectorElement>();

    private Geometry _geometry;

    private bool _splitGeometryGroup;

    public Geometry Geometry
    {
        get => _geometry;
        set => SetValue(ref _geometry, value);
    }

    public bool SplitGeometryGroup
    {
        get => _splitGeometryGroup;
        set => SetValue(ref _splitGeometryGroup, value);
    }

    public Collection<BrandLogoVectorElement> Elements => _elements;

    private void SetValue<T>(ref T field, T value)
    {
        if (Equals(field, value))
        {
            return;
        }
        field = value;
        MarkChanged();
    }
}

public sealed class BrandLogoVectorElement
{
    public Geometry Geometry { get; set; }

    public Rect LayoutBounds { get; set; } = Rect.Empty;
}

public static class BrandLogoDefaults
{
    public const string DefaultText = "NUIT";

    public const string DefaultResourceKeyPrefix = "BrandLogo.";

    public const double DefaultGlyphFontSize = 220.0;

    public static FontFamily CreateDefaultFontFamily()
    {
        try
        {
            return new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/Bundled/moderne-3d-schwabacher/#Moderne 3D Schwabacher");
        }
        catch (UriFormatException)
        {
            return new FontFamily("Segoe UI");
        }
    }

    public static TextBrandLogoDefinition CreateDefaultDefinition()
    {
        return new TextBrandLogoDefinition();
    }
}
