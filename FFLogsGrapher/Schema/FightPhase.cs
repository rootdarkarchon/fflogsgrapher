using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal partial record FightPhase
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("startTime")]
    public TimeSpan StartTimeOffset { get; set; }
}
