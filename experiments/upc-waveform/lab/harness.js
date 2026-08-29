// Node harness for the UPC waveform decoder: loads the decoder file with a window
// shim and runs its self-tests. With pngjs installed (npm i pngjs) and a saved
// scanner frame at FRAME (never commit one — frames show the user's surroundings),
// it also decodes a real frame via the same scanBand entry point the worker uses.
const fs = require('fs');
const vm = require('vm');

const DECODER = 'C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js';
const FRAME = process.argv[3] || 'test-frame.png';

const ctx = { window: {}, console };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(DECODER, 'utf8'), ctx);
const U = ctx.window.UpcWaveform;

const cmd = process.argv[2] || 'self';

if (cmd === 'self' || cmd === 'all') {
    const t0 = Date.now();
    console.log('selfTest:', JSON.stringify(U.selfTest()), (Date.now() - t0) + 'ms');
}

if (cmd === 'real' || cmd === 'all') {
    if (!fs.existsSync(FRAME)) {
        console.log(`no frame at ${FRAME} — pass a path: node harness.js real <frame.png>`);
        process.exit(cmd === 'real' ? 1 : 0);
    }
    const { PNG } = require('pngjs');
    const png = PNG.sync.read(fs.readFileSync(FRAME));
    const img = { width: png.width, height: png.height, data: png.data };
    // Same central band the live scanner feeds the worker.
    const bandH = Math.min(img.height, Math.max(96, Math.round(img.height * 0.2)));
    const y0 = (img.height - bandH) >> 1;
    const t0 = Date.now();
    const r = U.scanBand(img, y0, y0 + bandH, {});
    console.log('scanBand:', r ? JSON.stringify(r) : 'null', (Date.now() - t0) + 'ms');
}
