# CalTrack MCP server

Tell Claude *"I had two eggs and toast"* and it lands in [CalTrack](https://jakotheshadows.github.io/CalorieTracker/).

A local [Model Context Protocol](https://modelcontextprotocol.io) server (C#, stdio). It reads the
CalTrack data folder you connect in the app and logs food into it. Nothing is hosted and no ports
are opened: your MCP client launches the server as a local process, and the app and server meet
in a folder on your disk.

## Setup

**Using CalTrack?** You don't need any of this: on Windows, CalTrack's **Settings → Data
folder** offers **CalTrack for Claude**, a ready-made Claude Desktop bundle of this server
(built by the deploy workflow from [`mcpb/manifest.json`](mcpb/manifest.json)). The
[project README](../README.md) walks through it.

**Building it yourself:**

**1. Connect a data folder in CalTrack.** Settings → *Data folder* → *Connect data folder…* and
pick an empty folder, e.g. `Documents\CalTrack`. This needs Chrome or Edge on a desktop — Safari
and Firefox don't support the File System Access API, and the app says so when it detects that.

**2. Build the server** (requires the [.NET 9 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet publish CalTrack.Mcp -c Release -o ~/caltrack-mcp
```

**3. Register it with your MCP client**, pointing `--data-dir` at the folder from step 1.

Claude Desktop — `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "caltrack": {
      "command": "C:\\Users\\you\\caltrack-mcp\\caltrack-mcp.exe",
      "args": ["--data-dir", "C:\\Users\\you\\Documents\\CalTrack"]
    }
  }
}
```

Claude Code:

```bash
claude mcp add caltrack -- ~/caltrack-mcp/caltrack-mcp --data-dir ~/Documents/CalTrack
```

(`CALTRACK_DATA_DIR` works instead of `--data-dir`.)

**Seeing the protocol.** Add `--trace-protocol true` (or set `CALTRACK_TRACE_PROTOCOL=true`) and
each server process records every JSON-RPC message, exactly as sent, in
`mcp-trace/<start time>-<pid>.log` in the data folder: one line per message — UTC time,
direction (`C->S` client to server, `S->C` back), message. Lines marked `USDA` are the
server's own: one per USDA fetch it asks the app for, with the path, attempt, outcome, USDA's
status and reason, and how long the server waited and the app's fetch took. A tool result only
shows how a lookup ended; these show every try. It's off by default because the
messages include what you log. In the Claude Desktop bundle it's the extension's **Record
protocol traffic** setting.

The server needs no USDA key. `search_usda`, and adding foods or recipe ingredients by USDA id,
ask the CalTrack app to do the lookup with the key you saved in its Settings. For those, CalTrack
must be open in the browser (in the background is fine) with its data folder connected.
Everything else works whether it's open or not, and lands when it next opens.

## Tools

Everything a user does in CalTrack, except import and clear-all, the USDA key, connecting the
folder, and barcode scanning.

| Area | Tool | What it does |
| --- | --- | --- |
| Menu | `list_menu_items` | The menu (items + recipes) with per-serving nutrition — the only names `log_food` accepts. |
| | `get_menu_item` | One item or recipe in full: every nutrient, a recipe's ingredients, where it's used. |
| | `add_menu_item` | Add a food — by USDA id (the server fetches the numbers, at the label serving or USDA's household portion), or with numbers the user gave. |
| | `update_menu_item` | Rename or edit an item; only the fields passed change. |
| | `delete_menu_item` | Delete an item; logged days keep its numbers as one-offs. |
| | `add_recipe` | Add a recipe of ingredients: a USDA id with a household portion and count (numbers fetched by the server), a food already on the menu in servings, or numbers the user gave. |
| | `update_recipe` | Rename a recipe, change its servings, or remove, re-amount and add ingredients. |
| | `delete_recipe` | Delete a recipe; logged days keep its numbers. The result has its ingredients, to add it back. |
| | `search_usda` | Search USDA FoodData Central (via the open CalTrack app): generic foods first, including FNDDS's mixed dishes, each with its household portions; `branded` narrows to products or to generic foods. |
| Log | `get_day` | One day's numbered entries and totals, including changes queued but not yet applied. |
| | `get_summary` | Totals, per-day numbers and averages over a range of days. |
| | `log_food` | Log servings of a **menu** item. Unknown names fail with the closest real names. |
| | `log_adhoc` | Log a one-off with nutrition the user gave. Refuses foods that are on the menu. |
| | `update_entry` | Change an entry's servings. |
| | `remove_entry` | Remove an entry; the result has what it was, to log it again. |
| | `clear_day` | Remove a day's entries. |
| Templates | `list_templates`, `save_template`, `apply_template`, `delete_template` | Day templates, as on the Schedule page. |
| Goals | `get_goals` | Profile, adaptive maintenance calories, suggested calories, trend weight, expected date, recent weigh-ins. |
| | `set_goals` | Set up or change the goal, with the app's limits (no target below a healthy weight). |
| | `log_weight`, `remove_weight` | The weight log, in the user's units. |
| Planning | `plan_random_days` | The app's Randomize: plans days from the menu toward a calorie target. |

Edits, deletes and plans are annotated `destructiveHint`, so clients ask before running them.

Dates are `yyyy-MM-dd`, `today` or `yesterday`, in the user's local time zone.

## Design notes

**Instructions the model always has.** The server sends instructions at connection: how to log
something (menu, then one USDA food, then a recipe), to use household portions and say what it
assumed rather than stop to ask, never to estimate numbers itself, and to prefer generic foods.
Clients that load tools on demand show the model only tool names until it picks some, so a rule
that lives only in `add_recipe`'s description is never read. In testing, 18 of 20 fresh sessions
asked for numbers instead of building a recipe. The tools a model loads first
(`list_menu_items`, `search_usda`, `log_adhoc`, `add_menu_item`) also point to `add_recipe`.

**Generic first, with portions.** USDA ranks branded products among, often above, its generic
foods, and the model took whatever came first. `search_usda` lists generic foods first, and it
searches FNDDS, USDA's "as eaten" database, which has whole dishes like a Reuben sandwich. Each
generic result lists its household portions, so the model can pick "1 slice" × 2 instead of
guessing grams. FNDDS hits carry them; for the first few others the server fetches the full
record and keeps it for the add that follows.

**Change tools compatibly; version only what breaks.** Clients save a tool's definition the
first time they use it, and Claude Code keeps that copy until the conversation is compacted. A
changed description or a new optional parameter is safe: an old copy still makes valid calls. A
renamed or removed tool, parameter or required field is not. If one ever has to break, add it
under a new name (`add_recipe_v2`) and keep the old one working until clients have moved on;
renaming every tool on every release would throw that saved copy away each time.

**The app is the only writer of the data file.** The obvious design — app and server both
rewrite `caltrack-data.json` — loses data: if the server writes between the app's read and its
write, the app's write silently erases the server's, and the tool that made it has already told
the model "logged". A browser can't take OS file locks, so a lock file can't prevent it. Instead
the server never touches the main file: each change is one file in `inbox/`, created under a
temp name and renamed into place so it's never seen half-written. The app folds inbox ops in,
records their ids in the main file, saves, and only then deletes them — so a crash in between
can never apply an op twice.

**One copy of the logic.** The models, the JSON format and the code that applies an op live in
`CalTrack.Core`, shared by the Blazor app and this server. When the server previews a day
(saved data + pending ops), it runs exactly the code the app will run when it ingests them; a
test asserts the two agree.

**Numbers that never pass through the model.** `add_menu_item` takes a USDA id, not
nutrition values: the server fetches the record and builds the item itself, so the numbers
can't be misremembered or invented on the way. The item's description links to its USDA
record. USDA values can't be overridden in the same call.

**The app's API is the folder.** A browser app can't accept connections, so what only the app
can do — USDA lookups with the key it keeps in the browser — is asked through the folder: the
server drops `requests/<id>.json` naming a USDA path, the app adds the key, fetches it and
writes USDA's reply verbatim to `responses/<id>.json`, and the server reads it with the same
Core code the app's own lookups use. The app only fetches the two shapes Core builds (a
search, one food), so a stray file can't send the key elsewhere. It advertises which requests
it answers; a request nobody answers in 20 seconds is withdrawn with an "open CalTrack" error
rather than left to be answered into the void.

**Answered by a worker, not the page.** The server asks while you're in Claude — CalTrack in
the background — and browsers throttle a background page's timers to one wake-up a minute
(Edge after about a minute hidden), far past the server's wait. A dedicated Web Worker's timers
aren't throttled (measured: every 1.0 s while the page's slowed to once a minute), so a worker
watches `requests/` and replies in about a second. It stays thin on purpose — a keyed fetch —
so no lookup logic lives in JavaScript.

**USDA's chance failures are asked again.** Now and then USDA's web server answers a valid
request with a bare nginx "400 Bad Request" page. In one run of 20 agents, 62 of the 108
searches USDA answered got it, and the same request worked seconds later. So a lookup that gets
that page, or a 502-504, is asked again after 1 s and then 2 s, within the same 20-second wait
and only while there's a second left for the app to answer; if time runs out, the error is
USDA's answer, not a timeout. A 400 in JSON (the API objecting
to the request) and a 429 (the rate limit) aren't retried. The rule is Core's
(`UsdaClient.IsTransient`), so the app's own lookups retry the same way.

**Old app versions can't lose new ops.** The app records the op kinds it understands in the
data file; the server won't queue a newer kind (like `add_item`) until the app says it can
apply it, and an app that meets an unknown kind leaves it in the inbox for a newer version
instead of consuming it. Newer *contents* of a known op get the same treatment: a recipe
ingredient measured in servings of a menu item waits for an app that lists `serving_units` in
`InboxFeatures`, because an older one would apply the recipe but count that ingredient as zero.

**Edits are patches, tried before they're sent.** An edit carries only what changes, never a whole
item, so a change the user makes in the app before the edit arrives isn't overwritten. The server
first applies each op to a copy of the current view with Core's code. One the app would refuse is
refused right there, in words the model can act on, instead of being queued and dropped. Ops
apply in creation order, and the server stamps each one after every op already waiting.
An "apply template" and a "delete template" sent in the same millisecond still land in that order.

**Entries are found again by name, not position.** The user may change a day between the model
reading it and the app applying the edit. So an entry op names the entry: its name, whether it's
a one-off, and which of the same-named ones. Regular entries merge by name, so only repeated
one-offs need the number. If that entry is gone, the op is rejected rather than hitting a
neighbour.

**The model is told what was undone.** Every delete returns what it removed, in the form needed
to add it back: a removed one-off's numbers, or a deleted recipe's ingredients in `add_recipe`'s
manual form. Deleting a menu item or recipe keeps history: its log entries become one-offs with
the same numbers, in the app too, so past totals never change.

**Random plans are generated by the server.** The planner is Core code. The server runs it and
sends the app the concrete days, so the plan Claude describes is the plan that lands.

**Tools for a model, not a person.** Ground, don't guess: `log_food` won't log a name that isn't
on the menu, and never fuzzy-matches silently — it returns suggestions and makes the model pick
or ask. Verify, don't assume: every write is read back through the same view before success is
reported. Say what's true: responses say an entry is *queued* for the app, not that the app
shows it yet.

## Tests

```bash
dotnet test CalTrack.Tests
```

Covers the inbox protocol (ordering, idempotency across a crash, pruning, unreadable files),
the server against a real temp folder (atomic writes, refusal paths, local-time dates), the
race the inbox exists to prevent, and the invariant that the server's preview equals what the
app ends up with.
