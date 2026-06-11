using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClickSyncMouseTester.Controls.Brand;

internal sealed class BrandLogoDefinitionResolver
{
    private const int MaxResolutionDepth = 8;

    private readonly Func<string, object> _findResource;

    public BrandLogoDefinitionResolver(Func<string, object> findResource)
    {
        _findResource = findResource ?? BrandLogoResourceLookup.TryFindApplicationResource;
    }

    public BrandLogoDefinition Resolve(BrandLogoDefinition definition)
    {
        return Resolve(definition, new HashSet<BrandLogoDefinition>(), new HashSet<string>(StringComparer.Ordinal), 0);
    }

    private BrandLogoDefinition Resolve(
        BrandLogoDefinition definition,
        HashSet<BrandLogoDefinition> visitedDefinitions,
        HashSet<string> visitedResourceKeys,
        int depth)
    {
        if (definition == null)
        {
            return null;
        }

        if (definition is not SelectedBrandLogoDefinition selectedDefinition)
        {
            return definition;
        }

        if (depth >= MaxResolutionDepth || !visitedDefinitions.Add(definition))
        {
            Trace.WriteLine("Brand logo selection skipped because it is recursive.");
            return null;
        }

        BrandLogoDefinition selected = ResolveResourceKey(selectedDefinition.SelectedKey, selectedDefinition, visitedDefinitions, visitedResourceKeys, depth + 1);
        if (selected != null)
        {
            return selected;
        }

        return ResolveResourceKey(selectedDefinition.FallbackKey, selectedDefinition, visitedDefinitions, visitedResourceKeys, depth + 1);
    }

    private BrandLogoDefinition ResolveResourceKey(
        string selectedKey,
        SelectedBrandLogoDefinition selectedDefinition,
        HashSet<BrandLogoDefinition> visitedDefinitions,
        HashSet<string> visitedResourceKeys,
        int depth)
    {
        string resourceKey = BuildResourceKey(selectedDefinition.ResourceKeyPrefix, selectedKey);
        if (resourceKey.Length == 0 || !visitedResourceKeys.Add(resourceKey))
        {
            return null;
        }

        object resource;
        try
        {
            resource = _findResource(resourceKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Brand logo resource lookup failed for {resourceKey}: {ex.Message}");
            return null;
        }

        if (resource is BrandLogoDefinition definition)
        {
            return Resolve(definition, visitedDefinitions, visitedResourceKeys, depth);
        }

        if (resource != null)
        {
            Trace.WriteLine($"Brand logo resource {resourceKey} is not a logo definition.");
        }
        return null;
    }

    private static string BuildResourceKey(string resourceKeyPrefix, string selectedKey)
    {
        string normalizedKey = selectedKey?.Trim() ?? string.Empty;
        if (normalizedKey.Length == 0)
        {
            return string.Empty;
        }

        string prefix = resourceKeyPrefix?.Trim() ?? string.Empty;
        return prefix + normalizedKey;
    }

}
