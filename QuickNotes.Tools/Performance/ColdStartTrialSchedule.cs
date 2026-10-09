using System;
using System.Collections.Generic;

namespace QuickNotes.Tools.Performance;

public enum ColdStartPublishVariant
{
    Baseline,
    ReadyToRun
}

public sealed class ColdStartTrial
{
    public ColdStartPublishVariant Variant { get; init; }
    public bool IsWarmup { get; init; }
}

public static class ColdStartTrialSchedule
{
    private static readonly ColdStartPublishVariant[] Abba =
    {
        ColdStartPublishVariant.Baseline,
        ColdStartPublishVariant.ReadyToRun,
        ColdStartPublishVariant.ReadyToRun,
        ColdStartPublishVariant.Baseline
    };

    public static IReadOnlyList<ColdStartTrial> BuildAbba(int warmupEach, int measuredEach)
    {
        if (warmupEach < 0 || measuredEach < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupEach), "Warmup and measured counts must be non-negative.");
        }

        var list = new List<ColdStartTrial>(checked((warmupEach + measuredEach) * 2));
        Append(list, warmupEach, isWarmup: true);
        Append(list, measuredEach, isWarmup: false);
        return list;
    }

    private static void Append(List<ColdStartTrial> list, int each, bool isWarmup)
    {
        int baseline = 0;
        int r2r = 0;
        int i = 0;
        while (baseline < each || r2r < each)
        {
            ColdStartPublishVariant variant = Abba[i % Abba.Length];
            if (variant == ColdStartPublishVariant.Baseline && baseline < each)
            {
                list.Add(new ColdStartTrial { Variant = variant, IsWarmup = isWarmup });
                baseline++;
            }
            else if (variant == ColdStartPublishVariant.ReadyToRun && r2r < each)
            {
                list.Add(new ColdStartTrial { Variant = variant, IsWarmup = isWarmup });
                r2r++;
            }

            i++;
            if (i > 1_000_000)
            {
                throw new InvalidOperationException("ABBA schedule did not terminate.");
            }
        }
    }
}
