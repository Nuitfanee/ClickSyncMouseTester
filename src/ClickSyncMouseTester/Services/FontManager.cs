#define TRACE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ClickSyncMouseTester.Services;

public sealed class FontManager
{
    private const string SpaceGroteskDirectory = "Assets/Fonts/Bundled/SpaceGrotesk/ttf/static/";

    private const string SpaceGroteskFamilyName = "Space Grotesk";

    private static readonly Uri ApplicationFontBaseUri = new Uri("pack://application:,,,/");

    private static readonly FontManager _instance = new FontManager();

    private static readonly FontFamily _defaultUiFallback = new FontFamily("Segoe UI");

    private static readonly FontFamily _defaultChineseFallback = new FontFamily("Microsoft YaHei UI");

    private bool _initialized;

    public static FontManager Instance => _instance;

    private FontManager()
    {
    }

    public void Initialize()
    {
        if (!_initialized)
        {
            _initialized = true;
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyCurrentLanguageFonts();
        }
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        ApplyCurrentLanguageFonts();
    }

    private void ApplyCurrentLanguageFonts()
    {
        if (System.Windows.Application.Current == null)
        {
            return;
        }

        bool isChineseCulture = LocalizationManager.Instance.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        FontFamily spaceGrotesk = ResolveSpaceGrotesk();
        FontFamily chineseUiFont = ResolveChineseUiFont();
        FontFamily latinDisplayFont = ComposeFontFamily(spaceGrotesk, spaceGrotesk, chineseUiFont, _defaultUiFallback);
        FontFamily listDisplayFont = latinDisplayFont;
        FontFamily bodyFont = isChineseCulture ? chineseUiFont : latinDisplayFont;
        FontFamily metadataFont = bodyFont;
        FontFamily microUiFont = isChineseCulture ? ResolveSmallChineseUiFont() : latinDisplayFont;

        System.Windows.Application.Current.Resources["Font.Meta"] = metadataFont;
        System.Windows.Application.Current.Resources["Font.MicroUi"] = microUiFont;
        System.Windows.Application.Current.Resources["Font.Body"] = bodyFont;
        System.Windows.Application.Current.Resources["Font.DisplaySans"] = latinDisplayFont;
        System.Windows.Application.Current.Resources["Font.ListDisplay"] = listDisplayFont;
        System.Windows.Application.Current.Resources["Font.Serif"] = bodyFont;
        System.Windows.Application.Current.Resources["Font.Mono"] = latinDisplayFont;
        System.Windows.Application.Current.Resources["Font.EditorialSerif"] = bodyFont;
        System.Windows.Application.Current.Resources["Font.EditorialDisplay"] = latinDisplayFont;
        if (ShouldWriteDiagnostics())
        {
            WriteDiagnostics(latinDisplayFont, listDisplayFont, bodyFont, metadataFont, microUiFont);
        }
    }

    private static FontFamily ResolveSpaceGrotesk()
    {
        return ResolveBundledFontFamily("Assets/Fonts/Bundled/SpaceGrotesk/ttf/static/", "Space Grotesk", _defaultUiFallback);
    }

    private static FontFamily ResolveChineseUiFont()
    {
        return _defaultChineseFallback;
    }

    private static FontFamily ResolveSmallChineseUiFont()
    {
        return _defaultChineseFallback;
    }

    private static FontFamily ResolveBundledFontFamily(string relativeDirectory, string familyName, FontFamily fallback)
    {
        string normalizedDirectory = NormalizeFontDirectory(relativeDirectory);
        if (!HasBundledFontResources(normalizedDirectory))
        {
            Trace.WriteLine(string.Format(CultureInfo.InvariantCulture, "FontManager fallback: no bundled font assets found under {0}", normalizedDirectory));
            return fallback;
        }

        string fontSource = string.Format(CultureInfo.InvariantCulture, "./{0}#{1}", normalizedDirectory, familyName);
        return CreateFontFamily(new[] { fontSource }, fallback);
    }

    private static FontFamily ComposeFontFamily(FontFamily fallback, params FontFamily[] families)
    {
        if (families == null || families.Length == 0)
        {
            return fallback;
        }
        return CreateFontFamily((from fontFamily in families
                                 where fontFamily != null && !string.IsNullOrWhiteSpace(fontFamily.Source)
                                 select fontFamily.Source).Distinct<string>(StringComparer.OrdinalIgnoreCase), fallback);
    }

    private static FontFamily CreateFontFamily(IEnumerable<string> sources, FontFamily fallback)
    {
        if (sources == null)
        {
            return fallback;
        }

        string[] uniqueSources = sources.Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (uniqueSources.Length == 0)
        {
            return fallback;
        }

        string sourceList = string.Join(", ", uniqueSources);
        try
        {
            return new FontFamily(ApplicationFontBaseUri, sourceList);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(string.Format(CultureInfo.InvariantCulture, "FontManager fallback: failed to compose font family {0}. {1}", sourceList, ex.Message));
            return fallback;
        }
    }

    private static bool HasBundledFontResources(string normalizedDirectory)
    {
        string location = "./" + normalizedDirectory;
        try
        {
            return Fonts.GetFontFamilies(ApplicationFontBaseUri, location).Any();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(string.Format(CultureInfo.InvariantCulture, "FontManager fallback: failed to enumerate bundled fonts under {0}. {1}", normalizedDirectory, ex.Message));
            return false;
        }
    }

    private static string NormalizeFontDirectory(string relativeDirectory)
    {
        string normalizedDirectory = relativeDirectory.Replace('\\', '/').Trim('/');
        if (!normalizedDirectory.EndsWith("/", StringComparison.Ordinal))
        {
            normalizedDirectory += "/";
        }
        return normalizedDirectory;
    }

    private static bool ShouldWriteDiagnostics()
    {
        return string.Equals(Environment.GetEnvironmentVariable("CLIKSYNC_FONT_DIAGNOSTICS"), "1", StringComparison.Ordinal);
    }

    private static void WriteDiagnostics(FontFamily displaySans, FontFamily listDisplay, FontFamily body, FontFamily meta, FontFamily microUi)
    {
        try
        {
            List<string> diagnosticsLines = new List<string>
            {
                "Timestamp=" + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                "Culture=" + LocalizationManager.Instance.CurrentCulture.Name
            };
            AppendDiagnostics(diagnosticsLines, "Font.DisplaySans", displaySans);
            AppendDiagnostics(diagnosticsLines, "Font.ListDisplay", listDisplay);
            AppendDiagnostics(diagnosticsLines, "Font.Body", body);
            AppendDiagnostics(diagnosticsLines, "Font.Meta", meta);
            AppendDiagnostics(diagnosticsLines, "Font.MicroUi", microUi);
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "font-diagnostics.txt"), diagnosticsLines);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("FontManager diagnostics failed: " + ex.Message);
        }
    }

    private static void AppendDiagnostics(ICollection<string> lines, string key, FontFamily family)
    {
        if (lines == null)
        {
            return;
        }
        if (family == null)
        {
            lines.Add(key + ".Source=<null>");
            return;
        }
        lines.Add(key + ".Source=" + family.Source);
        Typeface typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        GlyphTypeface glyphTypeface = null;
        if (typeface.TryGetGlyphTypeface(out glyphTypeface) && glyphTypeface != null)
        {
            string englishFamilyName = glyphTypeface.FamilyNames.FirstOrDefault((KeyValuePair<CultureInfo, string> entry) => string.Equals(entry.Key.IetfLanguageTag, "en-US", StringComparison.Ordinal)).Value;
            if (string.IsNullOrWhiteSpace(englishFamilyName))
            {
                englishFamilyName = glyphTypeface.FamilyNames.Values.FirstOrDefault();
            }
            lines.Add(key + ".GlyphFamily=" + englishFamilyName);
        }
        else
        {
            lines.Add(key + ".GlyphFamily=<unresolved>");
        }
    }
}





