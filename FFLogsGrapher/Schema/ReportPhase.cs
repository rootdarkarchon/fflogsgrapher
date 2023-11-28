using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal record ReportPhase
{
    [JsonPropertyName("boss")]
    public int Boss { get; set; }
    [JsonPropertyName("phases")]
    public List<string> Phases { get; set; }
}