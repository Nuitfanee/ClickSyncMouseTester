namespace ClickSyncMouseTester.Models;

public sealed class KeyDetectionCustomKey
{
    private readonly int _virtualKey;

    private readonly int _scanCode;

    private readonly bool _isExtendedKey;

    private readonly string _displayName;

    public int VirtualKey => _virtualKey;

    public int ScanCode => _scanCode;

    public bool IsExtendedKey => _isExtendedKey;

    public string DisplayName => _displayName;

    public KeyDetectionCustomKey(int virtualKey, int scanCode, bool isExtendedKey, string displayName)
    {
        _virtualKey = virtualKey;
        _scanCode = scanCode;
        _isExtendedKey = isExtendedKey;
        _displayName = displayName ?? string.Empty;
    }

    public bool Matches(RawKeyboardInput input)
    {
        if (input == null)
        {
            return false;
        }
        return input.VirtualKey == VirtualKey && input.ScanCode == ScanCode && input.IsExtendedKey == IsExtendedKey;
    }

    public static KeyDetectionCustomKey FromInput(RawKeyboardInput input)
    {
        if (input == null)
        {
            return null;
        }
        return new KeyDetectionCustomKey(input.VirtualKey, input.ScanCode, input.IsExtendedKey, input.DisplayName);
    }
}





