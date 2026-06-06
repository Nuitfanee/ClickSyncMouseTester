using System.Text.Json.Serialization;

namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceExchangeMouseDocument
{
    [JsonPropertyName("n")]
    public string DisplayName { get; set; }

    [JsonPropertyName("d")]
    public string DeviceId { get; set; }

    [JsonPropertyName("vi")]
    public int? VendorId { get; set; }

    [JsonPropertyName("pi")]
    public int? ProductId { get; set; }

    [JsonPropertyName("b")]
    public int ButtonCount { get; set; }

    [JsonPropertyName("x")]
    public bool IsVirtual { get; set; }

    [JsonPropertyName("p")]
    public string PathSummary { get; set; }
}





