namespace ClickSyncMouseTester.Services;

internal readonly struct RawMouseDeviceMetadata
{
    public static RawMouseDeviceMetadata Empty => new RawMouseDeviceMetadata(string.Empty, string.Empty);

    public string DisplayName { get; }

    public string PhysicalDeviceKey { get; }

    public RawMouseDeviceMetadata(string displayName, string physicalDeviceKey)
    {
        DisplayName = displayName ?? string.Empty;
        PhysicalDeviceKey = physicalDeviceKey ?? string.Empty;
    }
}
