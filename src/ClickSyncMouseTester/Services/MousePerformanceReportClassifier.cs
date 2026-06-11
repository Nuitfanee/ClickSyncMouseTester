using ClickSyncMouseTester.Models;

namespace ClickSyncMouseTester.Services;

internal readonly struct MousePerformanceReportClassification
{
    public MousePerformancePacketKind PacketKind { get; }

    public int RelativeDeltaX { get; }

    public int RelativeDeltaY { get; }

    public bool IsControlReport { get; }

    public bool IsWheelOnlyReport { get; }

    public bool IsEmptyReport { get; }

    public bool IsZeroMotionReport { get; }

    public MousePerformanceReportClassification(MousePerformancePacketKind packetKind, int relativeDeltaX, int relativeDeltaY, bool isControlReport, bool isWheelOnlyReport, bool isEmptyReport, bool isZeroMotionReport)
    {
        PacketKind = packetKind;
        RelativeDeltaX = relativeDeltaX;
        RelativeDeltaY = relativeDeltaY;
        IsControlReport = isControlReport;
        IsWheelOnlyReport = isWheelOnlyReport;
        IsEmptyReport = isEmptyReport;
        IsZeroMotionReport = isZeroMotionReport;
    }
}

internal sealed class MousePerformanceReportClassifier
{
    private const ushort WheelButtonMask = 3072;

    public MousePerformanceReportClassification Classify(RawMousePacket packet)
    {
        int relativeDeltaX = ResolveRelativeDeltaX(packet);
        int relativeDeltaY = ResolveRelativeDeltaY(packet);
        MousePerformancePacketKind packetKind = ResolvePacketKind(packet);
        bool isControlReport = packetKind == MousePerformancePacketKind.ButtonOnly
            || packetKind == MousePerformancePacketKind.WheelOnly
            || packetKind == MousePerformancePacketKind.MotionWithButton
            || packetKind == MousePerformancePacketKind.MotionWithWheel
            || packetKind == MousePerformancePacketKind.MotionWithButtonAndWheel;
        bool isZeroMotionReport = packet == null || (packet.DeltaX == 0 && packet.DeltaY == 0);
        return new MousePerformanceReportClassification(
            packetKind,
            relativeDeltaX,
            relativeDeltaY,
            isControlReport,
            packetKind == MousePerformancePacketKind.WheelOnly,
            packetKind == MousePerformancePacketKind.Empty,
            isZeroMotionReport);
    }

    private static MousePerformancePacketKind ResolvePacketKind(RawMousePacket packet)
    {
        bool hasMotion = packet != null && (packet.DeltaX != 0 || packet.DeltaY != 0);
        ushort buttonFlags = packet?.ButtonFlags ?? 0;
        bool hasWheel = (buttonFlags & WheelButtonMask) != 0;
        bool hasButton = (buttonFlags & ~WheelButtonMask) != 0;
        if (hasMotion)
        {
            if (hasWheel && hasButton)
            {
                return MousePerformancePacketKind.MotionWithButtonAndWheel;
            }
            if (hasWheel)
            {
                return MousePerformancePacketKind.MotionWithWheel;
            }
            if (hasButton)
            {
                return MousePerformancePacketKind.MotionWithButton;
            }
            return MousePerformancePacketKind.Motion;
        }
        if (hasWheel)
        {
            return MousePerformancePacketKind.WheelOnly;
        }
        if (hasButton)
        {
            return MousePerformancePacketKind.ButtonOnly;
        }
        return MousePerformancePacketKind.Empty;
    }

    private static int ResolveRelativeDeltaX(RawMousePacket packet)
    {
        return packet != null && packet.MovementMode == RawMouseMovementMode.Relative ? packet.DeltaX : 0;
    }

    private static int ResolveRelativeDeltaY(RawMousePacket packet)
    {
        return packet != null && packet.MovementMode == RawMouseMovementMode.Relative ? packet.DeltaY : 0;
    }
}
