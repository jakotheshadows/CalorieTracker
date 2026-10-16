using System.ComponentModel;
using System.Globalization;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Services;
using CalorieTracker.Sync;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// The weight log and the goal profile, read and changed with the same math and limits as
/// the app's Goals screen (including its refusal to set a target below a healthy weight).
/// Weights are stored in kilograms; tools speak the user's units.
/// </summary>
public static partial class CalTrackTools
{
    [McpServerTool(Name = "get_goals", ReadOnly = true, Idempotent = true)]
    [Description(
        "The user's weight goal and progress, as CalTrack's Goals screen shows them: their profile (sex, age, height, " +
        "activity, target weight and weekly rate), the estimated maintenance calories (TDEE, which adapts to their logged " +
        "intake and weigh-ins), suggested daily calories, trend weight, BMI, expected date to reach the target, and " +
        "recent weigh-ins. Weights are in the user's units.")]
    public static string GetGoals(DataFolderStore store, TimeProvider clock)
    {
        var view = store.Read().View;
        var unit = WeightUnitOf(view);
        var recent = view.Weights
            .Select(w => (Ok: Inbox.TryParseDay(w.Key, out var d), Date: d, Kg: w.Value))
            .Where(w => w.Ok).OrderByDescending(w => w.Date).Take(14)
            .Select(w => new { date = AppData.DayKey(w.Date), weight = ShowWeight(w.Kg, unit) }).ToList();
        var status = view.GoalStatus(Today(clock));
        return Json(new
        {
            goalsSetUp = view.Goals is not null,
            weightUnit = unit,
            profile = view.Goals is { } g ? GoalProfile(g, Today(clock)) : null,
            status = status is null ? null : GoalStatusInfo(status, unit),
            recentWeights = recent.Count > 0 ? recent : null,
            note = view.Goals is null
                ? "Goals aren't set up. set_goals sets them up with the user's sex, birth date, height and target weight."
                : status is null ? "No weight logged yet — log_weight records one, and progress appears." : null,
        });
    }

    [McpServerTool(Name = "log_weight", Destructive = true, Idempotent = true)]
    [Description(
        "Record the user's body weight for a day, e.g. \"I weighed 182 this morning\". One weight per day: a second " +
        "one replaces the first (the result says so). Only a weight the user stated.")]
    public static string LogWeight(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The weight, e.g. 182.4.")] double weight,
        [Description("lb or kg. Defaults to the units in the user's goals.")] string? unit = null,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null)
    {
        var day = ResolveDate(date, clock);
        var snap = store.Read();
        var u = ResolveWeightUnit(unit, snap.View);
        var kg = u == "lb" ? GoalMath.LbToKg(weight) : weight;
        if (!(kg >= 20 && kg <= 400)) throw new McpException($"{weight} {u} doesn't look like a body weight; nothing was logged.");
        var key = AppData.DayKey(day);
        double? before = snap.View.Weights.TryGetValue(key, out var old) ? old : null;

        var op = NewOp(InboxOp.LogWeight, clock);
        op.Date = key;
        op.WeightKg = kg;
        var (after, status, _) = Submit(store, snap, op, "log weights");
        var goal = after.View.GoalStatus(Today(clock));
        return Json(new
        {
            logged = new { date = key, weight = ShowWeight(kg, u), unit = u },
            replaced = before is { } b ? new { weight = ShowWeight(b, u), unit = u } : null,
            status,
            note = QueuedNote,
            progress = goal is null ? null : GoalStatusInfo(goal, u),
        });
    }

    [McpServerTool(Name = "remove_weight", Destructive = true)]
    [Description("Remove the weight logged on a day, e.g. a mistyped weigh-in. The result says what it was.")]
    public static string RemoveWeight(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\" — always say which day.")] string date)
    {
        var day = ResolveDate(date ?? throw new McpException("date is required."), clock);
        var snap = store.Read();
        var key = AppData.DayKey(day);
        if (!snap.View.Weights.TryGetValue(key, out var kg)) throw new McpException($"No weight is logged on {key}, so nothing was changed.");

        var op = NewOp(InboxOp.RemoveWeight, clock);
        op.Date = key;
        var (_, status, _) = Submit(store, snap, op, "remove weights");
        var u = WeightUnitOf(snap.View);
        return Json(new { removed = new { date = key, weight = ShowWeight(kg, u), unit = u }, status, note = QueuedNote });
    }

    [McpServerTool(Name = "set_goals", Destructive = true, Idempotent = true)]
    [Description(
        "Set up or change the user's weight goal and profile — only values the user stated. Setting goals up the first " +
        "time needs sex, birthDate, height and targetWeight; after that, pass only what changes. CalTrack refuses " +
        "targets below a healthy weight for the user's height and caps the loss rate at 1% of body weight a week. " +
        "The result shows the profile before and after, and the new suggested calories.")]
    public static string SetGoals(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Imperial (lb, inches) or Metric (kg, cm) — how CalTrack shows weights and height.")] string? units = null,
        [Description("Male or Female (used by the calorie formula).")] string? sex = null,
        [Description("Birth date, yyyy-MM-dd.")] string? birthDate = null,
        [Description("Height, in heightUnit.")] double? height = null,
        [Description("in or cm. Defaults to the profile's units (Imperial: in).")] string? heightUnit = null,
        [Description("Sedentary, Light, Moderate, Active or VeryActive.")] string? activity = null,
        [Description("true to use the lean-mass (Katch-McArdle) formula, which needs bodyFatPercent.")] bool? useLeanMassFormula = null,
        [Description("Body fat %, e.g. 22.")] double? bodyFatPercent = null,
        [Description("Target weight, in weightUnit.")] double? targetWeight = null,
        [Description("lb or kg. Defaults to the profile's units.")] string? weightUnit = null,
        [Description("Weekly loss as % of body weight, 0 to 1 (0 = maintain). E.g. 0.5.")] double? targetRatePercentPerWeek = null)
    {
        var snap = store.Read();
        var current = snap.View.Goals;
        var patch = new GoalsPatch
        {
            Units = units is null ? null : ParseEnum<UnitSystem>(units, "units", "Imperial or Metric"),
            Sex = sex is null ? null : ParseEnum<Sex>(sex, "sex", "Male or Female"),
            Activity = activity is null ? null : ParseEnum<ActivityLevel>(activity, "activity", "Sedentary, Light, Moderate, Active or VeryActive"),
            UseLeanMassFormula = useLeanMassFormula,
        };
        var system = patch.Units ?? current?.Units ?? UnitSystem.Imperial;
        if (birthDate is not null)
        {
            if (!Inbox.TryParseDay(birthDate.Trim(), out var birth)) throw new McpException($"birthDate must be yyyy-MM-dd (got \"{birthDate}\").");
            var age = GoalMath.AgeYears(birth, Today(clock));
            if (age is < 13 or > 120) throw new McpException($"A birth date of {birthDate} makes the user {age}; CalTrack's formulas are for ages 13 to 120.");
            patch.BirthDate = birth;
        }
        if (height is { } h)
        {
            var hu = (heightUnit ?? (system == UnitSystem.Imperial ? "in" : "cm")).Trim().ToLowerInvariant();
            patch.HeightCm = hu switch
            {
                "cm" => h,
                "in" or "inch" or "inches" => h * 2.54,
                _ => throw new McpException($"heightUnit must be in or cm (got \"{heightUnit}\")."),
            };
        }
        if (targetWeight is { } tw)
        {
            var wu = weightUnit is null ? (system == UnitSystem.Imperial ? "lb" : "kg") : ResolveWeightUnit(weightUnit, snap.View);
            patch.TargetWeightKg = wu == "lb" ? GoalMath.LbToKg(tw) : tw;
        }
        if (bodyFatPercent is { } bf)
        {
            if (!(bf >= 3 && bf < 70)) throw new McpException($"bodyFatPercent must be between 3 and 70 (got {bf}).");
            patch.BodyFatPercent = bf;
        }
        if (targetRatePercentPerWeek is { } rate)
        {
            if (!(rate >= 0 && rate <= GoalMath.MaxRatePercentPerWeek))
                throw new McpException($"targetRatePercentPerWeek must be between 0 and {GoalMath.MaxRatePercentPerWeek} (got {rate}).");
            patch.TargetRatePercentPerWeek = rate;
        }
        if (patch is { Units: null, Sex: null, BirthDate: null, HeightCm: null, Activity: null, UseLeanMassFormula: null, BodyFatPercent: null, TargetWeightKg: null, TargetRatePercentPerWeek: null })
            throw new McpException("Nothing to change: pass at least one field.");

        var op = NewOp(InboxOp.SetGoals, clock);
        op.GoalsPatch = patch;
        var (after, status, _) = Submit(store, snap, op, "change goals");
        var u = WeightUnitOf(after.View);
        var goal = after.View.GoalStatus(Today(clock));
        return Json(new
        {
            before = current is null ? null : GoalProfile(current, Today(clock)),
            after = after.View.Goals is { } g ? GoalProfile(g, Today(clock)) : null,
            status,
            note = QueuedNote + (goal is null ? " Progress appears once a weight is logged (log_weight)." : ""),
            progress = goal is null ? null : GoalStatusInfo(goal, u),
        });
    }

    // ---------- helpers ----------

    private static string WeightUnitOf(AppData view) => view.Goals?.Units == UnitSystem.Metric ? "kg" : "lb";

    private static string ResolveWeightUnit(string? unit, AppData view)
    {
        if (unit is null)
            return view.Goals is not null
                ? WeightUnitOf(view)
                : throw new McpException("unit is required (lb or kg): the user hasn't set up goals, so CalTrack doesn't know their units.");
        return unit.Trim().ToLowerInvariant() switch
        {
            "lb" or "lbs" or "pound" or "pounds" => "lb",
            "kg" or "kgs" or "kilogram" or "kilograms" => "kg",
            _ => throw new McpException($"unit must be lb or kg (got \"{unit}\")."),
        };
    }

    private static double ShowWeight(double kg, string unit) => Math.Round(unit == "lb" ? GoalMath.KgToLb(kg) : kg, 1);

    private static T ParseEnum<T>(string value, string field, string allowed) where T : struct, Enum =>
        Enum.TryParse<T>(value.Trim().Replace(" ", ""), ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v
            : throw new McpException($"{field} must be {allowed} (got \"{value}\").");

    private static object GoalProfile(GoalSettings g, DateOnly today)
    {
        var unit = g.Units == UnitSystem.Metric ? "kg" : "lb";
        var inches = g.HeightCm / 2.54;
        return new
        {
            units = g.Units.ToString(),
            sex = g.Sex.ToString(),
            birthDate = g.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            age = GoalMath.AgeYears(g.BirthDate, today),
            height = g.Units == UnitSystem.Metric
                ? $"{g.HeightCm:0} cm"
                : $"{(int)(inches / 12)} ft {Math.Round(inches % 12):0} in ({g.HeightCm:0} cm)",
            activity = g.Activity.ToString(),
            formula = g.UseLeanMassFormula ? "Katch-McArdle (lean mass)" : "Mifflin-St Jeor",
            bodyFatPercent = g.BodyFatPercent,
            targetWeight = ShowWeight(g.TargetWeightKg, unit),
            weightUnit = unit,
            targetRatePercentPerWeek = g.TargetRatePercentPerWeek,
        };
    }

    private static object GoalStatusInfo(GoalStatus s, string unit) => new
    {
        latestWeight = ShowWeight(s.LatestWeightKg, unit),
        latestWeightDate = AppData.DayKey(s.LatestWeightDate),
        trendWeight = ShowWeight(s.TrendWeightKg, unit),
        weightUnit = unit,
        bmi = Math.Round(s.Bmi, 1),
        maintenanceCalories = Math.Round(s.Tdee),
        maintenanceFrom = s.ObservedTdee is null
            ? "the formula (not enough logged days and weigh-ins yet to learn from)"
            : $"{Math.Round(s.Confidence * 100)}% learned from {s.DataDays} logged days, the rest from the formula",
        suggestedDailyCalories = Math.Round(s.SuggestedCalories),
        floorApplied = s.FloorApplied ? true : (bool?)null,
        expectedChangePerWeek = s.AtTarget ? null : $"-{ShowWeight(s.KgPerWeek, unit)} {unit}",
        expectedTargetDate = s.Eta is { } eta ? AppData.DayKey(eta) : null,
        atTarget = s.AtTarget ? true : (bool?)null,
    };
}
