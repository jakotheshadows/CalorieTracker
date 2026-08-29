// Probe the SUITE's exact failing cases (clutter + suite RNG sequence) via scanBand
// with debug passthrough. Reuses closeblur's synth by re-implementing it verbatim.
const fs = require('fs');
const vm = require('vm');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

let seed = 42;
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const noise = (amp) => (rnd() + rnd() + rnd() - 1.5) * amp;
function synthBand(o) {
    const W = o.width || 1280, H = o.height || 96;
    const row = new Float64Array(W).fill(235);
    if (o.digits) {
        const mods = U.buildModules(o.digits);
        const seq = o.reversed ? [...mods].reverse().join('') : mods;
        for (let x = 0; x < W; x++) {
            let cov = 0;
            const k0 = Math.floor((x - o.xoff) / o.mw), k1 = Math.floor((x + 1 - o.xoff) / o.mw);
            for (let k = k0; k <= k1; k++) {
                if (k < 0 || k >= 95 || seq[k] !== '1') continue;
                const a = Math.max(x, o.xoff + k * o.mw), b = Math.min(x + 1, o.xoff + (k + 1) * o.mw);
                if (b > a) cov += b - a;
            }
            row[x] = 235 - cov * 210;
        }
    }
    const codeL = o.digits ? o.xoff - 30 : W, codeR = o.digits ? o.xoff + 95 * o.mw + 30 : -1;
    const nClutter = o.clutter === undefined ? 6 : o.clutter;
    for (let c = 0; c < nClutter; c++) {
        const cx = rnd() * W;
        if (cx > codeL - 60 && cx < codeR + 60) continue;
        for (let s2 = 0; s2 < 8; s2++) {
            const sx = Math.round(cx + s2 * (3 + rnd() * 4)), sw = 1 + Math.round(rnd() * 2);
            for (let x = sx; x < Math.min(W, sx + sw); x++) row[x] = 40 + rnd() * 40;
        }
    }
    let cur = row;
    for (let pass = 0; pass < 3; pass++) {
        const out = new Float64Array(W);
        const r = o.blurR;
        let acc = 0, n = 0;
        for (let x = -r; x <= r; x++) { if (x >= 0 && x < W) { acc += cur[x]; n++; } }
        for (let x = 0; x < W; x++) {
            out[x] = acc / n;
            const add = x + r + 1, del = x - r;
            if (add < W) { acc += cur[add]; n++; }
            if (del >= 0) { acc -= cur[del]; n--; }
        }
        cur = out;
    }
    const data = new Uint8ClampedArray(W * H * 4);
    for (let y = 0; y < H; y++)
        for (let x = 0; x < W; x++) {
            const light = o.gradient ? 0.7 + 0.3 * (x / W) : 1;
            const v = Math.max(0, Math.min(255, cur[x] * light + noise(o.noise === undefined ? 5 : o.noise)));
            const i = (y * W + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = v;
            data[i + 3] = 255;
        }
    return { width: W, height: H, data };
}

// EXACT suite order so the PRNG state matches closeblur.js
const CASES = [
    ['A mild', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3 }, '713733788632'],
    ['B heavy', { digits: '036000291452', mw: 6, xoff: 320, blurR: 5 }, '036000291452'],
    ['C vclose', { digits: '041196403824', mw: 8, xoff: 220, blurR: 8 }, '041196403824'],
    ['D small', { digits: '713733788632', mw: 4, xoff: 420, blurR: 5 }, '713733788632'],
    ['E rev', { digits: '036000291452', mw: 5, xoff: 380, blurR: 4, reversed: true }, '036000291452'],
    ['F grad', { digits: '713733788632', mw: 6, xoff: 320, blurR: 5, gradient: true, noise: 8 }, '713733788632'],
];
const only = process.argv[2];
for (const [name, o, truth] of CASES) {
    const img = synthBand(o);
    if (only && !name.startsWith(only)) continue;
    const loc = U.locate(img, 0, img.height);
    console.log(`\n=== ${name} ===  locate:`, loc ? `xl=${loc.xl} xr=${loc.xr} mwEst=${loc.mwEst.toFixed(2)}` : 'null',
        `(true ${o.xoff}..${Math.round(o.xoff + 95 * o.mw)})`);
    const t0 = Date.now();
    const r = U.scanBand(img, 0, img.height, { debug: true, mustScore: [truth], maxRatio: 1.01, minCousin: 0 });
    console.log('scanBand:', r ? `${r.digits} ratio=${r.ratio.toFixed(3)} cousin=${r.cousinRatio.toFixed(3)}` : 'null', (Date.now() - t0) + 'ms');
    if (r && r.top) console.log('  top:', r.top.join('  '), ' truthInPool:', r.inPool && r.inPool[0], ' phys:', JSON.stringify(r.phys));
}
