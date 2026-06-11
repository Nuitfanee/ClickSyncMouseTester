using System;
using System.Diagnostics;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceEvent
{
    private readonly int _rawDeltaX;

    private readonly int _rawDeltaY;

    private readonly int _deltaX;

    private readonly int _deltaY;

    private readonly ushort _buttonFlags;

    private readonly ushort _rawMouseFlags;

    private readonly ushort _buttonData;

    private readonly uint _extraInformation;

    private readonly long _rawCaptureTicks;

    private readonly long _rawRelativeTicks;

    private readonly long _logicalTicks;

    private readonly MousePerformancePacketKind _packetKind;

    private readonly RawMouseMovementMode _movementMode;

    private readonly long _sessionCumulativeX;

    private readonly long _sessionCumulativeY;

    private readonly int _sessionSegmentId;

    private readonly long _captureSequence;

    private readonly long _timingSequence;

    public int RawDeltaX => _rawDeltaX;

    public int RawDeltaY => _rawDeltaY;

    public int DeltaX => _deltaX;

    public int DeltaY => _deltaY;

    public ushort ButtonFlags => _buttonFlags;

    public ushort RawMouseFlags => _rawMouseFlags;

    public ushort ButtonData => _buttonData;

    public uint ExtraInformation => _extraInformation;

    public long RawCaptureTicks => _rawCaptureTicks;

    public long RawRelativeTicks => _rawRelativeTicks;

    public long LogicalTicks => _logicalTicks;

    public double RawTimeMs => (double)_rawRelativeTicks * 1000.0 / (double)Stopwatch.Frequency;

    public double LogicalTimeMs => (double)_logicalTicks * 1000.0 / (double)Stopwatch.Frequency;

    public MousePerformancePacketKind PacketKind => _packetKind;

    public RawMouseMovementMode MovementMode => _movementMode;

    public long SessionCumulativeX => _sessionCumulativeX;

    public long SessionCumulativeY => _sessionCumulativeY;

    public int SessionSegmentId => _sessionSegmentId;

    public long CaptureSequence => _captureSequence;

    public long TimingSequence => _timingSequence;

    public MousePerformanceEvent(int rawDeltaX, int rawDeltaY, int deltaX, int deltaY, ushort buttonFlags, ushort rawMouseFlags, ushort buttonData, uint extraInformation, long rawCaptureTicks, long rawRelativeTicks, long logicalTicks, MousePerformancePacketKind packetKind, RawMouseMovementMode movementMode, long sessionCumulativeX, long sessionCumulativeY, int sessionSegmentId, long captureSequence, long timingSequence)
    {
        _rawDeltaX = rawDeltaX;
        _rawDeltaY = rawDeltaY;
        _deltaX = deltaX;
        _deltaY = deltaY;
        _buttonFlags = buttonFlags;
        _rawMouseFlags = rawMouseFlags;
        _buttonData = buttonData;
        _extraInformation = extraInformation;
        _rawCaptureTicks = Math.Max(0L, rawCaptureTicks);
        _rawRelativeTicks = Math.Max(0L, rawRelativeTicks);
        _logicalTicks = Math.Max(0L, logicalTicks);
        _packetKind = packetKind;
        _movementMode = movementMode;
        _sessionCumulativeX = sessionCumulativeX;
        _sessionCumulativeY = sessionCumulativeY;
        _sessionSegmentId = Math.Max(0, sessionSegmentId);
        _captureSequence = Math.Max(0L, captureSequence);
        _timingSequence = Math.Max(0L, timingSequence);
    }
}





