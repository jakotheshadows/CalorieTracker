using CalorieTracker.Models;

namespace CalorieTracker.Data;

/// <summary>A randomized plan for a range of days, ready to store with <see cref="AppDataOps.SetDays"/>.</summary>
/// <param name="Days">yyyy-MM-dd → the generated entries, for the days that were generated.</param>
/// <param name="Missed">How many of those couldn't fully meet the targets (the closest fit was used).</param>
/// <param name="Skipped">Days left alone because they already had entries.</param>
public sealed record PlanResult(string? Error, SortedDictionary<string, List<ScheduleEntry>> Days, int Missed, List<string> Skipped);

/// <summary>
/// The schedule randomizer: fills days from the menu toward a calorie target and nutrient
/// minimums. Shared by the app's Randomize and the MCP server, which generates a plan and
/// sends the app the concrete days, so what the user is told is exactly what lands.
/// </summary>
public static class Planner
{
    public static bool IsRandomEligible(FoodItem item) => (item.Calories ?? 0) > 0 && !item.ExcludeFromRandom;

    /// <summary>
    /// Plan each day in [start, end]. The calorie target acts as a daily cap (tolerance sets how
    /// far a day may exceed it); nutrient targets act as daily minimums.
    /// </summary>
    public static PlanResult Plan(AppData data, DateOnly start, DateOnly endInclusive, RandomTargets targets, bool overwriteExisting, Random rng)
    {
        var days = new SortedDictionary<string, List<ScheduleEntry>>(StringComparer.Ordinal);
        var skipped = new List<string>();
        if (!(targets.Calories > 0)) return new("Calorie target must be greater than zero.", days, 0, skipped);
        if (endInclusive < start) return new("End date must be on or after the start date.", days, 0, skipped);

        var pool = data.AllMenuItems().Where(IsRandomEligible).ToList();
        if (pool.Count == 0) return new("Random generation needs at least one menu item or recipe with calories greater than zero that isn't excluded from randomize.", days, 0, skipped);

        var missed = 0;
        for (var d = start; d <= endInclusive; d = d.AddDays(1))
        {
            var key = AppData.DayKey(d);
            if (!overwriteExisting && data.Days.TryGetValue(key, out var existing) && existing.Count > 0)
            {
                skipped.Add(key);
                continue;
            }
            var (entries, meetsTargets) = GenerateDay(pool, targets, rng);
            days[key] = entries;
            if (!meetsTargets) missed++;
        }
        return new(null, days, missed, skipped);
    }

    /// <summary>The warning the app shows when some days couldn't meet the targets.</summary>
    public static string? MissedWarning(PlanResult plan) =>
        plan.Missed > 0
            ? $"{plan.Missed} of {plan.Days.Count} generated day(s) couldn't fully meet the targets with the eligible menu items — the closest fit was used. Consider adding higher-nutrient items or raising the calorie cap."
            : null;

    public static (List<ScheduleEntry> Entries, bool MeetsTargets) GenerateDay(List<FoodItem> pool, RandomTargets t, Random rng)
    {
        var minItems = Math.Max(1, Math.Min(t.MinItemsPerDay, pool.Count));
        var maxItems = Math.Max(minItems, Math.Min(t.MaxItemsPerDay, pool.Count));
        var maxServings = Math.Max(1, t.MaxServingsPerItem);
        var tol = Math.Max(0.5, t.TolerancePercent) / 100.0;
        var calCap = t.Calories * (1 + tol);
        var targets = t.NutrientTargets.Where(kv => kv.Value > 0).ToArray();

        FoodItem[]? bestItems = null;
        int[]? bestServings = null;
        var bestK = 0;
        var bestScore = double.MaxValue;
        var bestMeets = false;
        var indices = Enumerable.Range(0, pool.Count).ToArray();

        for (var iter = 0; iter < 600; iter++)
        {
            // Random distinct item set, one serving each.
            var k = rng.Next(minItems, maxItems + 1);
            for (var i = 0; i < k; i++)
            {
                var j = rng.Next(i, indices.Length);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            var items = new FoodItem[k];
            var servings = new int[k];
            for (var i = 0; i < k; i++)
            {
                items[i] = pool[indices[i]];
                servings[i] = 1;
            }

            double Calories()
            {
                var c = 0.0;
                for (var i = 0; i < k; i++) c += (items[i].Calories ?? 0) * servings[i];
                return c;
            }

            double NutrientTotal(string nutrientKey)
            {
                var v = 0.0;
                for (var i = 0; i < k; i++) v += (items[i].Nutrient(nutrientKey) ?? 0) * servings[i];
                return v;
            }

            // Asymmetric score: exceeding the calorie cap and missing nutrient minimums are heavily
            // penalized; staying under the calorie target is only mildly penalized (fill it up).
            double Score(out bool meets)
            {
                var cal = Calories();
                var score = 0.0;
                meets = true;
                if (cal > calCap)
                {
                    score += (cal - calCap) / t.Calories * 8.0;
                    meets = false;
                }
                else
                {
                    score += Math.Max(0, t.Calories - cal) / t.Calories;
                }
                foreach (var (nk, target) in targets)
                {
                    var have = NutrientTotal(nk);
                    if (have < target)
                    {
                        score += (target - have) / target * 4.0;
                        if (have < target * (1 - tol)) meets = false;
                    }
                }
                return score;
            }

            // Greedy phase 1: satisfy nutrient minimums by repeatedly bumping the item that
            // delivers the most missing nutrients per calorie, while it still fits under the cap.
            for (var guard = 0; guard < 200 && targets.Length > 0; guard++)
            {
                if (!targets.Any(kv => NutrientTotal(kv.Key) < kv.Value)) break;
                var cal = Calories();
                var bestIdx = -1;
                var bestRatio = 0.0;
                for (var i = 0; i < k; i++)
                {
                    if (servings[i] >= maxServings) continue;
                    var itemCal = items[i].Calories ?? 0;
                    if (cal + itemCal > calCap) continue;
                    var gain = 0.0;
                    foreach (var (nk, target) in targets)
                    {
                        var deficit = target - NutrientTotal(nk);
                        if (deficit > 0) gain += Math.Min(items[i].Nutrient(nk) ?? 0, deficit) / target;
                    }
                    if (gain <= 0) continue;
                    var ratio = gain / Math.Max(itemCal, 1);
                    if (ratio > bestRatio)
                    {
                        bestRatio = ratio;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) break;
                servings[bestIdx]++;
            }

            // Greedy phase 2: fill remaining room up toward the calorie target with random bumps.
            for (var guard = 0; guard < 100; guard++)
            {
                var cal = Calories();
                if (cal >= t.Calories * (1 - tol)) break;
                var fillable = new List<int>();
                for (var i = 0; i < k; i++)
                {
                    var itemCal = items[i].Calories ?? 0;
                    if (servings[i] < maxServings && itemCal > 0 && cal + itemCal <= calCap)
                        fillable.Add(i);
                }
                if (fillable.Count == 0) break;
                servings[fillable[rng.Next(fillable.Count)]]++;
            }

            // Local search: single-serving tweaks while they improve the score.
            var current = Score(out var meets);
            var improved = true;
            for (var pass = 0; improved && pass < 20; pass++)
            {
                improved = false;
                for (var i = 0; i < k; i++)
                {
                    foreach (var delta in Deltas)
                    {
                        var ns = servings[i] + delta;
                        if (ns < 1 || ns > maxServings) continue;
                        servings[i] = ns;
                        var s = Score(out var m);
                        if (s < current - 1e-9)
                        {
                            current = s;
                            meets = m;
                            improved = true;
                        }
                        else
                        {
                            servings[i] -= delta;
                        }
                    }
                }
            }

            if (current < bestScore)
            {
                bestScore = current;
                bestMeets = meets;
                bestItems = items;
                bestServings = servings;
                bestK = k;
                // Perfect enough: minimums met, under the cap, and filled to within tolerance.
                if (meets && current <= tol + 0.001) break;
            }
        }

        var entries = new List<ScheduleEntry>(bestK);
        for (var i = 0; i < bestK; i++)
            entries.Add(new ScheduleEntry { ItemName = bestItems![i].Name, Servings = bestServings![i] });
        entries.Sort((a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));
        return (entries, bestMeets);
    }

    private static readonly int[] Deltas = { 1, -1 };
}
