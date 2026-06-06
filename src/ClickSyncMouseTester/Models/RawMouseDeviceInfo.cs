namespace ClickSyncMouseTester.Models;

public class RawMouseDeviceInfo
{
    private const string MouseInterfaceClassGuid = "#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";

    private readonly string _deviceId;
    private readonly string _displayName;
    private readonly string _selectionDisplayName;
    private readonly int? _vendorId;
    private readonly int? _productId;
    private readonly int _buttonCount;
    private readonly bool _isVirtual;
    private readonly string _physicalDeviceKey;
    private readonly RawMouseEndpointKind _endpointKind;
    private readonly bool _isVisibleByDefault;
    private readonly bool _isRecommended;
    private readonly string _endpointToken;

    public string DeviceId => _deviceId;

    public string DisplayName => _displayName;

    public string SelectionDisplayName => string.IsNullOrWhiteSpace(_selectionDisplayName) ? _displayName : _selectionDisplayName;

    public int? VendorId => _vendorId;

    public int? ProductId => _productId;

    public int ButtonCount => _buttonCount;

    public bool IsVirtual => _isVirtual;

    public string PhysicalDeviceKey => _physicalDeviceKey;

    public RawMouseEndpointKind EndpointKind => _endpointKind;

    public bool IsVisibleByDefault => _isVisibleByDefault;

    public bool IsRecommended => _isRecommended;

    public string EndpointToken => _endpointToken;

    public string VendorProductLabel
    {
        get
        {
            if (VendorId.HasValue && ProductId.HasValue)
            {
                return $"VID_{VendorId.Value:X4} / PID_{ProductId.Value:X4}";
            }
            return string.Empty;
        }
    }

    public string PathSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DeviceId))
            {
                return "Unknown path";
            }

            string normalizedPath = DeviceId.Replace("\\\\?\\", string.Empty);
            normalizedPath = normalizedPath.Replace(MouseInterfaceClassGuid, string.Empty, System.StringComparison.OrdinalIgnoreCase);
            if (normalizedPath.Length <= 48)
            {
                return normalizedPath;
            }

            const int prefixLength = 24;
            const int suffixLength = 18;
            return normalizedPath.Substring(0, prefixLength) + "..." + normalizedPath.Substring(normalizedPath.Length - suffixLength);
        }
    }

    public string DisplayLabel
    {
        get
        {
            string displayName = SelectionDisplayName;
            if (string.IsNullOrWhiteSpace(VendorProductLabel))
            {
                return displayName;
            }
            return displayName + "   " + VendorProductLabel;
        }
    }

    public RawMouseDeviceInfo(string deviceId, string displayName, int? vendorId, int? productId, int buttonCount, bool isVirtual)
        : this(deviceId, displayName, vendorId, productId, buttonCount, isVirtual, string.Empty)
    {
    }

    public RawMouseDeviceInfo(string deviceId, string displayName, int? vendorId, int? productId, int buttonCount, bool isVirtual, string physicalDeviceKey)
        : this(
              deviceId,
              displayName,
              displayName,
              vendorId,
              productId,
              buttonCount,
              isVirtual,
              physicalDeviceKey,
              isVirtual ? RawMouseEndpointKind.Virtual : RawMouseEndpointKind.Unknown,
              !isVirtual,
              !isVirtual,
              string.Empty)
    {
    }

    private RawMouseDeviceInfo(
        string deviceId,
        string displayName,
        string selectionDisplayName,
        int? vendorId,
        int? productId,
        int buttonCount,
        bool isVirtual,
        string physicalDeviceKey,
        RawMouseEndpointKind endpointKind,
        bool isVisibleByDefault,
        bool isRecommended,
        string endpointToken)
    {
        _deviceId = deviceId ?? string.Empty;
        _displayName = displayName ?? "HID Mouse";
        _selectionDisplayName = string.IsNullOrWhiteSpace(selectionDisplayName) ? _displayName : selectionDisplayName;
        _vendorId = vendorId;
        _productId = productId;
        _buttonCount = buttonCount;
        _isVirtual = isVirtual;
        _physicalDeviceKey = physicalDeviceKey ?? string.Empty;
        _endpointKind = isVirtual ? RawMouseEndpointKind.Virtual : endpointKind;
        _isVisibleByDefault = isVisibleByDefault && !isVirtual;
        _isRecommended = isRecommended && !isVirtual;
        _endpointToken = endpointToken ?? string.Empty;
    }

    public RawMouseDeviceInfo WithEndpointMetadata(RawMouseEndpointKind endpointKind, bool isVisibleByDefault, bool isRecommended, string endpointToken)
    {
        return new RawMouseDeviceInfo(
            DeviceId,
            DisplayName,
            DisplayName,
            VendorId,
            ProductId,
            ButtonCount,
            IsVirtual,
            PhysicalDeviceKey,
            endpointKind,
            isVisibleByDefault,
            isRecommended,
            endpointToken);
    }

    public RawMouseDeviceInfo WithSelectionDisplayName(string selectionDisplayName)
    {
        return new RawMouseDeviceInfo(
            DeviceId,
            DisplayName,
            selectionDisplayName,
            VendorId,
            ProductId,
            ButtonCount,
            IsVirtual,
            PhysicalDeviceKey,
            EndpointKind,
            IsVisibleByDefault,
            IsRecommended,
            EndpointToken);
    }

    public override string ToString()
    {
        return DisplayLabel;
    }
}
