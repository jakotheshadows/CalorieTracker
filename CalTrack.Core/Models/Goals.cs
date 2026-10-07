namespace CalorieTracker.Models;

public enum Sex { Male, Female }

public enum ActivityLevel { Sedentary, Light, Moderate, Active, VeryActive }

public enum UnitSystem { Imperial, Metric }

/// <summary>
/// The user's profile and weight goal. Weights are stored in kilograms and heights in
/// centimeters regardless of the display unit system.
/// </summary>
public class GoalSettings
{
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;
    public Sex Sex { get; set; } = Sex.Male;
    public DateOnly BirthDate { get; set; } = new(1990, 1, 1);
    public double HeightCm { get; set; }
    public ActivityLevel Activity { get; set; } = ActivityLevel.Sedentary;

    /// <summary>Use the Katch-McArdle (lean mass) formula instead of Mifflin-St Jeor. Needs a body fat %.</summary>
    public bool UseLeanMassFormula { get; set; }
    public double? BodyFatPercent { get; set; }

    public double TargetWeightKg { get; set; }

    /// <summary>Desired loss rate as % of body weight per week. 0 = maintain. Clamped to 1%.</summary>
    public double TargetRatePercentPerWeek { get; set; } = 0.5;
}
