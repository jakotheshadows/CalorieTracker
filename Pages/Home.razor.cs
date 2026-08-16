using CalorieTracker.Models;
using CalorieTracker.Services;

namespace CalorieTracker.Pages;

public partial class Home
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private double? _quickWeight;

    private string WeightUnit => State.Data.Goals?.Units == UnitSystem.Metric ? "kg" : "lb";

    private async Task QuickLogAsync()
    {
        if (_quickWeight is not > 0) return;
        var kg = State.Data.Goals?.Units == UnitSystem.Metric ? _quickWeight.Value : GoalMath.LbToKg(_quickWeight.Value);
        await State.LogWeightAsync(Today, kg);
        _quickWeight = null;
    }

    protected override void OnInitialized() => State.Changed += OnChanged;
    private void OnChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => State.Changed -= OnChanged;
}
