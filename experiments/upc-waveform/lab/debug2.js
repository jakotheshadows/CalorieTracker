// Deep probe of the mw6/r5 heavy-blur failure: full scanBand flow, instrumented.
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

const o = { digits: '036000291452', mw: 6, xoff: 320, blurR: 5 };
const img = synthBand(o);
const loc = (U.locate(img, 0, img.height) || [null])[0]; // locate now returns ranked candidates
console.log('locate:', loc ? `xl=${loc.xl} xr=${loc.xr} mwEst=${loc.mwEst.toFixed(2)}` : 'null',
    `(true 320..${320 + 95 * 6})`);
if (loc) {
    const scale = loc.mwEst / 1.2;
    const margin = 14 * loc.mwEst;
    const xa = Math.max(0, loc.xl - margin), xb = Math.min(img.width, loc.xr + margin);
    const H = img.height;
    const bands = [[0, (H * 2 / 3) | 0], [(H / 6) | 0, H - ((H / 6) | 0)], [H - ((H * 2 / 3) | 0), H]];
    const profiles = bands.map(([a, b]) => U.extractProfile(img, xa, xb, a, b, 0, scale));
    console.log(`scale=${scale.toFixed(2)} trueMwV=${(o.mw / scale).toFixed(3)} trueX0V=${((o.xoff - xa) / scale).toFixed(1)} n=${profiles[0].length}`);
    for (let i = 0; i < profiles.length; i++) {
        const gen = U.decodeProfile(profiles[i], { grids: 6 });
        if (!gen) { console.log(`profile ${i}: decodeProfile null`); continue; }
        console.log(`profile ${i}: cands=${gen.candMap.size} truthIn=${gen.candMap.has(o.digits)}`);
        console.log('  grids:', gen.guardGrids.slice(0, 3).map(g => `x0=${g.x0.toFixed(1)} mw=${g.mw.toFixed(3)} sig=${g.sigmaM} gc=${Math.round(g.gc)}`).join(' | '));
        console.log('  top cands:', [...gen.candMap.keys()].slice(0, 6).join(','));
    }
    const t0 = Date.now();
    const fwd = U.decodeJoint(profiles, { grids: 6, debug: true, mustScore: [o.digits] });
    console.log('decodeJoint fwd:', fwd ? `${fwd.digits} cost=${Math.round(fwd.cost)} ratio=${fwd.ratio.toFixed(3)} nCands=${fwd.nCands}` : 'null', (Date.now() - t0) + 'ms');
    if (fwd && fwd.top) {
        console.log('  truthInPool:', fwd.inPool[0], ' phys:', JSON.stringify(fwd.phys));
        console.log('  top scored:', fwd.top.join('  '));
        console.log('  TRUTH ' + o.digits + ' scored: look for it above');
    }
}
