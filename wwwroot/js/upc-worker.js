// Web Worker for the UPC waveform decoder: model-fitting decode of large-but-blurry
// barcodes (webcams can't focus close-held items) without janking the UI thread.
// In: {seq, width, height, buffer} — RGBA pixels of a horizontal band around the scan
// line. Out: {seq, result} where result is {digits, ratio, cousinRatio, mwPx} or null
// (ratio/cousinRatio are the certification margins — app.js uses them to decide
// whether a read is decisive enough to accept without a second agreeing read).
importScripts("upc-waveform.js");

self.onmessage = (ev) => {
    const { seq, width, height, buffer } = ev.data;
    let result = null;
    try {
        const img = { width, height, data: new Uint8ClampedArray(buffer) };
        result = UpcWaveform.scanBand(img, 0, height, { budgetMs: 14000 });
    } catch { /* a bad frame must not kill the worker */ }
    self.postMessage({ seq, result });
};
