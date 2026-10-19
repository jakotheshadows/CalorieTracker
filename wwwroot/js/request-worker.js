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

        // One at a time, oldest first (see pending), listing again after each: a request written
        // while another was being fetched (often a retry) goes next instead of after the rest of
        // a batch.
        const tried = new Set();
        for (;;) {
            const names = await pending(requests);
            for (const name of [...stuck]) if (!names.includes(name)) stuck.delete(name);
            const name = names.find(n => !stuck.has(n) && !tried.has(n));
            if (!name) break;
            tried.add(name);
            try {
                let req = null;
                try { req = JSON.parse(await (await (await requests.getFileHandle(name)).getFile()).text()); }
                catch { /* unreadable or vanished: nothing to answer */ }
                // Past its asker's timeout: answering would only leave an orphaned response.
                if (req && req.Id && !expired(req)) await respond(req.Id, await answer(req));
            } catch {
                continue; // the answer couldn't be written: leave the request for the next poll
            }
            try { await requests.removeEntry(name); } catch { stuck.add(name); }
        }
    } catch {
        // Folder briefly unavailable: the next poll retries.
    } finally {
        busy = false;
    }
}

// Writers create ".<id>.tmp" and rename when complete: only finished requests count. Oldest
// first: newer servers start each id with its lookup's time (UTC yyyyMMddTHHmmssfff); an older
// server's random id is placed by when this worker first saw it, written the same way, so it
// takes its turn instead of sorting behind every timed id.
const TIME_FIRST = /^\d{8}T\d{9}-/;
const firstSeen = new Map();

async function pending(requests) {
    const names = [];
    for await (const [name, h] of requests.entries())
        if (h.kind === "file" && !name.startsWith(".") && name.toLowerCase().endsWith(".json")) names.push(name);
    for (const name of [...firstSeen.keys()]) if (!names.includes(name)) firstSeen.delete(name);
    const order = (name) => {
        if (TIME_FIRST.test(name)) return name;
        if (!firstSeen.has(name)) firstSeen.set(name, new Date().toISOString().replace(/[-:.Z]/g, "") + "-");
        return firstSeen.get(name) + name;
    };
    return names.map(n => [order(n), n]).sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0)).map(p => p[1]);
}

function expired(req) {
    const created = Date.parse(req.CreatedUtc);
    const timeout = Math.max(1, Number(req.TimeoutSeconds) || 20) * 1000;
    return !(created > 0) || Date.now() - created > timeout;
}

// AppResponse: { Id, Error, Status, Body, FetchMs } — Error when the app couldn't ask, else
// USDA's status (0 = unreachable), reply verbatim, and how long the fetch took. Retrying is
// the server's decision (UsdaClient.IsTransient), not the worker's.
async function answer(req) {
    if (req.Kind !== "usda_get")
        return { Error: `This version of CalTrack doesn't understand "${req.Kind}" requests.` };
    const path = String(req.Path || "");
    if (!ALLOWED.test(path) || /api_key/i.test(path))
        return { Error: "CalTrack only fetches USDA food searches and food records." };
    if (!key) return { Error: "No API key configured. Add one in Settings." };
    const started = performance.now();
    try {
        const resp = await fetch(API + path + (path.includes("?") ? "&" : "?") + "api_key=" + encodeURIComponent(key));
        const body = await resp.text();
        return { Status: resp.status, Body: body, FetchMs: Math.round(performance.now() - started) };
    } catch {
        return { Status: 0, Body: null, FetchMs: Math.round(performance.now() - started) };
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
