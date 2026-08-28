const fs = require('fs'); const vm = require('vm'); const { PNG } = require('pngjs');
const ctx = { window: {}, console }; vm.createContext(ctx);
vm.runInContext(fs.readFileSync('C:/Users/awolf/source/repos/CalorieTracker/wwwroot/js/upc-waveform.js', 'utf8'), ctx);
const U = ctx.window.UpcWaveform;
const png = PNG.sync.read(fs.readFileSync('C:/Users/awolf/source/repos/CalorieTracker/wwwroot/test-frame.png'));
const img = { width: png.width, height: png.height, data: png.data };
const TRUTH = '713733788632';
const profs = [[512,552],[518,548],[524,556]].map(([y1,y2]) => U.extractProfile(img, 770, 925, y1, y2, 0));
const PHYS = { sigmaM: 1.2, e: 0.2 };
const L = ['0001101','0011001','0010011','0111101','0100011','0110001','0101111','0111011','0110111','0001011'];
const Rp = L.map(p => [...p].map(ch => ch === '0' ? '1' : '0').join(''));
const forcedCheck = (d11) => { let s = 0; for (let i = 0; i < 11; i++) s += (i % 2 === 0 ? 3 : 1) * +d11[i]; return String((10 - s % 10) % 10); };

const geoRefineGrid = (prof, digits, seed) => {
  let cur = { ...seed };
  let curCost = U.fullCost(prof, digits, cur);
  for (let pass = 0; pass < 4; pass++) {
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
  return cur;
};

const seed = { x0: 26.3, mw: 1.07, q: 0, r: 0, ...PHYS };
// per-profile grids start at the same seed
let grids = profs.map(() => ({ ...seed }));

// warm start on profile 0
const cdf0 = U.cdfFor(seed.sigmaM * seed.mw);
const pos0 = U.gridPos(seed);
let d11 = '';
for (let d = 0; d < 11; d++) {
  const sm = d < 6 ? 3 + 7 * d : 50 + 7 * (d - 6);
  const table = d < 6 ? L : Rp;
  let bd = 5, bc = Infinity;
  for (let dig = 0; dig < 10; dig++) {
    const c = U.segCost(profs[0], pos0, seed.mw, cdf0, seed.e, sm, table[dig]);
    if (c < bc) { bc = c; bd = dig; }
  }
  d11 += bd;
}
let digits = d11 + forcedCheck(d11);

const jointCost = (d) => {
  let s = 0;
  for (let i = 0; i < profs.length; i++) s += U.fullCost(profs[i], d, grids[i]);
  return s;
};

const t0 = Date.now();
for (let sweep = 0; sweep < 6; sweep++) {
  let changed = false;
  // single-digit moves
  for (let p = 0; p < 11; p++) {
    let bestD = digits, bestC = jointCost(digits);
    for (let dig = 0; dig < 10; dig++) {
      const nd11 = digits.slice(0, p) + dig + digits.slice(p + 1, 11);
      const trial = nd11 + forcedCheck(nd11);
      const c = jointCost(trial);
      if (c < bestC) { bestC = c; bestD = trial; }
    }
    if (bestD !== digits) { digits = bestD; changed = true; }
  }
  // adjacent-pair moves
  for (let p = 0; p < 10; p++) {
    let bestD = digits, bestC = jointCost(digits);
    for (let d1 = 0; d1 < 10; d1++) for (let d2 = 0; d2 < 10; d2++) {
      if (+digits[p] === d1 && +digits[p+1] === d2) continue;
      const nd11 = digits.slice(0, p) + d1 + d2 + digits.slice(p + 2, 11);
      const trial = nd11 + forcedCheck(nd11);
      const c = jointCost(trial);
      if (c < bestC) { bestC = c; bestD = trial; }
    }
    if (bestD !== digits) { digits = bestD; changed = true; }
  }
  // refit each profile grid on current digits
  grids = grids.map((g, i) => geoRefineGrid(profs[i], digits, g));
  console.log('sweep', sweep, '->', digits, 'jointCost', Math.round(jointCost(digits)), digits === TRUTH ? '*** CORRECT ***' : '');
  if (!changed) break;
}
console.log('truth jointCost at final grids:', Math.round(jointCost(TRUTH)));
console.log('ms:', Date.now() - t0);
