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
    // decoder (Chrome on Android), otherwise the vendored ZXing library (desktop
    // Chrome/Edge/Firefox, iOS Safari), lazy-loaded on first scan.
    // A session counter guards every await and the detect loop: stop() (or a newer
    // start()) bumps it, so a cancelled start releases the camera it just acquired and
    // a superseded loop exits instead of touching the new session's state.
    scanner: {
        stream: null,
        session: 0,
        zxingLoading: null,

        // Starts the camera + detection loop. Returns null on success or a
        // user-facing error message; on a hit calls dotnetRef.OnBarcodeDetected(value).
        start: async function (videoId, dotnetRef) {
            const session = ++this.session;
            this.teardown();
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
                : await this.runZxing(session, video, dotnetRef);
        },

        // High resolution + continuous focus give 1D decoding enough sharp pixels;
        // fixed-focus webcams at 640x480 rarely resolve the bars.
        cameraConstraints: function () {
            return {
                video: {
                    facingMode: { ideal: "environment" },
                    width: { ideal: 1920 },
                    height: { ideal: 1080 },
                    advanced: [{ focusMode: "continuous" }],
                },
                audio: false,
            };
        },

        // Acquires the camera onto the video element. Returns null on success (or when
        // superseded — caller re-checks the session) and an error message otherwise.
        openCamera: async function (session, video) {
            let stream;
            try {
                stream = await navigator.mediaDevices.getUserMedia(this.cameraConstraints());
            } catch (err) {
                return this.cameraError(err);
            }
            if (session !== this.session) {
                // Cancelled while the permission prompt / warm-up was pending.
                stream.getTracks().forEach(t => t.stop());
                return null;
            }

            this.stream = stream;
            video.srcObject = stream;
            try { await video.play(); } catch { /* interrupted by teardown */ }
            return null;
        },

        runNative: async function (session, video, detector, dotnetRef) {
            const err = await this.openCamera(session, video);
            if (err !== null || session !== this.session) return err;

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

        runZxing: async function (session, video, dotnetRef) {
            try {
                await this.loadZxing();
            } catch {
                return "Couldn't load the barcode scanner — type the number instead.";
            }
            if (session !== this.session) return null;

            const err = await this.openCamera(session, video);
            if (err !== null || session !== this.session) return err;

            const hints = new Map();
            hints.set(ZXing.DecodeHintType.POSSIBLE_FORMATS, [
                ZXing.BarcodeFormat.EAN_13,
                ZXing.BarcodeFormat.UPC_A,
                ZXing.BarcodeFormat.EAN_8,
                ZXing.BarcodeFormat.UPC_E,
            ]);
            // TRY_HARDER copes with the soft focus and low contrast of typical webcams.
            hints.set(ZXing.DecodeHintType.TRY_HARDER, true);
            const reader = new ZXing.MultiFormatReader();
            reader.setHints(hints);

            const region = document.createElement("canvas");
            const decodeCanvas = (canvas) => {
                try {
                    const source = new ZXing.HTMLCanvasElementLuminanceSource(canvas);
                    const bitmap = new ZXing.BinaryBitmap(new ZXing.HybridBinarizer(source));
                    return reader.decodeWithState(bitmap).getText();
                } catch { return null; /* nothing found in this attempt */ }
            };

            // Draws the band around the aiming line, optionally rotation-corrected and
            // upscaled, and tries to decode it. Decoding tolerates only a couple degrees
            // of tilt, so small angle corrections plus frame-to-frame hand jitter do the
            // rest; 2x magnification covers barcodes that are small in the frame.
            const decodeBand = (w, h, angleDeg, scale) => {
                const sx = Math.round(w * 0.10), sw = Math.round(w * 0.80);
                const sy = Math.round(h * 0.28), sh = Math.round(h * 0.44);
                region.width = sw * scale;
                region.height = sh * scale;
                const ctx = region.getContext("2d");
                ctx.setTransform(1, 0, 0, 1, 0, 0);
                ctx.translate(region.width / 2, region.height / 2);
                ctx.rotate(angleDeg * Math.PI / 180);
                ctx.drawImage(video, sx, sy, sw, sh, -region.width / 2, -region.height / 2, region.width, region.height);
                return decodeCanvas(region);
            };

            // Blurry frames can decode into a wrong-but-checksum-valid code, so a value
            // is only accepted once two attempts in a row agree on it.
            let candidate = null;
            const scan = async () => {
                if (session !== this.session) return;
                const w = video.videoWidth, h = video.videoHeight;
                if (w > 0 && h > 0) {
                    let text = null;
                    for (const [angle, scale] of [[0, 1], [-4, 1], [4, 1], [0, 2]]) {
                        text = decodeBand(w, h, angle, scale);
                        if (text !== null) break;
                        if (session !== this.session) return;
                    }

                    if (text !== null && session === this.session) {
                        if (text === candidate) {
                            this.stop();
                            await dotnetRef.invokeMethodAsync("OnBarcodeDetected", text);
                            return;
                        }
                        candidate = text;
                    }
                }
                if (session === this.session) setTimeout(scan, 100);
            };
            scan();
            return null;
        },

        loadZxing: function () {
            if (window.ZXing) return Promise.resolve();
            if (!this.zxingLoading) {
                this.zxingLoading = new Promise((resolve, reject) => {
                    const s = document.createElement("script");
                    s.src = "js/zxing.min.js";
                    s.onload = resolve;
                    s.onerror = () => { this.zxingLoading = null; reject(new Error("script load failed")); };
                    document.head.appendChild(s);
                });
            }
            return this.zxingLoading;
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
