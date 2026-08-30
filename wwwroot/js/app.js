// Interop helpers for CalTrack: localStorage and file download.
window.calTracker = {
    storage: {
        get: (key) => localStorage.getItem(key),
        set: (key, value) => localStorage.setItem(key, value),
        remove: (key) => localStorage.removeItem(key),
    },

    // Service-worker update handling: detect a freshly installed new version,
    // tell Blazor so it can show an "update available" banner, and force-activate on request.
    updates: {
        reg: null,
        dotnetRef: null,
        refreshing: false,
        pendingReload: false,
        controllerChanged: false,

        init: async function (dotnetRef) {
            if (!("serviceWorker" in navigator)) return;
            this.dotnetRef = dotnetRef;
            this.reg = await navigator.serviceWorker.ready.catch(() => null);
            if (!this.reg) return;

            // A new version may already be parked and waiting from a previous session.
            if (this.reg.waiting) this.notify();

            this.reg.addEventListener("updatefound", () => {
                const incoming = this.reg.installing;
                if (!incoming) return;
                incoming.addEventListener("statechange", () => {
                    // "installed" with an existing controller = an update, not the first install.
                    if (incoming.state === "installed" && navigator.serviceWorker.controller) this.notify();
                });
            });

            // Reload once when the new worker takes over — but ONLY if this tab asked
            // for it (applyUpdate). controllerchange also fires on activations we did
            // not initiate — DevTools' "Update on reload" force-activates on every
            // load, which made an unconditional reload here loop forever (and killed
            // in-progress barcode scans). An external activation is noted instead, so
            // a later "Update now" click can finish the job with a reload.
            navigator.serviceWorker.addEventListener("controllerchange", () => {
                if (!this.pendingReload || this.refreshing) {
                    this.controllerChanged = true;
                    return;
                }
                this.refreshing = true;
                window.location.reload();
            });

            // Installed PWAs can stay open for days: poll hourly and when re-focused.
            setInterval(() => this.reg.update().catch(() => { }), 60 * 60 * 1000);
            document.addEventListener("visibilitychange", () => {
                if (!document.hidden) this.reg.update().catch(() => { });
            });
        },

        notify: function () {
            if (this.dotnetRef) this.dotnetRef.invokeMethodAsync("OnUpdateAvailable");
        },

        // Returns true if an update is downloading/waiting, false if up to date, null if the check failed.
        checkNow: async function () {
            if (!this.reg) return null;
            try {
                await this.reg.update();
                return !!(this.reg.waiting || this.reg.installing);
            } catch {
                return null;
            }
        },

        // Returns true if a force-update was started (page will reload via controllerchange).
        applyUpdate: function () {
            if (!this.reg) return false;
            // Armed only at the moment the skip is actually sent: a stale arm on a
            // worker that never activates would re-enable the unconditional external
            // reload the pendingReload gate exists to prevent.
            const skip = (w) => { this.pendingReload = true; w.postMessage("SKIP_WAITING"); };
            if (this.reg.waiting) {
                skip(this.reg.waiting);
                return true;
            }
            // New worker still downloading: queue the skip for when it finishes installing.
            const incoming = this.reg.installing;
            if (incoming) {
                incoming.addEventListener("statechange", () => {
                    if (incoming.state === "redundant") {
                        // Install failed (bad network, mid-deploy hash mismatch):
                        // disarm and put the banner's buttons back for a retry.
                        this.pendingReload = false;
                        if (this.dotnetRef) this.dotnetRef.invokeMethodAsync("OnUpdateFailed");
                    } else if (this.reg.waiting) {
                        skip(this.reg.waiting);
                    }
                });
                return true;
            }
            // Nothing waiting or installing, but the controller changed under us: the
            // update was already activated elsewhere (another tab, DevTools) while this
            // tab kept running the old assets — finish it with the reload ourselves.
            if (this.controllerChanged) {
                this.refreshing = true;
                window.location.reload();
                return true;
            }
            return false;
        },
    },

    getVersion: () =>
        document.querySelector('meta[name="app-version"]')?.content || "dev",

    // Camera barcode scanning: the native BarcodeDetector API where the browser has a
    // decoder (Chrome on Android), otherwise the vendored zxing-wasm reader (the C++
    // ZXing rewrite — far stronger on blurry/tilted webcam frames than the old JS port),
    // lazy-loaded on first scan.
    // A session counter guards every await and the detect loop: stop() (or a newer
    // start()) bumps it, so a cancelled start releases the camera it just acquired and
    // a superseded loop exits instead of touching the new session's state.
    scanner: {
        stream: null,
        video: null,
        session: 0,
        wasmLoading: null,
        waveWorker: null,
        deviceId: null,
        locLoading: false,
        boxSeenAt: 0,

        // Starts the camera + detection loop. Returns null on success or a
        // user-facing error message; on a hit calls dotnetRef.OnBarcodeDetected(value).
        // deviceId (optional) picks a specific camera — desktops often have virtual
        // cameras (OBS etc.) that the browser may grab by default.
        start: async function (videoId, dotnetRef, deviceId) {
            const session = ++this.session;
            this.teardown();
            this.deviceId = deviceId || null;
            this.track = null; // a region tracked on the old camera/session is meaningless now
            this.detectCache = null;
            const video = document.getElementById(videoId);
            if (!video) return "Scanner video element not found.";

            // Pick the decoder before touching the camera.
            let detector = null;
            if ("BarcodeDetector" in window) {
                try {
                    const supported = await BarcodeDetector.getSupportedFormats();
                    const formats = ["ean_13", "upc_a", "ean_8", "upc_e"].filter(f => supported.includes(f));
                    if (formats.length > 0) detector = new BarcodeDetector({ formats });
                } catch { /* fall through to ZXing */ }
            }
            if (session !== this.session) return null;

            return detector
                ? await this.runNative(session, video, detector, dotnetRef)
                : await this.runWasm(session, video, dotnetRef);
        },

        // Ask for the camera's maximum resolution (ideal 4K pulls whatever the sensor
        // can do; a 1080p cam still yields 1080p). Pixels per bar module are the
        // decisive factor: a small bottle's barcode at arm's length is undecodable at
        // 1080p but fine at 4K, which is how commercial scanner demos win.
        cameraConstraints: function () {
            const video = {
                width: { ideal: 3840 },
                height: { ideal: 2160 },
                advanced: [{ focusMode: "continuous" }],
            };
            if (this.deviceId) video.deviceId = { exact: this.deviceId };
            else video.facingMode = { ideal: "environment" };
            return { video, audio: false };
        },

        // Video input devices, for the in-panel camera picker (labels are available
        // once camera permission has been granted).
        listCameras: async function () {
            try {
                const devs = await navigator.mediaDevices.enumerateDevices();
                return devs.filter(d => d.kind === "videoinput")
                    .map((d, i) => ({ id: d.deviceId, label: d.label || ("Camera " + (i + 1)) }));
            } catch {
                return [];
            }
        },

        // Tells Blazor the negotiated capture resolution (shown in the panel so a
        // wrong device/mode is visible instead of silently degrading decoding).
        notifyCameraReady: function (dotnetRef, video) {
            try { dotnetRef.invokeMethodAsync("OnCameraReady", video.videoWidth, video.videoHeight); } catch { /* UI-only */ }
        },

        // Acquires the camera onto the video element. Returns null on success (or when
        // superseded — caller re-checks the session) and an error message otherwise.
        openCamera: async function (session, video) {
            let stream;
            try {
                stream = await navigator.mediaDevices.getUserMedia(this.cameraConstraints());
            } catch (err) {
                if (this.deviceId) {
                    // The remembered camera is gone (unplugged, id rotated): fall
                    // back to the default device instead of failing the scan.
                    this.deviceId = null;
                    try { stream = await navigator.mediaDevices.getUserMedia(this.cameraConstraints()); }
                    catch (err2) { return this.cameraError(err2); }
                } else {
                    return this.cameraError(err);
                }
            }
            if (session !== this.session) {
                // Cancelled while the permission prompt / warm-up was pending.
                stream.getTracks().forEach(t => t.stop());
                return null;
            }

            this.stream = stream;
            this.video = video;
            video.srcObject = stream;
            try { await video.play(); } catch { /* interrupted by teardown */ }
            return null;
        },

        // Downloads the current camera frame as a PNG (stays on the user's machine).
        // Lets a user hand over a frame the scanner fails on, so decoding can be
        // tuned against real failures instead of guesses.
        saveFrame: function () {
            const video = this.video;
            if (!video || !video.videoWidth) return false;
            const c = document.createElement("canvas");
            c.width = video.videoWidth;
            c.height = video.videoHeight;
            c.getContext("2d").drawImage(video, 0, 0);
            c.toBlob((blob) => {
                if (!blob) return;
                const url = URL.createObjectURL(blob);
                const a = document.createElement("a");
                a.href = url;
                // The pixels stay pristine (overlays would corrupt decoder tests on
                // the frame), but the CURRENT detection state rides along in the
                // filename so the box the app showed at capture time is known.
                let tag = "";
                const lc = this.lastChoice;
                if (lc && Date.now() - (this.lastChoiceAt || 0) < 2000) {
                    // Tracked coords are smoothed floats now — round for the filename.
                    tag = "-box" + Math.round(lc.xl) + "x" + Math.round(lc.xr) +
                        "y" + Math.round(lc.by1) + "-" + Math.round(lc.by2) +
                        "s" + (lc.slope || 0).toFixed(2).replace("-", "n") +
                        (lc.score !== undefined ? "sc" + Math.round(lc.score) : "");
                }
                a.download = "caltrack-frame-" + Date.now() + tag + ".png";
                document.body.appendChild(a);
                a.click();
                a.remove();
                URL.revokeObjectURL(url);
            }, "image/png");
            return true;
        },

        runNative: async function (session, video, detector, dotnetRef) {
            const err = await this.openCamera(session, video);
            if (err !== null || session !== this.session) return err;
            this.notifyCameraReady(dotnetRef, video);

            const scan = async () => {
                if (session !== this.session) return;
                try {
                    const codes = await detector.detect(video);
                    if (session === this.session && codes.length > 0) {
                        const value = codes[0].rawValue;
                        this.stop();
                        await dotnetRef.invokeMethodAsync("OnBarcodeDetected", value);
                        return;
                    }
                } catch { /* frame not ready yet */ }
                if (session === this.session) setTimeout(scan, 150);
            };
            scan();
            return null;
        },

        runWasm: async function (session, video, dotnetRef) {
            let zxing;
            try {
                zxing = await this.loadWasmReader();
            } catch {
                return "Couldn't load the barcode scanner — type the number instead.";
            }
            if (session !== this.session) return null;

            const err = await this.openCamera(session, video);
            if (err !== null || session !== this.session) return err;
            this.notifyCameraReady(dotnetRef, video);
            this.loadLocator();

            const frame = document.createElement("canvas");
            const ctx = frame.getContext("2d", { willReadFrequently: true });
            const baseOpts = {
                formats: ["EAN-13", "UPC-A", "EAN-8", "UPC-E"],
                tryHarder: true,
                tryRotate: true,
                tryInvert: true,
            };
            // The default LocalAverage binarizer handles uneven real-world lighting;
            // FixedThreshold rescues soft, low-contrast frames it gives up on.
            const binarizers = ["LocalAverage", "FixedThreshold"];

            // Blurry frames can decode into a wrong-but-checksum-valid code, so a value
            // is only accepted once two reads agree on it (zxing ticks and waveform
            // results share the pool, so cross-method agreement counts too).
            let candidate = null;
            const accept = async (text) => {
                if (session !== this.session) return;
                if (text === candidate) {
                    this.stop();
                    await dotnetRef.invokeMethodAsync("OnBarcodeDetected", text);
                    return;
                }
                candidate = text;
            };

            // Waveform side-channel: zxing binarizes, so it fails on close-held codes a
            // fixed-focus webcam can't focus (big modules, defocus blur). The model-
            // fitting decoder handles exactly that; it runs in a worker off-thread and
            // is fed the scan-line band whenever it's idle. Its own gates (>=2 px/module,
            // decisive-margin) plus the double-read rule guard against misreads.
            let waveBusy = false, waveSeq = 0, waveCooldownUntil = 0, waveNullStreak = 0;
            let fastBusy = false, fastSeq = 0, fastCooldownUntil = 0, fastDead = false;
            // Live status line: the deep decode takes tens of seconds, so without
            // feedback "working on it" and "seeing nothing" look identical. Stages
            // are pushed to Blazor only when they change.
            let lastStatusKey = "", noreadUntil = 0, analyzingN = 0, waveDead = false;
            const status = (stage, n) => {
                const key = stage + ":" + n;
                if (key === lastStatusKey || session !== this.session) return;
                lastStatusKey = key;
                try { dotnetRef.invokeMethodAsync("OnScanStatus", stage, n).catch(() => { }); } catch { /* teardown race */ }
            };
            // TWO workers: "fast" runs a small-budget single-frame attempt on the
            // tracked region continuously (easy codes decode in a couple of seconds,
            // Dynamsoft-style), while "wave" runs the deep multi-frame bursts that
            // crack the heavy-blur frames no single frame can. Independent workers,
            // so a 25s burst never blocks the quick attempts.
            if (!this.waveWorker) {
                try { this.waveWorker = new Worker("js/upc-worker.js"); } catch { /* optional */ }
            }
            if (!this.fastWorker) {
                try { this.fastWorker = new Worker("js/upc-worker.js"); } catch { /* optional */ }
            }
            const wave = this.waveWorker;
            const fastW = this.fastWorker;
            const acceptDecode = (digits) => {
                waveNullStreak = 0;
                this.stop();
                dotnetRef.invokeMethodAsync("OnBarcodeDetected", digits);
            };
            if (wave) {
                wave.onmessage = (ev) => {
                    waveBusy = false;
                    const { seq, result } = ev.data;
                    if (session !== this.session || seq !== waveSeq) return;
                    if (result && result.digits) {
                        // A burst result already carries multi-frame agreement on top
                        // of the certification gates — accept directly.
                        acceptDecode(result.digits);
                    } else {
                        // Nothing decodable in view: don't grind the CPU re-analyzing
                        // an unchanged codeless scene at full tilt; back off further
                        // the longer nothing turns up.
                        waveNullStreak++;
                        waveCooldownUntil = Date.now() + Math.min(1500 * waveNullStreak, 6000);
                        // The "adjust distance/angle" hint covers exactly the deliberate
                        // idle window — flipping back to "hold still" mid-cooldown would
                        // coach the user to freeze on the framing that just failed.
                        noreadUntil = waveCooldownUntil;
                        // Several deep passes refused the tracked region: stop defending
                        // it — dropping the track lets a rival region win the box and
                        // the next burst, instead of dead-locking on (say) print.
                        if (waveNullStreak >= 3) this.track = null;
                    }
                };
                // A worker that can't load (stale SW cache, deploy mismatch) or dies
                // still fires 'error'; without this, waveBusy would stick and the
                // status line would promise an analysis that will never happen.
                wave.onerror = () => { waveDead = true; waveBusy = false; };
            }
            if (fastW) {
                fastW.onmessage = (ev) => {
                    fastBusy = false;
                    const { seq, result } = ev.data;
                    if (session !== this.session || seq !== fastSeq) return;
                    if (result && result.digits && !result.refused) acceptDecode(result.digits);
                    else fastCooldownUntil = Date.now() + 900;
                };
                fastW.onerror = () => { fastDead = true; fastBusy = false; };
            }
            // Stillness gate: hand motion double-exposes the bars (measured ~8px
            // ghosting on a real frame — no blur model fits a double image), and the
            // deep decoder gets one shot every several seconds. Spend it on a frame
            // captured while the hand was still: mean per-pixel band difference
            // between ticks, low for two consecutive ticks.
            const motion = document.createElement("canvas");
            motion.width = 96;
            motion.height = 24;
            const mctx = motion.getContext("2d", { willReadFrequently: true });
            let lastBand = null, stillTicks = 0, lastWaveAt = Date.now();
            const motionOf = (w, h, y0, bandH) => {
                mctx.drawImage(frame, 0, y0, w, bandH, 0, 0, 96, 24);
                const d = mctx.getImageData(0, 0, 96, 24).data;
                let diff = 0;
                if (lastBand) {
                    for (let i = 0; i < d.length; i += 4) diff += Math.abs(d[i] - lastBand[i >> 2]);
                    diff /= 96 * 24;
                } else {
                    diff = 999;
                }
                if (!lastBand) lastBand = new Float64Array(96 * 24);
                for (let i = 0; i < d.length; i += 4) lastBand[i >> 2] = d[i];
                return diff;
            };
            // MULTI-FRAME collection: every tick with a detected code, the band
            // around it is added to a rolling burst (motion-gated loosely — frames
            // with moderate shake still carry evidence, and their DIFFERENT ghost
            // offsets are precisely what fusion averages through). When enough
            // frames are banked and the hand settles, the whole burst goes to the
            // worker, which decodes it as one joint problem: a single frame at
            // sigma/mw ~1.5 is ambiguous, N frames under different noise are not.
            let burstBuf = [];
            const bandCut = (image, w, h, choice) => {
                const bandH = Math.min(h, Math.max(96, Math.round(h * 0.2)));
                const y0 = Math.max(0, Math.min(h - bandH, Math.round((choice.by1 + choice.by2) / 2 - bandH / 2)));
                return {
                    y0,
                    meta: {
                        width: w, height: bandH,
                        buffer: new Uint8ClampedArray(image.data.subarray(y0 * w * 4, (y0 + bandH) * w * 4)).buffer,
                        xl: choice.xl, xr: choice.xr, mwEst: choice.mwEst,
                        slope: choice.slope || 0,
                        by1: choice.by1 - y0, by2: choice.by2 - y0,
                    },
                };
            };
            const feedWave = (image, w, h, choice) => {
                if ((!wave || waveDead) && (!fastW || fastDead)) return;
                if (!choice) return;
                const bandH = Math.min(h, Math.max(96, Math.round(h * 0.2)));
                const y0m = Math.max(0, Math.min(h - bandH, Math.round((choice.by1 + choice.by2) / 2 - bandH / 2)));
                const m = motionOf(w, h, y0m, bandH);
                stillTicks = m < 3.5 ? stillTicks + 1 : 0;
                // FAST attempt: continuous small-budget single-frame decodes on the
                // tracked region — the "Dynamsoft path". Fires as soon as the tracker
                // has held a region for a few ticks; sharp/moderate frames decode in
                // ~2s without waiting for burst collection or stillness.
                if (fastW && !fastDead && !fastBusy && Date.now() >= fastCooldownUntil
                    && choice.age >= 3 && choice.missTicks === 0 && m < 8) {
                    const cut = bandCut(image, w, h, choice);
                    fastBusy = true;
                    fastSeq++;
                    fastW.postMessage({ seq: fastSeq, fast: true, ...cut.meta }, [cut.meta.buffer]);
                }
                if (!wave || waveDead) return;
                if (m < 8) {
                    burstBuf.push(bandCut(image, w, h, choice).meta);
                    if (burstBuf.length > 10) burstBuf.shift();
                }
                if (waveBusy || Date.now() < waveCooldownUntil) return;
                if (burstBuf.length < 4) return;
                // Send once the hand settles; after 6s without any attempt send
                // whatever is banked, so a trembling hand degrades instead of starving.
                if (stillTicks < 2 && Date.now() - lastWaveAt < 6000) return;
                const burst = burstBuf;
                burstBuf = [];
                waveBusy = true;
                waveSeq++;
                lastWaveAt = Date.now();
                analyzingN = burst.length;
                wave.postMessage({ seq: waveSeq, burst }, burst.map(b => b.buffer));
            };

            const scan = async () => {
                if (session !== this.session) return;
                const w = video.videoWidth, h = video.videoHeight;
                if (w > 0 && h > 0) {
                    let text = null;
                    try {
                        frame.width = w;
                        frame.height = h;
                        ctx.drawImage(video, 0, 0);
                        // Detection + worker feed run on the pristine frame, BEFORE
                        // the red-channel pass below mutates it in place.
                        const image = ctx.getImageData(0, 0, w, h);
                        const choice = this.updateDetection(video, image, w, h);
                        feedWave(image, w, h, choice);
                        // Wedged-worker watchdog: no reply long past the 25s budget →
                        // stop claiming "analyzing" and allow a re-post (a stale reply
                        // arriving later is dropped by the seq check).
                        if (waveBusy && Date.now() - lastWaveAt > 60000) waveBusy = false;
                        if (waveBusy) status("analyzing", analyzingN);
                        else if (fastBusy) status("reading", 0);
                        else if (Date.now() < noreadUntil) status("noread", 0);
                        else if (choice && wave && !waveDead) status("locked", burstBuf.length);
                        else status("searching", 0);
                        decode: for (const red of [false, true]) {
                            if (red) {
                                // Red-channel pass: colored inks (blue bars on bottles are
                                // common) are dark in the red channel but washed out in
                                // luminance grayscale — same trick as red-laser scanners.
                                const d = image.data;
                                for (let i = 0; i < d.length; i += 4) d[i + 1] = d[i + 2] = d[i];
                            }
                            for (const binarizer of binarizers) {
                                const results = await zxing.readBarcodes(image, { ...baseOpts, binarizer });
                                const hit = results.find(r => r.isValid && r.text);
                                if (hit) { text = hit.text; break decode; }
                                if (session !== this.session) return;
                            }
                        }
                    } catch { /* decoder hiccup on this frame */ }

                    if (text !== null) await accept(text);
                }
                if (session === this.session) setTimeout(scan, 120);
            };
            scan();
            return null;
        },

        // The decoder module doubles as the barcode LOCATOR; on the main thread it
        // powers the orange found-a-barcode box (locate() itself costs ~2ms/frame).
        loadLocator: function () {
            if (window.UpcWaveform || this.locLoading) return;
            this.locLoading = true;
            const s = document.createElement("script");
            s.src = "js/upc-waveform.js";
            s.onerror = () => { this.locLoading = false; };
            document.head.appendChild(s);
        },

        // Full-frame barcode detection each tick (the code is wherever the user
        // holds it, not on a center line): overlapping bands are located cheaply,
        // candidates ranked by crossings x vertical self-similarity (bars repeat
        // down their height, text lines do not), and the current leader is verified
        // against actual UPC structure (quickFit, ~150ms) at most every 600ms with
        // cached results. Returns the chosen candidate for the box + the worker.
        detectCache: null,
        lastQuickFitAt: 0,
        updateDetection: function (video, image, w, h) {
            const U = window.UpcWaveform;
            if (!U) return null;
            if (!this.detectCache) this.detectCache = new Map();
            let pool = [];
            const bandH = Math.min(h, Math.max(96, Math.round(h * 0.2)));
            const step = Math.max(48, bandH >> 1);
            try {
                for (let y0 = 0; y0 + bandH <= h; y0 += step) {
                    const cands = U.locate(image, y0, y0 + bandH);
                    if (cands)
                        // Keep all of a band's candidates: a real code has ranked
                        // THIRD in its band behind wider merged spans (measured).
                        for (const c of cands) {
                            c.y0 = y0;
                            c.y1 = y0 + bandH;
                            pool.push(c);
                        }
                }
            } catch { return null; }
            const box = video && video.parentElement ? video.parentElement.querySelector(".scan-box") : null;
            if (!pool.length) {
                // No candidates this tick — the tracker still gets to ride out its
                // short grace window (locate flickers on real frames).
                const kept = this.updateTrack([], Date.now());
                if (kept && box) this.drawBox(video, box, kept, w, h);
                else if (box && Date.now() - this.boxSeenAt > 600) box.classList.add("hidden");
                this.lastChoice = kept;
                this.lastChoiceAt = Date.now();
                return kept;
            }
            pool.sort((a, b) => b.crossings - a.crossings);
            pool = pool.slice(0, 8);
            const now = Date.now();
            for (const c of pool) {
                try {
                    const br = U.barRows(image, c, c.y0, c.y1);
                    c.by1 = br[0];
                    c.by2 = br[1];
                    c.corr = br[2] === undefined ? 0.5 : br[2];
                } catch { c.by1 = c.y0; c.by2 = c.y1; c.corr = 0.3; }
                c.score = c.crossings * Math.max(0.15, c.corr);
                c.key = c.y0 + ":" + (c.xl >> 5) + ":" + ((c.xr - c.xl) >> 5);
                const v = this.detectCache.get(c.key);
                if (v && now - v.t < 2500) c.pre = v.pre;
            }
            // Verify the best UNVERIFIED candidate each slot. quickFit's structural
            // cost is a JUNK VETO only, never a preference: under real blur a text
            // line fits the guard model cheaper than the true code (measured on every
            // saved corpus frame — the old lowest-pre-wins rule locked the box onto
            // print and sent whole decode budgets into it).
            if (now - this.lastQuickFitAt > 600) {
                const target = pool
                    .filter(c => c.pre === undefined)
                    .reduce((a, b) => (a && a.score >= b.score ? a : b), null);
                if (target) {
                    this.lastQuickFitAt = now;
                    try {
                        const q = U.quickFit(image, target, target.y0, target.y1);
                        this.detectCache.set(target.key, { pre: q.pre, t: now });
                        target.pre = q.pre;
                    } catch { /* verification is best-effort */ }
                }
            }
            pool = pool.filter(c => c.pre === undefined || c.pre < 3);
            if (this.detectCache.size > 40)
                for (const [k, v] of this.detectCache) if (now - v.t > 4000) this.detectCache.delete(k);
            const choice = this.updateTrack(pool, now);
            if (choice && box) this.drawBox(video, box, choice, w, h);
            else if (box && Date.now() - this.boxSeenAt > 600) box.classList.add("hidden");
            // For saveFrame: what the box was showing at capture time.
            this.lastChoice = choice;
            this.lastChoiceAt = now;
            return choice;
        },

        // Temporal tracker over the per-tick candidate pools: per-tick winners are
        // noisy (crossing counts flicker with motion), so the box used to jump all
        // over the frame. The incumbent region is kept while any candidate overlaps
        // it, its geometry eased toward the fresh measurement, and a challenger has
        // to out-score it decisively for several consecutive ticks to take over.
        track: null,
        updateTrack: function (pool, now) {
            const t = this.track;
            const overlaps = (c) => {
                if (!t) return false;
                const ox = Math.min(c.xr, t.xr) - Math.max(c.xl, t.xl);
                const oy = Math.min(c.by2, t.by2) - Math.max(c.by1, t.by1);
                return ox > 0.5 * Math.min(c.xr - c.xl, t.xr - t.xl) && oy > -40;
            };
            const top = pool.reduce((a, b) => (a && a.score >= b.score ? a : b), null);
            let match = null;
            for (const c of pool) if (overlaps(c) && (!match || c.score > match.score)) match = c;
            if (t && match) {
                // Ease toward the fresh measurement: kills pixel jitter without
                // lagging real hand movement by more than a tick or two.
                const e = 0.45;
                t.xl += (match.xl - t.xl) * e;
                t.xr += (match.xr - t.xr) * e;
                t.by1 += (match.by1 - t.by1) * e;
                t.by2 += (match.by2 - t.by2) * e;
                t.slope += ((match.slope || 0) - t.slope) * e;
                t.mwEst = match.mwEst;
                t.score = 0.7 * t.score + 0.3 * match.score;
                t.missTicks = 0;
                t.age++;
                if (top && top !== match && top.score > 1.35 * t.score) {
                    t.challengeTicks = (t.challengeTicks || 0) + 1;
                    if (t.challengeTicks >= 3) this.track = this.newTrack(top, now);
                } else t.challengeTicks = 0;
            } else if (t && t.missTicks < 4) {
                // Locate flickers on real frames; keep the region briefly so the
                // burst buffer isn't starved by one empty tick.
                t.missTicks++;
                t.age++;
            } else {
                this.track = top ? this.newTrack(top, now) : null;
            }
            return this.track;
        },
        newTrack: function (c, now) {
            return {
                xl: c.xl, xr: c.xr, by1: c.by1, by2: c.by2,
                slope: c.slope || 0, mwEst: c.mwEst, score: c.score,
                missTicks: 0, challengeTicks: 0, age: 1, bornAt: now,
            };
        },

        // Snug orange box over the chosen candidate (bar rows only), tilted to the
        // measured bar angle (slope = dx/dy; CSS +deg is clockwise = negative slope).
        drawBox: function (video, box, c, w, h) {
            const dispW = video.clientWidth, dispH = video.clientHeight;
            if (dispW <= 0 || dispH <= 0) return;
            const scale = Math.max(dispW / w, dispH / h);
            const offX = (dispW - w * scale) / 2, offY = (dispH - h * scale) / 2;
            box.style.left = (c.xl * scale + offX) + "px";
            box.style.width = ((c.xr - c.xl) * scale) + "px";
            box.style.top = (c.by1 * scale + offY) + "px";
            box.style.height = ((c.by2 - c.by1) * scale) + "px";
            box.style.transform = "rotate(" + (-Math.atan(c.slope || 0) * 180 / Math.PI).toFixed(1) + "deg)";
            box.classList.remove("hidden");
            this.boxSeenAt = Date.now();
        },

        loadWasmReader: function () {
            if (!this.wasmLoading) {
                // The ES module resolves zxing_reader.wasm next to itself in js/.
                this.wasmLoading = import("./zxing-wasm-reader.js").catch(err => {
                    this.wasmLoading = null;
                    throw err;
                });
            }
            return this.wasmLoading;
        },

        cameraError: function (err) {
            return err && err.name === "NotAllowedError"
                ? "Camera permission was denied — allow it in your browser, or type the number instead."
                : "Couldn't open the camera — type the number instead.";
        },

        stop: function () {
            this.session++;
            this.teardown();
        },

        teardown: function () {
            if (this.stream) {
                this.stream.getTracks().forEach(t => t.stop());
                this.stream = null;
            }
            this.video = null;
            if (this.waveWorker) {
                this.waveWorker.terminate();
                this.waveWorker = null;
            }
            if (this.fastWorker) {
                this.fastWorker.terminate();
                this.fastWorker = null;
            }
        },
    },

    // PWA install helper: same walkthrough-based experience in every browser.
    install: {
        // True when running as an installed app (its own window / home-screen launch).
        isStandalone: function () {
            return window.matchMedia("(display-mode: standalone)").matches || window.navigator.standalone === true;
        },

        // Which walkthrough fits this device/browser.
        platform: function () {
            const ua = navigator.userAgent;
            const ios = /iPhone|iPad|iPod/.test(ua) ||
                (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
            if (ios) return "ios";
            if (/Android/.test(ua)) return "android";
            return /Firefox/.test(ua) ? "firefox" : "desktop";
        },

        getState: function () {
            return { standalone: this.isStandalone(), platform: this.platform() };
        },
    },

    downloadFile: (fileName, content) => {
        const blob = new Blob([content], { type: "application/json" });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    },
};
