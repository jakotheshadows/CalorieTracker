using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class ConfirmButton
{
    [Parameter] public string Label { get; set; } = "Delete";
    [Parameter] public EventCallback OnConfirm { get; set; }

    private bool _armed;

    private async Task ClickAsync()
    {
        if (!_armed)
        {
            _armed = true;
            return;
        }
        _armed = false;
        await OnConfirm.InvokeAsync();
    }
}
