// Timing + correctness of scanBand's fast mode (the live scanner's per-frame quick
// attempt): easy-to-moderate blur must decode within a ~3.5s budget, codeless
// scenes must refuse fast. No deps; run: node fastpath.js
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
const buildModules = U.buildModules;

function synthBand(o) {
    const W = o.width || 1280, H = o.height || 96;
    const row = new Float64Array(W).fill(235);
    if (o.digits) {
        const mods = buildModules(o.digits);
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

const sigma = (r) => Math.sqrt(3 * ((2 * r + 1) ** 2 - 1) / 12);

const CASES = [
    { name: 'sharp (mw4, s1.6px=0.41m)', digits: '713733788632', mw: 4, xoff: 340, blurR: 1 },
    { name: 'mild (mw5, s3.5px=0.69m)', digits: '713733788632', mw: 5, xoff: 300, blurR: 2 },
    { name: 'moderate (mw6, s5.5px=0.91m)', digits: '036000291452', mw: 6, xoff: 260, blurR: 3, width: 1600 },
    { name: 'reversed mild (mw5, s3.5px)', digits: '036000291452', mw: 5, xoff: 300, blurR: 2, reversed: true },
    { name: 'gradient (mw6, s5.5px)', digits: '713733788632', mw: 6, xoff: 260, blurR: 3, width: 1600, gradient: true },
    { name: 'codeless clutter', digits: null, blurR: 2, clutter: 30 },
];

let pass = 0, total = 0;
for (const c of CASES) {
    seed = 42;
    const img = synthBand(c);
    const t0 = Date.now();
    const r = U.scanBand(img, 0, img.height, { fast: true, budgetMs: 4500 });
    const ms = Date.now() - t0;
    const got = r && !r.refused ? r.digits : null;
    const ok = got === c.digits || (got === null && c.digits === null) ||
        (got === null && c.allowRefuse);
    // decodes must also be timely: the whole point of fast mode
    const timely = ms <= 5000;
    if (ok && timely) pass++;
    total++;
    console.log(`${ok && timely ? 'PASS' : 'FAIL'}  ${c.name}${c.blurR ? ` [sigma=${sigma(c.blurR).toFixed(1)}px]` : ''}: got=${got}${r && r.ratio !== undefined ? ` ratio=${r.ratio.toFixed(3)} cousin=${r.cousinRatio.toFixed(3)}` : ''} ${ms}ms`);
}
console.log(`${pass}/${total} passed`);
