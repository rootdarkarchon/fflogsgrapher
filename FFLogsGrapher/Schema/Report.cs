using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal partial record Report
{
    [JsonPropertyName("title")]
    public string Title { get; set; }
    [JsonPropertyName("start")]
    public DateTime Start { get; set; }
    [JsonPropertyName("end")]
    public DateTime End { get; set; }
    [JsonPropertyName("fights")]
    public List<Fight> Fights { get; set; }
    [JsonPropertyName("phases")]
    public List<ReportPhase> Phases { get; set; }
    [JsonPropertyName("friendlies")]
    public List<Player> Players { get; set; }
}
