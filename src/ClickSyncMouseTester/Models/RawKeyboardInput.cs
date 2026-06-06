namespace ClickSyncMouseTester.Models;

public class RawKeyboardInput
{
    private readonly int _virtualKey;

    private readonly int _scanCode;

    private readonly bool _isExtendedKey;

    private readonly bool _isKeyDown;

    private readonly double _timestampMs;

    private readonly string _displayName;

    public int VirtualKey => _virtualKey;

    public int ScanCode => _scanCode;

    public bool IsExtendedKey => _isExtendedKey;

    public bool IsKeyDown => _isKeyDown;

    public double TimestampMs => _timestampMs;

    public string DisplayName => _displayName;

    public RawKeyboardInput(int virtualKey, int scanCode, bool isExtendedKey, bool isKeyDown, double timestampMs, string displayName)
    {
        _virtualKey = virtualKey;
        _scanCode = scanCode;
        _isExtendedKey = isExtendedKey;
        _isKeyDown = isKeyDown;
        _timestampMs = timestampMs;
        _displayName = displayName ?? string.Empty;
    }
}





