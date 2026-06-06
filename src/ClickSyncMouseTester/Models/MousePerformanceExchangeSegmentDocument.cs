using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceExchangeSegmentDocument
{
    [JsonPropertyName("i")]
    public int SegmentId { get; set; }

    [JsonPropertyName("t")]
    public long StartedAtRawCaptureTicks { get; set; }

    [JsonPropertyName("q")]
    public long BaseCaptureSequence { get; set; }

    [JsonPropertyName("r")]
    public List<long[]> PacketRows { get; set; }
}





