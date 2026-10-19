using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using CalorieTracker.Usda;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// Growing the menu from USDA. The grounding rule extends here: add_menu_item takes a USDA
/// id and the SERVER fetches the nutrition, so the numbers on a new item never pass through
/// the model and can't be misremembered or invented along the way.
/// </summary>
public static partial class CalTrackTools
{
    // Generic results whose household portions search_usda looks up (search hits other than
    // FNDDS's leave them out): enough to choose amounts from, few enough to stay quick.
    private const int PortionLookups = 3;
    private const int MaxPortionsShown = 6;

    [McpServerTool(Name = "search_usda", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Search USDA FoodData Central for a food that isn't on the user's menu. Generic foods come first (USDA's " +
        "standard data, including mixed dishes such as \"Reuben sandwich\"), then branded products; pass branded: true " +
        "when the user named a brand, store or restaurant. If one result is the food itself, add it with add_menu_item " +
        "(by fdcId). If it's a dish made of several foods and no single result is that dish, build it with add_recipe from " +
        "generic ingredients. Generic results list USDA's household portions (e.g. \"1 slice (32 g)\") to choose amounts from. " +
        "If several results could fit, use the closest generic one and tell the user which. The search runs in the " +
        "CalTrack app with the user's USDA key, so CalTrack must be open.")]
    public static async Task<string> SearchUsda(
        UsdaGateway usda,
        [Description("What to look for, e.g. \"ice cream sandwich\" or a brand and product name.")] string query,
        [Description("How many results to return, 1-15. Defaults to 8.")] int maxResults = 8,
        [Description("true: only branded products (the user named a brand, store or restaurant). false: only generic foods. Omit for generic first, then branded.")] bool? branded = null)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query is required.");
        var max = Math.Clamp(maxResults, 1, 15);
        List<UsdaFood> results;
        if (branded is { } onlyBranded)
            results = (await usda.SearchAsync(query.Trim(), max, onlyBranded ? UsdaClient.BrandedTypes : UsdaClient.GenericTypes)).Take(max).ToList();
        else
        {
            // USDA ranks branded products in among (often above) the generic foods; fetch a
            // wider page and put generic first, each group in USDA's own order.
            var page = await usda.SearchAsync(query.Trim(), Math.Min(50, max * 3));
            results = page.Where(UsdaClient.IsGeneric).Concat(page.Where(f => !UsdaClient.IsGeneric(f))).Take(max).ToList();
        }

        // Household portions for the first few generic foods that came without them.
        var portions = new Dictionary<int, List<UsdaPortion>>();
        foreach (var f in results) portions.TryAdd(f.FdcId, f.Portions);
        foreach (var food in results.Where(f => UsdaClient.IsGeneric(f) && f.Portions.Count == 0).Take(PortionLookups))
        {
            try { portions[food.FdcId] = (await usda.GetAsync(food.FdcId)).Food.Portions; }
            catch (McpException) { /* listed without portions; add_menu_item/add_recipe report them */ }
        }

        return Json(new
        {
            count = results.Count,
            results = results.Select(f =>
            {
                var per = f.NutrientsFor(f.DefaultAmount);
                var shown = UsdaClient.IsGeneric(f) ? portions.GetValueOrDefault(f.FdcId) : null;
                return new
                {
                    fdcId = f.FdcId,
                    name = f.Description,
                    brand = f.Brand,
                    dataType = f.DataType,
                    serving = f.DefaultServingDisplay,
                    calories = f.CaloriesFor(f.DefaultAmount),
                    proteinG = per.TryGetValue("protein", out var p) ? p : (double?)null,
                    carbsG = per.TryGetValue("carbs", out var c) ? c : (double?)null,
                    fatG = per.TryGetValue("fat", out var fat) ? fat : (double?)null,
                    portions = shown is { Count: > 0 } ? shown.Take(MaxPortionsShown).Select(PortionText).ToList() : null,
                };
            }),
            note = "Nutrition is per the listed serving: the package label for branded foods, 100 g for generic ones. " +
                   "Use a listed portion with add_menu_item (portion) or add_recipe (portion and count, e.g. \"1 slice\" × 2).",
        });
    }

    [McpServerTool(Name = "add_menu_item", Destructive = false, OpenWorld = true)]
    [Description(
        "Add one food to the user's CalTrack menu so it can be logged with log_food (for a dish made of several " +
        "foods, use add_recipe). Preferred: pass usdaFdcId from search_usda — the server fetches the nutrition from " +
        "USDA itself, using the package label serving for branded foods or USDA's household portion (e.g. \"1 sandwich\") " +
        "for generic ones. For a branded product, put the brand in the name (e.g. \"Walgreens Reuben\"). Without " +
        "usdaFdcId, calories are required and must come from the user (e.g. a label), never estimated. Fails if the " +
        "name is already on the menu. The item can be logged with log_food immediately.")]
    public static async Task<string> AddMenuItem(
        DataFolderStore store,
        UsdaGateway usda,
        [Description("USDA FoodData Central id from search_usda. Nutrition then comes from USDA; leave the manual calorie/macro fields empty.")] int? usdaFdcId = null,
        [Description("Menu name. Defaults to the USDA description; pass the everyday name the user would say, e.g. \"Ice cream sandwich\".")] string? name = null,
        [Description("USDA only: one of the food's household portions to use as a serving, e.g. \"1 sandwich\". Defaults to the label serving or USDA's first portion.")] string? portion = null,
        [Description("USDA only: one serving as an amount, e.g. 59. Overrides portion.")] double? servingAmount = null,
        [Description("Unit for servingAmount: g, kg, oz, lb, ml, l or fl oz. Defaults to the food's base unit (g, or ml for liquids).")] string? servingUnit = null,
        [Description("Food, Beverage or Snack. Defaults to Food (Beverage for liquids).")] string? category = null,
        [Description("Manual only: calories per serving, as given by the user.")] double? calories = null,
        [Description("Manual only: protein per serving, grams.")] double? proteinG = null,
        [Description("Manual only: carbohydrates per serving, grams.")] double? carbsG = null,
        [Description("Manual only: fat per serving, grams.")] double? fatG = null,
        [Description("Manual only: what one serving is, e.g. \"1 bar (40 g)\".")] string? servingSize = null)
    {
        // Before spending a USDA request: can the user's app even apply this op?
        var snap = store.Read();
        if (!snap.MainExists || !Inbox.AppUnderstands(snap.View, InboxOp.AddItem))
            throw new McpException(
                "The user's CalTrack app is a version that can't add menu items over MCP yet, so nothing was added. " +
                "Ask them to open CalTrack and update it (the \"Update now\" banner, or Settings → Check for updates) — " +
                "it takes effect within seconds. Meanwhile, log_adhoc can log the food as a one-off with numbers the user confirms.");
        if (name is not null && snap.View.ResolveItem(name.Trim()) is { } clash)
            throw new McpException($"\"{clash.Name}\" is already on the menu — log it with log_food.");

        var manual = calories is not null || proteinG is not null || carbsG is not null || fatG is not null || servingSize is not null;
        FoodItem item;
        object source;
        List<string>? otherPortions = null;
        string? warning = null;
        var brandedProduct = false;

        if (usdaFdcId is { } fdcId)
        {
            if (manual)
                throw new McpException("Pass either usdaFdcId or manual nutrition (calories, macros, servingSize), not both — USDA numbers aren't overridden.");
            var (food, warn) = await usda.GetAsync(fdcId);
            warning = warn;
            if (food.CaloriesPer100 is null)
                throw new McpException($"USDA's record #{fdcId} has no calorie data, so it wasn't added. Pick another result.");

            var (amountInBase, servingText, chosen) = PickServing(food, portion, servingAmount, servingUnit);
            brandedProduct = !UsdaClient.IsGeneric(food);
            if (brandedProduct && food.LabelServingAmount is null && servingAmount is null && chosen is null)
                warning = string.Join(" ", new[] { warning, $"USDA lists no package serving for this product, so one serving is 100 {food.BaseUnit}. " +
                    "If the user ate a whole package, log the matching number of servings (e.g. 2.5 for 250 g) and say so." }
                    .Where(w => w is not null));
            var itemName = string.IsNullOrWhiteSpace(name) ? food.Description : name.Trim();
            if (snap.View.ResolveItem(itemName) is { } clash2)
                throw new McpException($"\"{clash2.Name}\" is already on the menu — log it with log_food.");

            item = food.ToMenuItem(itemName, amountInBase, servingText);
            item.Category = ParseCategory(category, food.BaseUnit);
            // Provenance, rendered as a link by the app's markdown descriptions.
            item.Description = $"Nutrition from [USDA FoodData Central #{food.FdcId}](https://fdc.nal.usda.gov/food-details/{food.FdcId}/nutrients)" +
                               $" ({food.DataType}{(food.Brand is null ? "" : ", " + food.Brand)}).";
            source = new { usdaFdcId = food.FdcId, usdaName = food.Description, brand = food.Brand, dataType = food.DataType };
            otherPortions = food.Portions.Where(p => p != chosen)
                .Select(p => $"{p.Description} ({p.Grams.ToString("0.#", CultureInfo.InvariantCulture)} g)").ToList();
        }
        else
        {
            if (portion is not null || servingAmount is not null || servingUnit is not null)
                throw new McpException("portion, servingAmount and servingUnit apply to USDA foods — pass usdaFdcId, or describe the serving with servingSize.");
            var itemName = name?.Trim() ?? "";
            if (itemName.Length == 0) throw new McpException("name is required when adding without usdaFdcId.");
            if (calories is not { } kcal)
                throw new McpException("calories is required when adding without usdaFdcId — use the number the user gave (e.g. from the label), or look the food up with search_usda.");
            if (!(kcal >= 0) || kcal > MaxCaloriesPerServing)
                throw new McpException($"calories must be between 0 and {MaxCaloriesPerServing} per serving (got {kcal}).");
            foreach (var (label, v) in new[] { ("proteinG", proteinG), ("carbsG", carbsG), ("fatG", fatG) })
                if (v is { } g && (!(g >= 0) || g > 1000)) throw new McpException($"{label} must be between 0 and 1000 grams (got {g}).");

            item = new FoodItem
            {
                Name = itemName,
                Calories = kcal,
                ServingSize = string.IsNullOrWhiteSpace(servingSize) ? null : servingSize.Trim(),
                Category = ParseCategory(category, "g"),
            };
            if (proteinG is { } pg) item.Nutrients["protein"] = pg;
            if (carbsG is { } cg) item.Nutrients["carbs"] = cg;
            if (fatG is { } fg) item.Nutrients["fat"] = fg;
            source = "user-provided";
        }

        var op = new InboxOp
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Kind = InboxOp.AddItem,
            Item = item,
            Source = "mcp",
        };
        var (after, status) = WriteVerified(store, op);
        if (after.View.ResolveItem(item.Name) is null)
            throw new McpException("The item was written but isn't visible when read back, so it may not have been added. Check the CalTrack app before retrying.");

        return Json(new
        {
            added = new
            {
                name = item.Name,
                serving = item.ServingSize,
                calories = item.Calories,
                proteinG = item.Nutrient("protein"),
                carbsG = item.Nutrient("carbs"),
                fatG = item.Nutrient("fat"),
                category = item.Category.ToString(),
                source,
            },
            otherPortions = otherPortions is { Count: > 0 } ? otherPortions : null,
            status,
            note = $"Queued for the CalTrack app, which adds it to the menu within seconds when open. It can be logged right away: log_food with itemName \"{item.Name}\"." +
                   (brandedProduct ? " These are a branded product's numbers: tell the user which product they're for." : ""),
            warning,
        });
    }

    /// <summary>
    /// One serving for a USDA food: an explicit amount, else a named USDA portion, else the
    /// label serving (branded), else USDA's first household portion, else 100 g/ml.
    /// </summary>
    private static (double AmountInBase, string ServingText, UsdaPortion? Chosen) PickServing(
        UsdaFood food, string? portion, double? servingAmount, string? servingUnit)
    {
        if (servingAmount is { } amount)
        {
            var unit = string.IsNullOrWhiteSpace(servingUnit) ? food.BaseUnit : servingUnit.Trim().ToLowerInvariant();
            if (!Units.Mass.Contains(unit) && !Units.Volume.Contains(unit))
                throw new McpException($"servingUnit must be one of {string.Join(", ", Units.Mass.Concat(Units.Volume))} (got \"{servingUnit}\").");
            if (Units.BaseUnitFor(unit) != food.BaseUnit)
                throw new McpException($"This food is measured in {food.BaseUnit}; use one of {string.Join(", ", Units.ForBase(food.BaseUnit))}.");
            if (Units.ToBase(amount, unit) is not { } inBase || inBase > 5000)
                throw new McpException($"servingAmount must be greater than 0 and a realistic single serving (got {amount} {unit}).");
            return (inBase, food.ServingText(amount, unit, inBase), null);
        }
        if (!string.IsNullOrWhiteSpace(portion))
        {
            // search_usda shows portions as "1 sandwich (112 g)": accept that form as well as the bare name.
            var want = portion.Trim();
            var bare = ShownGrams.Replace(want, "");
            var match = food.Portions.FirstOrDefault(p => p.Description.Equals(want, StringComparison.OrdinalIgnoreCase))
                        ?? food.Portions.FirstOrDefault(p => p.Description.Equals(bare, StringComparison.OrdinalIgnoreCase))
                        ?? food.Portions.FirstOrDefault(p => bare.Length > 0 && p.Description.Contains(bare, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new McpException(food.Portions.Count == 0
                    ? $"USDA lists no household portions for this food; use servingAmount + servingUnit instead."
                    : $"No USDA portion matches \"{want}\". Available: {string.Join(", ", food.Portions.Select(p => $"\"{p.Description}\""))}.");
            return (match.Grams, PortionText(match), match);
        }
        if (food.LabelServingAmount is not null)
            return (food.DefaultAmount, food.DefaultServingDisplay, null);
        if (food.Portions.Count > 0)
            return (food.Portions[0].Grams, PortionText(food.Portions[0]), food.Portions[0]);
        return (100, $"100 {food.BaseUnit}", null);
    }

    private static string PortionText(UsdaPortion p) =>
        $"{p.Description} ({p.Grams.ToString("0.#", CultureInfo.InvariantCulture)} g)";

    // The " (112 g)" PortionText adds, at the end of a portion name.
    private static readonly Regex ShownGrams = new(@"\s*\(\s*\d+(?:\.\d+)?\s*g\s*\)\s*$", RegexOptions.IgnoreCase);

    private static FoodCategory ParseCategory(string? category, string baseUnit)
    {
        if (string.IsNullOrWhiteSpace(category)) return baseUnit == "ml" ? FoodCategory.Beverage : FoodCategory.Food;
        if (Enum.TryParse<FoodCategory>(category.Trim(), ignoreCase: true, out var c)) return c;
        throw new McpException($"category must be Food, Beverage or Snack (got \"{category}\").");
    }
}
