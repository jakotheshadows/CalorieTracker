using CalorieTracker.Models;

namespace CalorieTracker.Pages;

public partial class Foods
{
    private enum Tab { Items, Recipes }

    private Tab _tab = Tab.Items;
    private string _search = "";
    private string _categoryFilter = "";

    private FoodItem? _editing;
    private string? _originalName;
    private string? _error;

    private Recipe? _recipe;
    private string? _recipeOriginalName;
    private string? _recipeError;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    private string? _ingRecipeName;
    private string? _ingOriginalName;
    private RecipeIngredient _ing = new();
    private int? _ingIndex;
    private string? _ingError;

    protected override void OnInitialized() => State.Changed += OnChanged;
    private void OnChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => State.Changed -= OnChanged;

    private IEnumerable<FoodItem> Filtered()
    {
        var q = State.Data.Items.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_search))
            q = q.Where(i => i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                          || (i.Description?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));
        if (Enum.TryParse<FoodCategory>(_categoryFilter, out var cat))
            q = q.Where(i => i.Category == cat);
        return q;
    }

    private IEnumerable<Recipe> FilteredRecipes()
    {
        var q = State.Data.Recipes.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_search))
            q = q.Where(r => r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                          || (r.Description?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
                          || r.Ingredients.Any(i => i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)));
        return q;
    }

    private static string FormatNutrient(FoodItem item, string key)
    {
        var v = item.Nutrient(key);
        return v is null ? "—" : v.Value.ToString("0.#");
    }

    // ---------- Item editor ----------

    private void StartAdd()
    {
        _editing = new FoodItem();
        _originalName = null;
        _error = null;
    }

    private void StartEdit(FoodItem item)
    {
        _editing = item.Clone();
        _originalName = item.Name;
        _error = null;
    }

    private void CancelEdit()
    {
        _editing = null;
        _error = null;
    }

    private async Task SaveAsync()
    {
        if (_editing is null) return;
        _error = await State.UpsertItemAsync(_editing, _originalName);
        if (_error is null) CancelEdit();
    }

    private async Task DeleteAsync(string name) => await State.DeleteItemAsync(name);

    // ---------- Recipe editor (metadata only) ----------

    private void StartAddRecipe()
    {
        _recipe = new Recipe();
        _recipeOriginalName = null;
        _recipeError = null;
    }

    private void StartEditRecipe(Recipe recipe)
    {
        _recipe = recipe.Clone();
        _recipeOriginalName = recipe.Name;
        _recipeError = null;
    }

    private void CancelRecipe()
    {
        _recipe = null;
        _recipeError = null;
    }

    private async Task SaveRecipeAsync()
    {
        if (_recipe is null) return;
        var savedName = _recipe.Name?.Trim() ?? "";
        _recipeError = await State.UpsertRecipeAsync(_recipe, _recipeOriginalName);
        if (_recipeError is not null) return;

        // Keep the expander tracking a renamed recipe, and open a brand-new one.
        if (_recipeOriginalName is not null) _expanded.Remove(_recipeOriginalName);
        _expanded.Add(savedName);
        CancelRecipe();
    }

    private void ToggleExpanded(string recipeName)
    {
        if (!_expanded.Remove(recipeName)) _expanded.Add(recipeName);
    }

    private async Task DeleteRecipeAsync(string name) => await State.DeleteRecipeAsync(name);

    // ---------- Ingredient editor ----------

    private bool IsAddingIngredientTo(Recipe recipe) =>
        _ingIndex is null && string.Equals(_ingRecipeName, recipe.Name, StringComparison.OrdinalIgnoreCase);

    private bool IsEditingIngredient(Recipe recipe, int index) =>
        _ingIndex == index && string.Equals(_ingRecipeName, recipe.Name, StringComparison.OrdinalIgnoreCase);

    private void StartAddIngredient(Recipe recipe)
    {
        _ingRecipeName = recipe.Name;
        _ingOriginalName = null;
        _ing = new RecipeIngredient();
        _ingIndex = null;
        _ingError = null;
        _expanded.Add(recipe.Name);
    }

    private void StartEditIngredient(Recipe recipe, int index)
    {
        _ingRecipeName = recipe.Name;
        _ing = recipe.Ingredients[index].Clone();
        _ingOriginalName = _ing.Name;
        _ingIndex = index;
        _ingError = null;
    }

    private void CancelIngredient()
    {
        _ingRecipeName = null;
        _ingOriginalName = null;
        _ingError = null;
    }

    private async Task SaveIngredientAsync()
    {
        if (_ingRecipeName is null) return;
        _ingError = await State.SaveIngredientAsync(_ingRecipeName, _ing, _ingIndex);
        if (_ingError is null) CancelIngredient();
    }

    private async Task DeleteIngredientAsync(string recipeName, int index)
    {
        // Editing state indexes shift when an earlier row is removed.
        if (string.Equals(_ingRecipeName, recipeName, StringComparison.OrdinalIgnoreCase) && _ingIndex is { } editing)
        {
            if (editing == index) CancelIngredient();
            else if (editing > index) _ingIndex = editing - 1;
        }
        await State.RemoveIngredientAsync(recipeName, index);
    }
}
