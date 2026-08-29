// Measure decodeJoint's fitNorm on real-code vs clutter-only bands to place the
// early-bail threshold. Mirrors closeblur's synth + PRNG sequence.
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

const CASES = [
    ['A code', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3 }],
    ['B code', { digits: '036000291452', mw: 6, xoff: 320, blurR: 5 }],
    ['C code', { digits: '041196403824', mw: 8, xoff: 220, blurR: 8 }],
    ['D code', { digits: '713733788632', mw: 4, xoff: 420, blurR: 5 }],
    ['E code', { digits: '036000291452', mw: 5, xoff: 380, blurR: 4, reversed: true }],
    ['F code', { digits: '713733788632', mw: 6, xoff: 320, blurR: 5, gradient: true, noise: 8 }],
    ['G clutter', { blurR: 2, clutter: 14 }],
    ['H tiny', { digits: '713733788632', mw: 1.05, xoff: 500, blurR: 1 }],
];
// Patch decodeJoint via debug: scanBand exposes fitNorm only in the result when it
// decodes; for refused/failed cases call decodeJoint directly.
for (const [name, o] of CASES) {
    const img = synthBand(o);
    const loc = (U.locate(img, 0, img.height) || [null])[0]; // locate now returns ranked candidates
    if (!loc) { console.log(`${name}: locate null`); continue; }
    const scale = loc.mwEst / 1.2;
    const margin = 14 * loc.mwEst;
    const xa = Math.max(0, loc.xl - margin), xb = Math.min(img.width, loc.xr + margin);
    const anchors = [
        [Math.max(xa, loc.xl - 10 * loc.mwEst), loc.xl - 2 * loc.mwEst],
        [loc.xr + 2 * loc.mwEst, Math.min(xb, loc.xr + 10 * loc.mwEst)],
    ];
    const H = img.height;
    const bands = [[0, (H * 2 / 3) | 0], [(H / 6) | 0, H - ((H / 6) | 0)], [H - ((H * 2 / 3) | 0), H]];
    const profiles = bands.map(([a, b]) => U.extractProfile(img, xa, xb, a, b, 0, scale, anchors));
    const env = { xl: (loc.xl - xa) / scale, xr: (loc.xr - xa) / scale };
    const t0 = Date.now();
    const r = U.decodeJoint(profiles, { grids: 6, env, repairIters: 0, verify: 2 });
    console.log(`${name}: mwEst=${loc.mwEst.toFixed(2)} cross=${loc.crossings} fitNorm=${r ? r.fitNorm.toFixed(3) : 'n/a'} (${Date.now() - t0}ms)`);
}
