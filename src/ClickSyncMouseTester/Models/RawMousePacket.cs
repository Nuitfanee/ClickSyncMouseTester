using System;

namespace ClickSyncMouseTester.Models;

public class RawMousePacket
{
    private readonly string _deviceId;

    private readonly long _rawCaptureTicks;

    private readonly double _rawCaptureMs;

    private readonly long _captureSequence;

    private readonly long _timingSequence;

    private readonly int _deltaX;

    private readonly int _deltaY;

    private readonly ushort _buttonFlags;

    private readonly ushort _rawMouseFlags;

    private readonly ushort _buttonData;

    private readonly uint _extraInformation;

    private readonly RawMouseMovementMode _movementMode;

    public string DeviceId => _deviceId;

    public long RawCaptureTicks => _rawCaptureTicks;

    public double RawCaptureMs => _rawCaptureMs;

    public long CaptureSequence => _captureSequence;

    public long TimingSequence => _timingSequence;

    public int DeltaX => _deltaX;

    public int DeltaY => _deltaY;

    public ushort ButtonFlags => _buttonFlags;

    public ushort RawMouseFlags => _rawMouseFlags;

    public ushort ButtonData => _buttonData;

    public uint ExtraInformation => _extraInformation;

    public RawMouseMovementMode MovementMode => _movementMode;

    public long TimestampTicks => _rawCaptureTicks;

    public double TimestampMs => _rawCaptureMs;

    public RawMousePacket(string deviceId, long rawCaptureTicks, double rawCaptureMs, long captureSequence, int deltaX, int deltaY, ushort buttonFlags = 0, ushort rawMouseFlags = 0, ushort buttonData = 0, uint extraInformation = 0u, long timingSequence = 0L, RawMouseMovementMode movementMode = RawMouseMovementMode.Unknown)
    {
        _deviceId = deviceId ?? string.Empty;
        _rawCaptureTicks = Math.Max(0L, rawCaptureTicks);
        _rawCaptureMs = rawCaptureMs;
        _captureSequence = Math.Max(0L, captureSequence);
        _timingSequence = Math.Max(0L, timingSequence);
        _deltaX = deltaX;
        _deltaY = deltaY;
        _buttonFlags = buttonFlags;
        _rawMouseFlags = rawMouseFlags;
        _buttonData = buttonData;
        _extraInformation = extraInformation;
        _movementMode = movementMode == RawMouseMovementMode.Unknown ? ResolveMovementMode(rawMouseFlags) : movementMode;
    }

    private static RawMouseMovementMode ResolveMovementMode(ushort rawMouseFlags)
    {
        return (rawMouseFlags & 1) == 1 ? RawMouseMovementMode.Absolute : RawMouseMovementMode.Relative;
    }
}





