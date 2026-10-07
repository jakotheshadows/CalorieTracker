# CalTrack MCP server

Tell Claude *"I had two eggs and toast"* and it lands in [CalTrack](https://jakotheshadows.github.io/CalorieTracker/).

A local [Model Context Protocol](https://modelcontextprotocol.io) server (C#, stdio). It reads the
CalTrack data folder you connect in the app and logs food into it. Nothing is hosted and no ports
are opened: your MCP client launches the server as a local process, and the app and server meet
in a folder on your disk.

## Setup

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

## Tools

| Tool | What it does |
| --- | --- |
| `list_menu_items` | The user's menu (items + recipes) with per-serving nutrition — the only names `log_food` accepts. |
| `get_day` | One day's entries and totals, including ones queued but not yet seen by the app. |
| `log_food` | Log servings of a **menu** item. Unknown names fail with the closest real names. |
| `log_adhoc` | Log a one-off food with nutrition the user gave. Refuses foods that are on the menu. |

Dates are `yyyy-MM-dd`, `today` or `yesterday`, in the user's local time zone.

## Design notes

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
