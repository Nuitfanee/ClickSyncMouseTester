namespace ClickSyncMouseTester.Controls.Brand;

internal static class BrandLogoResourceLookup
{
    public static object TryFindApplicationResource(string resourceKey)
    {
        if (System.Windows.Application.Current == null || string.IsNullOrWhiteSpace(resourceKey))
        {
            return null;
        }
        return System.Windows.Application.Current.TryFindResource(resourceKey);
    }
}
