using CalorieTracker.Models;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class TotalsPanel
{
    [Parameter] public string Title { get; set; } = "Totals";
    [Parameter] public Totals Totals { get; set; } = new();
    [Parameter] public int DayCount { get; set; } = 1;
}
