using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickSyncMouseTester.Services;

public sealed class MousePerformanceExchangeService
{
    private const int CurrentFormatVersion = 8;

    private const int MinimumSupportedFormatVersion = 4;

    private static readonly HashSet<string> ReservedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
        "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
        "LPT8", "LPT9"
    };

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private MousePerformanceExchangeService()
    {
    }

    public static MousePerformanceSessionArchive ImportSession(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A valid file path is required.", nameof(filePath));
        }
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The selected file does not exist.", filePath);
        }
        MousePerformanceExchangeDocument mousePerformanceExchangeDocument = JsonSerializer.Deserialize<MousePerformanceExchangeDocument>(File.ReadAllText(filePath, Encoding.UTF8), JsonOptions);
        if (mousePerformanceExchangeDocument == null)
        {
            throw new InvalidDataException("The selected file is empty or invalid.");
        }
        if (mousePerformanceExchangeDocument.FormatVersion < MinimumSupportedFormatVersion || mousePerformanceExchangeDocument.FormatVersion > CurrentFormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {mousePerformanceExchangeDocument.FormatVersion}.");
        }
        return ImportRawPacketDocument(mousePerformanceExchangeDocument, filePath);
    }

    public static void ExportSession(MousePerformanceSessionArchive session, string filePath)
    {
        if (session == null || session.Snapshot == null || !session.Snapshot.HasData)
        {
            throw new InvalidOperationException("There is no mouse performance session data to export.");
        }
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A valid export path is required.", nameof(filePath));
        }
        string contents = JsonSerializer.Serialize(CreateDocument(session), JsonOptions);
        File.WriteAllText(filePath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string BuildSuggestedFileName(MousePerformanceSessionArchive session)
    {
        string fileName = ResolveFallbackFileName();
        if (session != null && session.Metadata != null && !string.IsNullOrWhiteSpace(session.Metadata.DisplayName))
        {
            fileName = session.Metadata.DisplayName.Trim();
        }
        string arg = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"{SanitizeFileName(fileName)}_{arg}.json";
    }

    public static string SanitizeFileName(string fileName)
    {
        string sanitizedFileName = (fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sanitizedFileName))
        {
            sanitizedFileName = ResolveFallbackFileName();
        }
        char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
        foreach (char oldChar in invalidFileNameChars)
        {
            sanitizedFileName = sanitizedFileName.Replace(oldChar, '_');
        }
        sanitizedFileName = sanitizedFileName.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(sanitizedFileName))
        {
            sanitizedFileName = ResolveFallbackFileName();
        }
        if (ReservedFileNames.Contains(sanitizedFileName))
        {
            sanitizedFileName += "_";
        }
        return sanitizedFileName;
    }

    private static string ResolveFallbackFileName()
    {
        LocalizationManager instance = LocalizationManager.Instance;
        instance.Initialize();
        string fallbackFileName = instance.GetString("MousePerformance.Export.FileNameFallback");
        if (string.IsNullOrWhiteSpace(fallbackFileName) || string.Equals(fallbackFileName, "MousePerformance.Export.FileNameFallback", StringComparison.Ordinal))
        {
            return "mouse-performance-session";
        }
        return fallbackFileName.Trim();
    }

    private static MousePerformanceExchangeDocument CreateDocument(MousePerformanceSessionArchive session)
    {
        MousePerformanceSnapshot snapshot = session.Snapshot;
        MousePerformanceSessionMetadata metadata = session.Metadata;
        MousePerformanceExchangeDocument mousePerformanceExchangeDocument = new MousePerformanceExchangeDocument();
        mousePerformanceExchangeDocument.FormatVersion = CurrentFormatVersion;
        mousePerformanceExchangeDocument.ExportedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        mousePerformanceExchangeDocument.Mouse = new MousePerformanceExchangeMouseDocument
        {
            DisplayName = metadata?.DisplayName,
            DeviceId = metadata?.DeviceId,
            VendorId = metadata?.VendorId,
            ProductId = metadata?.ProductId,
            ButtonCount = (metadata?.ButtonCount ?? 0),
            IsVirtual = (metadata?.IsVirtual ?? false),
            PathSummary = metadata?.PathSummary
        };
        mousePerformanceExchangeDocument.Session = new MousePerformanceExchangeSessionDocument
        {
            EffectiveCpi = snapshot?.EffectiveCpi,
            HostTimestampFrequency = Stopwatch.Frequency
        };
        mousePerformanceExchangeDocument.Segments = CreateCompactSegmentDocuments(snapshot);
        return mousePerformanceExchangeDocument;
    }

    private static MousePerformanceSessionArchive ImportRawPacketDocument(MousePerformanceExchangeDocument document, string filePath)
    {
        if (document.Mouse == null)
        {
            throw new InvalidDataException("The selected file is missing required mouse metadata.");
        }
        MousePerformanceSnapshot snapshot = RebuildSnapshotFromRawPackets(document.Mouse, document.Session, document.Segments);
        return CreateImportedSessionArchive(document.Mouse, snapshot, filePath);
    }

    private static MousePerformanceSessionArchive CreateImportedSessionArchive(MousePerformanceExchangeMouseDocument mouseDocument, MousePerformanceSnapshot snapshot, string filePath)
    {
        MousePerformanceSessionMetadata metadata = CreateMetadata(mouseDocument);
        return new MousePerformanceSessionArchive(MousePerformanceSessionSourceMode.Imported, metadata, snapshot, filePath);
    }

    private static MousePerformanceSessionMetadata CreateMetadata(MousePerformanceExchangeMouseDocument document)
    {
        if (document == null)
        {
            return new MousePerformanceSessionMetadata(string.Empty, string.Empty, null, null, 0, isVirtual: false, string.Empty);
        }
        return new MousePerformanceSessionMetadata(document.DisplayName, document.DeviceId, document.VendorId, document.ProductId, document.ButtonCount, document.IsVirtual, document.PathSummary);
    }

    private static MousePerformanceSnapshot RebuildSnapshotFromRawPackets(MousePerformanceExchangeMouseDocument mouseDocument, MousePerformanceExchangeSessionDocument sessionDocument, IReadOnlyList<MousePerformanceExchangeSegmentDocument> segments)
    {
        MousePerformanceEngine mousePerformanceEngine = new MousePerformanceEngine(MousePerformanceAnalysisOptions.Default);
        double? effectiveCpi = NormalizeEffectiveCpi(sessionDocument?.EffectiveCpi);
        mousePerformanceEngine.SetCpiState(effectiveCpi, effectiveCpi.HasValue);
        string deviceId = mouseDocument?.DeviceId ?? string.Empty;
        bool hasStartedCollection = false;
        bool hasDecodedPackets = false;
        long sourceTimestampFrequency = NormalizeTimestampFrequency(sessionDocument?.HostTimestampFrequency);
        foreach (MousePerformanceExchangeSegmentDocument segment in EnumerateCompactSegments(segments, sourceTimestampFrequency))
        {
            if (!hasStartedCollection)
            {
                mousePerformanceEngine.BeginCollecting(deviceId, segment.StartedAtRawCaptureTicks, startFresh: true);
                hasStartedCollection = true;
            }
            else
            {
                mousePerformanceEngine.PauseCollecting();
                mousePerformanceEngine.BeginCollecting(deviceId, segment.StartedAtRawCaptureTicks, startFresh: false);
            }
            foreach (RawMousePacket decodedPacket in DecodePacketRows(segment))
            {
                hasDecodedPackets = true;
                mousePerformanceEngine.PushPacket(new RawMousePacket(deviceId, decodedPacket.RawCaptureTicks, 0.0, decodedPacket.CaptureSequence, decodedPacket.DeltaX, decodedPacket.DeltaY, decodedPacket.ButtonFlags, decodedPacket.RawMouseFlags, decodedPacket.ButtonData, decodedPacket.ExtraInformation, decodedPacket.TimingSequence, decodedPacket.MovementMode));
            }
        }
        if (!hasStartedCollection || !hasDecodedPackets)
        {
            throw new InvalidDataException("The selected file does not contain any usable raw motion packets.");
        }
        mousePerformanceEngine.StopCollecting();
        MousePerformanceSnapshot mousePerformanceSnapshot = mousePerformanceEngine.CreateAnalysisSnapshot(MousePerformanceSessionStatus.Stopped, isLocked: false, 0, 0, 0, includeDataQuality: false);
        if (mousePerformanceSnapshot == null || mousePerformanceSnapshot.Events == null || mousePerformanceSnapshot.Events.Count == 0)
        {
            throw new InvalidDataException("The selected file does not contain any motion events.");
        }
        return mousePerformanceSnapshot;
    }

    private static List<MousePerformanceExchangeSegmentDocument> CreateCompactSegmentDocuments(MousePerformanceSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Events == null || snapshot.Events.Count == 0)
        {
            return new List<MousePerformanceExchangeSegmentDocument>();
        }
        List<MousePerformanceSessionSegment> sessionSegments = (snapshot.SessionSegments ?? Array.Empty<MousePerformanceSessionSegment>()).Where((MousePerformanceSessionSegment segment) => segment != null).ToList();
        Dictionary<int, long> segmentStartTicksById = new Dictionary<int, long>();
        foreach (MousePerformanceSessionSegment segment in sessionSegments)
        {
            if (segment.SegmentId <= 0)
            {
                throw new InvalidOperationException("The session contains an invalid segment identifier.");
            }
            if (segmentStartTicksById.ContainsKey(segment.SegmentId))
            {
                throw new InvalidOperationException("The session contains duplicate segment identifiers.");
            }
            segmentStartTicksById[segment.SegmentId] = Math.Max(0L, segment.StartedAtRawCaptureTicks);
        }
        List<MousePerformanceExchangeSegmentDocument> compactSegments = new List<MousePerformanceExchangeSegmentDocument>();
        MousePerformanceExchangeSegmentDocument mousePerformanceExchangeSegmentDocument = null;
        int currentSegmentId = int.MinValue;
        long segmentStartedAtTicks = 0L;
        long segmentBaseCaptureSequence = 0L;
        long previousRawCaptureTicks = 0L;
        long previousCaptureSequence = 0L;
        foreach (MousePerformanceEvent @event in snapshot.Events)
        {
            if (@event == null)
            {
                continue;
            }
            if (@event.SessionSegmentId != currentSegmentId)
            {
                int sessionSegmentId = @event.SessionSegmentId;
                if (sessionSegmentId <= 0 || !segmentStartTicksById.TryGetValue(sessionSegmentId, out segmentStartedAtTicks))
                {
                    throw new InvalidOperationException("The session contains events without segment anchors.");
                }
                currentSegmentId = sessionSegmentId;
                segmentBaseCaptureSequence = Math.Max(0L, @event.CaptureSequence);
                mousePerformanceExchangeSegmentDocument = new MousePerformanceExchangeSegmentDocument
                {
                    SegmentId = compactSegments.Count + 1,
                    StartedAtRawCaptureTicks = segmentStartedAtTicks,
                    BaseCaptureSequence = segmentBaseCaptureSequence,
                    PacketRows = new List<long[]>()
                };
                previousRawCaptureTicks = segmentStartedAtTicks;
                previousCaptureSequence = segmentBaseCaptureSequence;
                compactSegments.Add(mousePerformanceExchangeSegmentDocument);
            }
            if (mousePerformanceExchangeSegmentDocument == null)
            {
                throw new InvalidOperationException("The session could not be compacted for export.");
            }
            long rawTickDelta = @event.RawCaptureTicks - previousRawCaptureTicks;
            if (rawTickDelta < 0)
            {
                throw new InvalidOperationException("The session contains raw capture ticks out of order.");
            }
            long captureSequenceDelta = @event.CaptureSequence - previousCaptureSequence;
            if (captureSequenceDelta < 0)
            {
                throw new InvalidOperationException("The session contains capture sequences out of order.");
            }
            mousePerformanceExchangeSegmentDocument.PacketRows.Add(new long[11] { @event.RawDeltaX, @event.RawDeltaY, @event.ButtonFlags, @event.RawMouseFlags, @event.ButtonData, @event.ExtraInformation, rawTickDelta, captureSequenceDelta, (long)@event.PacketKind, (long)@event.MovementMode, Math.Max(0L, @event.TimingSequence) });
            previousRawCaptureTicks = @event.RawCaptureTicks;
            previousCaptureSequence = @event.CaptureSequence;
        }
        return compactSegments;
    }

    private static IEnumerable<MousePerformanceExchangeSegmentDocument> EnumerateCompactSegments(IReadOnlyList<MousePerformanceExchangeSegmentDocument> segments, long sourceTimestampFrequency)
    {
        if (segments == null || segments.Count == 0)
        {
            throw new InvalidDataException("The selected file does not contain any session segment anchors.");
        }
        List<MousePerformanceExchangeSegmentDocument> orderedSegments = (from segment in segments
                                                                         where segment != null
                                                                         orderby segment.SegmentId
                                                                         select segment).ToList();
        if (orderedSegments.Count == 0)
        {
            throw new InvalidDataException("The selected file does not contain any usable session segment anchors.");
        }
        int previousSegmentId = 0;
        long previousSegmentStartTicks = long.MinValue;
        long previousBaseCaptureSequence = long.MinValue;
        foreach (MousePerformanceExchangeSegmentDocument segment in orderedSegments)
        {
            if (segment.SegmentId <= 0)
            {
                throw new InvalidDataException("The selected file contains an invalid session segment identifier.");
            }
            if (segment.SegmentId <= previousSegmentId)
            {
                throw new InvalidDataException("The selected file contains duplicate or out-of-order session segment identifiers.");
            }
            if (segment.StartedAtRawCaptureTicks < 0)
            {
                throw new InvalidDataException("The selected file contains a negative session segment anchor.");
            }
            if (segment.BaseCaptureSequence < 0)
            {
                throw new InvalidDataException("The selected file contains a negative session segment base sequence.");
            }
            if (segment.StartedAtRawCaptureTicks < previousSegmentStartTicks)
            {
                throw new InvalidDataException("The selected file contains session segments out of chronological order.");
            }
            if (segment.BaseCaptureSequence < previousBaseCaptureSequence)
            {
                throw new InvalidDataException("The selected file contains session segments with out-of-order base sequences.");
            }
            if (segment.PacketRows == null || segment.PacketRows.Count == 0)
            {
                throw new InvalidDataException("The selected file contains an empty session segment.");
            }
            previousSegmentStartTicks = segment.StartedAtRawCaptureTicks;
            previousBaseCaptureSequence = segment.BaseCaptureSequence;
            previousSegmentId = segment.SegmentId;
            yield return NormalizeSegmentTiming(segment, sourceTimestampFrequency);
        }
    }

    private static MousePerformanceExchangeSegmentDocument NormalizeSegmentTiming(MousePerformanceExchangeSegmentDocument segment, long sourceTimestampFrequency)
    {
        if (segment == null || sourceTimestampFrequency == Stopwatch.Frequency)
        {
            return segment;
        }

        MousePerformanceExchangeSegmentDocument normalizedSegment = new MousePerformanceExchangeSegmentDocument
        {
            SegmentId = segment.SegmentId,
            StartedAtRawCaptureTicks = ConvertTimestampTicks(segment.StartedAtRawCaptureTicks, sourceTimestampFrequency),
            BaseCaptureSequence = segment.BaseCaptureSequence,
            PacketRows = new List<long[]>()
        };
        foreach (long[] row in segment.PacketRows ?? new List<long[]>())
        {
            if (row == null)
            {
                continue;
            }

            long[] normalizedRow = (long[])row.Clone();
            int rawTickDeltaColumn = normalizedRow.Length >= 8 ? 6 : 3;
            if (normalizedRow.Length == 5 || normalizedRow.Length >= 8)
            {
                normalizedRow[rawTickDeltaColumn] = ConvertTimestampTicks(normalizedRow[rawTickDeltaColumn], sourceTimestampFrequency);
            }
            normalizedSegment.PacketRows.Add(normalizedRow);
        }
        return normalizedSegment;
    }

    private static IEnumerable<RawMousePacket> DecodePacketRows(MousePerformanceExchangeSegmentDocument segment)
    {
        if (segment == null)
        {
            throw new InvalidDataException("The selected file contains an invalid session segment.");
        }
        List<long[]> packetRows = (segment.PacketRows ?? new List<long[]>()).Where((long[] row) => row != null).ToList();
        if (packetRows.Count == 0)
        {
            yield break;
        }
        long currentRawCaptureTicks = Math.Max(0L, segment.StartedAtRawCaptureTicks);
        long currentCaptureSequence = Math.Max(0L, segment.BaseCaptureSequence);
        long currentTimingSequence = 0L;
        foreach (long[] packetRow in packetRows)
        {
            if (packetRow.Length != 5 && packetRow.Length != 8 && packetRow.Length != 9 && packetRow.Length != 10 && packetRow.Length != 11)
            {
                throw new InvalidDataException("The selected file contains an invalid compact packet row.");
            }
            ushort rawMouseFlags = 0;
            ushort buttonData = 0;
            uint extraInformation = 0u;
            RawMouseMovementMode movementMode = RawMouseMovementMode.Unknown;
            int rawTickDeltaColumn = 3;
            int captureSequenceDeltaColumn = 4;
            if (packetRow.Length >= 8)
            {
                rawMouseFlags = NormalizeUShortValue(packetRow[3], "raw mouse flag");
                buttonData = NormalizeUShortValue(packetRow[4], "button data");
                extraInformation = NormalizeUIntValue(packetRow[5], "extra information");
                rawTickDeltaColumn = 6;
                captureSequenceDeltaColumn = 7;
            }
            if (packetRow.Length >= 10)
            {
                movementMode = NormalizeMovementMode(packetRow[9]);
            }
            long rawTickDelta = packetRow[rawTickDeltaColumn];
            long captureSequenceDelta = packetRow[captureSequenceDeltaColumn];
            long timingSequence = packetRow.Length >= 11 ? packetRow[10] : currentCaptureSequence + captureSequenceDelta;
            if (rawTickDelta < 0 || captureSequenceDelta < 0 || timingSequence < 0)
            {
                throw new InvalidDataException("The selected file contains a compact packet row with negative deltas.");
            }
            long nextRawCaptureTicks = currentRawCaptureTicks + rawTickDelta;
            long nextCaptureSequence = currentCaptureSequence + captureSequenceDelta;
            if (nextRawCaptureTicks < currentRawCaptureTicks)
            {
                throw new InvalidDataException("The selected file contains compact packet rows out of order.");
            }
            if (nextCaptureSequence < currentCaptureSequence)
            {
                throw new InvalidDataException("The selected file contains compact packet sequences out of order.");
            }
            if (timingSequence < currentTimingSequence)
            {
                throw new InvalidDataException("The selected file contains compact packet timing sequences out of order.");
            }
            currentRawCaptureTicks = nextRawCaptureTicks;
            currentCaptureSequence = nextCaptureSequence;
            currentTimingSequence = timingSequence;
            yield return new RawMousePacket(string.Empty, nextRawCaptureTicks, 0.0, nextCaptureSequence, NormalizeIntValue(packetRow[0], "delta X"), NormalizeIntValue(packetRow[1], "delta Y"), NormalizeUShortValue(packetRow[2], "button flag"), rawMouseFlags, buttonData, extraInformation, timingSequence, movementMode);
        }
    }

    private static int NormalizeIntValue(long value, string fieldName)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new InvalidDataException($"The selected file contains an invalid {fieldName} value.");
        }
        return (int)value;
    }

    private static RawMouseMovementMode NormalizeMovementMode(long value)
    {
        if (value == (long)RawMouseMovementMode.Relative)
        {
            return RawMouseMovementMode.Relative;
        }
        if (value == (long)RawMouseMovementMode.Absolute)
        {
            return RawMouseMovementMode.Absolute;
        }
        if (value == (long)RawMouseMovementMode.Unknown)
        {
            return RawMouseMovementMode.Unknown;
        }
        throw new InvalidDataException("The selected file contains an invalid movement mode value.");
    }

    private static ushort NormalizeUShortValue(long value, string fieldName)
    {
        if (value < 0 || value > 65535)
        {
            throw new InvalidDataException($"The selected file contains an invalid {fieldName} value.");
        }
        return (ushort)value;
    }

    private static uint NormalizeUIntValue(long value, string fieldName)
    {
        if (value < 0 || value > uint.MaxValue)
        {
            throw new InvalidDataException($"The selected file contains an invalid {fieldName} value.");
        }
        return (uint)value;
    }

    private static double? NormalizeEffectiveCpi(double? value)
    {
        return value.HasValue && value.Value > 0.0 && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value.Value
            : null;
    }

    private static long NormalizeTimestampFrequency(long? value)
    {
        if (value.HasValue && value.Value > 0L)
        {
            return value.Value;
        }
        return Stopwatch.Frequency;
    }

    private static long ConvertTimestampTicks(long ticks, long sourceTimestampFrequency)
    {
        long sourceFrequency = sourceTimestampFrequency > 0L ? sourceTimestampFrequency : Stopwatch.Frequency;
        if (ticks <= 0L || sourceFrequency == Stopwatch.Frequency)
        {
            return Math.Max(0L, ticks);
        }
        decimal convertedTicks = (decimal)ticks * Stopwatch.Frequency / sourceFrequency;
        if (convertedTicks >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return (long)Math.Round(convertedTicks, MidpointRounding.AwayFromZero);
    }
}
