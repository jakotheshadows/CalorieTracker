using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CalorieTracker.Usda;

/// <summary>
/// USDA FoodData Central API (https://fdc.nal.usda.gov). Stateless: callers supply the
/// HttpClient and the api.data.gov key, which only the app holds (browser storage). The MCP
/// server has no key: it asks the app to fetch a <see cref="SearchPath"/> / <see cref="FoodPath"/>
/// and reads the raw reply with <see cref="ReadSearch"/> / <see cref="ReadFood"/> — the same
/// code the app's own lookups go through. Errors come back as user-facing text, never exceptions.
/// </summary>
public static class UsdaClient
{
    private const string Api = "https://api.nal.usda.gov/fdc/v1/";

    // FDC nutrient numbers → CalTrack nutrient keys (units already match the catalog).
    private static readonly Dictionary<string, string> NutrientMap = new()
    {
        ["203"] = "protein",
        ["204"] = "fat",
        ["205"] = "carbs",
        ["291"] = "fiber",
        ["269"] = "sugar",
        ["307"] = "sodium",
        ["306"] = "potassium",
        ["301"] = "calcium",
        ["303"] = "iron",
        ["601"] = "cholesterol",
        ["320"] = "vitaminA",
        ["401"] = "vitaminC",
        ["328"] = "vitaminD",
    };

    // ---------- Requests: a path under the API root, without the key ----------
    // The app fetches these for the MCP server and refuses any other shape, so keep them in
    // step with ALLOWED in wwwroot/js/request-worker.js.

    /// <summary>
    /// USDA's generic data types. Survey (FNDDS) is the "as eaten" database: mixed dishes like
    /// a Reuben sandwich or a banana split, with household portions even in search results.
    /// </summary>
    public const string GenericTypes = "Foundation,SR Legacy,Survey (FNDDS)";

    /// <summary>Packaged and restaurant products, with their label serving.</summary>
    public const string BrandedTypes = "Branded";

    public const string AllTypes = BrandedTypes + "," + GenericTypes;

    /// <summary>A text search over <paramref name="dataTypes"/> (default: generic and branded).</summary>
    public static string SearchPath(string query, int pageSize, string dataTypes = AllTypes) =>
        "foods/search" +
        $"?query={Uri.EscapeDataString(query.Trim())}" +
        $"&pageSize={pageSize}" +
        "&dataType=" + Uri.EscapeDataString(dataTypes);

    /// <summary>True for USDA's generic data types (everything but Branded).</summary>
    public static bool IsGeneric(UsdaFood food) => food.DataType != BrandedTypes;

    /// <summary>
    /// One food by id, from the per-food endpoint — the only one that carries household
    /// portions ("1 sandwich" = 59 g), which search results leave out.
    /// </summary>
    public static string FoodPath(int fdcId) => $"food/{fdcId}";

    /// <summary>The full URL for a path, with the key.</summary>
    public static string Url(string path, string key) =>
        Api + path + (path.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key);

    // ---------- Replies: HTTP status + body → foods, or a user-facing error ----------

    /// <param name="status">HTTP status; 0 when USDA couldn't be reached at all.</param>
    /// <param name="body">The reply's text.</param>
    /// <param name="keyHint">Appended to a rejected-key error: where the user fixes the key.</param>
    public static (List<UsdaFood>? Results, string? Error) ReadSearch(int status, string? body, string keyHint = "")
    {
        var (doc, error) = Interpret(status, body, keyHint);
        if (doc is null) return (null, error);
        using (doc)
        {
            var results = new List<UsdaFood>();
            if (doc.RootElement.TryGetProperty("foods", out var foods))
                foreach (var food in foods.EnumerateArray())
                    results.Add(ParseSearchFood(food));
            return (results, null);
        }
    }

    /// <inheritdoc cref="ReadSearch"/>
    public static (UsdaFood? Food, string? Error) ReadFood(int status, string? body, int fdcId, string keyHint = "")
    {
        var (doc, error) = Interpret(status, body, keyHint, notFound: $"USDA has no food with id {fdcId}.");
        if (doc is null) return (null, error);
        using (doc) return (ParseDetailFood(doc.RootElement), null);
    }

    // ---------- Failures worth asking again ----------

    /// <summary>
    /// Whether asking again could get a different answer. Now and then USDA's web server answers
    /// a valid request with a bare nginx "400 Bad Request" page, and the same request works
    /// seconds later: on 2026-10-17 over half of one run's searches got it. Gateway errors
    /// (502-504) pass too. A 400 in JSON is the API objecting to the request itself, and 429 is
    /// the rate limit, so asking again can't help those.
    /// </summary>
    public static bool IsTransient(int status, string? body) =>
        status is 502 or 503 or 504 || (status == 400 && IsHtml(body));

    /// <summary>
    /// The waits before each retry of a <see cref="IsTransient"/> failure: three retries, soon
    /// after one another. In a run of 30 agents a retry failed as often as a first try (about
    /// 42%) however long it waited, so more tries help and longer waits don't.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) };

    private static bool IsHtml(string? body) => body?.TrimStart().StartsWith('<') == true;

    // ---------- The app's own lookups (it holds the key) ----------

    /// <param name="retryDelays">Waits before retrying a transient failure; <see cref="RetryDelays"/> by default.</param>
    public static async Task<(List<UsdaFood>? Results, string? Error)> SearchAsync(
        HttpClient http, string key, string query, int pageSize, string keyHint = "", IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<UsdaFood>(), null);
        var (status, body) = await GetAsync(http, Url(SearchPath(query, pageSize), key), retryDelays ?? RetryDelays);
        return ReadSearch(status, body, keyHint);
    }

    /// <summary>Look up a scanned/typed GTIN/UPC. Matches on FDC's gtinUpc, ignoring leading zeros.</summary>
    public static async Task<(UsdaFood? Food, string? Error)> LookupBarcodeAsync(
        HttpClient http, string key, string code, string keyHint = "", IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return (null, "That doesn't look like a product barcode.");

        // FDC's text search only matches the exact stored gtinUpc string, and its zero-padding
        // varies (12/13/14 digits) while scanners emit UPC-A (12) or EAN-13 (13) — so try the
        // code at each standard width until one hits. An 8-digit UPC-E is a mid-code zero
        // suppression of UPC-A (padding can never match), so expand it first; EAN-8 pads fine.
        var accepted = new HashSet<string>();
        var candidates = new List<string>();

        if (digits.Length == 8 && ExpandUpcE(digits) is { } upcA)
        {
            var expandedTrim = upcA.TrimStart('0');
            accepted.Add(expandedTrim);
            candidates.AddRange(new[] { 12, 13, 14 }.Select(len => expandedTrim.PadLeft(len, '0')));
        }

        var trimmed = digits.TrimStart('0');
        accepted.Add(trimmed);
        candidates.AddRange(new[] { digits.Length, 12, 13, 14 }
            .Where(len => len >= trimmed.Length && len >= 8)
            .Select(len => trimmed.PadLeft(len, '0')));

        string? lastError = null;
        foreach (var candidate in candidates.Distinct())
        {
            var (results, error) = await SearchAsync(http, key, candidate, 10, keyHint, retryDelays);
            if (error is not null) { lastError = error; continue; }
            var match = results!.FirstOrDefault(f =>
                f.GtinUpc is { } gtin && accepted.Contains(new string(gtin.Where(char.IsDigit).ToArray()).TrimStart('0')));
            if (match is not null) return (match, null);
        }
        return (null, lastError ?? $"No USDA food matches barcode {digits} — try the name search instead.");
    }

    /// <summary>
    /// Expand an 8-digit UPC-E (number system 0/1) to its 12-digit UPC-A per GS1 zero
    /// suppression. Null when the input isn't a valid UPC-E (wrong shape or check digit).
    /// </summary>
    public static string? ExpandUpcE(string code)
    {
        if (code.Length != 8 || code[0] is not ('0' or '1') || !code.All(char.IsDigit)) return null;
        var x = code.Substring(1, 6);
        var body = x[5] switch
        {
            '0' or '1' or '2' => $"{x[0]}{x[1]}{x[5]}0000{x[2]}{x[3]}{x[4]}",
            '3' => $"{x[0]}{x[1]}{x[2]}00000{x[3]}{x[4]}",
            '4' => $"{x[0]}{x[1]}{x[2]}{x[3]}00000{x[4]}",
            _ => $"{x[0]}{x[1]}{x[2]}{x[3]}{x[4]}0000{x[5]}",
        };
        var upcA = $"{code[0]}{body}{code[7]}";
        return UpcACheckDigit(upcA) == code[7] ? upcA : null;
    }

    /// <summary>UPC-A check digit computed from the first 11 digits (odd positions ×3).</summary>
    private static char UpcACheckDigit(string upcA)
    {
        var sum = 0;
        for (var i = 0; i < 11; i++)
        {
            var d = upcA[i] - '0';
            sum += i % 2 == 0 ? d * 3 : d;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }

    private static async Task<(int Status, string? Body)> GetAsync(HttpClient http, string url, IReadOnlyList<TimeSpan> retryDelays)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, body) = await GetOnceAsync(http, url);
            if (attempt >= retryDelays.Count || !IsTransient(status, body)) return (status, body);
            await Task.Delay(retryDelays[attempt]);
        }
    }

    private static async Task<(int Status, string? Body)> GetOnceAsync(HttpClient http, string url)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception)
        {
            return (0, null);
        }
    }

    private static (JsonDocument? Doc, string? Error) Interpret(int status, string? body, string keyHint, string? notFound = null)
    {
        if (status == 0)
            return (null, "Couldn't reach the USDA API — are you offline?");
        if (status == (int)HttpStatusCode.Forbidden)
            return (null, ("The USDA API rejected the key (HTTP 403). " + keyHint).Trim());
        if (status == 429)
            return (null, "Rate limit reached for this key — try again in a bit.");
        if (status == (int)HttpStatusCode.NotFound && notFound is not null)
            return (null, notFound);
        if (status is < 200 or > 299)
        {
            var error = ErrorReason(body) is { } reason
                ? $"USDA API error (HTTP {status}): {reason}"
                : $"USDA API error (HTTP {status}).";
            return (null, IsTransient(status, body) ? error + " This happens now and then; try again in a moment." : error);
        }
        try
        {
            return (JsonDocument.Parse(body ?? ""), null);
        }
        catch (JsonException)
        {
            return (null, "The USDA API sent a reply that couldn't be read.");
        }
    }

    /// <summary>
    /// USDA's own explanation of a failed request, so an error says why and not just the status:
    /// a JSON reply's message (api.data.gov nests it as {"error":{"message"}}), else the text
    /// itself. One line, capped, with any api_key value masked. Null when there's nothing useful.
    /// </summary>
    public static string? ErrorReason(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var reason = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                    root = err;
                reason = Str(root, "message") is { Length: > 0 } m ? m
                       : Str(root, "error") is { Length: > 0 } e ? e
                       : body;
            }
        }
        catch (JsonException) { }

        // An HTML page is a web server's error page (nginx), not the API's own JSON. The page
        // doesn't say which of the servers in front of the API sent it, only its title.
        if (IsHtml(reason))
        {
            var title = Regex.Match(reason, @"<title>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return title.Success && title.Groups[1].Value.Length > 0
                ? $"USDA's web server answered with an error page (\"{title.Groups[1].Value}\")."
                : null;
        }
        reason = Regex.Replace(reason, @"api_key=[^&\s""']+", "api_key=***",
            RegexOptions.IgnoreCase);
        reason = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return reason.Length <= 200 ? reason : reason[..200] + "…";
    }

    /// <summary>
    /// A search hit: nutrients per 100 g (or 100 ml), flat {nutrientNumber, value} rows. Survey
    /// (FNDDS) hits also carry household measures; other data types' come only from the per-food endpoint.
    /// </summary>
    public static UsdaFood ParseSearchFood(JsonElement food) =>
        Build(food, EnumerateRows(food, "nutrientNumber", "value", nested: false), ParseSearchMeasures(food));

    /// <summary>The per-food endpoint: nested {nutrient:{number}, amount} rows, plus portions.</summary>
    public static UsdaFood ParseDetailFood(JsonElement food) =>
        Build(food, EnumerateRows(food, "number", "amount", nested: true), ParsePortions(food));

    private static IEnumerable<(string Number, double Value)> EnumerateRows(JsonElement food, string numberProp, string valueProp, bool nested)
    {
        if (!food.TryGetProperty("foodNutrients", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var n in list.EnumerateArray())
        {
            var src = n;
            if (nested && !n.TryGetProperty("nutrient", out src)) continue;
            if (!src.TryGetProperty(numberProp, out var num)) continue;
            var number = num.ValueKind == JsonValueKind.String ? num.GetString() ?? "" : num.ToString();
            if (!n.TryGetProperty(valueProp, out var v) || !v.TryGetDouble(out var value)) continue;
            yield return (number, value);
        }
    }

    private static UsdaFood Build(JsonElement food, IEnumerable<(string Number, double Value)> rows, List<UsdaPortion> portions)
    {
        // Branded items carry their label serving; other data types report per 100 g only.
        // Some Branded full records drop the serving size but keep its unit, so the unit alone
        // still says whether the food is measured in g or ml (UsdaFood.WithLabelServingFrom
        // fills the size back in from the search hit).
        var baseUnit = "g";
        double? labelAmount = null;
        string? labelText = null;
        if (food.TryGetProperty("servingSizeUnit", out var ssu) && ssu.ValueKind == JsonValueKind.String)
        {
            var unit = (ssu.GetString() ?? "").ToLowerInvariant();
            if (unit is "g" or "grm" or "ml" or "mlt")
            {
                baseUnit = unit.StartsWith('m') ? "ml" : "g";
                if (food.TryGetProperty("servingSize", out var ss) && ss.TryGetDouble(out var size) && size > 0)
                {
                    labelAmount = size;
                    labelText = food.TryGetProperty("householdServingFullText", out var h) ? h.GetString() : null;
                }
            }
        }

        // Some Branded records repeat a nutrient with divergent values; the first row is the
        // canonical per-100 figure, so first occurrence wins throughout.
        double? kcal208 = null, kcalAtwater = null;
        var nutrientsPer100 = new Dictionary<string, double>();
        foreach (var (number, value) in rows)
        {
            // Energy: prefer 208 (kcal); Foundation foods may only have Atwater energy (957/958).
            if (number == "208")
                kcal208 ??= value;
            else if (number == "957" || number == "958")
                kcalAtwater ??= value;
            else if (NutrientMap.TryGetValue(number, out var nutrientKey) && value > 0)
                nutrientsPer100.TryAdd(nutrientKey, value);
        }

        return new UsdaFood
        {
            FdcId = food.TryGetProperty("fdcId", out var id) && id.TryGetInt32(out var fdcId) ? fdcId : 0,
            Description = food.TryGetProperty("description", out var d) ? ToTitleCase(d.GetString() ?? "") : "",
            Brand = food.TryGetProperty("brandOwner", out var b) ? ToTitleCase(b.GetString() ?? "") :
                    food.TryGetProperty("brandName", out var bn) ? ToTitleCase(bn.GetString() ?? "") : null,
            DataType = food.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "" : "",
            GtinUpc = food.TryGetProperty("gtinUpc", out var gtin) ? gtin.GetString() : null,
            BaseUnit = baseUnit,
            LabelServingAmount = labelAmount,
            LabelServingText = labelText,
            CaloriesPer100 = kcal208 ?? kcalAtwater,
            NutrientsPer100 = nutrientsPer100,
            Portions = baseUnit == "g" ? portions : new List<UsdaPortion>(),
        };
    }

    /// <summary>
    /// The name given to FNDDS's "Quantity not specified" portion: the amount its survey assumes
    /// when someone didn't say how much they ate (31 g of salad dressing, one 205 g Big Mac).
    /// </summary>
    public const string TypicalServing = "1 typical serving";

    /// <summary>
    /// Household measures. SR Legacy phrases them as amount + modifier ("1" "sandwich");
    /// Foundation/Survey foods use portionDescription or a measure unit. A Survey (FNDDS)
    /// "Quantity not specified" row is named <see cref="TypicalServing"/>: built from its
    /// modifier, a code (90000), it used to show up as a portion named "1 90000".
    /// </summary>
    private static List<UsdaPortion> ParsePortions(JsonElement food)
    {
        var portions = new List<UsdaPortion>();
        if (!food.TryGetProperty("foodPortions", out var list) || list.ValueKind != JsonValueKind.Array) return portions;
        foreach (var p in list.EnumerateArray())
        {
            if (!p.TryGetProperty("gramWeight", out var gw) || !gw.TryGetDouble(out var grams) || grams <= 0) continue;
            var amount = p.TryGetProperty("amount", out var a) && a.TryGetDouble(out var av) && av > 0 ? av : 1;
            var desc = Str(p, "portionDescription");
            if (desc.Contains("Quantity not specified", StringComparison.OrdinalIgnoreCase)) desc = TypicalServing;
            if (string.IsNullOrWhiteSpace(desc))
            {
                var modifier = Str(p, "modifier");
                if (modifier.All(char.IsDigit)) modifier = ""; // FNDDS modifiers are codes, not words
                var unit = p.TryGetProperty("measureUnit", out var mu) ? Str(mu, "name") : "";
                if (unit is "undetermined") unit = "";
                desc = string.Join(' ', new[] { amount.ToString("0.##", CultureInfo.InvariantCulture), unit, modifier }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            if (!string.IsNullOrWhiteSpace(desc) && !portions.Any(x => x.Description.Equals(desc, StringComparison.OrdinalIgnoreCase)))
                portions.Add(new UsdaPortion(desc.Trim(), grams));
        }
        return portions;
    }

    /// <summary>
    /// Household measures in a search hit ("foodMeasures": Survey/FNDDS foods fill them in),
    /// in USDA's rank order, skipping FNDDS's "Quantity not specified" stand-in.
    /// </summary>
    private static List<UsdaPortion> ParseSearchMeasures(JsonElement food)
    {
        var portions = new List<(int Rank, UsdaPortion Portion)>();
        if (!food.TryGetProperty("foodMeasures", out var list) || list.ValueKind != JsonValueKind.Array) return new();
        var n = 0;
        foreach (var m in list.EnumerateArray())
        {
            n++;
            if (!m.TryGetProperty("gramWeight", out var gw) || !gw.TryGetDouble(out var grams) || grams <= 0) continue;
            var desc = Str(m, "disseminationText").Trim();
            if (desc.Length == 0 || desc.Contains("Quantity not specified", StringComparison.OrdinalIgnoreCase)) continue;
            if (portions.Any(x => x.Portion.Description.Equals(desc, StringComparison.OrdinalIgnoreCase))) continue;
            var rank = m.TryGetProperty("rank", out var r) && r.TryGetInt32(out var rv) ? rv : 1000 + n;
            portions.Add((rank, new UsdaPortion(desc, grams)));
        }
        return portions.OrderBy(p => p.Rank).Select(p => p.Portion).ToList();
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ToTitleCase(string s) =>
        s.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())
            : s;
}
