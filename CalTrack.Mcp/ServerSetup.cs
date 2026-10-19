using System.Text.Json;
using CalorieTracker.Sync;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// The server's identity and its instructions: text a client keeps in the model's context for
/// the whole session. Tool descriptions aren't enough on their own — a client that loads tools
/// on demand shows the model only tool NAMES until it picks some, and in testing it picked the
/// menu, log and USDA search tools and never read add_recipe's description (18 of 20 fresh
/// sessions stopped to ask instead of building a recipe). Clients decide when the instructions
/// arrive: Claude Desktop's subagents got them only after their first tool load, so the tools
/// they load first must still point to add_recipe themselves.
/// </summary>
public static class ServerSetup
{
    public const string Instructions =
        """
        CalTrack is the user's calorie tracker. To log something they ate:
        1. Check their menu (list_menu_items with a word from the food). If it's there, log it with log_food.
        2. Otherwise search USDA (search_usda). If one result is the food itself, add it with add_menu_item, then log it.
        3. If it's a dish made of several foods and no single result is that dish (a sandwich, a bowl, a dessert with toppings), build it with add_recipe from generic USDA ingredients, or foods already on their menu, then log it.
        Don't stop to ask just because the user gave no amounts or brand: use typical household portions (search_usda lists them; add_recipe takes a portion and a count), log it, and tell the user what you assumed so they can correct it with update_recipe or update_entry. Ask only when you can't tell what the food is.
        Nutrition numbers come from USDA, the user's menu or the user, never from you: don't estimate or add up numbers yourself for log_adhoc or a manual ingredient. add_recipe adds up the ingredients for you.
        Prefer generic USDA foods. Use a branded one only when the user named the brand, store or restaurant, and then put the brand in the item's name.
        USDA lookups run in the CalTrack app with the user's own key, so CalTrack must be open for them; everything else works without it.
        """;

    public static void Configure(McpServerOptions o)
    {
        o.ServerInfo = new Implementation { Name = "caltrack", Version = ServerStatus.CurrentVersion };
        o.ServerInstructions = Instructions;
    }

    public static void Filters(IMcpRequestFilterBuilder filters) => filters.AddCallToolFilter(NameMissingArguments);

    /// <summary>
    /// A call without a required argument would fail inside the SDK's binder, which hides
    /// the reason ("An error occurred invoking 'log_food'."). Check the tool's own schema
    /// first and say which argument is missing and what it's for.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> NameMissingArguments(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        var name = context.Params?.Name;
        var tool = context.MatchedPrimitive as McpServerTool
                   ?? (name is not null && context.Server.ServerOptions.ToolCollection?.TryGetPrimitive(name, out var found) == true ? found : null);
        if (tool?.ProtocolTool.InputSchema is { ValueKind: JsonValueKind.Object } schema
            && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            var given = context.Params?.Arguments;
            var missing = required.EnumerateArray().Select(r => r.GetString() ?? "")
                .Where(r => r.Length > 0 && (given is null || !given.TryGetValue(r, out var v) || v.ValueKind == JsonValueKind.Null))
                .ToList();
            if (missing.Count > 0)
            {
                string About(string arg) =>
                    schema.TryGetProperty("properties", out var props) && props.TryGetProperty(arg, out var p)
                    && p.TryGetProperty("description", out var d) ? $"{arg} ({d.GetString()})" : arg;
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"{tool.ProtocolTool.Name} needs {string.Join("; ", missing.Select(About))}. Nothing was changed." }],
                };
            }
        }
        return await next(context, cancellationToken);
    };
}
