namespace MyPlasm.Inspector.ProtocolAnalysis;

internal static class Statistics
{
    public static DistributionReport Summarize(IEnumerable<long> source)
    {
        long[] values = source.Order().ToArray();
        if (values.Length == 0)
        {
            return new DistributionReport(0, null, null, null, null, null, null);
        }

        double median = values.Length % 2 == 0
            ? ((double)values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
        double mean = values.Average(value => (double)value);

        return new DistributionReport(
            values.LongLength,
            values[0],
            Round(median),
            PercentileNearestRank(values, 0.95),
            PercentileNearestRank(values, 0.99),
            Round(mean),
            values[^1]);
    }

    public static double Entropy(IEnumerable<long> counts)
    {
        long[] materialized = counts.Where(count => count > 0).ToArray();
        long total = materialized.Sum();
        if (total == 0)
        {
            return 0;
        }

        double entropy = 0;
        foreach (long count in materialized)
        {
            double probability = (double)count / total;
            entropy -= probability * Math.Log2(probability);
        }

        return Math.Round(entropy, 6, MidpointRounding.AwayFromZero);
    }

    private static double PercentileNearestRank(long[] sorted, double percentile)
    {
        int rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
        return sorted[rank - 1];
    }

    private static double Round(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
