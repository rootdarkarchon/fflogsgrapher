using FFLogsGrapher.Schema;
using FFLogsGrapher.Utils;
using ScottPlot;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;

namespace FFLogsGrapher;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var dataDir = new DirectoryInfo("data");

        HttpClient client = new HttpClient();

        // todo get guild, server, region from input/config
        var guild = Uri.EscapeDataString("");
        var server = Uri.EscapeDataString("");
        var region = Uri.EscapeDataString("");

        var guildDataPath = Path.Combine(dataDir.FullName, $"{guild}-{server}-{region}");
        if (!Directory.Exists(guildDataPath)) Directory.CreateDirectory(guildDataPath);

        // todo get apikey from input/config
        var apikey = "";
        var response = await client.GetStringAsync($"https://www.fflogs.com:443/v1/reports/guild/{guild}/{server}/{region}?api_key={apikey}");

        JsonSerializerOptions options = new();
        options.Converters.Add(new UnixTimeConverter());
        options.Converters.Add(new UnixTimeSpanConverter());
        var availableLogs = JsonSerializer.Deserialize<List<LogEntry>>(response, options)!;
        List<Report> reports = new();

        var firstTag = string.Empty;

        foreach (var log in availableLogs)
        {
            string tag = string.Empty;
            try
            {
                tag = log.Title.Split(new string[] { "#" }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                if (string.IsNullOrEmpty(firstTag)) firstTag = tag;
                if (tag != firstTag) continue;
            }
            catch (Exception)
            {
                Console.WriteLine("Missing Tag for {0}", log.Id);
                continue;
            }

            var reportName = $"report-{log.Id}.json";
            var localFile = Path.Combine(guildDataPath, tag, reportName);
            var tagFolder = Path.Combine(guildDataPath, tag);
            if (!Directory.Exists(tagFolder)) Directory.CreateDirectory(tagFolder);
            if (!File.Exists(localFile))
            {
                var reportJson = await client.GetStringAsync($"https://www.fflogs.com:443/v1/report/fights/{log.Id}?api_key={apikey}");
                File.WriteAllText(localFile, reportJson);
                Console.WriteLine("Downloaded {0} to {1}", log.Id, localFile);
            }

            var reportContents = await File.ReadAllTextAsync(localFile);
            var report = JsonSerializer.Deserialize<Report>(reportContents, options)!;
            Console.WriteLine("Processing report {0}: {1}", log.Id, log.Title);
            if (report.Phases == null)
            {
                Console.WriteLine("Error processing report {0}, missing report phases", log.Id);
                continue;
            }
            // remove all trash
            report.Fights.RemoveAll(f => f.Boss == 0 || f.Phases == null);
            if (report.Fights.Count == 0)
            {
                Console.WriteLine("Error processing report {0}, contains no fights", log.Id);
                continue;
            }

            // grab fight data
            try
            {
                report.SetValues(tag);
                reports.Add(report);

                var reportFightsFolder = Path.Combine(tagFolder, log.Id);
                if (!Directory.Exists(reportFightsFolder)) Directory.CreateDirectory(reportFightsFolder);
                CancellationTokenSource cts = new();

                await Parallel.ForEachAsync(report.Fights,
                     new ParallelOptions()
                     {
                         MaxDegreeOfParallelism = 4,
                         CancellationToken = cts.Token
                     },
                     async (fight, token) =>
                {
                    foreach (var phase in fight.Phases)
                    {
                        var phaseStart = (int)phase.StartTimeOffset.TotalMilliseconds;
                        var phaseEnd = (int)(phaseStart + phase.Duration.TotalMilliseconds);
                        var dpsDataFileName = $"dps-{fight.Id}-{phase.Id}.json";
                        var dpsDataFile = Path.Combine(reportFightsFolder, dpsDataFileName);
                        if (!File.Exists(dpsDataFile))
                        {
                            var reportJson = await client.GetStringAsync($"https://www.fflogs.com:443/v1/report/tables/damage-done/{log.Id}?start={phaseStart}&end={phaseEnd}&api_key={apikey}", token);
                            File.WriteAllText(dpsDataFile, reportJson);
                            Console.WriteLine("Downloaded {0} Fight {1} Phase {2}", log.Id, fight.Id, phase.Id);
                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                    }
                });

                foreach (var fight in report.Fights)
                {
                    foreach (var phase in fight.Phases)
                    {
                        var dpsDataFileName = $"dps-{fight.Id}-{phase.Id}.json";
                        var dpsDataFile = Path.Combine(reportFightsFolder, dpsDataFileName);
                        var dps = JsonSerializer.Deserialize<Dps>(File.ReadAllText(dpsDataFile), options)!;
                        dps.SetValues(report, fight, phase);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error processing report {0}: {1}", log.Id, ex.Message);
            }
        }

        var groupedReports = reports.GroupBy(r => r.Tag)
            .ToDictionary(g => g.First().Tag, g =>
            g.OrderBy(o => o.Start).ToList());

        foreach (var grp in groupedReports)
        {
            var plotDir = Path.Combine(guildDataPath, "plots");
            if (!Directory.Exists(plotDir)) Directory.CreateDirectory(plotDir);

            int height = 600;
            int width = 1280;
            using var fightProgressPlot = DrawFightProgressPlot(width * 2, height / 2, grp.Value);
            using var percentageBmp = DrawPercentagePlot(width, height, grp.Value);
            using var timeBmp = DrawTimePlot(width, height, grp.Value);
            using var pullBmp = DrawLongestPullPlot(width, height, grp.Value);
            using var avgpullBmp = DrawAverageWeightedPullPlot(width, height, grp.Value);
            using var lastFightBmp = DrawRdpsForFightPlot(width * 2, height, grp.Value.Last(), grp.Value.Count);
            using Bitmap bmp = new Bitmap(width * 2, (int)(height * 3.5));
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(fightProgressPlot, 0, 0);
            g.DrawImage(percentageBmp, 0, height / 2);
            g.DrawImage(timeBmp, width, height / 2);
            g.DrawImage(pullBmp, 0, height + height / 2);
            g.DrawImage(avgpullBmp, width, height + height / 2);
            g.DrawImage(lastFightBmp, 0, height * 2 + height / 2);
            bmp.Save(Path.Combine(plotDir, $"{grp.Key}-summary.png"), ImageFormat.Png);
        }
    }

    private static Bitmap DrawRdpsForFightPlot(int targetWidth, int targetHeight, Report grp, int sessionNo)
    {
        var plt = new Plot(targetWidth, targetHeight);
        var plots = GenerateRdpsForFightData(grp);

        plt.XLabel("Pull");
        plt.YLabel("RDPS");
        plt.YAxis2.Line(false);
        plt.XAxis2.Line(false);
        plt.XAxis.ManualTickSpacing(1);
        plt.Layout(0, 0, 0, 0, 0);

        int i = 0;
        double[] lastValues = new double[plots.First().Value.Count];
        Dictionary<(string Name, string Job), double[]> finalResults = new();
        foreach (var player in plots)
        {
            var value = player.Value.Zip(lastValues, (a, b) => a + b).ToArray();
            finalResults[player.Key] = lastValues = value;
        }

        plt.XTicks(Enumerable.Range(0, grp.Fights.Count)
            .Select(i => (i + 1) + " [" + PhaseLabelsAbbreviation[grp.Fights[i].EndPhaseName] + "]").ToArray());

        foreach (var results in finalResults.Reverse())
        {
            var bar = plt.AddBar(results.Value);
            bar.Label = results.Key.Name;
            bar.Color = JobColors[results.Key.Job];
        }

        plt.Title(grp.Fights.First().ZoneName + " RDPS per Pull [Session #" + sessionNo + "]");
        plt.SetAxisLimits(yMin: 0);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt);
    }

    private static Bitmap DrawFightProgressPlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);
        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        var plots = GenerateFightProgressPlotData(grp);

        plt.YLabel("Fight %");
        plt.XLabel("Session");
        plt.XAxis.Line(true);
        var tickSpacing = Math.Ceiling(plots.Sum(f => f.Length) / 10f / 25) * 25;
        plt.XAxis.ManualTickSpacing(tickSpacing);
        plt.YAxis.ManualTickSpacing(25);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(true);
        plt.YAxis2.Line(false);
        plt.Layout(0, 0, 0, 0, 0);

        int i = 1;
        int currentPos = 1;
        foreach (var entry in plots)
        {
            var plotIdx = plots.IndexOf(entry);
            bool hasNext = plotIdx != plots.Count() - 1;
            var range = Enumerable.Range(currentPos, entry.Count() + (hasNext ? 1 : 0)).Select(k => (double)k).ToArray();
            var arr = hasNext ? entry.Concat(new[] { plots[plotIdx + 1].First() }).ToArray() : entry;
            var signal = plt.AddSignalXY(range, arr);
            signal.FillBelow();
            signal.Label = "Session " + i++;
            signal.MarkerShape = MarkerShape.none;
            currentPos += entry.Count();
        }

        var bestSession = grp.OrderBy(g => g.Fights.Select(f => f.EndFightPercentage).Min()).ThenBy(f => f.Start).First();
        var sessionNo = grp.IndexOf(bestSession) + 1;

        var bestPull = bestSession.Fights.OrderBy(f => f.EndFightPercentage).First();
        var pullIdx = bestSession.Fights.IndexOf(bestPull) + 1;

        plt.Title(grp.First().Fights.First().ZoneName + " Progress -- Total Pulls: "
            + grp.Sum(g => g.Fights.Count) + " -- Best Pull: Session " + sessionNo + ", Pull " + pullIdx + ": " + bestPull.EndPhaseName
            + (bestPull.Kill ? " (KILL)" : " (" + bestPull.EndPhasePercentage + "%)"));
        plt.SetAxisLimits(xMin: 0, xMax: plots.Sum(f => f.Length), yMin: 0, yMax: 100);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt, false);
    }

    private static Bitmap DrawLongestPullPlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);

        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        var plots = GenerateLongestPullData(grp, phases);

        plt.XTicks(Enumerable.Range(1, grp.Count()).Select(k => k.ToString()).ToArray());
        plt.YLabel("Seconds");
        plt.XLabel("Session");
        plt.XAxis.Line(true);
        var tickSpacing = Math.Ceiling(plots.Max() / 4f / 50) * 50;
        plt.YAxis.ManualTickSpacing(tickSpacing);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);

        plt.Layout(0, 0, 0, 0, 0);

        var bar = plt.AddBar(plots);
        bar.Label = "Longest Pull (s)";
        bar.BorderLineWidth = 0;
        bar.Color = ColorTranslator.FromHtml("#ff4285f4");
        bar.BorderColor = Color.Transparent;
        plt.SetAxisLimitsX(-1, plots.Count() + 1);

        int i = 0;
        foreach (var val in plots)
        {
            var text = plt.AddText(val.ToString(), i++, val, color: ColorTranslator.FromHtml("#ffffffff"));
            text.Alignment = Alignment.UpperCenter;
            text.PixelOffsetY = -5;
            text.Color = Color.White;
        }

        plt.Title(grp.First().Fights.First().ZoneName + " Longest Pulls");
        plt.SetAxisLimits(yMin: 0, yMax: tickSpacing * 4 + tickSpacing * 4 / 100);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt);
    }

    private static Bitmap DrawAverageWeightedPullPlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);

        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        var plots = GenerateAverageWeightedPullData(grp, phases);

        plt.XTicks(Enumerable.Range(1, grp.Count()).Select(k => k.ToString()).ToArray());
        plt.YLabel("Seconds");
        plt.XLabel("Session");
        plt.XAxis.Line(true);
        var tickSpacing = Math.Ceiling(plots.Max() / 4f / 50) * 50;
        plt.YAxis.ManualTickSpacing(tickSpacing);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);

        plt.Layout(0, 0, 0, 0, 0);

        var signal = plt.AddSignal(plots);
        signal.Label = "Average Weighted Pulltime (s)";
        signal.LineStyle = LineStyle.Solid;
        signal.Smooth = false;
        signal.Color = ColorTranslator.FromHtml("#ff4285f4");
        signal.MarkerShape = MarkerShape.none;

        int i = 0;
        foreach (var val in plots)
        {
            var text = plt.AddText(val.ToString(), i++, val, color: ColorTranslator.FromHtml("#ff4285f4"));
            text.Alignment = Alignment.LowerCenter;
            text.PixelOffsetY = 5;
        }

        plt.Title(grp.First().Fights.First().ZoneName + " Average Weighted Pulltime");
        plt.SetAxisLimits(yMin: 0, yMax: tickSpacing * 4 + tickSpacing * 4 / 100);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt);
    }

    private static Bitmap DrawPercentagePlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);

        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        Dictionary<string, double[]> plots = GeneratePercentagePlotData(grp, phases);

        plt.XTicks(Enumerable.Range(1, grp.Count()).Select(k => k.ToString()).ToArray());
        plt.YLabel("%");
        plt.XLabel("Session");
        plt.XAxis.Line(true);
        plt.YAxis.ManualTickSpacing(25);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);

        plt.Layout(0, 0, 0, 0, 0);

        foreach (var item in plots.Reverse())
        {
            var bar = plt.AddBar(item.Value);
            if (!PhaseLabels.TryGetValue(item.Key, out string? label))
            {
                label = item.Key;
            }
            bar.Label = label + " %";
            bar.BorderLineWidth = 0;
            bar.BorderColor = Color.Transparent;

            if (PhaseColors.TryGetValue(item.Key, out var color))
            {
                bar.Color = color;
            }
        }

        plt.Title(grp.First().Fights.First().ZoneName + " Wipes in Percentage");
        plt.SetAxisLimits(yMin: 0, yMax: 100);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt);
    }

    private static Bitmap DrawTimePlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);

        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        Dictionary<string, double[]> plots = GenerateTimePlotData(grp, phases);

        plt.XTicks(Enumerable.Range(1, grp.Count()).Select(k => k.ToString()).ToArray());
        plt.YLabel("Seconds");
        plt.XLabel("Session");
        plt.XAxis.Line(true);
        var tickSpacing = Math.Ceiling(plots.Last().Value.Max() / 4f / 500) * 500;
        plt.YAxis.ManualTickSpacing(tickSpacing);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);
        plt.Layout(0, 0, 0, 0, 0);

        foreach (var item in plots.Reverse())
        {
            var bar = plt.AddBar(item.Value);
            if (!PhaseLabels.TryGetValue(item.Key, out string? label))
            {
                label = item.Key;
            }
            bar.Label = label + " (s)";
            bar.BorderLineWidth = 0;
            bar.BorderColor = Color.Transparent;

            if (PhaseColors.TryGetValue(item.Key, out var color))
            {
                bar.Color = color;
            }
        }

        plt.Title(grp.First().Fights.First().ZoneName + " Wipes in Time");
        plt.SetAxisLimits(yMin: 0, yMax: tickSpacing * 4 + tickSpacing * 4 / 100);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt);
    }

    private static Bitmap GeneratePlotBitmap(int targetWidth, int targetHeight, Plot plt, bool includeLegend = true)
    {
        var padding = 10;
        if (includeLegend)
        {
            var legend = plt.Legend(false);
            legend.Orientation = Orientation.Horizontal;
            legend.Padding = 0;
            legend.ReverseOrder = true;
            legend.OutlineColor = Color.Transparent;
            legend.IsDetached = true;
        }
        using var renderlegend = plt.RenderLegend();
        plt.Width = targetWidth - padding * 2;
        plt.Height = targetHeight - (includeLegend ? (padding * 3 + renderlegend.Height) : padding * 2);
        using var plot = plt.GetBitmap();
        var bmp = new Bitmap(targetWidth, targetHeight);
        using var graphics = Graphics.FromImage(bmp);
        graphics.FillRectangle(Brushes.White, new(0, 0, bmp.Width, bmp.Height));
        graphics.DrawImage(plot, padding, padding);
        if (includeLegend)
            graphics.DrawImage(renderlegend, plot.Width / 2 - renderlegend.Width / 2, plot.Height + padding * 2);

        return bmp;
    }

    private static Dictionary<string, double[]> GeneratePercentagePlotData(List<Report> grp, List<string> phases)
    {
        Dictionary<int, Dictionary<string, double>> dataSet = new();

        int id = 1;
        foreach (var report in grp)
        {
            dataSet[id] = new();
            foreach (var ph in phases)
            {
                dataSet[id][ph] = 0;
            }

            var ending = report.PullsEndingInPhase;
            foreach (var time in ending)
            {
                dataSet[id][time.Key] = (double)time.Value.Percentage * 100;
            }
            id++;
        }

        Dictionary<string, double[]> plots = new();
        foreach (var ph in phases)
        {
            var barValues = dataSet.Values.Select(k => k[ph]);
            plots[ph] = barValues.ToArray();
            var prevLabel = phases.IndexOf(ph) - 1;
            if (prevLabel >= 0)
            {
                plots[ph] = plots[ph].Zip(plots[phases.ElementAt(prevLabel)], (x, y) => Math.Clamp(Math.Round(x + y, 2, MidpointRounding.ToPositiveInfinity), 0, 100)).ToArray();
            }
        }

        return plots;
    }

    private static Dictionary<string, double[]> GenerateTimePlotData(List<Report> grp, List<string> phases)
    {
        Dictionary<int, Dictionary<string, double>> dataSet = new();

        int id = 1;
        foreach (var report in grp)
        {
            dataSet[id] = new();
            foreach (var ph in phases)
            {
                dataSet[id][ph] = 0;
            }

            var ending = report.PullsEndingInPhase;
            foreach (var time in ending)
            {
                dataSet[id][time.Key] = (double)time.Value.TotalSeconds;
            }
            id++;
        }

        Dictionary<string, double[]> plots = new();
        foreach (var ph in phases)
        {
            var barValues = dataSet.Values.Select(k => k[ph]);
            plots[ph] = barValues.ToArray();
            var prevLabel = phases.IndexOf(ph) - 1;
            if (prevLabel >= 0)
            {
                plots[ph] = plots[ph].Zip(plots[phases.ElementAt(prevLabel)], (x, y) => x + y).ToArray();
            }
        }

        return plots;
    }

    private static Dictionary<(string Name, string Job), List<double>> GenerateRdpsForFightData(Report report)
    {
        Dictionary<(string, string), List<double>> output = new();

        foreach (var player in report.Players.OrderBy(p => JobOrder[p.Job]).Reverse())
        {
            output[(player.Name, player.Job)] = new();
        }

        foreach (var fight in report.Fights)
        {
            foreach (var player in report.Players.OrderBy(p => JobOrder[p.Job]).Reverse())
            {
                output[(player.Name, player.Job)].Add(player.GetRdpsForFight(fight));
            }
        }

        return output;
    }

    private static List<double[]> GenerateFightProgressPlotData(List<Report> grp)
    {
        return grp.Select(g => g.Fights).Select(k => k.Select(f => (double)f.EndFightPercentage).ToArray()).ToList();
    }

    private static double[] GenerateLongestPullData(List<Report> grp, List<string> phases)
    {
        return grp.Select(v => Math.Round(v.LongestPull.TotalSeconds, 0, MidpointRounding.AwayFromZero)).ToArray();
    }

    private static double[] GenerateAverageWeightedPullData(List<Report> grp, List<string> phases)
    {
        return grp.Select(v => Math.Round((double)v.WeightedAverage, 0, MidpointRounding.AwayFromZero)).ToArray();
    }

    private static Dictionary<string, Color> PhaseColors = new(StringComparer.OrdinalIgnoreCase)
    {
        {  "P1: Adelphel, Grinnaux and Charibert", ColorTranslator.FromHtml("#ff4285f4") },
        {  "P2: King Thordan", ColorTranslator.FromHtml("#ffea4335") },
        {  "P3: Nidhogg", ColorTranslator.FromHtml("#fffbbc04") },
        {  "P4: The Eyes", ColorTranslator.FromHtml("#ff34a853") },
        {  "Intermission: Rewind!", ColorTranslator.FromHtml("#ffff00ff") },
        {  "P5: King Thordan II", ColorTranslator.FromHtml("#ff46bdc6") },
        {  "P6: Nidhogg and Hraesvelgr", ColorTranslator.FromHtml("#ffffe599") },
        {  "P7: The Dragon King", ColorTranslator.FromHtml("#ff85200c") }
    };

    private static Dictionary<string, string> PhaseLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        {  "P1: Adelphel, Grinnaux and Charibert", "P1: Double Knights" },
        {  "P2: King Thordan", "P2: Thordan" },
        {  "P3: Nidhogg", "P3: I AM NAMED KYLE" },
        {  "P4: The Eyes", "P4: Eyes" },
        {  "Intermission: Rewind!", "I1: Haurchefant" },
        {  "P5: King Thordan II", "P5: Dark Thordan" },
        {  "P6: Nidhogg and Hraesvelgr", "P6: Double Dragons" },
        {  "P7: The Dragon King", "P7: Ultimate Thordan" }
    };

    private static Dictionary<string, string> PhaseLabelsAbbreviation = new(StringComparer.OrdinalIgnoreCase)
    {
        {  "P1: Adelphel, Grinnaux and Charibert", "P1" },
        {  "P2: King Thordan", "P2" },
        {  "P3: Nidhogg", "P3" },
        {  "P4: The Eyes", "P4" },
        {  "Intermission: Rewind!", "I1" },
        {  "P5: King Thordan II", "P5" },
        {  "P6: Nidhogg and Hraesvelgr", "P6" },
        {  "P7: The Dragon King", "P7" }
    };

    private static Dictionary<string, int> JobOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Warrior", 0 },
        { "Paladin", 1 },
        { "DarkKnight", 2 },
        { "Gunbreaker", 3 },
        { "WhiteMage", 4 },
        { "Astrologician", 5 },
        { "Sage", 6 },
        { "Scholar", 7 },
        { "Samurai", 8 },
        { "Ninja", 9 },
        { "Dragoon", 10 },
        { "Monk", 11 },
        { "Reaper", 12 },
        { "Bard", 13 },
        { "Dancer", 14 },
        { "Machinist", 15 },
        { "Summoner", 16 },
        { "RedMage", 17 },
        { "BlackMage", 18 }
    };

    private static Dictionary<string, Color> JobColors = new()
    {
        { "Warrior", ColorTranslator.FromHtml("#cf2621") },
        { "Paladin", ColorTranslator.FromHtml("#a8d2e6") },
        { "DarkKnight", ColorTranslator.FromHtml("#d126cc") },
        { "Gunbreaker", ColorTranslator.FromHtml("#796d30") },
        { "WhiteMage", ColorTranslator.FromHtml("#fff0dc") },
        { "Astrologician", ColorTranslator.FromHtml("#ffe74a") },
        { "Sage", ColorTranslator.FromHtml("#80a0f0") },
        { "Scholar", ColorTranslator.FromHtml("#8657ff") },
        { "Samurai", ColorTranslator.FromHtml("#e46d04") },
        { "Ninja", ColorTranslator.FromHtml("#af1964") },
        { "Dragoon", ColorTranslator.FromHtml("#4164cd") },
        { "Monk", ColorTranslator.FromHtml("#d69c00") },
        { "Reaper", ColorTranslator.FromHtml("#965a90") },
        { "Bard", ColorTranslator.FromHtml("#91ba5e") },
        { "Dancer", ColorTranslator.FromHtml("#e2b0af") },
        { "Machinist", ColorTranslator.FromHtml("#6ee1d6") },
        { "Summoner", ColorTranslator.FromHtml("#2d9b78") },
        { "RedMage", ColorTranslator.FromHtml("#e87b7b") },
        { "BlackMage", ColorTranslator.FromHtml("#a579d6") }
    };
}