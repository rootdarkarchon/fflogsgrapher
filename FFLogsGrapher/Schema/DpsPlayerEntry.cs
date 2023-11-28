using System.Text.Json.Serialization;

namespace FFLogsGrapher.Schema;

internal partial record DpsPlayerEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("guid")]
    public int Guid { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("total")]
    public double TotalDPS { get; set; }
    [JsonPropertyName("totalRDPS")]
    public double TotalRDPS { get; set; }
    [JsonPropertyName("totalRDPSTaken")]
    public double TotalRDPSTaken { get; set; }
    [JsonPropertyName("totalRDPSGiven")]
    public double TotalRDPSGiven { get; set; }
    [JsonPropertyName("totalADPS")]
    public double TotalADPS { get; set; }
    [JsonPropertyName("totalNDPS")]
    public double TotalNDPS { get; set; }
    [JsonPropertyName("activeTime")]
    public TimeSpan ActiveTime { get; set; }
}

internal partial record DpsPlayerEntry
{
    public double ActualDPS { get; set; }
    public double ActualRDPS { get; set; }
    public double ActualActiveTime { get; set; }
    public double ActualActiveTimePercent => ActualActiveTime * 100;

    public void SetValues(Dps entry)
    {
        ActualDPS = TotalDPS / entry.TotalTime.TotalSeconds;
        ActualRDPS = TotalRDPS / entry.TotalTime.TotalSeconds;
        ActualActiveTime = ActiveTime.TotalSeconds / entry.TotalTime.TotalSeconds;
    }
}