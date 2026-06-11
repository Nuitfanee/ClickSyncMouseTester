using System.Text.Json.Serialization;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceExchangeSessionDocument
{
    [JsonPropertyName("c")]
    public double? EffectiveCpi { get; set; }

    [JsonPropertyName("f")]
    public long HostTimestampFrequency { get; set; }
}





