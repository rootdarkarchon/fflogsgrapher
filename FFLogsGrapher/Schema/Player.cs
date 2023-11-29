using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal partial record Player
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("guid")]
    public int Guid { get; set; }
    [JsonPropertyName("type")]
    public string Job { get; set; }
    [JsonPropertyName("server")]
    public string Server { get; set; }
}

internal partial record Player
{
    public Dictionary<Fight, Dictionary<FightPhase, DpsPlayerEntry>> DpsEntries { get; set; }
    public double GetDpsForFight(Fight fight)
    {
        if (!DpsEntries.TryGetValue(fight, out var phases))
        {
            return 0;
        }

        var allPhases = phases.Select(p => p.Value).ToList();
        return allPhases.Sum(p => p.TotalDPS) / phases.Sum(p => p.Key.Duration.TotalSeconds);
    }

    public double GetRdpsForFight(Fight fight)
    {
        if (!DpsEntries.TryGetValue(fight, out var phases))
        {
            return 0;
        }

        var allPhases = phases.Select(p => p.Value).ToList();
        return allPhases.Sum(p => p.TotalRDPS) / phases.Sum(p => p.Key.Duration.TotalSeconds);
    }
}