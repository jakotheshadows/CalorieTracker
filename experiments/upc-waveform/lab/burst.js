// MULTI-FRAME FUSION acceptance suite: bursts of frames that INDIVIDUALLY refuse
// (heavy defocus + per-frame motion ghosts + hand drift) must decode jointly, and
// codeless bursts must refuse. No deps; run: node burst.js
const fs = require('fs');
const vm = require('vm');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

let seed = 1234;
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const noise = (amp) => (rnd() + rnd() + rnd() - 1.5) * amp;

const W = 1280, H = 140;

// One frame: bars at xoff, box-blur (defocus), two-impulse ghost (hand motion
// during exposure), noise. Returns {img, meta} where meta mimics app detection
// (extent with realistic error).
function synthFrame(o) {
    const bar = new Float64Array(W).fill(235);
    const mods = U.buildModules(o.digits);
    for (let x = 0; x < W; x++) {
        let cov = 0;
        const k0 = Math.floor((x - o.xoff) / o.mw), k1 = Math.floor((x + 1 - o.xoff) / o.mw);
        for (let k = k0; k <= k1; k++) {
            if (k < 0 || k >= 95 || mods[k] !== '1') continue;
            const a = Math.max(x, o.xoff + k * o.mw), b = Math.min(x + 1, o.xoff + (k + 1) * o.mw);
            if (b > a) cov += b - a;
        }
        bar[x] = 235 - cov * 210;
    }
    let cur = bar;
    for (let p = 0; p < 3; p++) {
        const out = new Float64Array(W);
        const r = o.blurR;
        let acc = 0, n = 0;
        for (let x = -r; x <= r; x++) if (x >= 0 && x < W) { acc += cur[x]; n++; }
        for (let x = 0; x < W; x++) {
            out[x] = acc / n;
            if (x + r + 1 < W) { acc += cur[x + r + 1]; n++; }
            if (x - r >= 0) { acc -= cur[x - r]; n--; }
        }
        cur = out;
    }
    // motion ghost: average of two sub-pixel-shifted copies +/- d/2
    const at = (arr, x) => {
        const xi = Math.max(0, Math.min(W - 2, Math.floor(x)));
        const fx = x - xi;
        return arr[xi] * (1 - fx) + arr[xi + 1] * fx;
    };
    const g = new Float64Array(W);
    for (let x = 0; x < W; x++) g[x] = (at(cur, x - o.ghost / 2) + at(cur, x + o.ghost / 2)) / 2;
    const data = new Uint8ClampedArray(W * H * 4);
    for (let y = 0; y < H; y++)
        for (let x = 0; x < W; x++) {
            const v = Math.max(0, Math.min(255, g[x] + noise(o.noise)));
            const i = (y * W + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = v;
            data[i + 3] = 255;
        }
    return {
        img: { width: W, height: H, data },
        meta: {
            xl: Math.round(o.xoff + (rnd() - 0.5) * 12),
            xr: Math.round(o.xoff + 95 * o.mw + (rnd() - 0.5) * 12),
            mwEst: o.mw * (1 + (rnd() - 0.5) * 0.08),
            slope: 0, by1: 0, by2: H,
        },
    };
}

function makeBurst(digits, mw, blurR, nFrames, ghostMax, noiseAmp) {
    const frames = [];
    for (let i = 0; i < nFrames; i++) {
        const o = {
            digits, mw,
            xoff: 360 + Math.round((rnd() - 0.5) * 30), // hand drift
            blurR: blurR + Math.round((rnd() - 0.5) * 2), // focus hunting
            ghost: rnd() * ghostMax,
            noise: noiseAmp,
        };
        const f = synthFrame(o);
        frames.push({ img: f.img, ...f.meta });
    }
    return frames;
}

const sig = (r) => Math.sqrt(3 * ((2 * r + 1) ** 2 - 1) / 12);
const CASES = [
    // [name, digits, mw, blurR, frames, ghostMax, noise]
    ['C-class: mw5 s(1.1m) ghost6', '041196403824', 5, 4, 8, 6, 5],
    ['frame4-class: mw4.3 s(1.5m) ghost8', '713733788632', 4.3, 5, 10, 8, 5],
    ['brutal: mw6 s(1.5m) ghost9', '036000291452', 6, 7, 8, 9, 6],
    ['very brutal: mw4 s(1.8m) ghost8', '713733788632', 4, 6, 10, 8, 6],
];

let pass = 0, fail = 0;
for (const [name, digits, mw, blurR, nF, ghostMax, nz] of CASES) {
    const burst = makeBurst(digits, mw, blurR, nF, ghostMax, nz);
    // single-frame baseline on the first frame (expect refuse for these cases)
    const one = U.scanBand(burst[0].img, 0, H, {});
    const t0 = Date.now();
    const r = U.scanBurst(burst, {});
    const ms = Date.now() - t0;
    const ok = r && r.digits === digits;
    ok ? pass++ : fail++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} [sigma=${sig(blurR).toFixed(1)}px]: single=${one ? one.digits + (one.digits === digits ? '(ok)' : '(WRONG)') : 'refused'} burst=${r ? `${r.digits} ratio=${r.ratio.toFixed(3)} cousin=${r.cousinRatio.toFixed(3)} n=${r.frames}` : 'refused'} ${ms}ms`);
}

// Safety: a codeless burst (text-like clutter bands) must refuse.
{
    const frames = [];
    for (let i = 0; i < 8; i++) {
        const data = new Uint8ClampedArray(W * H * 4);
        const row = new Float64Array(W).fill(235);
        let x = 300;
        while (x < 980) {
            const sw = 2 + Math.round(rnd() * 4), gap = 3 + Math.round(rnd() * 7);
            for (let k = 0; k < sw && x + k < W; k++) row[x + k] = 50;
            x += sw + gap;
        }
        for (let y = 0; y < H; y++)
            for (let xx = 0; xx < W; xx++) {
                const v = Math.max(0, Math.min(255, row[xx] + noise(5)));
                const j = (y * W + xx) * 4;
                data[j] = data[j + 1] = data[j + 2] = v;
                data[j + 3] = 255;
            }
        frames.push({ img: { width: W, height: H, data }, xl: 300, xr: 980, mwEst: 7.15, slope: 0, by1: 0, by2: H });
    }
    const t0 = Date.now();
    const r = U.scanBurst(frames, {});
    const ok = !r;
    ok ? pass++ : fail++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  codeless burst: ${r ? 'ACCEPTED ' + r.digits + ' (BAD!)' : 'refused'} ${Date.now() - t0}ms`);
}

console.log(`\n${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
