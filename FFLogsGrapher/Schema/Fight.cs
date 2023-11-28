using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal partial record Fight
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("boss")]
    public int Boss { get; set; }
    [JsonPropertyName("zoneName")]
    public string ZoneName { get; set; }
    [JsonPropertyName("kill")]
    public bool Kill { get; set; }
    [JsonPropertyName("start_time")]
    public TimeSpan StartTimeOffset { get; set; }
    [JsonPropertyName("end_time")]
    public TimeSpan EndTimeOffset { get; set; }
    [JsonPropertyName("combatTime")]
    public TimeSpan CombatTime { get; set; }
    [JsonPropertyName("lastPhaseAsAbsoluteIndex")]
    public int LastPhaseAsAbsoluteIndex { get; set; }
    [JsonPropertyName("lastPhaseForPercentageDisplay")]
    public int LastPhaseForPercentageDisplay { get; set; }
    [JsonPropertyName("phases")]
    public List<FightPhase> Phases { get; set; }
    [JsonPropertyName("bossPercentage")]
    public int PhasePercentage { get; set; }
    [JsonPropertyName("fightPercentage")]
    public int FightPercentage { get; set; }
}
