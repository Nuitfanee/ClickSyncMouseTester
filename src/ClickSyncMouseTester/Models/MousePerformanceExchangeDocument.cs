using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceExchangeDocument
{
    [JsonPropertyName("v")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("t")]
    public string ExportedAtUtc { get; set; }

    [JsonPropertyName("m")]
    public MousePerformanceExchangeMouseDocument Mouse { get; set; }

    [JsonPropertyName("s")]
    public MousePerformanceExchangeSessionDocument Session { get; set; }

    [JsonPropertyName("g")]
    public List<MousePerformanceExchangeSegmentDocument> Segments { get; set; }
}





