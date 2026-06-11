using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClickSyncMouseTester.Services;

internal sealed class DeviceBrandLogoMatcher
{
    public const string FallbackBrandKey = "nuit";

    private static readonly IReadOnlyList<BrandMatchProfile> MatchProfiles = new[]
    {
        new BrandMatchProfile("maicong", new[] { "maicong", "mai cong", "mchose", "\u8fc8\u4ece" }),
        new BrandMatchProfile("logi", new[] { "logitech", "logi" }, new[] { 0x046D }),
        new BrandMatchProfile("razer", new[] { "razer" }, new[] { 0x1532 }),
        new BrandMatchProfile("crdrako", new[] { "crdrako" }),
        new BrandMatchProfile("rapoo", new[] { "rapoo", "\u96f7\u67cf" }, new[] { 0x24AE }),
        new BrandMatchProfile("lamzu", new[] { "lamzu" }, new[] { 0x37B0 }),
        new BrandMatchProfile("zowie", new[] { "zowie" }, new[] { 0x2345 }),
        new BrandMatchProfile("vaxee", new[] { "vaxee" }),
        new BrandMatchProfile("atk", new[] { "atk" }, new[] { 0x373B }),
        new BrandMatchProfile("rog", new[] { "asus", "rog" })
    };

    public string ResolveBrandKey(RawMouseDeviceInfo device)
    {
        string searchText = BuildSearchText(device);
        string nameBrandKey = ResolveNameBrandKey(searchText);
        if (!string.IsNullOrWhiteSpace(nameBrandKey))
        {
            return nameBrandKey;
        }

        string hardwareBrandKey = ResolveHardwareBrandKey(device);
        if (!string.IsNullOrWhiteSpace(hardwareBrandKey))
        {
            return hardwareBrandKey;
        }

        return FallbackBrandKey;
    }

    private static string ResolveNameBrandKey(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return string.Empty;
        }

        foreach (BrandMatchProfile profile in MatchProfiles)
        {
            if (profile.IsNameMatch(searchText))
            {
                return profile.BrandKey;
            }
        }

        return string.Empty;
    }

    private static string ResolveHardwareBrandKey(RawMouseDeviceInfo device)
    {
        if (device?.VendorId == null)
        {
            return string.Empty;
        }

        foreach (BrandMatchProfile profile in MatchProfiles)
        {
            if (profile.IsHardwareMatch(device.VendorId.Value))
            {
                return profile.BrandKey;
            }
        }

        return string.Empty;
    }

    private static string BuildSearchText(RawMouseDeviceInfo device)
    {
        if (device == null)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        AppendSearchText(builder, device.SelectionDisplayName);
        AppendSearchText(builder, device.DisplayName);
        AppendSearchText(builder, device.VendorProductLabel);
        return NormalizeSearchText(builder.ToString());
    }

    private static void AppendSearchText(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }
        builder.Append(value);
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(value.Length);
        bool previousWasWhitespace = false;
        foreach (char ch in value.Trim().ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-')
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(ch);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private readonly struct BrandMatchProfile
    {
        private readonly string[] _nameTokens;

        private readonly int[] _vendorIds;

        public BrandMatchProfile(string brandKey, string[] nameTokens, int[] vendorIds = null)
        {
            BrandKey = brandKey ?? FallbackBrandKey;
            _nameTokens = nameTokens ?? Array.Empty<string>();
            _vendorIds = vendorIds ?? Array.Empty<int>();
        }

        public string BrandKey { get; }

        public bool IsNameMatch(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return false;
            }

            foreach (string token in _nameTokens)
            {
                if (!string.IsNullOrWhiteSpace(token) && IsTokenMatch(searchText, token))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsHardwareMatch(int vendorId)
        {
            for (int index = 0; index < _vendorIds.Length; index++)
            {
                if (_vendorIds[index] == vendorId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTokenMatch(string searchText, string token)
        {
            if (token.IndexOf(' ') >= 0 || ContainsNonAscii(token))
            {
                return searchText.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            int searchStart = 0;
            while (searchStart < searchText.Length)
            {
                int matchIndex = searchText.IndexOf(token, searchStart, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return false;
                }

                int matchEnd = matchIndex + token.Length;
                if (IsBoundary(searchText, matchIndex - 1) && IsBoundary(searchText, matchEnd))
                {
                    return true;
                }

                searchStart = matchEnd;
            }

            return false;
        }

        private static bool IsBoundary(string value, int index)
        {
            return index < 0 || index >= value.Length || !char.IsLetterOrDigit(value[index]);
        }

        private static bool ContainsNonAscii(string value)
        {
            foreach (char ch in value)
            {
                if (ch > 0x7F)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
