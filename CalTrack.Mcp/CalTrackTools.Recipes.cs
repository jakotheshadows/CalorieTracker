using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using CalorieTracker.Usda;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>One ingredient of <see cref="CalTrackTools.AddRecipe"/>: a USDA food, or one with numbers the user gave.</summary>
public sealed class RecipeIngredientInput
{
    [JsonPropertyName("usdaFdcId")]
    [Description("USDA FoodData Central id from search_usda. Its nutrition then comes from USDA; leave calories and macros empty.")]
    public int? UsdaFdcId { get; init; }

    [JsonPropertyName("name")]
    [Description("Ingredient name. Defaults to the USDA description; required for a manual ingredient.")]
    public string? Name { get; init; }

    [JsonPropertyName("portion")]
    [Description("USDA only: one of the food's household portions, e.g. \"1 slice\". Defaults to the label serving or USDA's first portion.")]
    public string? Portion { get; init; }

    [JsonPropertyName("amount")]
    [Description("How much goes in, e.g. 56. USDA: overrides portion. Manual: required — the amount the calories below are for.")]
    public double? Amount { get; init; }

    [JsonPropertyName("unit")]
    [Description("Unit for amount: g, kg, oz, lb, ml, l or fl oz. Defaults to g (USDA liquids: ml).")]
    public string? Unit { get; init; }

    [JsonPropertyName("calories")]
    [Description("Manual only: calories in that amount, as given by the user.")]
    public double? Calories { get; init; }

    [JsonPropertyName("proteinG")]
    [Description("Manual only: protein in that amount, grams.")]
    public double? ProteinG { get; init; }

    [JsonPropertyName("carbsG")]
    [Description("Manual only: carbohydrates in that amount, grams.")]
    public double? CarbsG { get; init; }

    [JsonPropertyName("fatG")]
    [Description("Manual only: fat in that amount, grams.")]
    public double? FatG { get; init; }
}

/// <summary>A new amount for one of a recipe's ingredients (<see cref="CalTrackTools.UpdateRecipe"/>).</summary>
public sealed class IngredientAmountInput
{
    [JsonPropertyName("name")]
    [Description("The ingredient's name, as get_menu_item shows it.")]
    public string? Name { get; init; }

    [JsonPropertyName("amount")]
    [Description("The new amount, e.g. 150.")]
    public double? Amount { get; init; }

    [JsonPropertyName("unit")]
    [Description("Unit for amount, of the same kind as now (weight: g, kg, oz, lb; volume: ml, l, fl oz). Defaults to its current unit.")]
    public string? Unit { get; init; }
}

/// <summary>
/// Recipes: a food made of several ingredients, for dishes no single USDA entry covers. The
/// grounding rule carries over from add_menu_item: USDA ingredients are fetched by id, so their
/// numbers never pass through the model; manual ones must use numbers the user gave.
/// </summary>
public static partial class CalTrackTools
{
    private const int MaxIngredients = 30;

    [McpServerTool(Name = "add_recipe", Destructive = false, OpenWorld = true)]
    [Description(
        "Add a recipe — a food made of several ingredients, e.g. a homemade chili or a sandwich the user builds " +
        "themselves — to the user's menu, so it can be logged with log_food like any menu item. Use it when no single " +
        "USDA entry matches what the user ate. Each ingredient is either a USDA food (usdaFdcId from search_usda; the " +
        "server fetches its nutrition) with an optional portion or amount, or — only with numbers the user gave — a " +
        "manual ingredient (name, amount, unit, calories). Never invent amounts or numbers: if the user didn't say how " +
        "much, use USDA's household portions and tell them, or ask. Fails if the name is already on the menu.")]
    public static async Task<string> AddRecipe(
        DataFolderStore store,
        UsdaGateway usda,
        [Description("Recipe name as the user would say it, e.g. \"Turkey chili\".")] string name,
        [Description("The ingredients, 1 to 30.")] List<RecipeIngredientInput> ingredients,
        [Description("How many servings the whole recipe makes. Defaults to 1 (the whole thing is one serving).")] double servings = 1,
        [Description("Optional note shown with the recipe.")] string? description = null)
    {
        // Before spending any USDA requests: can the user's app apply this op, and is the name free?
        var snap = store.Read();
        RequireAppSupports(snap, InboxOp.AddRecipe, "add recipes");
        var recipeName = name?.Trim() ?? "";
        if (recipeName.Length == 0) throw new McpException("name is required.");
        if (snap.View.ResolveItem(recipeName) is { } clash)
            throw new McpException($"\"{clash.Name}\" is already on the menu — log it with log_food.");
        if (!(servings > 0) || servings > MaxServings || double.IsInfinity(servings))
            throw new McpException($"servings must be greater than 0 and at most {MaxServings} (got {servings}).");
        if (ingredients is not { Count: > 0 })
            throw new McpException("ingredients is required: at least one.");
        if (ingredients.Count > MaxIngredients)
            throw new McpException($"A recipe can have at most {MaxIngredients} ingredients (got {ingredients.Count}).");

        var (built, report, sources, warnings) = await BuildIngredientsAsync(usda, ingredients);

        // The USDA lookups took a while: make sure nobody took the name meanwhile.
        if (store.Read().View.ResolveItem(recipeName) is { } clash2)
            throw new McpException($"\"{clash2.Name}\" is already on the menu — log it with log_food.");

        var recipe = new Recipe
        {
            Name = recipeName,
            Servings = servings,
            Ingredients = built,
            Description = RecipeDescription(description, sources),
        };
        var op = new InboxOp
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Kind = InboxOp.AddRecipe,
            Recipe = recipe,
            Source = "mcp",
        };
        var (after, status) = WriteVerified(store, op);
        if (after.View.ResolveItem(recipeName) is null)
            throw new McpException("The recipe was written but isn't visible when read back, so it may not have been added. Check the CalTrack app before retrying.");

        var perServing = recipe.ToMenuItem();
        return Json(new
        {
            added = new
            {
                name = recipeName,
                servings,
                perServing = new
                {
                    calories = perServing.Calories,
                    proteinG = perServing.Nutrient("protein"),
                    carbsG = perServing.Nutrient("carbs"),
                    fatG = perServing.Nutrient("fat"),
                },
                wholeRecipeCalories = recipe.TotalCalories() is { } total ? Round(total) : (double?)null,
            },
            ingredients = report,
            status,
            note = $"Queued for the CalTrack app, which adds it to the menu within seconds when open. Log it with log_food, itemName \"{recipeName}\"" +
                   (servings > 1 ? $" — one serving is 1/{servings.ToString("0.##", CultureInfo.InvariantCulture)} of the recipe." : "."),
            warnings = warnings.Count > 0 ? warnings : null,
        });
    }

    [McpServerTool(Name = "update_recipe", Destructive = true, OpenWorld = true)]
    [Description(
        "Edit a recipe (not a plain menu item — use update_menu_item): rename it, change how many servings it makes, " +
        "its description or whether Randomize may pick it, remove ingredients, change ingredient amounts, or add " +
        "ingredients (same form as add_recipe: a USDA id, or a manual one with numbers the user gave). Only what you " +
        "pass changes. Use get_menu_item first to see its ingredients. The result shows the recipe before and after.")]
    public static async Task<string> UpdateRecipe(
        DataFolderStore store,
        UsdaGateway usda,
        TimeProvider clock,
        [Description("The recipe's current exact name, as returned by list_menu_items.")] string name,
        [Description("A new name. Must not clash with another item or recipe.")] string? newName = null,
        [Description("How many servings the whole recipe makes.")] double? servings = null,
        [Description("A note shown with the recipe. Empty string clears it.")] string? description = null,
        [Description("true to keep Randomize from picking this recipe; false to allow it.")] bool? excludeFromRandom = null,
        [Description("Names of ingredients to remove, as get_menu_item shows them.")] List<string>? removeIngredients = null,
        [Description("New amounts for existing ingredients; the nutrition scales with the amount.")] List<IngredientAmountInput>? ingredientAmounts = null,
        [Description("Ingredients to add, like add_recipe's.")] List<RecipeIngredientInput>? addIngredients = null)
    {
        var snap = store.Read();
        var wanted = name?.Trim() ?? "";
        if (snap.View.FindRecipe(wanted) is not { } recipe)
            throw snap.View.FindItem(wanted) is { } i
                ? new McpException($"\"{i.Name}\" is a menu item, not a recipe — use update_menu_item.")
                : NotOnMenu(wanted, snap.View);
        // Before spending any USDA requests.
        RequireAppSupports(snap, InboxOp.UpdateRecipe, "edit recipes");

        var renamed = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        if (renamed is null && servings is null && description is null && excludeFromRandom is null
            && removeIngredients is not { Count: > 0 } && ingredientAmounts is not { Count: > 0 } && addIngredients is not { Count: > 0 })
            throw new McpException("Nothing to change: pass at least one field to update.");
        if (servings is { } s && (!(s > 0) || s > MaxServings || double.IsInfinity(s)))
            throw new McpException($"servings must be greater than 0 and at most {MaxServings} (got {s}).");
        if (renamed is not null && snap.View.ResolveItem(renamed) is { } clash && !string.Equals(clash.Name, recipe.Name, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"\"{clash.Name}\" is already on the menu; pick another name.");
        string Ingredients() => string.Join(", ", recipe.Ingredients.Select(x => $"\"{x.Name}\" ({x.AmountDisplay})"));
        foreach (var remove in removeIngredients ?? new())
            if (!recipe.Ingredients.Any(x => string.Equals(x.Name, remove?.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new McpException($"\"{recipe.Name}\" has no ingredient \"{remove}\". Its ingredients: {Ingredients()}.");
        foreach (var change in ingredientAmounts ?? new())
            if (!recipe.Ingredients.Any(x => string.Equals(x.Name, change.Name?.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new McpException($"\"{recipe.Name}\" has no ingredient \"{change.Name}\". Its ingredients: {Ingredients()}.");
        if (recipe.Ingredients.Count - (removeIngredients?.Count ?? 0) + (addIngredients?.Count ?? 0) > MaxIngredients)
            throw new McpException($"A recipe can have at most {MaxIngredients} ingredients.");

        var (built, report, _, warnings) = addIngredients is { Count: > 0 }
            ? await BuildIngredientsAsync(usda, addIngredients)
            : (new List<RecipeIngredient>(), new List<object>(), new List<UsdaFood>(), new List<string>());

        var op = NewOp(InboxOp.UpdateRecipe, clock);
        op.ItemName = recipe.Name;
        op.RecipePatch = new RecipePatch
        {
            NewName = renamed,
            Servings = servings,
            Description = description,
            ExcludeFromRandom = excludeFromRandom,
            RemoveIngredients = removeIngredients is { Count: > 0 } ? removeIngredients.Select(r => r.Trim()).ToList() : null,
            IngredientAmounts = ingredientAmounts is { Count: > 0 }
                ? ingredientAmounts.Select(a => new IngredientAmount
                {
                    Name = a.Name?.Trim() ?? "",
                    Amount = a.Amount ?? 0,
                    Unit = string.IsNullOrWhiteSpace(a.Unit)
                        ? recipe.Ingredients.First(x => string.Equals(x.Name, a.Name?.Trim(), StringComparison.OrdinalIgnoreCase)).Unit
                        : a.Unit.Trim(),
                }).ToList()
                : null,
            AddIngredients = built.Count > 0 ? built : null,
        };
        // The USDA lookups took a while: submit against what's there now.
        var (after, status, _) = Submit(store, store.Read(), op, "edit recipes");
        var updated = after.View.FindRecipe(renamed ?? recipe.Name);
        return Json(new
        {
            before = RecipeInfo(recipe),
            after = updated is null ? null : RecipeInfo(updated),
            addedIngredients = report.Count > 0 ? report : null,
            status,
            note = QueuedNote + (renamed is not null ? " Log entries for it follow the new name." : ""),
            warnings = warnings.Count > 0 ? warnings : null,
        });
    }

    [McpServerTool(Name = "delete_recipe", Destructive = true)]
    [Description(
        "Delete a recipe (not a plain menu item — use delete_menu_item). The user's history is kept: log and template " +
        "entries that used it become one-offs with the same numbers, so past totals don't change. Only when the user " +
        "asks to delete it. The result has its ingredients in add_recipe's form, so it can be added back.")]
    public static string DeleteRecipe(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Exact recipe name, as returned by list_menu_items.")] string name)
    {
        var snap = store.Read();
        var wanted = name?.Trim() ?? "";
        if (snap.View.FindRecipe(wanted) is not { } recipe)
            throw snap.View.FindItem(wanted) is { } i
                ? new McpException($"\"{i.Name}\" is a menu item, not a recipe — use delete_menu_item.")
                : NotOnMenu(wanted, snap.View);

        var (days, templates) = snap.View.UsesOf(recipe.Name);
        var op = NewOp(InboxOp.DeleteRecipe, clock);
        op.ItemName = recipe.Name;
        var (_, status, _) = Submit(store, snap, op, "delete recipes");
        return Json(new
        {
            deleted = RecipeInfo(recipe),
            historyKept = HistoryKept(days, templates),
            status,
            note = QueuedNote + " To add it back: add_recipe with these ingredients (as manual ingredients; past entries stay one-offs).",
        });
    }

    /// <summary>A recipe as reported back; its ingredients are in add_recipe's manual form, so it can be recreated.</summary>
    private static object RecipeInfo(Recipe r)
    {
        var perServing = r.ToMenuItem();
        return new
        {
            name = r.Name,
            servings = r.Servings,
            description = r.Description,
            excludeFromRandom = r.ExcludeFromRandom ? true : (bool?)null,
            perServing = new { calories = perServing.Calories, nutrients = NutrientMap(perServing.Nutrients) },
            wholeRecipeCalories = r.TotalCalories() is { } total ? Round(total) : (double?)null,
            ingredients = r.Ingredients.Select(i => new
            {
                name = i.Name,
                amount = i.Amount,
                unit = i.Unit,
                calories = i.Calories is { } c ? Round(c) : (double?)null,
                proteinG = i.Nutrient("protein") is { } p ? Round(p) : (double?)null,
                carbsG = i.Nutrient("carbs") is { } cb ? Round(cb) : (double?)null,
                fatG = i.Nutrient("fat") is { } f ? Round(f) : (double?)null,
            }).ToList(),
        };
    }

    /// <summary>
    /// Turn ingredient inputs into recipe ingredients: a USDA one is fetched through the app
    /// (its numbers never pass through the model); a manual one must carry the user's numbers.
    /// </summary>
    private static async Task<(List<RecipeIngredient> Built, List<object> Report, List<UsdaFood> Sources, List<string> Warnings)> BuildIngredientsAsync(
        UsdaGateway usda, List<RecipeIngredientInput> ingredients)
    {
        var built = new List<RecipeIngredient>();
        var report = new List<object>();
        var sources = new List<UsdaFood>();
        var warnings = new List<string>();
        for (var n = 0; n < ingredients.Count; n++)
        {
            var input = ingredients[n];
            var label = $"Ingredient {n + 1}";
            var manual = input.Calories is not null || input.ProteinG is not null || input.CarbsG is not null || input.FatG is not null;

            if (input.UsdaFdcId is { } fdcId)
            {
                if (manual)
                    throw new McpException($"{label}: pass either usdaFdcId or manual numbers (calories, macros), not both — USDA numbers aren't overridden.");
                var (food, warn) = await usda.GetAsync(fdcId);
                if (warn is not null) warnings.Add($"{label} (#{fdcId}): {warn}");
                if (food.CaloriesPer100 is null)
                    throw new McpException($"{label}: USDA's record #{fdcId} has no calorie data. Pick another result.");
                (double AmountInBase, string ServingText, UsdaPortion? Chosen) serving;
                try { serving = PickServing(food, input.Portion, input.Amount, input.Unit); }
                catch (McpException ex) { throw new McpException($"{label} (#{fdcId}): {ex.Message}"); }

                var ingredient = new RecipeIngredient
                {
                    Name = string.IsNullOrWhiteSpace(input.Name) ? food.Description : input.Name.Trim(),
                    Amount = input.Amount ?? Math.Round(serving.AmountInBase, 1),
                    Unit = input.Amount is null || string.IsNullOrWhiteSpace(input.Unit) ? food.BaseUnit : input.Unit.Trim().ToLowerInvariant(),
                    CaloriesPer100 = food.CaloriesPer100,
                    NutrientsPer100 = new Dictionary<string, double>(food.NutrientsPer100),
                };
                built.Add(ingredient);
                sources.Add(food);
                report.Add(new
                {
                    name = ingredient.Name,
                    amount = serving.ServingText,
                    calories = Round(ingredient.Calories ?? 0),
                    source = $"USDA #{food.FdcId} {food.Description}",
                    otherPortions = food.Portions.Where(p => p != serving.Chosen).Select(PortionText).ToList() is { Count: > 0 } other ? other : null,
                });
            }
            else
            {
                if (input.Portion is not null)
                    throw new McpException($"{label}: portion applies to USDA ingredients — pass usdaFdcId, or give amount and unit.");
                var ingName = input.Name?.Trim() ?? "";
                if (ingName.Length == 0)
                    throw new McpException($"{label}: name is required for an ingredient without usdaFdcId.");
                if (input.Calories is not { } kcal)
                    throw new McpException($"{label} ({ingName}): calories is required without usdaFdcId — use the number the user gave, or look it up with search_usda.");
                if (input.Amount is not { } amount)
                    throw new McpException($"{label} ({ingName}): amount is required — the amount those calories are for.");
                var unit = string.IsNullOrWhiteSpace(input.Unit) ? "g" : input.Unit.Trim().ToLowerInvariant();
                if (!Units.Mass.Contains(unit) && !Units.Volume.Contains(unit))
                    throw new McpException($"{label} ({ingName}): unit must be one of {string.Join(", ", Units.Mass.Concat(Units.Volume))} (got \"{input.Unit}\").");
                if (Units.ToBase(amount, unit) is not { } inBase || inBase > 5000)
                    throw new McpException($"{label} ({ingName}): amount must be greater than 0 and realistic (got {amount} {unit}).");
                if (!(kcal >= 0) || kcal > MaxCaloriesPerServing)
                    throw new McpException($"{label} ({ingName}): calories must be between 0 and {MaxCaloriesPerServing} (got {kcal}).");

                var ingredient = new RecipeIngredient
                {
                    Name = ingName,
                    Amount = amount,
                    Unit = unit,
                    CaloriesPer100 = kcal * 100.0 / inBase,
                };
                foreach (var (key, grams) in new[] { ("protein", input.ProteinG), ("carbs", input.CarbsG), ("fat", input.FatG) })
                {
                    if (grams is not { } g) continue;
                    if (!(g >= 0) || g > 1000) throw new McpException($"{label} ({ingName}): {key} must be between 0 and 1000 grams (got {g}).");
                    ingredient.NutrientsPer100[key] = g * 100.0 / inBase;
                }
                built.Add(ingredient);
                report.Add(new { name = ingName, amount = ingredient.AmountDisplay, calories = Round(kcal), source = "user-provided" });
            }
        }
        return (built, report, sources, warnings);
    }

    /// <summary>The user's note plus links to the USDA records the numbers came from.</summary>
    private static string? RecipeDescription(string? note, List<UsdaFood> sources)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(note)) parts.Add(note.Trim());
        if (sources.Count > 0)
            parts.Add("Nutrition from USDA FoodData Central: " + string.Join(", ", sources.DistinctBy(s => s.FdcId).Select(s =>
                $"[#{s.FdcId} {s.Description}](https://fdc.nal.usda.gov/food-details/{s.FdcId}/nutrients)")) + ".");
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
