using CalorieTracker.Models;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class NutrientFields
{
    [Parameter] public string MacrosLabel { get; set; } = "Macronutrients";
    [Parameter] public string MicrosLabel { get; set; } = "Micronutrients";
    [Parameter] public string Suffix { get; set; } = "optional";
    [Parameter] public Func<string, string?> GetValue { get; set; } = _ => null;
    [Parameter] public Action<string, string?> SetValue { get; set; } = (_, _) => { };

    private bool _microsOpen;
    private bool _userToggled;

    private void ToggleMicros()
    {
        _microsOpen = !_microsOpen;
        _userToggled = true;
    }

    protected override void OnParametersSet()
    {
        // Auto-open when micro values exist (e.g. after a USDA fill), until the user takes over.
        if (!_userToggled)
            _microsOpen = NutrientCatalog.Micros.Any(m => !string.IsNullOrEmpty(GetValue(m.Key)));
    }
}
