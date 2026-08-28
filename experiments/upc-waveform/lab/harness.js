// Node harness for the UPC waveform decoder: loads the decoder file with a window
// shim, reads the real frame via pngjs, and runs experiments at native speed.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const { PNG } = require('pngjs');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const FRAME = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/test-frame.png';

const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

const png = PNG.sync.read(fs.readFileSync(FRAME));
const img = { width: png.width, height: png.height, data: png.data };

const TRUTH = '713733788632';

const cmd = process.argv[2] || 'all';

if (cmd === 'self' || cmd === 'all') {
    const t0 = Date.now();
    console.log('selfTest:', JSON.stringify(U.selfTest()), (Date.now() - t0) + 'ms');
}

if (cmd === 'real' || cmd === 'all') {
    const bands = [[512, 552], [518, 548], [524, 556]];
    const profs = bands.map(([y1, y2]) => U.extractProfile(img, 770, 925, y1, y2, 0));
    const t0 = Date.now();
    const r = U.decodeJoint(profs, { mwMin: 0.95, mwMax: 1.3, grids: 6 });
    console.log('realFrame:', r ? JSON.stringify({ digits: r.digits, ratio: +r.ratio.toFixed(3), nCands: r.nCands }) : 'null',
        (Date.now() - t0) + 'ms', 'CORRECT=' + (r && r.digits === TRUTH));
}

if (cmd === 'diag') {
    // Per-profile generation diagnostics.
    const bands = [[512, 552], [518, 548], [524, 556]];
    for (const [y1, y2] of bands) {
        const prof = U.extractProfile(img, 770, 925, y1, y2, 0);
        const gen = U.decodeProfile(prof, { mwMin: 0.95, mwMax: 1.3, grids: 6 });
        if (!gen) { console.log(`band ${y1}-${y2}: decodeProfile null`); continue; }
        console.log(`band ${y1}-${y2}: shortlist=${gen.shortlistLen} cands=${gen.candMap.size}`);
        console.log('  guardGrids:', gen.guardGrids.map(g => `x0=${g.x0.toFixed(1)} mw=${g.mw.toFixed(2)} gc=${Math.round(g.gc)}`).join(' | '));
        const hasTruth = gen.candMap.has(TRUTH);
        console.log('  truthInCandidates:', hasTruth, ' sample:', [...gen.candMap.keys()].slice(0, 5).join(','));
    }
}
