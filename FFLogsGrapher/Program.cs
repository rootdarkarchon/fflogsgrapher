using FFLogsGrapher.Schema;
using FFLogsGrapher.Utils;
using ScottPlot;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace FFLogsGrapher;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var dataDir = new DirectoryInfo("data");

        HttpClient client = new HttpClient();

        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText("settings.json"))!;

        // todo get guild, server, region from input/config
        var guild = Uri.EscapeDataString(settings.Guild);
        var server = Uri.EscapeDataString(settings.Server);
        var region = Uri.EscapeDataString(settings.Region);

        var guildDataPath = Path.Combine(dataDir.FullName, $"{settings.Guild}-{settings.Server}-{settings.Region}");
        if (!Directory.Exists(guildDataPath)) Directory.CreateDirectory(guildDataPath);

        // todo get apikey from input/config
        var apikey = settings.ApiKey;
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
                if (settings.CreateOnlyLatest && tag != firstTag) continue;
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
                                await Task.Delay(TimeSpan.FromSeconds(0.75));
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
            int width = 1250;
            using var fightProgressPlot = DrawFightProgressTimePlot(width * 2, height / 2, grp.Value);
            using var percentageBmp = DrawPercentagePlot(width, height, grp.Value);
            using var timeBmp = DrawTimePlot(width, height, grp.Value);
            using var pullBmp = DrawLongestPullPlot(width, height, grp.Value);
            using var timeSpentBmp = DrawTimeSpentPlot(width, height, grp.Value);
            using var avgpullBmp = DrawAverageWeightedPullPlot(width, height, grp.Value);
            using var sessionRdpsBmp = DrawRdpsForFightPlot(width, height, grp.Value.Last(), grp.Value.Count);
            using var dpsPhaseBmp = DrawAverageRdpsOverallPerPhasePlot(width * 2, height, grp.Value);
            using var dpsPhaseSessionBmp = DrawAverageRdpsPerPhasePlot(width * 2, height, grp.Value.Last(), grp.Value.Count);
            using Bitmap bmp = new Bitmap(width * 2, (int)(height * 5.5));
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(fightProgressPlot, 0, 0);
            g.DrawImage(sessionRdpsBmp, 0, height / 2);
            g.DrawImage(timeSpentBmp, width, height / 2);
            g.DrawImage(dpsPhaseSessionBmp, 0, height + height / 2);
            g.DrawImage(percentageBmp, 0, height * 2 + height / 2);
            g.DrawImage(timeBmp, width, height * 2 + height / 2);
            g.DrawImage(pullBmp, 0, height * 3 + height / 2);
            g.DrawImage(avgpullBmp, width, height * 3 + height / 2);
            g.DrawImage(dpsPhaseBmp, 0, height * 4 + height / 2);
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

        double[] lastValues = new double[plots.First().Value.Count];
        Dictionary<(string Name, string Job), double[]> finalResults = new();
        foreach (var player in plots)
        {
            var value = player.Value.Zip(lastValues, (a, b) => a + b).ToArray();
            finalResults[player.Key] = lastValues = value;
        }

        plt.XTicks(Enumerable.Range(0, grp.Fights.Count)
            .Select(i => (i + 1) + Environment.NewLine + "[" + PhaseLabelsAbbreviation[grp.Fights[i].EndPhaseName] + "]").ToArray());

        foreach (var results in finalResults.Reverse())
        {
            var bar = plt.AddBar(results.Value);
            bar.Label = AbbreviateName(results.Key.Name) + " (" + results.Key.Job + ")";
            bar.Color = JobColors[results.Key.Job];
            bar.BorderLineWidth = 0;
            for (int j = 0; j < results.Value.Length; j++)
            {
                var txt = plt.AddText(Math.Round(plots[results.Key][j], 0).ToString(), j, results.Value[j]);
                txt.BackgroundFill = true;
                txt.BackgroundColor = Color.FromArgb(128, Color.White);
                txt.Color = Color.Black;
                txt.Alignment = Alignment.UpperCenter;
                txt.PixelOffsetY = bar.BorderLineWidth - 1;
            }
        }

        plt.Title(grp.Fights.First().ZoneName + " RDPS per Pull [Session #" + sessionNo + "]");
        plt.SetAxisLimits(yMin: 0);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt, reverseLegend: false);
    }

    private static Bitmap DrawAverageRdpsPerPhasePlot(int targetWidth, int targetHeight, Report grp, int sessionNo)
    {
        var plt = new Plot(targetWidth, targetHeight);
        Dictionary<(string, string), Dictionary<string, (double Average, double StdDev)>> plots
            = GenerateAverageRdpsData(new() { grp }, grp.Players.Select(p => (p.Name, p.Job)).ToList());

        plt.YLabel("RDPS");
        plt.YAxis2.Line(false);
        plt.XAxis2.Line(false);
        plt.Layout(0, 0, 0, 0, 0);

        var sortedPlayers = plots.OrderBy(p => JobOrder[p.Key.Item2]);
        var sortedPhases = sortedPlayers.First().Value.OrderBy(p => PhaseLabels.Keys.ToList().IndexOf(p.Key)).ToList();
        var barGroup = plt.AddBarGroups(sortedPlayers.Select(k => AbbreviateName(k.Key.Item1) + " (" + k.Key.Item2 + ")").ToArray(),
            sortedPhases.Select(k => PhaseLabels[k.Key]).ToArray(),
            sortedPhases.Select(p => sortedPlayers.Select(k => k.Value.ContainsKey(p.Key) ? Math.Round(k.Value[p.Key].Average, 0) : 0).ToArray()).ToArray(),
            sortedPhases.Select(p => sortedPlayers.Select(k => k.Value.ContainsKey(p.Key) ? k.Value[p.Key].StdDev : 0).ToArray()).ToArray());
        for (int i = 0; i < barGroup.Length; i++)
        {
            barGroup[i].Color = PhaseColors[sortedPhases[i].Key];
            barGroup[i].ShowValuesAboveBars = true;
            barGroup[i].BorderLineWidth = 0;
        }

        plt.Title(grp.Fights.First().ZoneName + " RDPS per Phase [Session #" + sessionNo + "]");
        plt.SetAxisLimits(yMin: 0);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt, reverseLegend: false);
    }

    private static Bitmap DrawAverageRdpsOverallPerPhasePlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);
        Dictionary<(string, string), Dictionary<string, (double Average, double StdDev)>> plots
            = GenerateAverageRdpsData(grp, grp.Last().Players.Select(p => (p.Name, p.Job)).ToList());

        plt.YLabel("RDPS");
        plt.YAxis2.Line(false);
        plt.XAxis2.Line(false);
        plt.Layout(0, 0, 0, 0, 0);

        var sortedPlayers = plots.OrderBy(p => JobOrder[p.Key.Item2]);
        var sortedPhases = sortedPlayers.First().Value.OrderBy(p => PhaseLabels.Keys.ToList().IndexOf(p.Key)).ToList();
        var barGroup = plt.AddBarGroups(sortedPlayers.Select(k => AbbreviateName(k.Key.Item1) + " (" + k.Key.Item2 + ")").ToArray(),
            sortedPhases.Select(k => PhaseLabels[k.Key]).ToArray(),
            sortedPhases.Select(p => sortedPlayers.Select(k =>
            {
                if (k.Value.TryGetValue(p.Key, out var val))
                {
                    return Math.Round(val.Average, 0);
                }
                return 0;
            }).ToArray()).ToArray(),
            sortedPhases.Select(p => sortedPlayers.Select(k =>
            {
                if (k.Value.TryGetValue(p.Key, out var val))
                {
                    return val.StdDev;
                }
                return 0;
            }).ToArray()).ToArray());
        for (int i = 0; i < barGroup.Length; i++)
        {
            barGroup[i].Color = PhaseColors[sortedPhases[i].Key];
            barGroup[i].ShowValuesAboveBars = true;
            barGroup[i].BorderLineWidth = 0;
        }

        plt.Title(grp.First().Fights.First().ZoneName + " RDPS per Phase [All Sessions]");
        plt.SetAxisLimits(yMin: 0);

        return GeneratePlotBitmap(targetWidth, targetHeight, plt, reverseLegend: false);
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

    private static Bitmap DrawFightProgressTimePlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);
        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        var plots = GenerateFightProgressPlotDataByHour(grp);

        plt.YLabel("Fight %");
        plt.XLabel("Seconds");
        plt.XAxis.Line(true);
        var tickSpacing = Math.Ceiling(plots.Last().X.Last() / 10f / 1000) * 1000;
        plt.XAxis.ManualTickSpacing(tickSpacing);
        plt.YAxis.ManualTickSpacing(25);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(true);
        plt.YAxis2.Line(false);
        plt.Layout(0, 0, 0, 0, 0);

        int i = 1;
        foreach (var entry in plots)
        {
            var plotIdx = plots.IndexOf(entry);
            bool hasNext = plotIdx != plots.Count() - 1;
            var entryX = hasNext ? entry.X.Concat(new[] { plots[plotIdx + 1].X.First() }).ToArray() : entry.X;
            var entryY = hasNext ? entry.Y.Concat(new[] { plots[plotIdx + 1].Y.First() }).ToArray() : entry.Y;
            var signal = plt.AddSignalXY(entryX, entryY);
            signal.FillBelow();
            signal.Label = "Session " + i++;
            signal.MarkerShape = MarkerShape.none;
        }

        var bestSession = grp.OrderBy(g => g.Fights.Select(f => f.EndFightPercentage).Min()).ThenBy(f => f.Start).First();
        var sessionNo = grp.IndexOf(bestSession) + 1;

        var bestPull = bestSession.Fights.OrderBy(f => f.EndFightPercentage).First();
        var pullIdx = bestSession.Fights.IndexOf(bestPull) + 1;

        var bestXVal = plots[sessionNo - 1].X[pullIdx - 1];
        var bestYVal = plots[sessionNo - 1].Y[pullIdx - 1];
        var text = plt.AddText("Best Pull", bestXVal, bestYVal);
        text.Color = Color.Black;
        text.BackgroundColor = Color.FromArgb(128, Color.White);
        text.BackgroundFill = true;
        text.Alignment = Alignment.LowerRight;
        var horizLine = plt.AddLine(0, bestYVal, bestXVal, bestYVal, Color.FromArgb(192, Color.DarkGray));
        horizLine.LineStyle = LineStyle.Dash;
        var vertLine = plt.AddLine(bestXVal, 0, bestXVal, bestYVal, Color.FromArgb(192, Color.DarkGray));
        vertLine.LineStyle = LineStyle.Dash;
        plt.AddPoint(bestXVal, bestYVal, Color.Black, 5, MarkerShape.filledDiamond);

        plt.Title(grp.First().Fights.First().ZoneName + " Progress (in Time) -- Total Pulls: "
            + grp.Sum(g => g.Fights.Count) + " -- Best Pull: Session " + sessionNo + ", Pull " + pullIdx + ": " + bestPull.EndPhaseName
            + (bestPull.Kill ? " (KILL)" : " (" + bestPull.EndPhasePercentage + "%)"));
        plt.SetAxisLimits(xMin: 0, xMax: plots.Last().X.Last(), yMin: 0, yMax: 100);

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

    private static Bitmap DrawTimeSpentPlot(int targetWidth, int targetHeight, List<Report> grp)
    {
        var plt = new Plot(targetWidth, targetHeight);

        var phases = grp.First().Phases.First(f => f.Boss == grp.First().Fights.First().Boss).Phases;
        Dictionary<string, double[]> plots = GenerateTimeSpentPlotData(grp, phases);

        plt.XTicks(Enumerable.Range(1, grp.Count()).Select(k => k.ToString()).ToArray());
        plt.YLabel("Seconds");
        plt.XLabel("Session");
        plt.XAxis.Line(true);
        var tickSpacing = Math.Ceiling(plots.Last().Value.Max() / 4f / 500) * 500;
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);

        plt.Layout(0, 0, 0, 0, 0);
        double? barWidth = null;
        foreach (var item in plots.Reverse())
        {
            var bar = plt.AddBar(item.Value);
            barWidth ??= bar.BarWidth;
            if (barWidth != null) bar.BarWidth = barWidth.Value;
            if (!PhaseLabelsAbbreviation.TryGetValue(item.Key, out string? label))
            {
                label = item.Key;
            }
            bar.Label = label;
            bar.BorderLineWidth = 0;
            bar.BorderColor = Color.Transparent;

            if (PhaseColors.TryGetValue(item.Key, out var color))
            {
                bar.Color = color;
            }
        }

        plt.Title(grp.First().Fights.First().ZoneName + " Time Spent In Phases");
        plt.SetAxisLimits(yMin: 0);

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

    private static Bitmap GeneratePlotBitmap(int targetWidth, int targetHeight, Plot plt, bool includeLegend = true, bool reverseLegend = true)
    {
        var padding = 10;
        if (includeLegend)
        {
            var legend = plt.Legend(false);
            legend.Orientation = Orientation.Horizontal;
            legend.Padding = 0;
            legend.ReverseOrder = reverseLegend;
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
            var list = report.Players.OrderBy(p => JobOrder[p.Job]).GroupBy(b => (b.Name, b.Job)).Reverse();
            foreach (var player in list)
            {
                double maxDps = player.Max(p => p.GetRdpsForFight(fight));
                output[(player.Key.Name, player.Key.Job)].Add(maxDps);
            }
        }

        return output;
    }

    private static List<double[]> GenerateFightProgressPlotData(List<Report> grp)
    {
        return grp.Select(g => g.Fights).Select(k => k.Select(f => (double)f.EndFightPercentage).ToArray()).ToList();
    }

    private static Dictionary<string, double[]> GenerateTimeSpentPlotData(List<Report> grp, List<string> phases)
    {
        phases.Add("OOC");

        Dictionary<int, Dictionary<string, double>> dataSet = new();

        int id = 1;
        foreach (var report in grp)
        {
            dataSet[id] = new();
            foreach (var ph in phases)
            {
                dataSet[id][ph] = 0;
            }

            var ending = report.TimeSpentInPhase;
            foreach (var time in ending)
            {
                dataSet[id][time.Key] = (double)time.Value;
            }

            dataSet[id]["OOC"] = report.TimeNotInCombat.TotalSeconds;
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

    private static List<(double[] X, double[] Y)> GenerateFightProgressPlotDataByHour(List<Report> grp)
    {
        List<(double[] X, double[] Y)> result = new();
        double xPos = 0;

        foreach (var report in grp)
        {
            List<(double x, double y)> values = new();

            foreach (var fight in report.Fights)
            {
                var x = xPos + fight.ActualCombatTime.TotalSeconds;
                var y = (double)fight.EndFightPercentage;
                values.Add((x, y));
                xPos = x;
            }

            result.Add((values.Select(v => v.x).ToArray(), values.Select(v => v.y).ToArray()));
        }

        return result;
    }

    private static double[] GenerateLongestPullData(List<Report> grp, List<string> phases)
    {
        return grp.Select(v => Math.Round(v.LongestPull.TotalSeconds, 0, MidpointRounding.AwayFromZero)).ToArray();
    }

    private static double[] GenerateAverageWeightedPullData(List<Report> grp, List<string> phases)
    {
        return grp.Select(v => Math.Round((double)v.WeightedAverage, 0, MidpointRounding.AwayFromZero)).ToArray();
    }

    private static Dictionary<(string Name, string Job), Dictionary<string, (double Average, double StdDev)>> GenerateAverageRdpsData(List<Report> grp, List<(string, string)> playerWithJobs)
    {
        var dict = new Dictionary<(string, string), Dictionary<string, (double Average, double StdDev)>>();

        var players = grp.SelectMany(grp => grp.Players).Where(p => playerWithJobs.Contains((p.Name, p.Job))).GroupBy(g => (g.Name, g.Job));

        foreach (var player in players)
        {
            try
            {
                dict[player.Key] = new();
                var allDpsEntries = player.Where(p => p.DpsEntries != null).SelectMany(p => p.DpsEntries.Values).ToList();
                Dictionary<string, List<double>> rdpsValues = new();

                foreach (var entry in allDpsEntries)
                {
                    foreach (var kvp in entry)
                    {
                        bool addEntry = kvp.Key.HasNextPhase
                            || (PhaseActiveEnrageTimes.TryGetValue(kvp.Key.Name, out TimeSpan enrageTime)
                                && enrageTime == default ? true : (Math.Abs(kvp.Key.Duration.TotalSeconds - enrageTime.TotalSeconds) < 5));
                        if (!addEntry) continue;

                        if (!rdpsValues.TryGetValue(kvp.Key.Name, out var values))
                        {
                            rdpsValues[kvp.Key.Name] = values = new();
                        }

                        values.Add(kvp.Value.ActualRDPS);
                    }
                }

                foreach (var value in rdpsValues)
                {
                    var avg = value.Value.Average();
                    var med = value.Value.OrderBy(v => v).Skip(value.Value.Count / 2).First();
                    var stdDev = Math.Sqrt(value.Value.Sum(v => Math.Pow(v - avg, 2)) / value.Value.Count);
                    var ci = avg + 0.95 * (stdDev / Math.Sqrt(value.Value.Count));
                    var se = stdDev / Math.Sqrt(value.Value.Count);

                    dict[player.Key][value.Key] = (avg, (ci - avg) * 2);
                }
            }
            catch (Exception ex)
            {
                continue;
            }
        }

        return dict;
    }

    private static Dictionary<string, TimeSpan> PhaseActiveEnrageTimes = new()
    {
        { "P1: Adelphel, Grinnaux and Charibert", TimeSpan.FromSeconds(171.3) },
        { "P2: King Thordan", TimeSpan.FromSeconds(194.7) },
        { "P3: Nidhogg", TimeSpan.FromSeconds(126.3) },
        { "P4: The Eyes", TimeSpan.FromSeconds(91.0) },
        { "Intermission: Rewind!", TimeSpan.FromSeconds(87.0) },
        { "P5: King Thordan II", TimeSpan.FromSeconds(178) },
        { "P6: Nidhogg and Hraesvelgr", TimeSpan.FromSeconds(189.6) },
        { "P7: The Dragon King", TimeSpan.FromSeconds(233.5) },
        { "P1: Omega", TimeSpan.FromSeconds(133.4) },
        { "P2: Omega-M/F", TimeSpan.FromSeconds(151.1) },
        { "P3: Omega Reconfigured", TimeSpan.FromSeconds(190.1) },
    };

    private static Dictionary<string, Color> PhaseColors = new(StringComparer.OrdinalIgnoreCase)
    {
        {  "P1: Adelphel, Grinnaux and Charibert", ColorTranslator.FromHtml("#ff4285f4") },
        {  "P2: King Thordan", ColorTranslator.FromHtml("#ffea4335") },
        {  "P3: Nidhogg", ColorTranslator.FromHtml("#fffbbc04") },
        {  "P4: The Eyes", ColorTranslator.FromHtml("#ff34a853") },
        {  "Intermission: Rewind!", ColorTranslator.FromHtml("#ffff00ff") },
        {  "P5: King Thordan II", ColorTranslator.FromHtml("#ff46bdc6") },
        {  "P6: Nidhogg and Hraesvelgr", ColorTranslator.FromHtml("#ffffe599") },
        {  "P7: The Dragon King", ColorTranslator.FromHtml("#ff85200c") },
        {  "P1: Living Liquid", ColorTranslator.FromHtml("#4385f5") },
        {  "Intermission: Limit Cut", ColorTranslator.FromHtml("#e94335") },
        {  "P2: Brute Justice and Cruise Chaser", ColorTranslator.FromHtml("#fcbc05") },
        {  "Intermission: Temporal Stasis", ColorTranslator.FromHtml("#34a853") },
        {  "P3: Alexander Prime", ColorTranslator.FromHtml("#ff6d02") },
        {  "P4: Perfect Alexander", ColorTranslator.FromHtml("#47bdc6") },
        {  "P1: Twintania", ColorTranslator.FromHtml("#4385f5") },
        {  "P2: Nael deus Darnus", ColorTranslator.FromHtml("#e94335") },
        {  "P3: Bahamut Prime", ColorTranslator.FromHtml("#fcbc05") },
        {  "P4: Triple Threat", ColorTranslator.FromHtml("#34a853") },
        {  "P5: Reborn!", ColorTranslator.FromHtml("#47bdc6") },
        {  "Garuda", ColorTranslator.FromHtml("#4385f5") },
        {  "Ifrit", ColorTranslator.FromHtml("#e94335") },
        {  "Titan", ColorTranslator.FromHtml("#fcbc05") },
        {  "Magitek Bits", ColorTranslator.FromHtml("#34a853") },
        {  "The Ultima Weapon", ColorTranslator.FromHtml("#47bdc6") },
        { "P1: Omega", ColorTranslator.FromHtml("#ff4285f4") },
        { "P2: Omega-M/F", ColorTranslator.FromHtml("#ffea4335") },
        { "P3: Omega Reconfigured", ColorTranslator.FromHtml("#fffbbc04") },
        { "P4: Blue Screen", ColorTranslator.FromHtml("#ff34a853") },
        { "P5: Run: Dynamis", ColorTranslator.FromHtml("#47bdc6") },
        { "P6: Alpha Omega", ColorTranslator.FromHtml("#ffffe599") },
        {  "OOC", ColorTranslator.FromHtml("#ababab") }
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
        {  "P7: The Dragon King", "P7: Ultimate Thordan" },
        {  "P1: Living Liquid", "P1: Pepsiman" },
        {  "Intermission: Limit Cut", "I1: Limit Cut" },
        {  "P2: Brute Justice and Cruise Chaser", "P2: BJCC" },
        {  "Intermission: Temporal Stasis", "I2: Statis" },
        {  "P3: Alexander Prime", "P3: Alexander" },
        {  "P4: Perfect Alexander", "P4: Perfect Alexander" },
        {  "P1: Twintania", "P1: Twintania" },
        {  "P2: Nael deus Darnus", "P2: Nael" },
        {  "P3: Bahamut Prime", "P3: Bahamut" },
        {  "P4: Triple Threat", "P4: Adds" },
        {  "P5: Reborn!", "P5: Golden Bahamut" },
        {  "Garuda", "P1: Garuda" },
        {  "Ifrit", "P2: Ifrit" },
        {  "Titan", "P3: Titan" },
        {  "Magitek Bits", "I1: Magitek" },
        {  "The Ultima Weapon", "P5: Ultima Weapon" },
        { "P1: Omega", "P1: Omega Mariokart" },
        { "P2: Omega-M/F", "P2: Omega M/F" },
        { "P3: Omega Reconfigured", "P3: Omega Reconfigured" },
        { "P4: Blue Screen", "P4: Blue Screen" },
        { "P5: Run: Dynamis", "P5: Run: Dynamis" },
        { "P6: Alpha Omega", "P6: Alpha Omega" },
        {  "OOC", "Not in Combat" }
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
        {  "P7: The Dragon King", "P7" },
        {  "P1: Living Liquid", "P1" },
        {  "Intermission: Limit Cut", "I1" },
        {  "P2: Brute Justice and Cruise Chaser", "P2" },
        {  "Intermission: Temporal Stasis", "I2" },
        {  "P3: Alexander Prime", "P3" },
        {  "P4: Perfect Alexander", "P4" },
        {  "P1: Twintania", "P1" },
        {  "P2: Nael deus Darnus", "P2" },
        {  "P3: Bahamut Prime", "P3" },
        {  "P4: Triple Threat", "P4" },
        {  "P5: Reborn!", "P5" },
        {  "Garuda", "P1" },
        {  "Ifrit", "P2" },
        {  "Titan", "P3" },
        {  "Magitek Bits", "I1" },
        {  "The Ultima Weapon", "P5" },
        { "P1: Omega", "P1" },
        { "P2: Omega-M/F", "P2" },
        { "P3: Omega Reconfigured", "P3" },
        { "P4: Blue Screen", "P4" },
        { "P5: Run: Dynamis", "P5" },
        { "P6: Alpha Omega", "P6" },
        {  "OOC", "OOC" }
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

    private static Dictionary<string, string> JobAbbreviation = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Warrior", "WAR" },
        { "Paladin", "PLD" },
        { "DarkKnight", "DRK" },
        { "Gunbreaker", "GNB" },
        { "WhiteMage", "WHM" },
        { "Astrologician", "AST" },
        { "Sage", "SGE" },
        { "Scholar", "SCH" },
        { "Samurai", "SAM" },
        { "Ninja", "NIN" },
        { "Dragoon", "DRG" },
        { "Monk", "MNK" },
        { "Reaper", "RPR" },
        { "Bard", "BRD" },
        { "Dancer", "DNC" },
        { "Machinist", "MCH" },
        { "Summoner", "SMN" },
        { "RedMage", "RDM" },
        { "BlackMage", "BLM" }
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

    private static string AbbreviateName(string name)
    {
        var split = name.Split(" ");
        return split[0][0] + ". " + split[1][0] + ".";
    }
}