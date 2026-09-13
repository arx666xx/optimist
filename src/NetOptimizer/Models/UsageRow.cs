using System.Windows.Media;
using NetOptimizer.Services;

namespace NetOptimizer.Models;

/// <summary>
/// One application in the statistics view: how much it moved over the selected
/// period, a proportional bar, and — for the live session — a 60-second sparkline.
/// Everything is precomputed here so the XAML stays free of converters.
/// </summary>
public sealed class UsageRow
{
    public const double BarMaxWidth = 220;
    public const double SparkWidth = 120;
    public const double SparkHeight = 22;

    public string Name { get; init; } = "";
    public long Down { get; init; }
    public long Up { get; init; }
    public ImageSource? Icon { get; init; }

    public long Total => Down + Up;

    public string DownText => UsageStats.FormatBytes(Down);
    public string UpText => UsageStats.FormatBytes(Up);
    public string TotalText => UsageStats.FormatBytes(Total);

    /// <summary>Width of the proportion bar, relative to the biggest row.</summary>
    public double BarWidth { get; set; }

    /// <summary>Polyline points for the sparkline, or null when there is no history.</summary>
    public PointCollection? Spark { get; set; }

    /// <summary>Builds the bar widths and returns the list ready for binding.</summary>
    public static List<UsageRow> Rank(IEnumerable<UsageRow> rows, int take)
    {
        var list = rows.Where(r => r.Total > 0)
                       .OrderByDescending(r => r.Total)
                       .Take(take)
                       .ToList();

        long max = list.Count > 0 ? list[0].Total : 0;
        foreach (var r in list)
            r.BarWidth = max > 0 ? Math.Max(2, BarMaxWidth * r.Total / max) : 0;

        return list;
    }

    /// <summary>Turns a series of byte-per-second samples into a polyline.</summary>
    public static PointCollection? BuildSpark(double[] samples)
    {
        if (samples.Length < 2) return null;

        double peak = samples.Max();
        if (peak <= 0) return null;

        var pts = new PointCollection(samples.Length);
        double stepX = SparkWidth / (samples.Length - 1);
        for (int i = 0; i < samples.Length; i++)
        {
            double y = SparkHeight - (samples[i] / peak) * SparkHeight;
            pts.Add(new System.Windows.Point(i * stepX, y));
        }
        pts.Freeze();
        return pts;
    }
}
