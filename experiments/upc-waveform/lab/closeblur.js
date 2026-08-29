// End-to-end test of the live scanBand pipeline on synthetic close-held frames:
// large modules + heavy defocus blur + noise + clutter, the regime a fixed-focus
// webcam produces when a barcode is held close. No deps; run: node closeblur.js
const fs = require('fs');
const vm = require('vm');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

// Deterministic PRNG so failures reproduce.
let seed = 42;
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const noise = (amp) => (rnd() + rnd() + rnd() - 1.5) * amp;

const buildModules = U.buildModules;

// Render a band: white 235, ink 25, bars of the code at mw px/module starting at
// xoff, optional reversed, clutter strokes flanking, horizontal box blur x3
// (≈ Gaussian), lighting gradient, per-pixel noise.
function synthBand(o) {
    const W = o.width || 1280, H = o.height || 96;
    const row = new Float64Array(W).fill(235);
    if (o.digits) {
        const mods = buildModules(o.digits);
        const seq = o.reversed ? [...mods].reverse().join('') : mods;
        for (let x = 0; x < W; x++) {
            // ink coverage of pixel [x, x+1)
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
    // clutter: text-like stroke clusters left/right of the code (and everywhere if no code)
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
    // blur: 3 box passes of radius r
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

const sigma = (r) => Math.sqrt(3 * ((2 * r + 1) ** 2 - 1) / 12);

const CASES = [
    // [name, opts, expected digits or null]
    ['close mild blur (mw5, s3.5px=0.69m)', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3 }, '713733788632'],
    ['close heavy blur (mw6, s5.5px=0.91m)', { digits: '036000291452', mw: 6, xoff: 320, blurR: 5 }, '036000291452'],
    ['very close very blurry (mw8, s8.5px=1.06m)', { digits: '041196403824', mw: 8, xoff: 220, blurR: 8 }, '041196403824'],
    ['small + blurry (mw4, s5.5px=1.37m)', { digits: '713733788632', mw: 4, xoff: 420, blurR: 5 }, '713733788632'],
    ['reversed (mw5, s4.6px)', { digits: '036000291452', mw: 5, xoff: 380, blurR: 4, reversed: true }, '036000291452'],
    ['gradient + noise (mw6, s5.5px)', { digits: '713733788632', mw: 6, xoff: 320, blurR: 5, gradient: true, noise: 8 }, '713733788632'],
    ['clutter only, no code', { blurR: 2, clutter: 14 }, null],
    ['tiny code -> refused by gate (mw1.05)', { digits: '713733788632', mw: 1.05, xoff: 500, blurR: 1 }, null],
];

let pass = 0, fail = 0;
for (const [name, opts, expected] of CASES) {
    const img = synthBand(opts);
    const t0 = Date.now();
    const r = U.scanBand(img, 0, img.height);
    const ms = Date.now() - t0;
    const got = r ? r.digits : null;
    const ok = got === expected;
    ok ? pass++ : fail++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}: got=${got}${r ? ` ratio=${r.ratio.toFixed(3)} cousin=${r.cousinRatio.toFixed(3)} mwPx=${r.mwPx.toFixed(2)}` : ''} expected=${expected} ${ms}ms`);
}
console.log(`\n${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
