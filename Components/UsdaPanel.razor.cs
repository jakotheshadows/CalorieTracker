using CalorieTracker.Services;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class UsdaPanel
{
    /// <summary>The search text at open time (the parent's name field).</summary>
    [Parameter] public string? Query { get; set; }

    /// <summary>When set, the panel opens by looking up this barcode instead of searching the name.</summary>
    [Parameter] public string? InitialBarcode { get; set; }
    [Parameter] public EventCallback<UsdaApplied> OnApplied { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private bool _hasKey;
    private bool _busy;
    private int _searchSeq;
    private string? _lastQuery;
    private string? _scannedCode;
    private string? _error;
    private string? _applied;
    private List<UsdaFood>? _results;
    private UsdaFood? _selected;
    private double _servAmount;
    private string _servUnit = "g";

    protected override async Task OnInitializedAsync()
    {
        _hasKey = await Usda.GetApiKeyAsync() is not null;
        // A scanned barcode goes through even without a key: LookupBarcodeAsync then
        // just records the digits, so the read is shown instead of silently dropped.
        if (InitialBarcode is not null) await LookupBarcodeAsync(InitialBarcode);
        else if (_hasKey && !string.IsNullOrWhiteSpace(Query)) await SearchAsync(Query);
    }

    /// <summary>Run a search. Called on open and by the parent (via @ref) on later button clicks.</summary>
    public async Task SearchAsync(string? query)
    {
        if (!_hasKey || _busy) return;
        query = query?.Trim() ?? "";
        if (query.Length == 0)
        {
            _lastQuery = null;
            _scannedCode = null;
            _results = null;
            _selected = null;
            _error = null;
            _applied = null;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var seq = ++_searchSeq;
        _busy = true;
        _lastQuery = query;
        _scannedCode = null;
        _error = null;
        _applied = null;
        _results = null;
        _selected = null;
        await InvokeAsync(StateHasChanged);
        var (results, error) = await Usda.SearchAsync(query);
        if (seq != _searchSeq) return; // a newer search/lookup superseded this one
        (_results, _error) = (results, error);
        _busy = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Look up a scanned/typed barcode and auto-apply the matching food at its label serving.</summary>
    public async Task LookupBarcodeAsync(string code)
    {
        // Surface the decoded digits no matter what happens next — a working camera
        // read must be visible even when the USDA lookup can't run or finds nothing.
        // Below 8 digits nothing was really read (manual junk); the service rejects
        // those too, and a "Barcode read" banner over its rejection would be a lie.
        var digits = new string(code.Where(char.IsDigit).ToArray());
        _scannedCode = digits.Length >= 8 ? digits : null;
        if (!_hasKey)
        {
            await InvokeAsync(StateHasChanged);
            return;
        }
        var seq = ++_searchSeq;
        _busy = true;
        _lastQuery = $"barcode {code}";
        _error = null;
        _applied = null;
        _results = null;
        _selected = null;
        await InvokeAsync(StateHasChanged);

        var (food, error) = await Usda.LookupBarcodeAsync(code);
        if (seq != _searchSeq) return; // a newer search/lookup superseded this one
        _busy = false;
        if (food is null)
        {
            _error = error;
        }
        else
        {
            _results = new List<UsdaFood> { food };
            await ApplyAsync(food);
        }
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
