using System;

namespace ClickSyncMouseTester.Models;

public class LanguageOption
{
    private readonly string _cultureName;

    private readonly string _nativeName;

    private readonly string _englishName;

    public string CultureName => _cultureName;

    public string NativeName => _nativeName;

    public string EnglishName => _englishName;

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NativeName))
            {
                return EnglishName;
            }
            if (string.IsNullOrWhiteSpace(EnglishName) || string.Equals(NativeName, EnglishName, StringComparison.OrdinalIgnoreCase))
            {
                return NativeName;
            }
            return NativeName + " / " + EnglishName;
        }
    }

    public LanguageOption(string cultureName, string nativeName, string englishName)
    {
        _cultureName = cultureName ?? string.Empty;
        _nativeName = nativeName ?? string.Empty;
        _englishName = englishName ?? string.Empty;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}





