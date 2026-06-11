using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Services;

public sealed class LocalizationManager
{
    private const string DefaultCultureName = "en-US";
    private const string ChineseCultureName = "zh-CN";

    private static readonly LocalizationManager _instance = new LocalizationManager();

    private readonly ReadOnlyCollection<LanguageOption> _availableLanguages;
    private ResourceDictionary _activeDictionary;
    private LanguageOption _currentLanguage;
    private bool _initialized;

    public static LocalizationManager Instance => _instance;

    public ReadOnlyCollection<LanguageOption> AvailableLanguages => _availableLanguages;

    public LanguageOption CurrentLanguage => _currentLanguage;

    public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo(_currentLanguage?.CultureName ?? DefaultCultureName);

    public event EventHandler LanguageChanged;

    private LocalizationManager()
    {
        _availableLanguages = new ReadOnlyCollection<LanguageOption>(new List<LanguageOption>
        {
            new LanguageOption(ChineseCultureName, "简体中文", "Chinese"),
            new LanguageOption(DefaultCultureName, "English", "English")
        });
    }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        EnsureWpfResourceContext();
        _initialized = true;
        SetLanguage(ResolveCultureName(CultureInfo.CurrentUICulture.Name), raiseChanged: false);
    }

    public bool SetLanguage(string cultureName, bool raiseChanged = true)
    {
        EnsureWpfResourceContext();

        LanguageOption targetLanguage = FindLanguage(cultureName) ?? FindLanguage(DefaultCultureName);
        bool languageChanged = _activeDictionary == null
            || _currentLanguage == null
            || !string.Equals(_currentLanguage.CultureName, targetLanguage.CultureName, StringComparison.OrdinalIgnoreCase);

        if (languageChanged)
        {
            ResourceDictionary languageDictionary = LoadLanguageDictionary(targetLanguage.CultureName);
            ReplaceLanguageDictionary(languageDictionary);
            _currentLanguage = targetLanguage;
        }

        CultureInfo targetCulture = CultureInfo.GetCultureInfo(_currentLanguage.CultureName);
        Thread.CurrentThread.CurrentCulture = targetCulture;
        Thread.CurrentThread.CurrentUICulture = targetCulture;

        if (raiseChanged)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        return languageChanged;
    }

    public string GetString(string key, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (!_initialized)
        {
            Initialize();
        }

        string localizedText = ResolveResourceValue(key);
        if (string.IsNullOrWhiteSpace(localizedText))
        {
            return key;
        }

        return args == null || args.Length == 0
            ? localizedText
            : string.Format(CurrentCulture, localizedText, args);
    }

    private string ResolveResourceValue(string key)
    {
        if (_activeDictionary != null && _activeDictionary.Contains(key))
        {
            return _activeDictionary[key] as string;
        }

        if (System.Windows.Application.Current != null && System.Windows.Application.Current.Resources.Contains(key))
        {
            return System.Windows.Application.Current.Resources[key] as string;
        }

        return null;
    }

    private ResourceDictionary LoadLanguageDictionary(string cultureName)
    {
        string resourceCultureName = cultureName;

        return new ResourceDictionary
        {
            MergedDictionaries =
            {
                new ResourceDictionary
                {
                    Source = BuildResourceUri(string.Format(CultureInfo.InvariantCulture, "/Resources/Typography/Typography.{0}.xaml", resourceCultureName))
                },
                new ResourceDictionary
                {
                    Source = BuildResourceUri(string.Format(CultureInfo.InvariantCulture, "/Resources/Localization/Strings.{0}.xaml", resourceCultureName))
                }
            }
        };
    }

    private void ReplaceLanguageDictionary(ResourceDictionary languageDictionary)
    {
        if (languageDictionary == null || System.Windows.Application.Current == null)
        {
            _activeDictionary = languageDictionary;
            return;
        }

        if (_activeDictionary != null)
        {
            System.Windows.Application.Current.Resources.MergedDictionaries.Remove(_activeDictionary);
        }

        _activeDictionary = languageDictionary;
        System.Windows.Application.Current.Resources.MergedDictionaries.Add(_activeDictionary);
    }

    private LanguageOption FindLanguage(string cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        LanguageOption exactMatch = _availableLanguages.FirstOrDefault(language => string.Equals(language.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
        {
            return exactMatch;
        }

        string languagePrefix = cultureName.Split('-')[0];
        return _availableLanguages.FirstOrDefault(language => language.CultureName.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveCultureName(string cultureName)
    {
        return IsChineseSystemCulture(cultureName) ? ChineseCultureName : DefaultCultureName;
    }

    private static bool IsChineseSystemCulture(string cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        string normalizedCultureName = cultureName.Trim();
        if (normalizedCultureName.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedCultureName.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || normalizedCultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalizedCultureName.Equals("zh-CHS", StringComparison.OrdinalIgnoreCase)
            || normalizedCultureName.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(normalizedCultureName);
            if (!string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string regionName = culture.Name.Split('-').LastOrDefault();
            return string.Equals(regionName, "CN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(regionName, "SG", StringComparison.OrdinalIgnoreCase)
                || string.Equals(regionName, "TW", StringComparison.OrdinalIgnoreCase)
                || string.Equals(regionName, "HK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(regionName, "MO", StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static Uri BuildResourceUri(string resourcePath)
    {
        string normalizedPath = resourcePath.Replace('\\', '/');
        return new Uri(normalizedPath, UriKind.Relative);
    }

    private static void EnsureWpfResourceContext()
    {
        if (System.Windows.Application.Current != null)
        {
            _ = System.Windows.Application.Current.Dispatcher;
            return;
        }

        _ = Dispatcher.CurrentDispatcher;
    }
}
