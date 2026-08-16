using CalorieTracker.Services;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class UsdaNameField
{
    [Parameter] public string Label { get; set; } = "Name";
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public EventCallback<UsdaApplied> OnApplied { get; set; }

    private bool _open;
    private UsdaPanel? _panel;
    private int? _fdcId;

    private Task OnNameChangedAsync(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString());

    // First click opens the panel, which searches the name on init; later clicks re-search.
    private async Task SearchClickAsync()
    {
        if (!_open) _open = true;
        else if (_panel is not null) await _panel.SearchAsync(Value);
    }

    private async Task HandleAppliedAsync(UsdaApplied a)
    {
        if (a.Food.FdcId != _fdcId)
        {
            _fdcId = a.Food.FdcId;
            await ValueChanged.InvokeAsync(a.Food.Description);
        }
        await OnApplied.InvokeAsync(a);
    }
}
