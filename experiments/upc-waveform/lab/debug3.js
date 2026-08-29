// Segment-level guardCost dissection: true grid vs the stage-1 winner grid.
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
    const mods = U.buildModules(o.digits);
    for (let x = 0; x < W; x++) {
        let cov = 0;
        const k0 = Math.floor((x - o.xoff) / o.mw), k1 = Math.floor((x + 1 - o.xoff) / o.mw);
        for (let k = k0; k <= k1; k++) {
            if (k < 0 || k >= 95 || mods[k] !== '1') continue;
            const a = Math.max(x, o.xoff + k * o.mw), b = Math.min(x + 1, o.xoff + (k + 1) * o.mw);
            if (b > a) cov += b - a;
        }
        row[x] = 235 - cov * 210;
    }
    let cur = row;
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
    for (let y = 0; y < H; y++)
        for (let x = 0; x < W; x++) {
            const v = Math.max(0, Math.min(255, cur[x] + noise(5)));
            const i = (y * W + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = v;
            data[i + 3] = 255;
        }
    return { width: W, height: H, data };
}

const o = { digits: '036000291452', mw: 6, xoff: 320, blurR: 5 };
const img = synthBand(o);
const loc = (U.locate(img, 0, img.height) || [null])[0]; // locate now returns ranked candidates
const scale = loc.mwEst / 1.2;
const margin = 14 * loc.mwEst;
const xa = Math.max(0, loc.xl - margin);
const prof = U.extractProfile(img, xa, Math.min(img.width, loc.xr + margin), 8, 56, 0, scale);
// decodeProfile attaches profile.amp — call it once for the side effect
U.decodeProfile(prof, { grids: 1 });
console.log('profile.amp =', prof.amp && prof.amp.toFixed(1));

const trueMwV = o.mw / scale, trueX0V = (o.xoff - xa) / scale;
const grids = [
    ['TRUE   ', { x0: trueX0V, mw: trueMwV, q: 0, r: 0, sigmaM: 0.92, e: 0.0 }],
    ['TRUE+fit', null], // filled below: refine physics on true geometry
    ['WINNER ', { x0: 20.8, mw: 1.192, q: 0, r: 0, sigmaM: 1.3, e: 0.3 }],
    ['NEAR   ', { x0: 20.8, mw: 1.142, q: 0, r: 0, sigmaM: 0.9, e: 0.15 }],
];
grids[1][1] = U.refine((p) => U.guardCost(prof, p), { x0: trueX0V, mw: trueMwV, q: 0, r: 0, sigmaM: 1.0, e: 0.15 }, 5);

for (const [name, g] of grids) {
    const cdf = U.cdfFor(g.sigmaM * g.mw);
    const pos = U.gridPos(g);
    const a = U.segCost(prof, pos, g.mw, cdf, g.e, 0, '101');
    const b = U.segCost(prof, pos, g.mw, cdf, g.e, 45, '01010');
    const c = U.segCost(prof, pos, g.mw, cdf, g.e, 92, '101');
    const gc = U.guardCost(prof, g);
    console.log(`${name} x0=${g.x0.toFixed(2)} mw=${g.mw.toFixed(3)} sig=${g.sigmaM.toFixed(2)} e=${g.e.toFixed(2)}: L=${Math.round(a)} M=${Math.round(b)} R=${Math.round(c)} total=${Math.round(gc)} (qz=${Math.round(gc - a - b - c)})`);
    // full-code cost of truth and top impostor on this grid
    for (const digits of ['036000291452', '936000267252']) {
        console.log(`   fullCost(${digits}) = ${Math.round(U.fullCost(prof, digits, g))}`);
    }
}
