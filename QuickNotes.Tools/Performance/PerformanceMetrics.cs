using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickNotes.Tools.Performance;

public sealed class MetricSampleSummary
{
    public int Count { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public IReadOnlyList<double> RawSamples { get; init; } = Array.Empty<double>();

    public override string ToString()
        => $"n={Count}, median={Median:F2}, p95={P95:F2}, min={Min:F2}, max={Max:F2}";
}

public static class PerformanceMetricsCalculator
{
    public static MetricSampleSummary ComputeSummary(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return new MetricSampleSummary();
        }

        list.Sort();
        double min = list[0];
        double max = list[^1];
        double median = ComputePercentile(list, 0.50);
        double p95 = ComputePercentile(list, 0.95);
        double mean = list.Average();
        double sumSq = list.Sum(x => (x - mean) * (x - mean));
        double stdDev = list.Count > 1 ? Math.Sqrt(sumSq / (list.Count - 1)) : 0.0;

        return new MetricSampleSummary
        {
            Count = list.Count,
            Min = min,
            Max = max,
            Median = median,
            P95 = p95,
            Mean = mean,
            StdDev = stdDev,
            RawSamples = list
        };
    }

    public static MetricSampleSummary ComputeSummary(IEnumerable<long> values)
        => ComputeSummary(values.Select(v => (double)v));

    public static double ComputePercentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues == null || sortedValues.Count == 0)
        {
            throw new ArgumentException("Cannot compute percentile on empty collection.", nameof(sortedValues));
        }

        if (percentile < 0.0 || percentile > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0.0 and 1.0.");
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        double index = percentile * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = index - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
    }
}
