using FFLogsGrapher.Schema;
using FFLogsGrapher.Utils;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

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
        var files = Directory.EnumerateFiles(guildDataPath, "report-*.json", SearchOption.AllDirectories).ToList();

        foreach (var log in availableLogs)
        {
            string tag = string.Empty;
            try
            {
                tag = log.Title.Split(new string[] { "#" }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            }
            catch (Exception)
            {
                Console.WriteLine("Missing Tag for {0}", log.Id);
                continue;
            }

            var reportName = $"report-{log.Id}.json";
            var localFile = Path.Combine(guildDataPath, tag, reportName);
            if (!Directory.Exists(Path.Combine(guildDataPath, tag))) Directory.CreateDirectory(Path.Combine(guildDataPath, tag));
            if (!File.Exists(localFile))
            {
                var reportJson = await client.GetStringAsync($"https://www.fflogs.com:443/v1/report/fights/{log.Id}?api_key={apikey}");
                File.WriteAllText(localFile, reportJson);
                Console.WriteLine("Downloaded {0} to {1}", log.Id, localFile);
            }

            var reportContents = await File.ReadAllTextAsync(localFile);
            var report = JsonSerializer.Deserialize<Report>(reportContents, options)!;
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
            try
            {
                report.SetValues(tag);
                reports.Add(report);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error processing report {0}: {1}", log.Id, ex.Message);
            }
        }

        // todo: actually group up by "name #id" to uniquely group reports together
        var groupedReports = reports.GroupBy(r => r.Tag)
            .ToDictionary(g => g.First().Tag, g =>
            g.OrderBy(o => o.Start).ToList());

        foreach (var grp in groupedReports)
        {
            var plotDir = Path.Combine(guildDataPath, "plots");
            if (!Directory.Exists(plotDir)) Directory.CreateDirectory(plotDir);

            int height = 600;
            int width = 1220;
            using var percentageBmp = DrawPercentagePlot(width, height, grp);
            using var timeBmp = DrawTimePlot(width, height, grp);
            using Bitmap bmp = new Bitmap(percentageBmp.Width + timeBmp.Width, height);
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(percentageBmp, 0, 0);
            g.DrawImage(timeBmp, width, 0);
            bmp.Save(Path.Combine(plotDir, $"{grp.Key}-summary.png"), ImageFormat.Png);
        }
    }

    private static Bitmap DrawPercentagePlot(int targetWidth, int targetHeight, KeyValuePair<string, List<Report>> grp)
    {
        var plt = new ScottPlot.Plot(targetWidth, targetHeight);

        var phases = grp.Value.First().Phases.First(f => f.Boss == grp.Value.First().Fights.First().Boss).Phases;
        Dictionary<int, Dictionary<string, double>> dataSet = new();

        int id = 1;
        foreach (var report in grp.Value)
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

        var padding = 10;

        plt.XTicks(dataSet.Keys.Select(k => k.ToString()).ToArray());
        plt.YLabel("%");
        plt.XLabel("Session");
        plt.XAxis.Line(false);
        plt.YAxis.ManualTickSpacing(25);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);

        plt.Layout(0, 0, 0, 0, padding: padding);

        foreach (var item in Enumerable.Reverse(plots))
        {
            var bar = plt.AddBar(item.Value);
            bar.Label = item.Key + " %";
            bar.BorderLineWidth = 0;
            bar.BorderColor = Color.Transparent;

            if (PhaseColors.TryGetValue(item.Key, out var color))
            {
                bar.Color = color;
            }
        }

        plt.Title(grp.Value.First().Fights.First().ZoneName + " Wipes in Percentage");
        plt.SetAxisLimits(yMin: 0, yMax: 100);
        using var legend = plt.RenderLegend();
        var legendWidth = legend.Width;
        plt.Width = targetWidth - legendWidth;
        using var plot = plt.GetBitmap();
        var bmp = new Bitmap(targetWidth, targetHeight);
        using var graphics = Graphics.FromImage(bmp);
        graphics.FillRectangle(Brushes.White, new(0, 0, bmp.Width, bmp.Height));
        graphics.DrawImage(plot, 0, 0);
        graphics.DrawImage(legend, plot.Width, plot.Height / 2 - legend.Height / 2);

        return bmp;
    }

    private static Bitmap DrawTimePlot(int targetWidth, int targetHeight, KeyValuePair<string, List<Report>> grp)
    {
        var plt = new ScottPlot.Plot(targetWidth, targetHeight);

        var phases = grp.Value.First().Phases.First(f => f.Boss == grp.Value.First().Fights.First().Boss).Phases;
        Dictionary<int, Dictionary<string, double>> dataSet = new();

        int id = 1;
        foreach (var report in grp.Value)
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

        var padding = 10;

        plt.XTicks(dataSet.Keys.Select(k => k.ToString()).ToArray());
        plt.YLabel("Seconds");
        plt.XLabel("Session");
        plt.XAxis.Line(false);
        var tickSpacing = Math.Ceiling(plots.Last().Value.Max() / 4f / 500) * 500;
        plt.YAxis.ManualTickSpacing(tickSpacing);
        plt.XAxis2.Line(false);
        plt.YAxis.Line(false);
        plt.YAxis2.Line(false);
        plt.Layout(0, 0, 0, 0, padding: padding);

        foreach (var item in Enumerable.Reverse(plots))
        {
            var bar = plt.AddBar(item.Value);
            bar.Label = item.Key + " (s)";
            bar.BorderLineWidth = 0;
            bar.BorderColor = Color.Transparent;

            if (PhaseColors.TryGetValue(item.Key, out var color))
            {
                bar.Color = color;
            }
        }

        plt.Title(grp.Value.First().Fights.First().ZoneName + " Wipes in Time");
        plt.SetAxisLimits(yMin: 0, yMax: tickSpacing * 4 + (tickSpacing * 4 / 100));
        using var legend = plt.RenderLegend();
        var legendWidth = legend.Width;
        plt.Width = targetWidth - legendWidth - padding;
        using var plot = plt.GetBitmap();
        var bmp = new Bitmap(targetWidth, targetHeight);
        using var graphics = Graphics.FromImage(bmp);
        graphics.FillRectangle(Brushes.White, new(0, 0, bmp.Width, bmp.Height));
        graphics.DrawImage(plot, 0, 0);
        graphics.DrawImage(legend, plot.Width, plot.Height / 2 - legend.Height / 2);

        return bmp;
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
}