namespace FFLogsGrapher.Schema;

internal partial record FightPhase
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string Name { get; set; }
    public Dictionary<Player, DpsPlayerEntry> DpsPlayerEntries { get; set; }
    public bool HasNextPhase { get; set; }
    public void SetTime(Report report, Fight fight, FightPhase? nextPhase)
    {
        StartTime = report.Start.ToLocalTime() + StartTimeOffset;
        EndTime = report.Start.ToLocalTime() + (nextPhase == null ? fight.EndTimeOffset : nextPhase.StartTimeOffset);
        Duration = EndTime - StartTime;
        HasNextPhase = nextPhase != null;
    }
}
