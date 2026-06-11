using ClickSyncMouseTester.Controls.Brand;
using ClickSyncMouseTester.Models;
using System;

namespace ClickSyncMouseTester.Services;

internal sealed class NavigationBrandLogoService
{
    private const string BrandLogoResourceKeyPrefix = "BrandLogo.";

    private readonly DeviceBrandLogoMatcher _matcher;
    private readonly Func<string, object> _findResource;

    public NavigationBrandLogoService()
        : this(new DeviceBrandLogoMatcher(), TryFindApplicationResource)
    {
    }

    internal NavigationBrandLogoService(DeviceBrandLogoMatcher matcher, Func<string, object> findResource)
    {
        _matcher = matcher ?? new DeviceBrandLogoMatcher();
        _findResource = findResource ?? TryFindApplicationResource;
    }

    public BrandLogoDefinition ResolveLogoDefinition(RawMouseDeviceInfo device)
    {
        string brandKey = _matcher.ResolveBrandKey(device);
        BrandLogoDefinition logoDefinition = ResolveLogoDefinition(brandKey);
        if (logoDefinition != null)
        {
            return logoDefinition;
        }

        logoDefinition = ResolveLogoDefinition(DeviceBrandLogoMatcher.FallbackBrandKey);
        return logoDefinition ?? BrandLogoDefaults.CreateDefaultDefinition();
    }

    private BrandLogoDefinition ResolveLogoDefinition(string brandKey)
    {
        string resourceKey = BuildResourceKey(brandKey);
        if (resourceKey.Length == 0)
        {
            return null;
        }

        try
        {
            return _findResource(resourceKey) as BrandLogoDefinition;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildResourceKey(string brandKey)
    {
        string normalizedBrandKey = brandKey?.Trim() ?? string.Empty;
        return normalizedBrandKey.Length == 0
            ? string.Empty
            : BrandLogoResourceKeyPrefix + normalizedBrandKey;
    }

    private static object TryFindApplicationResource(string resourceKey)
    {
        if (System.Windows.Application.Current == null || string.IsNullOrWhiteSpace(resourceKey))
        {
            return null;
        }

        return System.Windows.Application.Current.TryFindResource(resourceKey);
    }
}
