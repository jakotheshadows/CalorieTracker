using CalorieTracker.Models;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class RecipeForm
{
    [Parameter] public Recipe Recipe { get; set; } = new();
    [Parameter] public string? OriginalName { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
}
