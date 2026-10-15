# CalTrack

A free calorie and nutrition tracker that runs in your browser and installs like an app. No
accounts, no ads, no tracking — your data stays on your device.

**Open it:** <https://jakotheshadows.github.io/CalorieTracker/>

Optionally, connect it to **Claude Desktop** and log food by just telling Claude what you ate:
*"I had two eggs and toast for breakfast."*

This guide walks through the full setup on **Windows**: the app, then the Claude integration —
about 10 minutes in all, nothing to build or configure by hand.

---

## What you need

| For | You need |
| --- | --- |
| The app | Windows 10 or 11 with **Microsoft Edge** or **Google Chrome** |
| USDA nutrition search (optional) | A free API key from [api.data.gov](https://api.data.gov/signup/) |
| The Claude integration (optional) | [Claude Desktop](https://claude.ai/download) for Windows |

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

## 3. Add CalTrack to Claude Desktop

1. Install [Claude Desktop](https://claude.ai/download) if you haven't, and sign in.
2. In CalTrack: **Settings → Data folder (Claude / MCP)**. Under **Claude Desktop**, click
   **Download CalTrack for Claude** (about 7 MB). This section appears once the folder is
   connected, and only on Windows.
3. In Claude Desktop, open **Settings → Extensions** (under *This computer*) and drag the
   downloaded `caltrack-windows.mcpb` from File Explorer onto that page.
   Don't double-click the file: Windows hands it to Claude, which attaches it to a new chat
   instead of installing it.
4. Click **Install**. When it asks for the **CalTrack data folder**, choose the folder you
   connected in CalTrack (section 2). It suggests `Documents\CalTrack`, so if that's where yours
   is, just confirm.
5. Back in CalTrack, click **Check again**. It should say **Set up — Claude Desktop is running
   CalTrack for Claude**.

In Claude Desktop the extension is called **CalTrack**. Everything it needs is in the download —
no .NET, no config files.

---

## 4. Try it

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

Claude can list your menu, read a day, log menu items and one-offs, search USDA, and add foods and
recipes to your menu. A recipe covers dishes USDA has no single entry for — a brownie sundae can be
a brownie plus a hot fudge sundae. Claude can't edit or delete entries, menu items or recipes, or
change your goals or weights — do those in CalTrack.

Claude can only log foods by their exact menu name, and foods and recipe ingredients it adds from
USDA get USDA's numbers directly. One-offs and other ingredients use numbers you give it — Claude is
instructed never to estimate them.

---

## Updating

**The app** updates itself. When a new version is ready, a banner offers **Update now**; you can
also use **Settings → App updates → Check for updates**.

**CalTrack for Claude:** when a new version comes with the app, **Settings → Data folder →
Claude Desktop** says **Update available**. Download it again and drag it onto Claude
Desktop's **Settings → Extensions**, as when you installed it.

If Claude ever says CalTrack needs updating, update the app.

---

## Troubleshooting

| Problem | Fix |
| --- | --- |
| No **Claude Desktop** section in CalTrack's Settings | Connect the data folder first (step 2), on Windows in Edge or Chrome. |
| Claude opened a chat with the file attached (or said the file is too big) | The file was double-clicked, which attaches it to a chat. Drag it onto Claude Desktop's **Settings → Extensions** page instead. |
| Settings still says **Tell Claude what you ate…** after installing | Click **Check again**. If it doesn't change, make sure the CalTrack extension is enabled in Claude Desktop's **Settings → Extensions**. |
| Claude finds an empty menu, or says the data folder doesn't exist | The extension points at a different folder. In Claude Desktop's **Settings → Extensions → CalTrack**, set the data folder to the one connected in CalTrack. |
| *"CalTrack didn't answer within 20 seconds"* | Open CalTrack. If it shows **Reconnect folder**, click it. Then ask again. |
| *"No API key configured"* | Save your key in CalTrack: **Settings → USDA nutrition lookup**. |
| A message about updating CalTrack | **Settings → App updates → Check for updates**, then **Update now**. |
| **Not available in this browser** on the Data folder card | Use Edge or Chrome on your PC. |
| Entries Claude logged aren't in CalTrack yet | Open CalTrack (and reconnect the folder if asked) — they're waiting in the folder and appear within seconds. |

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

The deploy workflow also builds **CalTrack for Claude** — the server as a self-contained
Windows exe, packed into a Claude Desktop bundle (`CalTrack.Mcp/mcpb/manifest.json`) — and
publishes it at `downloads/caltrack-windows.mcpb` next to the app.

To run the server from source instead (needs the [.NET 9 SDK](https://dotnet.microsoft.com/download)):

```powershell
dotnet publish CalTrack.Mcp -c Release -o "$env:USERPROFILE\caltrack-mcp"
```

…then register `caltrack-mcp.exe --data-dir <your data folder>` with your MCP client — in
Claude Desktop under **Settings → Developer → Edit Config**, or in Claude Code:

```powershell
claude mcp add --scope user caltrack -- "$env:USERPROFILE\caltrack-mcp\caltrack-mcp.exe" --data-dir "$env:USERPROFILE\Documents\CalTrack"
```
