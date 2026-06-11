using System;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceSessionMetadata
{
    private readonly string _displayName;

    private readonly string _deviceId;

    private readonly int? _vendorId;

    private readonly int? _productId;

    private readonly int _buttonCount;

    private readonly bool _isVirtual;

    private readonly string _pathSummary;

    public string DisplayName => _displayName;

    public string DeviceId => _deviceId;

    public int? VendorId => _vendorId;

    public int? ProductId => _productId;

    public int ButtonCount => _buttonCount;

    public bool IsVirtual => _isVirtual;

    public string PathSummary => _pathSummary;

    public string VendorProductLabel
    {
        get
        {
            int? vendorId = _vendorId;
            if (vendorId.HasValue)
            {
                vendorId = _productId;
                if (vendorId.HasValue)
                {
                    vendorId = _vendorId;
                    object arg = vendorId.Value;
                    vendorId = _productId;
                    return $"VID_{arg:X4} / PID_{vendorId.Value:X4}";
                }
            }
            return string.Empty;
        }
    }

    public MousePerformanceSessionMetadata(string displayName, string deviceId, int? vendorId, int? productId, int buttonCount, bool isVirtual, string pathSummary)
    {
        _displayName = displayName ?? string.Empty;
        _deviceId = deviceId ?? string.Empty;
        _vendorId = vendorId;
        _productId = productId;
        _buttonCount = Math.Max(0, buttonCount);
        _isVirtual = isVirtual;
        _pathSummary = pathSummary ?? string.Empty;
    }
}





