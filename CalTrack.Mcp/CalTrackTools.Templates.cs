using System.ComponentModel;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>Day templates: a saved day's entries that can be added to another day in one go.</summary>
public static partial class CalTrackTools
{
    [McpServerTool(Name = "list_templates", ReadOnly = true, Idempotent = true)]
    [Description("List the user's day templates — saved sets of entries, e.g. \"Usual breakfast\" — with their entries and calories.")]
    public static string ListTemplates(DataFolderStore store)
    {
        var view = store.Read().View;
        var templates = view.Templates.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(t => TemplateInfo(view, t.Key, t.Value)).ToList();
        return Json(new
        {
            count = templates.Count,
            templates,
            note = templates.Count == 0 ? "No templates yet. save_template saves a day's entries as one." : null,
        });
    }

    [McpServerTool(Name = "save_template", Destructive = true)]
    [Description(
        "Save a day's entries as a template, e.g. \"save today's breakfast as Usual breakfast\" — everything logged " +
        "that day is saved, so it can be added to other days with apply_template. An existing template of that " +
        "name is only replaced with overwrite: true.")]
    public static string SaveTemplate(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The template's name.")] string name,
        [Description("The day to copy, yyyy-MM-dd or \"today\" / \"yesterday\". Defaults to today.")] string? date = null,
        [Description("true to replace an existing template with this name.")] bool overwrite = false)
    {
        var day = ResolveDate(date, clock);
        var snap = store.Read();
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) throw new McpException("name is required.");
        var entries = snap.View.GetDay(day);
        if (entries.Count == 0) throw new McpException($"Nothing is logged on {AppData.DayKey(day)}, so there's nothing to save.");
        var existing = snap.View.FindTemplate(trimmed);
        if (existing is not null && !overwrite)
            throw new McpException($"A template named \"{existing}\" already exists. Pass overwrite: true to replace it (ask the user first), or pick another name.");

        var op = NewOp(InboxOp.SaveTemplate, clock);
        op.Date = AppData.DayKey(day);
        op.TemplateName = trimmed;
        op.Overwrite = overwrite;
        var (_, status, _) = Submit(store, snap, op, "save templates");
        return Json(new
        {
            saved = TemplateInfo(snap.View, trimmed, entries),
            replaced = existing is not null ? TemplateInfo(snap.View, existing, snap.View.Templates[existing]) : null,
            status,
            note = QueuedNote,
        });
    }

    [McpServerTool(Name = "apply_template", Destructive = false)]
    [Description(
        "Add a template's entries to a day, e.g. \"log my usual breakfast\". Entries for foods already logged that day " +
        "add to their servings. Use list_templates for the names.")]
    public static string ApplyTemplate(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The template's name, as list_templates shows it.")] string name,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null)
    {
        var day = ResolveDate(date, clock);
        var snap = store.Read();
        var template = RequireTemplate(snap.View, name);

        var op = NewOp(InboxOp.ApplyTemplate, clock);
        op.Date = AppData.DayKey(day);
        op.TemplateName = template;
        var (after, status, _) = Submit(store, snap, op, "apply templates");
        return Json(new
        {
            applied = TemplateInfo(snap.View, template, snap.View.Templates[template]),
            status,
            note = QueuedNote,
            day = DayReport(after, day, clock),
        });
    }

    [McpServerTool(Name = "delete_template", Destructive = true)]
    [Description("Delete a day template. Days it was added to keep their entries. The result lists its entries.")]
    public static string DeleteTemplate(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The template's name, as list_templates shows it.")] string name)
    {
        var snap = store.Read();
        var template = RequireTemplate(snap.View, name);
        var op = NewOp(InboxOp.DeleteTemplate, clock);
        op.TemplateName = template;
        var (_, status, _) = Submit(store, snap, op, "delete templates");
        return Json(new
        {
            deleted = TemplateInfo(snap.View, template, snap.View.Templates[template]),
            status,
            note = QueuedNote,
        });
    }

    private static string RequireTemplate(AppData view, string? name)
    {
        var wanted = name?.Trim() ?? "";
        return view.FindTemplate(wanted) ?? throw new McpException(
            $"There's no template named \"{wanted}\", so nothing was changed. " +
            (view.Templates.Count == 0
                ? "The user has no templates."
                : (NameMatcher.Closest(wanted, view.Templates.Keys, 5) is { Count: > 0 } close
                    ? $"Closest: {string.Join(", ", close.Select(c => $"\"{c}\""))}."
                    : $"Templates: {string.Join(", ", view.Templates.Keys.Order().Select(c => $"\"{c}\""))}.")));
    }

    private static object TemplateInfo(AppData view, string name, List<ScheduleEntry> entries) => new
    {
        name,
        calories = Round(entries.Sum(e => (view.ResolveEntry(e)?.Calories ?? 0) * e.Servings)),
        entries = entries.Select(e => new
        {
            name = e.ItemName,
            servings = e.Servings,
            oneOff = e.AdHoc is not null ? true : (bool?)null,
            calories = view.ResolveEntry(e)?.Calories is { } c ? Round(c * e.Servings) : (double?)null,
        }).ToList(),
    };
}
