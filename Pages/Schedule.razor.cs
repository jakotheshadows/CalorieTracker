using CalorieTracker.Models;
using CalorieTracker.Services;

namespace CalorieTracker.Pages;

public partial class Schedule
{
    private enum ViewMode { Day, Week, Month }

    private ViewMode _view = ViewMode.Day;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private string _addItemName = "";
    private double _addServings = 1;

    private FoodItem? _adhocItem;
    private ScheduleEntry? _adhocEntry;
    private double _adhocServings = 1;
    private bool _adhocSaveToMenu;
    private string? _adhocError;

    private string _templateSel = "";
    private string? _templateToDelete;
    private bool _savingTemplate;
    private bool _confirmOverwrite;
    private string _newTemplateName = "";
    private string? _templateError;

    private bool _randOpen;
    private bool _randOverwrite;
    private bool _randDone;
    private string? _randError;
    private string? _randWarning;
    private DateOnly _randStart = Today;
    private DateOnly _randEnd = Today;
    private RandomTargets _targets = new();
    private bool _randFromGoal;

    protected override void OnInitialized() => State.Changed += OnChanged;
    private void OnChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => State.Changed -= OnChanged;

    private void GoToday() => _date = Today;

    private void Navigate(int direction)
    {
        _date = _view switch
        {
            ViewMode.Day => _date.AddDays(direction),
            ViewMode.Week => _date.AddDays(7 * direction),
            _ => _date.AddMonths(direction),
        };
    }

    private void OpenDay(DateOnly day)
    {
        _date = day;
        _view = ViewMode.Day;
    }

    private (DateOnly Start, DateOnly End) VisibleRange() => _view switch
    {
        ViewMode.Day => (_date, _date),
        ViewMode.Week => (AppState.WeekStart(_date), AppState.WeekStart(_date).AddDays(6)),
        _ => (new DateOnly(_date.Year, _date.Month, 1),
              new DateOnly(_date.Year, _date.Month, DateTime.DaysInMonth(_date.Year, _date.Month))),
    };

    private string RangeLabel() => _view switch
    {
        ViewMode.Day => _date.ToString("MMM d, yyyy"),
        ViewMode.Week => $"{AppState.WeekStart(_date):MMM d} – {AppState.WeekStart(_date).AddDays(6):MMM d, yyyy}",
        _ => _date.ToString("MMMM yyyy"),
    };

    private string FormatGoalRate(double kgPerWeek) => State.Data.Goals?.Units == UnitSystem.Metric
        ? $"{kgPerWeek:0.00} kg"
        : $"{GoalMath.KgToLb(kgPerWeek):0.0} lb";

    private string TotalsTitle() => _view switch
    {
        ViewMode.Day => "Day totals",
        ViewMode.Week => "Week totals",
        _ => "Month totals",
    };

    private Totals RangeTotals()
    {
        var (start, end) = VisibleRange();
        return State.TotalsForRange(start, end);
    }

    private int RangeDayCount()
    {
        var (start, end) = VisibleRange();
        return end.DayNumber - start.DayNumber + 1;
    }

    private async Task AddEntryAsync()
    {
        if (string.IsNullOrEmpty(_addItemName) || _addServings <= 0) return;
        await State.AddEntryAsync(_date, _addItemName, _addServings);
        _addItemName = "";
        _addServings = 1;
    }

    private static string EntryMacro(FoodItem? item, string key, double servings)
    {
        var v = item?.Nutrient(key);
        return v is null ? "—" : (v.Value * servings).ToString("0.#");
    }

    // ---------- One-off entries ----------

    private void StartAddAdHoc()
    {
        _adhocItem = new FoodItem();
        _adhocEntry = null;
        _adhocServings = 1;
        _adhocSaveToMenu = false;
        _adhocError = null;
    }

    private void StartEditAdHoc(ScheduleEntry entry)
    {
        if (entry.AdHoc is null) return;
        _adhocItem = entry.AdHoc.Clone();
        _adhocEntry = entry;
        _adhocSaveToMenu = false;
        _adhocError = null;
    }

    private void CancelAdHoc()
    {
        _adhocItem = null;
        _adhocEntry = null;
        _adhocError = null;
    }

    private async Task SaveAdHocAsync()
    {
        if (_adhocItem is null) return;
        _adhocError = _adhocEntry is null
            ? await State.AddAdHocEntryAsync(_date, _adhocItem, _adhocServings, _adhocSaveToMenu)
            : await State.UpdateAdHocEntryAsync(_date, _adhocEntry, _adhocItem, _adhocSaveToMenu);
        if (_adhocError is null) CancelAdHoc();
    }

    private void StartSaveTemplate()
    {
        _savingTemplate = true;
        _newTemplateName = "";
        _templateError = null;
    }

    private void CancelSaveTemplate()
    {
        _savingTemplate = false;
        _templateError = null;
    }

    private async Task SaveTemplateAsync()
    {
        if (State.TemplateExists(_newTemplateName))
        {
            _templateError = null;
            _confirmOverwrite = true;
            return;
        }
        await DoSaveTemplateAsync(overwrite: false);
    }

    private async Task ConfirmOverwriteAsync()
    {
        _confirmOverwrite = false;
        await DoSaveTemplateAsync(overwrite: true);
    }

    private async Task DoSaveTemplateAsync(bool overwrite)
    {
        _templateError = await State.SaveTemplateAsync(_newTemplateName, State.GetDay(_date), overwrite);
        if (_templateError is null)
        {
            _templateSel = _newTemplateName.Trim();
            _savingTemplate = false;
        }
    }

    private async Task ApplyTemplateAsync()
    {
        if (_templateSel == "") return;
        _templateError = await State.ApplyTemplateAsync(_date, _templateSel);
    }

    private async Task DeleteTemplateAsync()
    {
        await State.DeleteTemplateAsync(_templateToDelete!);
        _templateToDelete = null;
        _templateSel = "";
    }

    private async Task OnServingsChanged(ScheduleEntry entry, string? raw)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
            await State.UpdateServingsAsync(_date, entry, v);
    }

    private void OpenRandomize()
    {
        var (start, end) = VisibleRange();
        _randStart = start;
        _randEnd = end;
        _randFromGoal = false;
        if (State.GetGoalStatus() is { } goal)
        {
            _targets.Calories = Math.Round(goal.SuggestedCalories / 10) * 10;
            _randFromGoal = true;
        }
        _randError = null;
        _randWarning = null;
        _randDone = false;
        _randOpen = true;
    }

    private string? TargetValue(string key) =>
        _targets.NutrientTargets.TryGetValue(key, out var v)
            ? v.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private void SetTarget(string key, string? raw)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            _targets.NutrientTargets[key] = v;
        else
            _targets.NutrientTargets.Remove(key);
    }

    private async Task RunRandomizeAsync()
    {
        _randDone = false;
        (_randError, _randWarning) = await State.RandomizeRangeAsync(_randStart, _randEnd, _targets, _randOverwrite);
        _randDone = _randError is null;
    }
}
