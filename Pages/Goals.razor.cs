using CalorieTracker.Models;
using CalorieTracker.Services;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Pages;

public partial class Goals
{
    private bool _editing;
    private GoalSettings _form = new();
    private DateTime _formBirth = new(1990, 1, 1);
    private int _formFeet = 5;
    private double _formInches = 10;
    private double _formCm = 178;
    private double? _formBodyFat;
    private double? _formWeight;
    private double? _formTarget;
    private string _formRate = "0.5";
    private string? _formError;
    private double? _quickWeight;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnChanged;
        await State.EnsureLoadedAsync();
    }

    private string WeightUnit => _form.Units == UnitSystem.Imperial ? "lb" : "kg";
    private UnitSystem Units => State.Data.Goals?.Units ?? _form.Units;

    private string FormatWeight(double kg) => Units == UnitSystem.Imperial
        ? $"{GoalMath.KgToLb(kg):0.#} lb"
        : $"{kg:0.#} kg";

    private string FormatRate(double kgPerWeek) => Units == UnitSystem.Imperial
        ? $"{GoalMath.KgToLb(kgPerWeek):0.0} lb"
        : $"{kgPerWeek:0.00} kg";

    private static string FormulaName(GoalSettings g) =>
        g.UseLeanMassFormula && g.BodyFatPercent is not null ? "Katch-McArdle" : "Mifflin-St Jeor";

    private IEnumerable<(DateOnly Date, double Kg)> RecentWeights() =>
        State.Data.Weights
            .Select(kv => (Ok: DateOnly.TryParse(kv.Key, out var d), Date: d, Kg: kv.Value))
            .Where(p => p.Ok)
            .OrderByDescending(p => p.Date)
            .Take(10)
            .Select(p => (p.Date, p.Kg));

    private void BeginEdit()
    {
        var g = State.Data.Goals!;
        _form = new GoalSettings
        {
            Units = g.Units,
            Sex = g.Sex,
            BirthDate = g.BirthDate,
            HeightCm = g.HeightCm,
            Activity = g.Activity,
            UseLeanMassFormula = g.UseLeanMassFormula,
            BodyFatPercent = g.BodyFatPercent,
            TargetWeightKg = g.TargetWeightKg,
            TargetRatePercentPerWeek = g.TargetRatePercentPerWeek,
        };
        _formBirth = g.BirthDate.ToDateTime(TimeOnly.MinValue);
        _formBodyFat = g.BodyFatPercent;
        _formRate = g.TargetRatePercentPerWeek.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetHeightFields(g.HeightCm);
        _formTarget = ToDisplay(g.TargetWeightKg);
        var latest = RecentWeights().FirstOrDefault();
        _formWeight = latest.Kg > 0 ? ToDisplay(latest.Kg) : null;
        _formError = null;
        _editing = true;
    }

    private double? ToDisplay(double kg) =>
        Math.Round(_form.Units == UnitSystem.Imperial ? GoalMath.KgToLb(kg) : kg, 1);

    private double FromDisplay(double value) =>
        _form.Units == UnitSystem.Imperial ? GoalMath.LbToKg(value) : value;

    private void SetHeightFields(double cm)
    {
        _formCm = Math.Round(cm, 1);
        var totalInches = cm / 2.54;
        _formFeet = (int)(totalInches / 12);
        _formInches = Math.Round(totalInches - _formFeet * 12, 1);
    }

    private void OnUnitsChanged(ChangeEventArgs e)
    {
        var newUnits = Enum.Parse<UnitSystem>((string)e.Value!);
        if (newUnits == _form.Units) return;
        // Convert the display values in place so nothing the user typed is lost.
        var weightKg = _formWeight is > 0 ? FromDisplay(_formWeight.Value) : (double?)null;
        var targetKg = _formTarget is > 0 ? FromDisplay(_formTarget.Value) : (double?)null;
        var heightCm = _form.Units == UnitSystem.Imperial ? (_formFeet * 12 + _formInches) * 2.54 : _formCm;
        _form.Units = newUnits;
        _formWeight = weightKg is null ? null : ToDisplay(weightKg.Value);
        _formTarget = targetKg is null ? null : ToDisplay(targetKg.Value);
        SetHeightFields(heightCm);
    }

    private async Task SaveAsync()
    {
        _formError = null;
        _form.HeightCm = _form.Units == UnitSystem.Imperial
            ? (_formFeet * 12 + _formInches) * 2.54
            : _formCm;
        _form.BirthDate = DateOnly.FromDateTime(_formBirth);
        _form.BodyFatPercent = _form.UseLeanMassFormula ? _formBodyFat : null;
        if (_form.UseLeanMassFormula && _formBodyFat is not (> 0 and < 70))
        {
            _formError = "The lean-mass formula needs a body fat percentage (or untick it to use Mifflin-St Jeor).";
            return;
        }
        if (_formWeight is not > 0 && !State.Data.Weights.Any())
        {
            _formError = "Enter your current weight so CalTrack has a starting point.";
            return;
        }
        if (_formTarget is not > 0)
        {
            _formError = "Enter a target weight (it can equal your current weight to maintain).";
            return;
        }
        _form.TargetWeightKg = FromDisplay(_formTarget.Value);
        _form.TargetRatePercentPerWeek = double.Parse(_formRate, System.Globalization.CultureInfo.InvariantCulture);

        var error = await State.SaveGoalsAsync(_form);
        if (error is not null)
        {
            _formError = error;
            return;
        }
        if (_formWeight is > 0)
            await State.LogWeightAsync(Today, FromDisplay(_formWeight.Value));
        _editing = false;
    }

    private static int DaysSinceWeighIn(GoalStatus status) =>
        Today.DayNumber - status.LatestWeightDate.DayNumber;

    private async Task QuickLogAsync()
    {
        if (_quickWeight is not > 0) return;
        var kg = Units == UnitSystem.Imperial ? GoalMath.LbToKg(_quickWeight.Value) : _quickWeight.Value;
        await State.LogWeightAsync(Today, kg);
        _quickWeight = null;
    }

    private async Task RemoveWeightAsync(DateOnly date) => await State.RemoveWeightAsync(date);

    private void OnChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => State.Changed -= OnChanged;
}
