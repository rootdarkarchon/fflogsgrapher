namespace FFLogsGrapher.Schema;

internal partial record Fight
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string EndPhaseName { get; set; }
    public decimal EndPhasePercentage { get; set; }
    public decimal EndFightPercentage { get; set; }
    public decimal Weight { get; set; }
    public TimeSpan ActualCombatTime => (CombatTime == TimeSpan.Zero) ? EndTimeOffset - StartTimeOffset : CombatTime;
    public void SetValues(Report report)
    {
        StartTime = (report.Start + StartTimeOffset).ToLocalTime();
        EndTime = (report.Start + EndTimeOffset).ToLocalTime();
        if (report.Phases == null) return;
        EndPhaseName = report.Phases.Single(u => u.Boss == Boss).Phases[LastPhaseAsAbsoluteIndex];
        EndPhasePercentage = PhasePercentage / 100M;
        EndFightPercentage = FightPercentage / 100M;
        List<FightPhase> phasesToRemove = new();

        var totalCombatTime = report.Fights.Sum(f => f.ActualCombatTime.TotalMilliseconds);
        Weight = (decimal)(ActualCombatTime.TotalMilliseconds / totalCombatTime);

        for (int i = 0; i < Phases.Count; i++)
        {
            var nextPhase = (i >= Phases.Count - 1) ? null : Phases[i + 1];
            Phases[i].Name = report.Phases.Single(u => u.Boss == Boss).Phases[Phases[i].Id - 1];
            Phases[i].SetTime(report, this, nextPhase);
            if (Phases[i].Duration == TimeSpan.Zero) phasesToRemove.Add(Phases[i]);
        }

        Phases.RemoveAll(phasesToRemove.Contains);
    }

    public void Print()
    {
        Console.WriteLine("PullId: {0}, {1}: Start: {2}, End: {3}, Duration: {4}", Id, ZoneName, StartTime.TimeOfDay, EndTime.TimeOfDay, ActualCombatTime);
        Console.WriteLine("\tEnded on: {0} ({1}%), Total {2}%, Weight: {3}", EndPhaseName, EndPhasePercentage, EndFightPercentage, Weight);
        foreach (var phase in Phases ?? new List<FightPhase>())
        {
            Console.WriteLine("\t\t{0}: Duration: {1}", phase.Name, phase.Duration);
        }
    }
}
