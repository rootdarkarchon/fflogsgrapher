using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal partial record Dps
{
    [JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }
    [JsonPropertyName("downtime")]
    public TimeSpan DownTime { get; set; }
    [JsonPropertyName("entries")]
    public List<DpsPlayerEntry> PlayerEntries { get; set; }
}

internal partial record Dps
{ 
    public void SetValues(Report report, Fight fight, FightPhase fightPhase)
    {
        PlayerEntries.RemoveAll(e => !report.Players.Select(e => e.Guid).Contains(e.Guid));

        foreach (var entry in PlayerEntries)
        {
            entry.SetValues(this);

            if (report.PlayerDict[entry.Guid].DpsEntries == null) report.PlayerDict[entry.Guid].DpsEntries = new();
            if (!report.PlayerDict[entry.Guid].DpsEntries.TryGetValue(fight, out var playerFight))
            {
                report.PlayerDict[entry.Guid].DpsEntries[fight] = playerFight = new();
            }

            playerFight.Add(fightPhase, entry);

            fightPhase.DpsPlayerEntries ??= new();

            fightPhase.DpsPlayerEntries[report.PlayerDict[entry.Guid]] = entry;
        }

    }
}