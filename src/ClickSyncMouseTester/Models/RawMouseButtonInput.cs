namespace ClickSyncMouseTester.Models;

public class RawMouseButtonInput
{
    private readonly MouseButtonKind _buttonKind;

    private readonly bool _isButtonDown;

    private readonly double _timestampMs;

    public MouseButtonKind ButtonKind => _buttonKind;

    public bool IsButtonDown => _isButtonDown;

    public double TimestampMs => _timestampMs;

    public RawMouseButtonInput(MouseButtonKind buttonKind, bool isButtonDown, double timestampMs)
    {
        _buttonKind = buttonKind;
        _isButtonDown = isButtonDown;
        _timestampMs = timestampMs;
    }
}





