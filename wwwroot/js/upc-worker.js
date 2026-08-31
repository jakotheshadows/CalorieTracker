// Web Worker for the UPC waveform decoder: model-fitting decode of large-but-blurry
// barcodes (webcams can't focus close-held items) without janking the UI thread.
// Three message kinds:
//   { seq, fast, width, height, buffer, xl, xr, mwEst, slope, by1, by2 }
//     — FAST single-band attempt (top tracked candidate only, small budget): the
//       live scanner sends these continuously so easy frames decode in seconds.
//   { seq, width, height, buffer }         — single band, deep (legacy path)
//   { seq, burst: [{ width, height, buffer, xl, xr, mwEst, slope, by1, by2 }...] }
//     — MULTI-FRAME burst: decoded jointly, each frame under its own fitted blur
//       physics; the true code is the only string consistent across frames.
// Out: { seq, result } where result is {digits, ratio, cousinRatio, mwPx, frames?}
// or null (ratio/cousinRatio are certification margins).
importScripts("upc-waveform.js");

self.onmessage = (ev) => {
    const msg = ev.data;
    let result = null;
    try {
        if (msg.burst) {
            const bands = msg.burst.map(b => ({
                img: { width: b.width, height: b.height, data: new Uint8ClampedArray(b.buffer) },
                xl: b.xl, xr: b.xr, cxl: b.cxl, mwEst: b.mwEst, slope: b.slope, by1: b.by1, by2: b.by2,
            }));
            result = UpcWaveform.scanBurst(bands, { budgetMs: 25000 });
        } else {
            const img = { width: msg.width, height: msg.height, data: new Uint8ClampedArray(msg.buffer) };
            // Reuse the region the scanner already tracked and verified, instead of
            // re-locating inside the band and possibly landing on different clutter.
            // The scanner's ranking is good but not infallible, so it hands over its
            // best few regions; scanBand already knows how to try them in order.
            const cands = msg.cands && msg.cands.length ? msg.cands : undefined;
            result = UpcWaveform.scanBand(img, 0, msg.height, msg.fast
                ? { fast: true, budgetMs: 4500, cands }
                : { budgetMs: 14000, cands });
        }
    } catch { /* a bad frame must not kill the worker */ }
    self.postMessage({ seq: msg.seq, result });
};
