// Rotated-code regression: tilted synthetic bands through the full scanBand
// pipeline. Verifies the structure-tensor slope estimate and the shear-following
// profile extraction. No deps; run: node rotation.js
const fs = require('fs');
const vm = require('vm');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

let seed = 77;
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const noise = (amp) => (rnd() + rnd() + rnd() - 1.5) * amp;

// Render a base row (bars + clutter), blur it, then build the band with each row
// sampled at x - slope*(y - H/2): a rigid shear, i.e. tilted parallel bars.
function synthBand(o) {
    const W = o.width || 1280, H = o.height || 216;
    const base = new Float64Array(W).fill(235);
    const mods = U.buildModules(o.digits);
    for (let x = 0; x < W; x++) {
        let cov = 0;
        const k0 = Math.floor((x - o.xoff) / o.mw), k1 = Math.floor((x + 1 - o.xoff) / o.mw);
        for (let k = k0; k <= k1; k++) {
            if (k < 0 || k >= 95 || mods[k] !== '1') continue;
            const a = Math.max(x, o.xoff + k * o.mw), b = Math.min(x + 1, o.xoff + (k + 1) * o.mw);
            if (b > a) cov += b - a;
        }
        base[x] = 235 - cov * 210;
    }
    const codeL = o.xoff - 30, codeR = o.xoff + 95 * o.mw + 30;
    for (let c = 0; c < 6; c++) {
        const cx = rnd() * W;
        if (cx > codeL - 60 && cx < codeR + 60) continue;
        for (let s2 = 0; s2 < 8; s2++) {
            const sx = Math.round(cx + s2 * (3 + rnd() * 4)), sw = 1 + Math.round(rnd() * 2);
            for (let x = sx; x < Math.min(W, sx + sw); x++) base[x] = 40 + rnd() * 40;
        }
    }
    let cur = base;
    for (let p = 0; p < 3; p++) {
        const out = new Float64Array(W);
        const r = o.blurR;
        let acc = 0, n = 0;
        for (let x = -r; x <= r; x++) if (x >= 0 && x < W) { acc += cur[x]; n++; }
        for (let x = 0; x < W; x++) {
            out[x] = acc / n;
            const add = x + r + 1, del = x - r;
            if (add < W) { acc += cur[add]; n++; }
            if (del >= 0) { acc -= cur[del]; n--; }
        }
        cur = out;
    }
    const data = new Uint8ClampedArray(W * H * 4);
    for (let y = 0; y < H; y++) {
        const shift = o.slope * (y - H / 2);
        for (let x = 0; x < W; x++) {
            const xs = x - shift;
            const xi = Math.max(0, Math.min(W - 2, Math.floor(xs)));
            const fx = xs - xi;
            const v = Math.max(0, Math.min(255, cur[xi] * (1 - fx) + cur[xi + 1] * fx + noise(5)));
            const i = (y * W + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = v;
            data[i + 3] = 255;
        }
    }
    return { width: W, height: H, data };
}

const CASES = [
    ['tilt +8.5deg mild', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3, slope: 0.15 }],
    ['tilt +15deg mild', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3, slope: 0.27 }],
    ['tilt -11deg mild', { digits: '036000291452', mw: 5, xoff: 380, blurR: 3, slope: -0.2 }],
    ['tilt +11deg heavy blur', { digits: '036000291452', mw: 6, xoff: 320, blurR: 5, slope: 0.2 }],
    ['upright control', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3, slope: 0 }],
];

let pass = 0, fail = 0;
for (const [name, o] of CASES) {
    const img = synthBand(o);
    const cands = U.locate(img, 0, img.height);
    const c = cands && cands[0];
    const t0 = Date.now();
    const r = U.scanBand(img, 0, img.height, {});
    const ms = Date.now() - t0;
    const ok = r && r.digits === o.digits;
    ok ? pass++ : fail++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}: slopeTrue=${o.slope} slopeEst=${c ? c.slope.toFixed(3) : 'n/a'} got=${r ? r.digits : null}${r ? ` ratio=${r.ratio.toFixed(3)} cousin=${r.cousinRatio.toFixed(3)}` : ''} ${ms}ms`);
}
console.log(`\n${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
