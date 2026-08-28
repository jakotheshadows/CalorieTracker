const fs = require('fs'); const vm = require('vm'); const { PNG } = require('pngjs');
const ctx = { window: {}, console }; vm.createContext(ctx);
vm.runInContext(fs.readFileSync('C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js', 'utf8'), ctx);
const U = ctx.window.UpcWaveform;
const png = PNG.sync.read(fs.readFileSync('C:/Users/awolf/source/repos/CalorieTracker/wwwroot/test-frame.png'));
const img = { width: png.width, height: png.height, data: png.data };
const profs = [[512,552],[518,548],[524,556]].map(([y1,y2]) => U.extractProfile(img, 770, 925, y1, y2, 0));
const PHYS = [{ sigmaM: 0.9, e: 0.2 }, { sigmaM: 1.2, e: 0.2 }, { sigmaM: 1.5, e: 0.2 }];
const seeds = [{ x0: 26.3, mw: 1.07, q: 0, r: 0 }, { x0: 27.5, mw: 1.04, q: 0, r: 0 }];

const geoRefine = (prof, digits, seed, phys) => {
  let cur = { x0: seed.x0, mw: seed.mw, q: seed.q, r: seed.r, ...phys };
  let curCost = U.fullCost(prof, digits, cur);
  for (let pass = 0; pass < 5; pass++) {
    let improved = false;
    for (const [key, deltas] of [['x0',[-0.5,-0.25,0.25,0.5]],['mw',[-0.02,-0.01,0.01,0.02]],['q',[-0.00015,0.00015]],['r',[-0.000003,0.000003]]]) {
      for (const d of deltas) {
        const t = { ...cur, [key]: cur[key] + d };
        const c = U.fullCost(prof, digits, t);
        if (c < curCost) { cur = t; curCost = c; improved = true; }
      }
    }
    if (!improved) break;
  }
  return curCost;
};
const honest = (digits) => {
  let s = 0;
  for (const prof of profs) for (const phys of PHYS) {
    let best = Infinity;
    for (const seed of seeds) { const c = geoRefine(prof, digits, seed, phys); if (c < best) best = c; }
    s += best;
  }
  return Math.round(s);
};
const t0 = Date.now();
for (const d of ['713733788632', '711113388632', '711313368632']) {
  console.log(d, '->', honest(d), d === '713733788632' ? '(truth)' : '');
}
console.log('ms:', Date.now() - t0);
