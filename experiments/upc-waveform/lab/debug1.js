// Stage-by-stage probe of scanBand on failing synthetic cases.
const fs = require('fs');
const vm = require('vm');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

// same synth as closeblur.js (copy, trimmed: no clutter, fixed noise)
let seed = 42;
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const noise = (amp) => (rnd() + rnd() + rnd() - 1.5) * amp;
function synthBand(o) {
    const W = o.width || 1280, H = o.height || 96;
    const row = new Float64Array(W).fill(235);
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
            const v = Math.max(0, Math.min(255, cur[x] + noise(o.noise === undefined ? 5 : o.noise)));
            const i = (y * W + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = v;
            data[i + 3] = 255;
        }
    return { width: W, height: H, data };
}

const CASES = [
    ['mw5 r3', { digits: '713733788632', mw: 5, xoff: 380, blurR: 3 }],
    ['mw6 r5', { digits: '036000291452', mw: 6, xoff: 320, blurR: 5 }],
];

for (const [name, o] of CASES) {
    console.log(`\n=== ${name} truth=${o.digits} trueX0=${o.xoff} trueMw=${o.mw} ===`);
    const img = synthBand(o);
    const loc = U.locate(img, 0, img.height);
    console.log('locate:', loc ? `xl=${loc.xl} xr=${loc.xr} mwEst=${loc.mwEst.toFixed(2)}` : 'null',
        ` (true extent ${o.xoff}..${o.xoff + 95 * o.mw})`);
    if (!loc) continue;
    const scale = loc.mwEst / 1.2;
    const margin = 14 * loc.mwEst;
    const xa = Math.max(0, loc.xl - margin), xb = Math.min(img.width, loc.xr + margin);
    const prof = U.extractProfile(img, xa, xb, 8, 56, 0, scale);
    console.log(`profile: n=${prof.length} scale=${scale.toFixed(2)} trueMwVirtual=${(o.mw / scale).toFixed(3)} trueX0Virtual=${((o.xoff - xa) / scale).toFixed(1)}`);
    let mx = 0; for (const v of prof) if (v > mx) mx = v;
    console.log('profile max:', mx.toFixed(1));
    const t0 = Date.now();
    const gen = U.decodeProfile(prof, { grids: 6 });
    console.log('decodeProfile:', gen ? `cands=${gen.candMap.size} shortlist=${gen.shortlistLen}` : 'null', (Date.now() - t0) + 'ms');
    if (gen) {
        console.log('  truthInCands:', gen.candMap.has(o.digits));
        console.log('  guardGrids:', gen.guardGrids.map(g => `x0=${g.x0.toFixed(1)} mw=${g.mw.toFixed(3)} gc=${Math.round(g.gc)} fd=${Math.round(g.fd)}`).join(' | '));
        console.log('  sample cands:', [...gen.candMap.keys()].slice(0, 8).join(','));
    }
}
