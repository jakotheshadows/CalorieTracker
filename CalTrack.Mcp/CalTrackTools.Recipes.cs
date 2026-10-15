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
    [Description("USDA only: one of the food's household portions, e.g. \"1 sundae\". Defaults to the label serving or USDA's first portion.")]
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
        "Add a recipe — a food made of several ingredients, e.g. a brownie sundae (a brownie plus a hot fudge " +
        "sundae) — to the user's menu, so it can be logged with log_food like any menu item. Use it when no single " +
        "USDA entry matches what the user ate. Each ingredient is either a USDA food (usdaFdcId from search_usda; the " +
        "server fetches its nutrition) with an optional portion or amount, or — only with numbers the user gave — a " +
        "manual ingredient (name, amount, unit, calories). Never invent amounts or numbers: if the user didn't say how " +
        "much, use USDA's household portions and tell them, or ask. Fails if the name is already on the menu.")]
    public static async Task<string> AddRecipe(
        DataFolderStore store,
        UsdaGateway usda,
        [Description("Recipe name as the user would say it, e.g. \"Brownie sundae\".")] string name,
        [Description("The ingredients, 1 to 30.")] List<RecipeIngredientInput> ingredients,
        [Description("How many servings the whole recipe makes. Defaults to 1 (the whole thing is one serving).")] double servings = 1,
        [Description("Optional note shown with the recipe.")] string? description = null)
    {
        // Before spending any USDA requests: can the user's app apply this op, and is the name free?
        var snap = store.Read();
        if (!snap.MainExists || !Inbox.AppUnderstands(snap.View, InboxOp.AddRecipe))
            throw new McpException(
                "The user's CalTrack app is a version that can't add recipes over MCP yet, so nothing was added. Ask them " +
                "to open CalTrack and update it (the \"Update now\" banner, or Settings → Check for updates).");
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
