using CalorieTracker.Models;

namespace CalorieTracker.Services;

/// <summary>One point of the smoothed body-weight series.</summary>
public record struct TrendPoint(DateOnly Date, double WeightKg, double TrendKg);

/// <summary>Everything the Goals UI needs, computed from the profile + weight log + schedule.</summary>
public class GoalStatus
{
    public double LatestWeightKg { get; init; }
    public DateOnly LatestWeightDate { get; init; }
    public double TrendWeightKg { get; init; }
    public double Bmi { get; init; }
    public double MinHealthyWeightKg { get; init; }

    public double SeedTdee { get; init; }
    public double? ObservedTdee { get; init; }
    /// <summary>0..1 — how much of the final TDEE comes from observed data vs the seed formula.</summary>
    public double Confidence { get; init; }
    public int DataDays { get; init; }
    public double Tdee { get; init; }

    public bool AtTarget { get; init; }
    public double RatePercentApplied { get; init; }
    public double SuggestedCalories { get; init; }
    public bool FloorApplied { get; init; }
    public double KgPerWeek { get; init; }
    public DateOnly? Eta { get; init; }
}

/// <summary>
/// Pure math for the goals feature: seed BMR formulas (Mifflin-St Jeor / Katch-McArdle),
/// exponentially smoothed trend weight, and the adaptive TDEE estimate that learns from
/// logged intake vs how trend weight actually moved.
/// </summary>
public static class GoalMath
{
    /// <summary>Approximate energy density of body-weight change, fine at week scale.</summary>
    public const double KcalPerKg = 7700;
    public const double LbPerKg = 2.2046226218;
    public const double MinHealthyBmi = 18.5;
    public const double MaxRatePercentPerWeek = 1.0;

    /// <summary>Days of intake data at which the observed TDEE fully replaces the seed formula.</summary>
    public const int FullConfidenceDays = 21;

    public static double CalorieFloor(Sex sex) => sex == Sex.Male ? 1500 : 1200;

    public static double KgToLb(double kg) => kg * LbPerKg;
    public static double LbToKg(double lb) => lb / LbPerKg;

    public static double ActivityMultiplier(ActivityLevel a) => a switch
    {
        ActivityLevel.Sedentary => 1.2,
        ActivityLevel.Light => 1.375,
        ActivityLevel.Moderate => 1.55,
        ActivityLevel.Active => 1.725,
        _ => 1.9,
    };

    public static int AgeYears(DateOnly birth, DateOnly today)
    {
        var age = today.Year - birth.Year;
        if (today < birth.AddYears(age)) age--;
        return Math.Max(0, age);
    }

    public static double MifflinBmr(Sex sex, int ageYears, double heightCm, double weightKg) =>
        10 * weightKg + 6.25 * heightCm - 5 * ageYears + (sex == Sex.Male ? 5 : -161);

    public static double KatchBmr(double weightKg, double bodyFatPercent) =>
        370 + 21.6 * weightKg * (1 - bodyFatPercent / 100.0);

    public static double SeedTdee(GoalSettings g, double weightKg, DateOnly today)
    {
        var bmr = g.UseLeanMassFormula && g.BodyFatPercent is > 0 and < 70
            ? KatchBmr(weightKg, g.BodyFatPercent.Value)
            : MifflinBmr(g.Sex, AgeYears(g.BirthDate, today), g.HeightCm, weightKg);
        return bmr * ActivityMultiplier(g.Activity);
    }

    /// <summary>
    /// Exponentially smoothed trend weight (25%/day toward each new reading, compounded across
    /// gap days) — damps day-to-day water/sodium noise so real change is visible.
    /// </summary>
    public static List<TrendPoint> TrendSeries(Dictionary<string, double> weights)
    {
        var points = new List<TrendPoint>();
        foreach (var (key, kg) in weights)
            if (DateOnly.TryParse(key, out var date) && kg > 0)
                points.Add(new TrendPoint(date, kg, kg));
        points.Sort((a, b) => a.Date.CompareTo(b.Date));

        for (var i = 1; i < points.Count; i++)
        {
            var gapDays = points[i].Date.DayNumber - points[i - 1].Date.DayNumber;
            var blend = 1 - Math.Pow(0.75, gapDays);
            var trend = points[i - 1].TrendKg + blend * (points[i].WeightKg - points[i - 1].TrendKg);
            points[i] = points[i] with { TrendKg = trend };
        }
        return points;
    }

    /// <summary>
    /// TDEE observed from the last ~4 weeks: average planned intake minus the energy equivalent
    /// of how trend weight moved. Null when there isn't enough weigh-in or intake data yet.
    /// </summary>
    public static (double? Tdee, int DataDays) ObservedTdee(
        List<TrendPoint> trend, Func<DateOnly, double> plannedCalories)
    {
        if (trend.Count < 2) return (null, 0);

        var end = trend[^1];
        var windowStart = end.Date.AddDays(-28);
        var start = trend.First(p => p.Date >= windowStart);
        var spanDays = end.Date.DayNumber - start.Date.DayNumber;
        if (spanDays < 7) return (null, 0);

        double intakeSum = 0;
        var dataDays = 0;
        for (var d = start.Date; d < end.Date; d = d.AddDays(1))
        {
            var cal = plannedCalories(d);
            if (cal > 0) { intakeSum += cal; dataDays++; }
        }
        // Days without a plan are assumed to look like the planned days' average, but if most of
        // the window is unplanned there is nothing trustworthy to learn from.
        if (dataDays * 2 < spanDays) return (null, dataDays);

        var avgIntake = intakeSum / dataDays;
        var kgPerDay = (end.TrendKg - start.TrendKg) / spanDays;
        var tdee = avgIntake - kgPerDay * KcalPerKg;
        if (tdee is < 800 or > 6000) return (null, dataDays);
        return (tdee, dataDays);
    }

    public static GoalStatus? Compute(GoalSettings g, Dictionary<string, double> weights,
        Func<DateOnly, double> plannedCalories, DateOnly today)
    {
        var trend = TrendSeries(weights);
        if (trend.Count == 0) return null;

        var latest = trend[^1];
        var currentKg = latest.TrendKg;
        var heightM = g.HeightCm / 100.0;
        var minHealthyKg = heightM > 0 ? MinHealthyBmi * heightM * heightM : 0;

        var seed = SeedTdee(g, currentKg, today);
        var (observed, dataDays) = ObservedTdee(trend, plannedCalories);
        var confidence = observed is null ? 0 : Math.Min(1.0, (double)dataDays / FullConfidenceDays);
        var tdee = observed is null ? seed : seed + confidence * (observed.Value - seed);

        var targetKg = Math.Max(g.TargetWeightKg, minHealthyKg);
        var rate = Math.Clamp(g.TargetRatePercentPerWeek, 0, MaxRatePercentPerWeek);
        var atTarget = currentKg <= targetKg + 0.1 || rate <= 0;

        double suggested, kgPerWeek = 0;
        var floorApplied = false;
        DateOnly? eta = null;

        if (atTarget)
        {
            suggested = tdee;
        }
        else
        {
            var deficit = currentKg * rate / 100.0 * KcalPerKg / 7.0;
            suggested = tdee - deficit;
            var floor = CalorieFloor(g.Sex);
            if (suggested < floor)
            {
                suggested = floor;
                floorApplied = true;
            }
            kgPerWeek = Math.Max(0, tdee - suggested) * 7.0 / KcalPerKg;
            if (kgPerWeek > 0.001)
                eta = today.AddDays((int)Math.Ceiling((currentKg - targetKg) / kgPerWeek * 7.0));
        }

        return new GoalStatus
        {
            LatestWeightKg = latest.WeightKg,
            LatestWeightDate = latest.Date,
            TrendWeightKg = currentKg,
            Bmi = heightM > 0 ? currentKg / (heightM * heightM) : 0,
            MinHealthyWeightKg = minHealthyKg,
            SeedTdee = seed,
            ObservedTdee = observed,
            Confidence = confidence,
            DataDays = dataDays,
            Tdee = tdee,
            AtTarget = atTarget,
            RatePercentApplied = rate,
            SuggestedCalories = suggested,
            FloorApplied = floorApplied,
            KgPerWeek = kgPerWeek,
            Eta = eta,
        };
    }
}
