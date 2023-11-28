namespace FFLogsGrapher.Schema;

internal partial record Report
{
    public TimeSpan TotalTime { get; set; }
    public TimeSpan TimeInCombat { get; set; }
    public TimeSpan TimeNotInCombat { get; set; }
    public decimal WeightedAverage { get; set; }
    public decimal Average { get; set; }
    public TimeSpan LongestPull { get; set; }
    public Dictionary<string, (int Count, decimal TotalSeconds, decimal Percentage)> PullsEndingInPhase { get; set; }
    public Dictionary<string, decimal> TimeSpentInPhase { get; set; }
    public int Boss { get; set; }
    public string Tag { get; set; }
    public void SetValues(string tag)
    {
        foreach (var fight in Fights)
        {
            fight.SetValues(this);
        }

        Tag = tag;
        Boss = Phases.First().Boss;
        TotalTime = Fights.Last().EndTime - Fights.First().StartTime;
        TimeInCombat = TimeSpan.FromTicks(Fights.Sum(f => f.CombatTime.Ticks));
        TimeNotInCombat = TotalTime - TimeInCombat;
        Average = (decimal)Fights.Sum(f => f.CombatTime.TotalSeconds) / (decimal)Fights.Count;
        WeightedAverage = Fights.Sum(f => f.Weight * (decimal)f.CombatTime.TotalSeconds) / Fights.Sum(f => f.Weight);
        LongestPull = Fights.Max(f => f.CombatTime);
        PullsEndingInPhase = Fights.GroupBy(f => f.EndPhaseName)
            .OrderBy(f => Phases.First(p => p.Boss == f.First().Boss).Phases.IndexOf(f.First().EndPhaseName))
            .ToDictionary(f => f.First().EndPhaseName, f => (f.Count(), (decimal)f.Sum(t => t.CombatTime.TotalSeconds), f.Sum(t => t.Weight)));
        TimeSpentInPhase = Fights.SelectMany(f => f.Phases).GroupBy(f => f.Name)
            .OrderBy(f => Phases.First().Phases.IndexOf(f.First().Name))
            .ToDictionary(f => f.First().Name, f => ((decimal)f.Sum(t => t.Duration.TotalSeconds)));
    }

    internal void Print()
    {
        Console.WriteLine("Log {0}", Title);
        Console.WriteLine("\t Total time: {0:N2}s, Time in combat: {1:N2}s, Time out of Combat: {2:N2}s",
            TotalTime.TotalSeconds, TimeInCombat.TotalSeconds, TimeNotInCombat.TotalSeconds);
        Console.WriteLine("\t Longest Pulltime: {0:N2}s", LongestPull.TotalSeconds);
        Console.WriteLine("\t Weighted Average Pulltime: {0:N2}s, Average Pulltime: {1:N2}s", WeightedAverage, Average);
        foreach (var kvp in PullsEndingInPhase)
        {
            Console.WriteLine("\t Pulls ended in {0}: {3} - {1:N2}s ({2:N2}%)", kvp.Key, kvp.Value.TotalSeconds, kvp.Value.Percentage * 100, kvp.Value.Count);
        }
        foreach (var kvp in TimeSpentInPhase)
        {
            Console.WriteLine("\t Time spent in {0}: {1:N2}s", kvp.Key, kvp.Value);
        }

        foreach (var fight in Fights)
        {
            fight.Print();
        }
    }
}