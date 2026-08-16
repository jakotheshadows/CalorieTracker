using CalorieTracker.Services;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class UsdaPanel
{
    /// <summary>The search text at open time (the parent's name field).</summary>
    [Parameter] public string? Query { get; set; }
    [Parameter] public EventCallback<UsdaApplied> OnApplied { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private bool _hasKey;
    private bool _busy;
    private string? _lastQuery;
    private string? _error;
    private string? _applied;
    private List<UsdaFood>? _results;
    private UsdaFood? _selected;
    private double _servAmount;
    private string _servUnit = "g";

    protected override async Task OnInitializedAsync()
    {
        _hasKey = await Usda.GetApiKeyAsync() is not null;
        if (_hasKey && !string.IsNullOrWhiteSpace(Query))
            await SearchAsync(Query);
    }

    /// <summary>Run a search. Called on open and by the parent (via @ref) on later button clicks.</summary>
    public async Task SearchAsync(string? query)
    {
        if (!_hasKey || _busy) return;
        query = query?.Trim() ?? "";
        if (query.Length == 0)
        {
            _lastQuery = null;
            _results = null;
            _selected = null;
            _error = null;
            _applied = null;
            await InvokeAsync(StateHasChanged);
            return;
        }

        _busy = true;
        _lastQuery = query;
        _error = null;
        _applied = null;
        _results = null;
        _selected = null;
        await InvokeAsync(StateHasChanged);
        (_results, _error) = await Usda.SearchAsync(query);
        _busy = false;
        await InvokeAsync(StateHasChanged);
    }

    private Task CloseAsync() => OnClose.InvokeAsync();

    private async Task ApplyAsync(UsdaFood food)
    {
        _selected = food;
        _servAmount = food.DefaultAmount;
        _servUnit = food.BaseUnit;
        await FireAppliedAsync();
    }

    private async Task OnServingAmountChangedAsync(string? raw)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
            _servAmount = v;
        await FireAppliedAsync();
    }

    private async Task OnServingUnitChangedAsync(string? unit)
    {
        if (!string.IsNullOrEmpty(unit)) _servUnit = unit;
        await FireAppliedAsync();
    }

    private async Task FireAppliedAsync()
    {
        if (_selected is null) return;
        var baseAmount = UsdaService.ToBaseAmount(_servAmount, _servUnit, _selected.BaseUnit);
        if (baseAmount is null)
        {
            _error = "Enter a serving amount greater than zero.";
            return;
        }
        _error = null;
        var text = ServingText(baseAmount.Value);
        _applied = $"Filled from “{_selected.Description}” at {text} — adjust the serving above to rescale.";
        await OnApplied.InvokeAsync(new UsdaApplied(_selected, _servAmount, _servUnit, baseAmount.Value, text));
    }

    private string ServingText(double baseAmount)
    {
        var food = _selected!;
        // The unmodified label serving keeps its household text, e.g. "0.5 cup (113 g)".
        if (food.LabelServingAmount is not null && _servUnit == food.BaseUnit &&
            Math.Abs(_servAmount - food.LabelServingAmount.Value) < 0.001)
            return food.DefaultServingDisplay;
        return _servUnit == food.BaseUnit
            ? $"{_servAmount:0.##} {_servUnit}"
            : $"{_servAmount:0.##} {_servUnit} ({baseAmount:0.#} {food.BaseUnit})";
    }
}
