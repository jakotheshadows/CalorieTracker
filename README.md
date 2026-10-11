# CalTrack

A free calorie and nutrition tracker that runs in your browser and installs like an app. No
accounts, no ads, no tracking — your data stays on your device.

**Open it:** <https://jakotheshadows.github.io/CalorieTracker/>

Optionally, connect it to **Claude Desktop** and log food by just telling Claude what you ate:
*"I had two eggs and toast for breakfast."*

This guide walks through the full setup on **Windows**: the app first (5 minutes), then the
Claude integration (about 15 minutes, mostly installs).

---

## What you need

| For | You need |
| --- | --- |
| The app | Windows 10 or 11 with **Microsoft Edge** or **Google Chrome** |
| USDA nutrition search (optional) | A free API key from [api.data.gov](https://api.data.gov/signup/) |
| The Claude integration (optional) | [Claude Desktop](https://claude.ai/download), plus the .NET 9 SDK and Git (installed in step 3) |

Firefox can run CalTrack, but it can't connect the data folder the Claude integration uses, so
use Edge or Chrome.

---

## 1. Install the app

1. Open <https://jakotheshadows.github.io/CalorieTracker/> in Edge or Chrome.
2. Click the **install icon** at the right end of the address bar (a screen with a down arrow),
   then **Install**.
   No icon? Open the browser menu (**⋯** in Edge, **⋮** in Chrome) — Edge: **Apps → Install
   CalTrack**; Chrome: **Cast, save, and share → Install CalTrack**.
3. CalTrack opens in its own window and gets a Start menu entry. It works offline.

**Optional — USDA nutrition search.** Request a free key at
[api.data.gov/signup](https://api.data.gov/signup/) (name + email; the key arrives instantly).
In CalTrack, go to **Settings → USDA nutrition lookup**, paste it, click **Save key**, then
**Test key**. Now **🔎 Search USDA** fills in nutrition when you add a food on **Menu Items** —
and Claude can look foods up too.

Your data lives in this browser. **Settings → Backup & sync → Export JSON** makes a backup any
time.

You can stop here if you don't want the Claude integration.

---

## 2. Connect a data folder

The Claude integration works through a folder on your PC: CalTrack keeps its data there, and
the CalTrack server (installed next) reads it and drops in what you ask Claude to log.

1. Create an empty folder: `Documents\CalTrack`.
2. In CalTrack: **Settings → Data folder (Claude / MCP) → Connect data folder…**
3. Choose the folder, and allow CalTrack to edit files in it. If the browser offers
   **Allow on every visit**, pick that so it asks less often.
4. The card now says **Connected to CalTrack**, and the folder contains `caltrack-data.json`.

Browsers occasionally ask for permission again (for example after a restart). CalTrack then
shows a **Reconnect folder** banner — click it. Until you do, changes are kept in the browser
and catch up when you reconnect.

Leave the folder's files to CalTrack. (If something else does change the data file, CalTrack
never silently overwrites it: it loads the change, or keeps that version as a separate
`caltrack-data.….json` copy.)

---

## 3. Install the CalTrack server

The server is a small program that Claude Desktop runs in the background. You build it once
from this repository.

Open **PowerShell** and install the tools (skip any you already have):

```powershell
winget install Microsoft.DotNet.SDK.9
winget install Git.Git
```

Close PowerShell and open a new window so it finds them, then:

```powershell
git clone https://github.com/jakotheshadows/CalorieTracker.git
cd CalorieTracker
dotnet publish CalTrack.Mcp -c Release -o "$env:USERPROFILE\caltrack-mcp"
```

This creates `C:\Users\<you>\caltrack-mcp\caltrack-mcp.exe`. Keep the `CalorieTracker` folder —
you'll use it to update later.

(No Git? On GitHub use **Code → Download ZIP**, unzip it, and run the `dotnet publish` line
inside the unzipped folder.)

---

## 4. Register it with Claude Desktop

1. In Claude Desktop, open **Settings → Developer → Edit Config**. (These are the desktop app's
   own settings, not your claude.ai account settings.) This opens `claude_desktop_config.json`.
2. Add the `caltrack` entry, replacing `<you>` with your Windows user name — twice:

   ```json
   {
     "mcpServers": {
       "caltrack": {
         "command": "C:\\Users\\<you>\\caltrack-mcp\\caltrack-mcp.exe",
         "args": ["--data-dir", "C:\\Users\\<you>\\Documents\\CalTrack"]
       }
     }
   }
   ```

   - Backslashes must be doubled (`\\`) in this file.
   - `--data-dir` must be the folder you connected in step 2.
   - If the file already has an `"mcpServers"` section, add `"caltrack": { … }` inside it,
     with a comma after the entry before it.
3. **Fully quit** Claude Desktop — right-click its icon in the taskbar's notification area and
   choose **Quit** (closing the window isn't enough) — then open it again.
4. Check it's connected: in a chat, click **+** (*Add files, connectors, and more*) at the
   bottom left of the message box → **Connectors → Manage connectors**. **caltrack** should be
   listed with six tools.

---

## 5. Try it

Start a new chat in Claude Desktop and try:

- *"What's on my CalTrack menu?"*
- *"I had a large egg for breakfast."*
- *"Add an ice cream sandwich."* — a food that isn't on your menu yet: Claude searches USDA,
  adds it to your menu with USDA's numbers, and logs it.
- *"What have I eaten today?"*

Claude Desktop asks for your approval before it uses CalTrack's tools.

Entries show up in CalTrack within a few seconds when it's open (up to a minute if its window
is in the background), with a **Via MCP:** notice — or the next time you open it.

### What needs CalTrack open

| Asking Claude to… | CalTrack open? |
| --- | --- |
| Log food that's on your menu, log a one-off, read a day or your menu | No |
| Search USDA or add a food from USDA | Yes — in the background is fine, with the folder connected and a USDA key saved |

USDA lookups need CalTrack open because your USDA key stays in CalTrack; the server never has it.

### What Claude can and can't do

Claude can list your menu, read a day, log menu items and one-offs, search USDA, and add foods to
your menu. It can't edit or delete entries, or change your goals, weights or recipes — do those
in CalTrack.

Claude can only log foods by their exact menu name, and foods it adds from USDA get USDA's numbers
directly. One-off foods use numbers you give it — Claude is instructed never to estimate them.

---

## Updating

**The app** updates itself. When a new version is ready, a banner offers **Update now**; you can
also use **Settings → App updates → Check for updates**.

**The server:** quit Claude Desktop fully first (the files are in use while it runs), then in
PowerShell:

```powershell
cd CalorieTracker
git pull
dotnet publish CalTrack.Mcp -c Release -o "$env:USERPROFILE\caltrack-mcp"
```

…and reopen Claude Desktop. If Claude ever says CalTrack needs updating, update the app.

---

## Troubleshooting

| Problem | Fix |
| --- | --- |
| **caltrack** isn't listed in Claude's connectors | Check the config: doubled backslashes, commas, and that both paths exist. Then fully quit and reopen Claude Desktop. Claude's logs are in `%APPDATA%\Claude\logs` (`mcp.log` and `mcp-server-caltrack.log`). |
| *"No CalTrack data folder is configured"* or *"…doesn't exist"* | The `--data-dir` path in the config is wrong — it must be the folder connected in CalTrack. |
| *"CalTrack didn't answer within 20 seconds"* | Open CalTrack. If it shows **Reconnect folder**, click it. Then ask again. |
| *"No API key configured"* | Save your key in CalTrack: **Settings → USDA nutrition lookup**. |
| A message about updating CalTrack | **Settings → App updates → Check for updates**, then **Update now**. |
| **Not available in this browser** on the Data folder card | Use Edge or Chrome on your PC. |
| `dotnet publish` fails because a file is in use | Quit Claude Desktop fully, then run it again. |
| Entries Claude logged aren't in CalTrack yet | Open CalTrack (and reconnect the folder if asked) — they're waiting in the folder and appear within seconds. |

---

## Also works with Claude Code

To use the same server from [Claude Code](https://claude.com/claude-code) in a terminal:

```powershell
claude mcp add --scope user caltrack -- "$env:USERPROFILE\caltrack-mcp\caltrack-mcp.exe" --data-dir "$env:USERPROFILE\Documents\CalTrack"
```

---

## Privacy

- Your data is stored in your browser and, if you connect one, in your data folder. There are no
  accounts and no CalTrack servers.
- Your USDA key is stored only in CalTrack's browser storage — never in exports or the data
  folder — and is only sent to USDA.
- When you use the Claude integration, what Claude reads through the tools (your menu, the days
  you ask about) becomes part of that Claude conversation.

---

## For developers

| Folder | What it is |
| --- | --- |
| `/` | The app: Blazor WebAssembly (.NET 9) PWA, deployed to GitHub Pages on every push to `main` |
| `CalTrack.Core` | Models, data operations and the data-folder protocol, shared by the app and the server |
| `CalTrack.Mcp` | The MCP server — design notes in [its README](CalTrack.Mcp/README.md) |
| `CalTrack.Tests` | Tests for the protocol and the server |

```powershell
dotnet run --project CalorieTracker.csproj   # the app, locally
dotnet test CalTrack.Tests                    # the tests
```
