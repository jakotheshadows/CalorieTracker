// CalTrack's API for the MCP server, answered from a dedicated worker.
//
// The server drops requests/<id>.json in the data folder and waits (20 s) for
// responses/<id>.json. It mostly asks while the user is in Claude, i.e. while CalTrack is in
// the background — where the browser throttles the page's timers to about one wake-up a
// minute (Edge does so after roughly a minute hidden). A dedicated worker's timers aren't
// throttled that way, so answering here keeps replies to about a second.
//
// Deliberately thin: the only request is "fetch this USDA path with the user's key". Building
// the path, reading the reply and wording errors are C# (CalTrack.Core's UsdaClient), shared
// with the app's own lookups. The key comes from the page (localStorage isn't available in
// workers) and is only ever sent to the USDA API.

const API = "https://api.nal.usda.gov/fdc/v1/";
// The two shapes UsdaClient builds (SearchPath, FoodPath) — anything else is refused, so a
// stray file in the folder can't spend the key elsewhere. Keep in step with UsdaClient.
const ALLOWED = /^(foods\/search\?[^#]*|food\/\d+)$/;
const POLL_MS = 500;

let dir = null;     // the data folder (FileSystemDirectoryHandle), from the page
let key = null;     // the user's USDA key, from the page
let busy = false;
const stuck = new Set(); // answered but couldn't be removed: don't fetch again

self.onmessage = (e) => {
    const m = e.data || {};
    if (m.type === "start") dir = m.dir || null;
    else if (m.type === "key") key = (m.key || "").trim() || null;
};

setInterval(poll, POLL_MS);

async function poll() {
    if (!dir || busy) return;
    busy = true;
    try {
        let requests;
        try { requests = await dir.getDirectoryHandle("requests"); }
        catch { return; } // none asked yet (or access lapsed: the asker times out with an explanation)

        // Writers create ".<id>.tmp" and rename when complete: only finished requests count.
        const names = [];
        for await (const [name, h] of requests.entries())
            if (h.kind === "file" && !name.startsWith(".") && name.toLowerCase().endsWith(".json")) names.push(name);
        for (const name of [...stuck]) if (!names.includes(name)) stuck.delete(name);

        for (const name of names.sort()) {
            if (stuck.has(name)) continue;
            let req = null;
            try { req = JSON.parse(await (await (await requests.getFileHandle(name)).getFile()).text()); }
            catch { /* unreadable or vanished: nothing to answer */ }
            // Past its asker's timeout: answering would only leave an orphaned response.
            if (req && req.Id && !expired(req)) await respond(req.Id, await answer(req));
            try { await requests.removeEntry(name); } catch { stuck.add(name); }
        }
    } catch {
        // Folder briefly unavailable: the next poll retries.
    } finally {
        busy = false;
    }
}

function expired(req) {
    const created = Date.parse(req.CreatedUtc);
    const timeout = Math.max(1, Number(req.TimeoutSeconds) || 20) * 1000;
    return !(created > 0) || Date.now() - created > timeout;
}

// AppResponse: { Id, Error, Status, Body } — Error when the app couldn't ask, else USDA's
// status (0 = unreachable) and reply verbatim.
async function answer(req) {
    if (req.Kind !== "usda_get")
        return { Error: `This version of CalTrack doesn't understand "${req.Kind}" requests.` };
    const path = String(req.Path || "");
    if (!ALLOWED.test(path) || /api_key/i.test(path))
        return { Error: "CalTrack only fetches USDA food searches and food records." };
    if (!key) return { Error: "No API key configured. Add one in Settings." };
    try {
        const resp = await fetch(API + path + (path.includes("?") ? "&" : "?") + "api_key=" + encodeURIComponent(key));
        return { Status: resp.status, Body: await resp.text() };
    } catch {
        return { Status: 0, Body: null };
    }
}

// Chromium writes to a swap file and swaps it in on close(), so the server never reads a
// half-written response.
async function respond(id, response) {
    const responses = await dir.getDirectoryHandle("responses", { create: true });
    const fh = await responses.getFileHandle(`${id}.json`, { create: true });
    const w = await fh.createWritable();
    try { await w.write(JSON.stringify({ Id: id, Error: null, Status: 0, Body: null, ...response })); }
    catch (e) { try { await w.abort(); } catch { } throw e; }
    await w.close();
}
