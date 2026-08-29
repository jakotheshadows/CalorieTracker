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

            // When the new worker takes over, reload once so the page runs the new assets.
            navigator.serviceWorker.addEventListener("controllerchange", () => {
                if (this.refreshing) return;
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
            if (this.reg.waiting) {
                this.reg.waiting.postMessage("SKIP_WAITING");
                return true;
            }
            // New worker still downloading: queue the skip for when it finishes installing.
            const incoming = this.reg.installing;
            if (incoming) {
                incoming.addEventListener("statechange", () => {
                    if (this.reg.waiting) this.reg.waiting.postMessage("SKIP_WAITING");
                });
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
                a.download = "caltrack-frame-" + Date.now() + ".png";
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
            if (!this.waveWorker) {
                try { this.waveWorker = new Worker("js/upc-worker.js"); } catch { /* optional */ }
            }
            const wave = this.waveWorker;
            if (wave) {
                wave.onmessage = (ev) => {
                    waveBusy = false;
                    const { seq, result } = ev.data;
                    if (session !== this.session || seq !== waveSeq) return;
                    if (result && result.digits) {
                        // A decisively certified read (far ahead of every rival AND
                        // every visual confusion; observed misreads sit at ratio>=0.9,
                        // cousin<=0.99) skips the double-read: a second worker pass
                        // costs 10-15s and re-reads near-identical pixels, adding no
                        // real independence. Borderline reads still need agreement.
                        waveNullStreak = 0;
                        if (result.ratio <= 0.7 && result.cousinRatio >= 1.25) {
                            this.stop();
                            dotnetRef.invokeMethodAsync("OnBarcodeDetected", result.digits);
                            return;
                        }
                        accept(result.digits);
                    } else {
                        // Nothing decodable in view: don't grind the CPU re-analyzing
                        // an unchanged codeless scene at full tilt; back off further
                        // the longer nothing turns up.
                        waveNullStreak++;
                        waveCooldownUntil = Date.now() + Math.min(1500 * waveNullStreak, 6000);
                    }
                };
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
            const motionOf = (w, h) => {
                const bandH = Math.min(h, Math.max(96, Math.round(h * 0.2)));
                const y0 = (h - bandH) >> 1;
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
            const feedWave = (ctx2d, w, h) => {
                if (!wave) return;
                stillTicks = motionOf(w, h) < 3.5 ? stillTicks + 1 : 0;
                if (waveBusy || Date.now() < waveCooldownUntil) return;
                // Fall back to shaky frames only after 8s without any attempt, so a
                // trembling hand degrades to the old behavior instead of starving.
                if (stillTicks < 2 && Date.now() - lastWaveAt < 8000) return;
                const bandH = Math.min(h, Math.max(96, Math.round(h * 0.2)));
                const y0 = (h - bandH) >> 1;
                const band = ctx2d.getImageData(0, y0, w, bandH);
                waveBusy = true;
                waveSeq++;
                lastWaveAt = Date.now();
                wave.postMessage(
                    { seq: waveSeq, width: band.width, height: band.height, buffer: band.data.buffer },
                    [band.data.buffer]);
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
                        feedWave(ctx, w, h);
                        this.updateBox(video, ctx, w, h);
                        const image = ctx.getImageData(0, 0, w, h);
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

        // Live visual feedback: outline the located barcode area over the preview,
        // independent of (much slower) decoding — the user can tell "it sees the
        // code" from "it can't read it yet" and adjust distance instead of guessing.
        updateBox: function (video, ctx2d, w, h) {
            const U = window.UpcWaveform;
            const box = video && video.parentElement ? video.parentElement.querySelector(".scan-box") : null;
            if (!U || !box) return;
            let cands = null;
            try {
                const bandH = Math.min(h, Math.max(96, Math.round(h * 0.2)));
                const y0 = (h - bandH) >> 1;
                const band = ctx2d.getImageData(0, y0, w, bandH);
                cands = U.locate(band, 0, band.height);
                if (cands && cands.length) {
                    const c = cands[0];
                    // Map capture coords onto the displayed element (object-fit: cover).
                    const dispW = video.clientWidth, dispH = video.clientHeight;
                    if (dispW > 0 && dispH > 0) {
                        const scale = Math.max(dispW / w, dispH / h);
                        const offX = (dispW - w * scale) / 2, offY = (dispH - h * scale) / 2;
                        box.style.left = (c.xl * scale + offX) + "px";
                        box.style.width = ((c.xr - c.xl) * scale) + "px";
                        box.style.top = (y0 * scale + offY) + "px";
                        box.style.height = (bandH * scale) + "px";
                        // Tilt the box to the measured bar angle (slope = dx/dy;
                        // CSS +deg is clockwise, which corresponds to negative slope).
                        box.style.transform = "rotate(" + (-Math.atan(c.slope || 0) * 180 / Math.PI).toFixed(1) + "deg)";
                        box.classList.remove("hidden");
                        this.boxSeenAt = Date.now();
                    }
                }
            } catch { /* overlay is best-effort */ }
            // Linger briefly so the box doesn't flicker on borderline frames.
            if ((!cands || !cands.length) && Date.now() - this.boxSeenAt > 600) box.classList.add("hidden");
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
