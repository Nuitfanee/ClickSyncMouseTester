using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Settings;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace ClickSyncMouseTester.Services;

public sealed class ThemeManager
{
    private const string DefaultThemeName = "Dark";
    private const string LightThemeName = "Light";
    private const string ThemeDictionaryPrefix = "/Resources/Themes/Theme.";

    private static readonly ThemeManager _instance = new ThemeManager();

    private ResourceDictionary _activeDictionary;
    private AppTheme _currentTheme;
    private bool _initialized;

    public static ThemeManager Instance => _instance;

    public AppTheme CurrentTheme => _currentTheme;

    public event EventHandler ThemeChanged;

    private ThemeManager()
    {
        _currentTheme = AppTheme.Dark;
    }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _activeDictionary = FindExistingThemeDictionary();
        SetTheme(ResolveTheme(GetSavedThemeName()), raiseChanged: false, persist: false);
    }

    public bool ToggleTheme()
    {
        AppTheme nextTheme = _currentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        return SetTheme(nextTheme);
    }

    public bool SetTheme(AppTheme theme, bool raiseChanged = true, bool persist = true)
    {
        ResourceDictionary themeDictionary = LoadThemeDictionary(theme);
        Uri previousSource = _activeDictionary?.Source;
        Uri nextSource = themeDictionary.Source;
        bool themeChanged = previousSource == null || !Equals(previousSource, nextSource);

        if (themeChanged)
        {
            ReplaceThemeDictionary(themeDictionary);
        }

        _currentTheme = theme;

        if (persist)
        {
            SaveThemeName(GetThemeName(theme));
        }

        if (raiseChanged)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        return themeChanged;
    }

    private ResourceDictionary FindExistingThemeDictionary()
    {
        if (System.Windows.Application.Current == null)
        {
            return null;
        }

        foreach (ResourceDictionary mergedDictionary in System.Windows.Application.Current.Resources.MergedDictionaries)
        {
            if (IsThemeDictionary(mergedDictionary))
            {
                return mergedDictionary;
            }
        }

        return null;
    }

    private ResourceDictionary LoadThemeDictionary(AppTheme theme)
    {
        string themeResourceName = GetThemeResourceName(theme);
        return new ResourceDictionary
        {
            Source = BuildResourceUri(string.Format(CultureInfo.InvariantCulture, "{0}{1}.xaml", ThemeDictionaryPrefix, themeResourceName))
        };
    }

    private static Uri BuildResourceUri(string resourcePath)
    {
        string normalizedPath = resourcePath.Replace('\\', '/');
        return new Uri(normalizedPath, UriKind.Relative);
    }

    private void ReplaceThemeDictionary(ResourceDictionary themeDictionary)
    {
        if (themeDictionary == null || System.Windows.Application.Current == null)
        {
            _activeDictionary = themeDictionary;
            return;
        }

        Collection<ResourceDictionary> mergedDictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        for (int index = mergedDictionaries.Count - 1; index >= 0; index--)
        {
            if (IsThemeDictionary(mergedDictionaries[index]))
            {
                mergedDictionaries.RemoveAt(index);
            }
        }

        _activeDictionary = themeDictionary;
        mergedDictionaries.Insert(0, _activeDictionary);
    }

    private static bool IsThemeDictionary(ResourceDictionary resourceDictionary)
    {
        return resourceDictionary?.Source != null && IsThemeDictionarySource(resourceDictionary.Source);
    }

    private static bool IsThemeDictionarySource(Uri source)
    {
        if (source == null)
        {
            return false;
        }

        string normalizedSource = source.ToString().Replace('\\', '/');
        return normalizedSource.IndexOf(ThemeDictionaryPrefix, StringComparison.OrdinalIgnoreCase) >= 0
            || normalizedSource.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase)
            || normalizedSource.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static AppTheme ResolveTheme(string themeName)
    {
        return string.Equals(themeName, LightThemeName, StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Light
            : AppTheme.Dark;
    }

    private static string GetThemeName(AppTheme theme)
    {
        return theme == AppTheme.Light ? LightThemeName : DefaultThemeName;
    }

    private static string GetThemeResourceName(AppTheme theme)
    {
        return GetThemeName(theme);
    }

    private static string GetSavedThemeName()
    {
        try
        {
            return ApplicationSettings.Default.ThemeName;
        }
        catch (Exception)
        {
            return DefaultThemeName;
        }
    }

    private static void SaveThemeName(string themeName)
    {
        try
        {
            ApplicationSettings.Default.ThemeName = themeName;
            ApplicationSettings.Default.Save();
        }
        catch (Exception)
        {
        }
    }
}
