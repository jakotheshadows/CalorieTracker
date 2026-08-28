// UPC-A waveform decoder: reads sub-pixel barcodes that binarizing decoders cannot.
// Approach (what commercial engines do for tiny/blurry 1D): average the scan band into
// a 1D analog profile, model the printed code as bars-with-ink-spread convolved with a
// blur kernel, anchor a (possibly curved) module grid on the guard patterns, decode
// digits by maximum-likelihood template matching, and verify checksum-valid candidates
// by full-code fit. No thresholding anywhere.
(function () {
    "use strict";

    const L = ['0001101','0011001','0010011','0111101','0100011','0110001','0101111','0111011','0110111','0001011'];
    const R = L.map(p => [...p].map(ch => ch === '0' ? '1' : '0').join(''));
    const STEP = 0.25; // profile samples per pixel = 1/STEP

    const buildModules = (d12) => {
        let m = '101';
        for (let i = 0; i < 6; i++) m += L[+d12[i]];
        m += '01010';
        for (let i = 6; i < 12; i++) m += R[+d12[i]];
        return m + '101';
    };

    const checksumOk = (d) => {
        let s = 0;
        for (let i = 0; i < 11; i++) s += (i % 2 === 0 ? 3 : 1) * +d[i];
        return (10 - s % 10) % 10 === +d[11];
    };

    // Cheap smooth CDF standing in for a Gaussian edge response with width s.
    const cdfFor = (s) => (x) => x <= -2 * s ? 0 : x >= 2 * s ? 1 : 0.5 + 0.25 * (x / s) * (2 - Math.abs(x / s) / 2);

    // Cost of matching modules valStr starting at startModule against the profile.
    // Amplitude and offset are fitted per segment (absorbs local lighting); lower = better.
    function segCost(profile, pos, mw, cdf, e, startModule, valStr, shift) {
        const N = profile.length;
        const nMods = valStr.length;
        const sh = shift || 0;
        const ia = Math.max(0, Math.round((pos(startModule) + sh - 1.0 * mw) / STEP));
        const ib = Math.min(N - 1, Math.round((pos(startModule + nMods) + sh + 1.0 * mw) / STEP));
        if (ib - ia < 4) return Infinity;
        const bars = [];
        for (let k = 0; k < nMods; k++) {
            if (valStr[k] === '1')
                bars.push([pos(startModule + k) + sh - e * mw, pos(startModule + k + 1) + sh + e * mw]);
        }
        let st = 0, so = 0, stt = 0, sto = 0, n = 0;
        const tv = new Float64Array(ib - ia + 1);
        for (let i = ia; i <= ib; i++) {
            const x = i * STEP;
            let v = 0;
            for (let b = 0; b < bars.length; b++) {
                const ba = bars[b][0], bb = bars[b][1];
                if (x >= ba - 3 && x <= bb + 3) v += cdf(x - ba) - cdf(x - bb);
            }
            v = Math.min(v, 1);
            tv[i - ia] = v;
            st += v; so += profile[i]; stt += v * v; sto += v * profile[i]; n++;
        }
        const denom = stt - st * st / n;
        const g = denom > 1e-9 ? (sto - st * so / n) / denom : 0;
        if (g < 5) {
            // No positive bar correlation here. Don't hard-veto (a slight blur-model
            // mismatch at 1px modules must not kill a true grid) — charge the natural
            // "unexplained" cost: the residual variance around the mean.
            const mean = so / n;
            let err = 0;
            for (let i = ia; i <= ib; i++) {
                const d = profile[i] - mean;
                err += d * d;
            }
            return err / n;
        }
        const off = (so - g * st) / n;
        let err = 0;
        for (let i = ia; i <= ib; i++) {
            const d = g * tv[i - ia] + off - profile[i];
            err += d * d;
        }
        return err / n;
    }

    // Module-k left edge. q (quadratic) and r (cubic) absorb label curvature;
    // a cylinder seen off-axis compresses the far edge asymmetrically, which
    // needs the cubic term.
    const gridPos = (p) => (k) => p.x0 + p.mw * (k + p.q * k * k + (p.r || 0) * k * k * k);

    // Mean squared barness over a module range — quiet zones must be white (≈0).
    function quietCost(profile, pos, kFrom, kTo) {
        const ia = Math.max(0, Math.round(pos(kFrom) / STEP));
        const ib = Math.min(profile.length - 1, Math.round(pos(kTo) / STEP));
        if (ib - ia < 2) return Infinity;
        let s = 0, n = 0;
        for (let i = ia; i <= ib; i++) { s += profile[i] * profile[i]; n++; }
        return s / n;
    }

    // Cost of only the fixed structure (guards + quiet zones); anchors the grid.
    // Quiet zones are the discriminator: misaligned grids put bars in their margins.
    function guardCost(profile, p) {
        const cdf = cdfFor(p.sigmaM * p.mw);
        const pos = gridPos(p);
        const a = segCost(profile, pos, p.mw, cdf, p.e, 0, '101');
        if (!isFinite(a)) return Infinity;
        const b = segCost(profile, pos, p.mw, cdf, p.e, 45, '01010');
        const c = segCost(profile, pos, p.mw, cdf, p.e, 92, '101');
        if (!isFinite(b) || !isFinite(c)) return Infinity;
        // Real labels often print text close to the code, so keep the windows tight
        // and cap the term: it should veto misaligned grids, not dominate ranking.
        const qz = quietCost(profile, pos, -3.5, -1) + quietCost(profile, pos, 96, 98.5);
        if (!isFinite(qz)) return Infinity;
        return a + b + c + Math.min(qz, 3000);
    }

    function fullCost(profile, digits, p) {
        const cdf = cdfFor(p.sigmaM * p.mw);
        return segCost(profile, gridPos(p), p.mw, cdf, p.e, 0, buildModules(digits));
    }

    // Coordinate-descent refinement of parameters p against a cost function.
    function refine(costFn, p, passes) {
        let cur = { ...p };
        let curCost = costFn(cur);
        if (cur.r === undefined) cur.r = 0;
        const moves = [
            ['x0', [-0.5, -0.25, 0.25, 0.5]],
            ['mw', [-0.02, -0.01, 0.01, 0.02]],
            ['q', [-0.00015, 0.00015]],
            ['r', [-0.000003, 0.000003]],
            ['sigmaM', [-0.2, -0.1, 0.1, 0.2]],
            ['e', [-0.1, -0.05, 0.05, 0.1]],
        ];
        for (let pass = 0; pass < (passes || 3); pass++) {
            let improved = false;
            for (const [key, deltas] of moves) {
                for (const d of deltas) {
                    const t = { ...cur, [key]: cur[key] + d };
                    if (t.sigmaM < 0.4 || t.sigmaM > 2.0 || t.e < 0 || t.e > 0.4 || t.mw < 0.8 || t.mw > 1.6) continue;
                    const c = costFn(t);
                    if (c < curCost) { cur = t; curCost = c; improved = true; }
                }
            }
            if (!improved) break;
        }
        cur.cost = curCost;
        return cur;
    }

    const forcedCheckDigit = (d11) => {
        let s = 0;
        for (let i = 0; i < 11; i++) s += (i % 2 === 0 ? 3 : 1) * +d11[i];
        return String((10 - s % 10) % 10);
    };

    // Per-digit ML table for a grid: top candidate digits at each of the 12 positions.
    function digitTable(profile, g, keep) {
        const cdf = cdfFor(g.sigmaM * g.mw);
        const pos = gridPos(g);
        const perDigit = [];
        for (let d = 0; d < 12; d++) {
            const startModule = d < 6 ? 3 + 7 * d : 50 + 7 * (d - 6);
            const table = d < 6 ? L : R;
            const cands = [];
            for (let digit = 0; digit < 10; digit++) {
                let bc = Infinity;
                for (const sf of [-0.15, 0, 0.15]) {
                    const c = segCost(profile, pos, g.mw, cdf, g.e, startModule, table[digit], sf * g.mw);
                    if (c < bc) bc = c;
                }
                if (isFinite(bc)) cands.push({ digit, cost: bc });
            }
            if (cands.length === 0) return null;
            cands.sort((u, v) => u.cost - v.cost);
            perDigit.push(cands.slice(0, keep));
        }
        return perDigit;
    }

    // Estimate the barcode's extent from local variance: the code region is where the
    // profile oscillates. Returns {xl, xr} in px or null. This anchors the module grid
    // directly instead of hoping a blind (x0, mw) sweep ranks the true basin first.
    function findEnvelope(profile) {
        const N = profile.length;
        const win = Math.round(2.5 / STEP);
        const act = new Float64Array(N);
        let maxAct = 0;
        for (let i = win; i < N - win; i++) {
            let mn = Infinity, mx = -Infinity;
            for (let j = i - win; j <= i + win; j++) {
                if (profile[j] < mn) mn = profile[j];
                if (profile[j] > mx) mx = profile[j];
            }
            act[i] = mx - mn;
            if (act[i] > maxAct) maxAct = act[i];
        }
        if (maxAct < 20) return null;
        const thr = maxAct * 0.25;
        // longest active run, tolerating short quiet gaps inside the code
        let bestL = -1, bestR = -1, curL = -1, gap = 0;
        const maxGap = Math.round(5 / STEP);
        for (let i = 0; i < N; i++) {
            if (act[i] >= thr) {
                if (curL < 0) curL = i;
                gap = 0;
            } else if (curL >= 0) {
                gap++;
                if (gap > maxGap) {
                    const r = i - gap;
                    if (bestL < 0 || (r - curL) > (bestR - bestL)) { bestL = curL; bestR = r; }
                    curL = -1;
                }
            }
        }
        if (curL >= 0) {
            const r = N - 1 - gap;
            if (bestL < 0 || (r - curL) > (bestR - bestL)) { bestL = curL; bestR = r; }
        }
        if (bestL < 0) return null;
        return { xl: bestL * STEP, xr: bestR * STEP };
    }

    // Decode one profile. Returns candidate pool + seed grids, or null.
    function decodeProfile(profile, opts) {
        const o = opts || {};
        const env = findEnvelope(profile);
        if (!env) return null;
        const spanMw = (env.xr - env.xl) / 95;
        if (spanMw < 0.7 || spanMw > 3.0) return null;

        // Seed grids: jitter around the envelope estimate, ranked by guard cost.
        // Asymmetric ranges: the envelope's left edge triggers early on the label
        // boundary (so the true x0 sits to the right), and printed glyphs flanking the
        // code inflate the span (so the true mw sits below the span estimate).
        const shortlist = [];
        for (const sigmaM of [0.9, 1.3])
            for (const e of [0.15, 0.3])
                for (let dm = -0.16; dm <= 0.05; dm += 0.01)
                    for (let dx = -2; dx <= 8; dx += 0.5)
                        for (const q of [-0.0002, 0, 0.0002]) {
                            const p = { x0: env.xl + dx, mw: spanMw + dm, q, sigmaM, e };
                            if (p.mw < 0.7) continue;
                            const c = guardCost(profile, p);
                            if (isFinite(c)) shortlist.push({ ...p, gc: c });
                        }
        if (shortlist.length === 0) return null;
        shortlist.sort((u, v) => u.gc - v.gc);

        // Re-rank the guard survivors by "free-digit fit": the summed cost of the
        // best-fitting pattern in each of the 12 digit windows. A misaligned grid's
        // windows straddle real bar boundaries and fit nothing well, so this metric
        // finds the true grid even when flanking print fools guards or the envelope.
        const freeDigitFit = (g) => {
            const cdf = cdfFor(g.sigmaM * g.mw);
            const pos = gridPos(g);
            let sum = 0;
            for (let d = 0; d < 12; d++) {
                const startModule = d < 6 ? 3 + 7 * d : 50 + 7 * (d - 6);
                const table = d < 6 ? L : R;
                let best = Infinity;
                for (let digit = 0; digit < 10; digit++) {
                    const c = segCost(profile, pos, g.mw, cdf, g.e, startModule, table[digit]);
                    if (c < best) best = c;
                }
                sum += best;
            }
            return sum;
        };
        for (const g of shortlist.slice(0, 150)) g.fd = g.gc + freeDigitFit(g);
        const ranked = shortlist.slice(0, 150).sort((u, v) => u.fd - v.fd);

        // Keep diversity across mw buckets anyway (cheap insurance).
        const buckets = new Map();
        for (const g of ranked) {
            const b = Math.round(g.mw / 0.03);
            if (!buckets.has(b)) buckets.set(b, g);
        }
        const stratified = [...buckets.values()].sort((u, v) => u.fd - v.fd);

        // Stage 2: for each top grid, EM-style decode -> refit grid on the decoded
        // template -> re-decode. Collect checksum-valid candidates along the way.
        const candMap = new Map();
        const noteCandidates = (perDigit, grid) => {
            // beams over the first 11 digits; digit 12 forced by checksum.
            let beams = [{ digits: '', cost: 0 }];
            for (let d = 0; d < 11; d++) {
                const next = [];
                for (const b of beams)
                    for (const c of perDigit[d])
                        next.push({ digits: b.digits + c.digit, cost: b.cost + c.cost });
                next.sort((u, v) => u.cost - v.cost);
                beams = next.slice(0, 200);
            }
            for (const b of beams.slice(0, 12)) {
                const full = b.digits + forcedCheckDigit(b.digits);
                if (!candMap.has(full)) candMap.set(full, grid);
            }
            // also standard 12-digit beams filtered by checksum
            let beams12 = [{ digits: '', cost: 0 }];
            for (let d = 0; d < 12; d++) {
                const next = [];
                for (const b of beams12)
                    for (const c of perDigit[d])
                        next.push({ digits: b.digits + c.digit, cost: b.cost + c.cost });
                next.sort((u, v) => u.cost - v.cost);
                beams12 = next.slice(0, 200);
            }
            for (const b of beams12) {
                if (checksumOk(b.digits) && !candMap.has(b.digits)) candMap.set(b.digits, grid);
                if (candMap.size > 80) break;
            }
        };

        const emGrids = [];
        for (const seed of stratified.slice(0, o.grids || 8)) {
            let g = { ...seed };
            for (let iter = 0; iter < 3; iter++) {
                const perDigit = digitTable(profile, g, 4);
                if (!perDigit) break;
                noteCandidates(perDigit, g);
                // refit the grid against the current best full template
                const best11 = perDigit.slice(0, 11).map(pd => pd[0].digit).join('');
                const bestStr = best11 + forcedCheckDigit(best11);
                const refined = refine((p) => fullCost(profile, bestStr, p), g, 2);
                if (Math.abs(refined.cost - (g.lastCost || Infinity)) < 1e-6) { g = refined; break; }
                refined.lastCost = refined.cost;
                g = refined;
            }
            emGrids.push(g);
        }
        if (candMap.size === 0) return null;

        // Hand the candidate pool and grids to the caller; verification happens
        // jointly across bands in decodeJoint. guardGrids are the raw stage-1 winners
        // per mw cluster — neutral seeds anchored only on guards/quiet zones.
        return { candMap, grids: emGrids, guardGrids: stratified.slice(0, 5), shortlistLen: shortlist.length };
    }

    // Geometry-only refinement of a candidate against one profile under frozen physics.
    // Blur and ink spread are properties of the frame, not of a candidate — leaving
    // them free lets wrong strings hide behind extra smear.
    function geoRefine(profile, digits, seed, phys) {
        let cur = { x0: seed.x0, mw: seed.mw, q: seed.q || 0, r: seed.r || 0, ...phys };
        let curCost = fullCost(profile, digits, cur);
        for (let pass = 0; pass < 4; pass++) {
            let improved = false;
            for (const [key, deltas] of [
                ['x0', [-0.5, -0.25, 0.25, 0.5]],
                ['mw', [-0.02, -0.01, 0.01, 0.02]],
                ['q', [-0.00015, 0.00015]],
                ['r', [-0.000003, 0.000003]],
            ]) {
                for (const d of deltas) {
                    const t = { ...cur, [key]: cur[key] + d };
                    const c = fullCost(profile, digits, t);
                    if (c < curCost) { cur = t; curCost = c; improved = true; }
                }
            }
            if (!improved) break;
        }
        return curCost;
    }

    const PHYSICS = [{ sigmaM: 0.9, e: 0.2 }, { sigmaM: 1.2, e: 0.2 }, { sigmaM: 1.5, e: 0.2 }];

    // Decode across several band profiles of the same barcode. A wrong string can fit
    // one noisy band; only the true one fits all of them — the within-frame version of
    // multi-frame fusion. Returns {digits, ratio} or null; ratio near 1 = unsure.
    function decodeJoint(profiles, opts) {
        const o = opts || {};
        // Candidate generation on each profile; collect neutral guard-anchored seeds.
        const pool = new Map();
        const seedPool = [];
        for (const prof of profiles) {
            const gen = decodeProfile(prof, o);
            if (!gen) continue;
            for (const [digits, seed] of gen.candMap) if (!pool.has(digits)) pool.set(digits, seed);
            seedPool.push(...gen.guardGrids.slice(0, 4));
        }
        if (pool.size === 0) return null;
        // Dedupe seeds by mw cluster across profiles, keep up to 6 diverse basins.
        const seedBuckets = new Map();
        for (const s of seedPool) {
            const b = Math.round(s.mw / 0.05);
            if (!seedBuckets.has(b) || s.gc < seedBuckets.get(b).gc) seedBuckets.set(b, s);
        }
        const seeds = [...seedBuckets.values()].sort((u, v) => u.gc - v.gc).slice(0, 6);
        const jointOf = new Map();
        const jointScore = (digits) => {
            if (jointOf.has(digits)) return jointOf.get(digits);
            let s = 0;
            for (const prof of profiles)
                for (const phys of PHYSICS) {
                    let best = Infinity;
                    for (const seed of seeds) {
                        const c = geoRefine(prof, digits, seed, phys);
                        if (c < best) best = c;
                    }
                    s += best;
                }
            jointOf.set(digits, s);
            return s;
        };
        // cheap pre-rank on the first profile / middle physics only
        const cheap = new Map();
        const cheapScore = (digits) => {
            if (!cheap.has(digits)) {
                let best = Infinity;
                for (const seed of seeds.slice(0, 2)) {
                    const c = geoRefine(profiles[0], digits, seed, PHYSICS[1]);
                    if (c < best) best = c;
                }
                cheap.set(digits, best);
            }
            return cheap.get(digits);
        };

        const ranked = [...pool.keys()].sort((a, b) => cheapScore(a) - cheapScore(b)).slice(0, o.verify || 10);
        let best = null;
        for (const digits of ranked) {
            const s = jointScore(digits);
            if (!best || s < jointOf.get(best)) best = digits;
        }

        // Hill-climb repairs: single and adjacent-pair substitutions (check digit
        // forced). Pairs matter because digit patterns overlap under blur, so errors
        // come in runs that no single substitution can escape. Moves are screened with
        // a static full-code cost on the current best geometry (very cheap), and only
        // the most promising are joint-verified.
        for (let iter = 0; iter < (o.repairIters === undefined ? 4 : o.repairIters); iter++) {
            let improved = false;
            let screenGeo = null;
            const staticScore = (digits) => {
                if (screenGeo === null) {
                    // reuse the refined geometry of the current best as the screening grid
                    let cur = { x0: seeds[0].x0, mw: seeds[0].mw, q: seeds[0].q || 0, r: seeds[0].r || 0, ...PHYSICS[1] };
                    let curCost = fullCost(profiles[0], best, cur);
                    for (let pass = 0; pass < 4; pass++) {
                        let imp = false;
                        for (const [key, deltas] of [['x0', [-0.5, -0.25, 0.25, 0.5]], ['mw', [-0.02, -0.01, 0.01, 0.02]], ['q', [-0.00015, 0.00015]], ['r', [-0.000003, 0.000003]]]) {
                            for (const d of deltas) {
                                const t = { ...cur, [key]: cur[key] + d };
                                const c = fullCost(profiles[0], best, t);
                                if (c < curCost) { cur = t; curCost = c; imp = true; }
                            }
                        }
                        if (!imp) break;
                    }
                    screenGeo = cur;
                }
                return fullCost(profiles[0], digits, screenGeo);
            };

            const repairs = new Set();
            for (let p = 0; p < 11; p++)
                for (let digit = 0; digit < 10; digit++) {
                    if (+best[p] === digit) continue;
                    const d11 = best.slice(0, p) + digit + best.slice(p + 1, 11);
                    repairs.add(d11 + forcedCheckDigit(d11));
                }
            for (let p = 0; p < 10; p++)
                for (let d1 = 0; d1 < 10; d1++)
                    for (let d2 = 0; d2 < 10; d2++) {
                        if (+best[p] === d1 && +best[p + 1] === d2) continue;
                        const d11 = best.slice(0, p) + d1 + d2 + best.slice(p + 2, 11);
                        repairs.add(d11 + forcedCheckDigit(d11));
                    }

            const screened = [...repairs].map(c => [c, staticScore(c)]).sort((u, v) => u[1] - v[1]);
            for (const [cand] of screened.slice(0, 10)) {
                if (jointScore(cand) < jointOf.get(best)) { best = cand; improved = true; }
            }
            if (!improved) break;
        }

        const sorted = [...jointOf.entries()].sort((u, v) => u[1] - v[1]);
        const top = sorted[0];
        const second = sorted.find(s => s[0] !== top[0]);
        return {
            digits: top[0],
            cost: top[1],
            ratio: second ? top[1] / second[1] : 0,
            nCands: pool.size,
        };
    }

    // Extract a barness profile from ImageData: red channel (colored inks stay dark),
    // rows averaged with shear, normalized against a rolling white level.
    function extractProfile(img, xa, xb, y1, y2, shear) {
        const W = img.width;
        const data = img.data;
        const redAt = (x, y) => {
            const xi = Math.max(0, Math.min(W - 2, Math.floor(x)));
            const fx = x - xi;
            const row = y * W;
            return data[(row + xi) * 4] * (1 - fx) + data[(row + xi + 1) * 4] * fx;
        };
        const n = Math.round((xb - xa) / STEP);
        const prof = new Float64Array(n);
        const rows = y2 - y1;
        for (let i = 0; i < n; i++) {
            let sum = 0;
            for (let y = y1; y < y2; y++)
                sum += redAt(xa + i * STEP + shear * ((y - y1) / rows - 0.5), y);
            prof[i] = sum / rows;
        }
        const win = Math.round(20 / STEP);
        const out = new Float64Array(n);
        for (let i = 0; i < n; i++) {
            let mx = 0;
            for (let j = Math.max(0, i - win); j < Math.min(n, i + win); j++)
                if (prof[j] > mx) mx = prof[j];
            out[i] = Math.max(0, mx - prof[i]);
        }
        return out;
    }

    const reverseProfile = (p) => {
        const r = new Float64Array(p.length);
        for (let i = 0; i < p.length; i++) r[i] = p[p.length - 1 - i];
        return r;
    };

    // Synthesize a profile for tests: module widths via mwFn, blur sigmaPx, ink spread ePx.
    function synthProfile(digits, mwFn, sigmaPx, xoff, ePx) {
        const mods = buildModules(digits);
        const edges = [xoff];
        for (let k = 0; k < 95; k++) edges.push(edges[k] + mwFn(k));
        const n = Math.round((edges[95] + 14) / STEP);
        const prof = new Float64Array(n);
        const cdf = cdfFor(sigmaPx);
        for (let i = 0; i < n; i++) {
            const x = i * STEP;
            let v = 0;
            for (let k = 0; k < 95; k++)
                if (mods[k] === '1') v += cdf(x - edges[k] + ePx) - cdf(x - edges[k + 1] - ePx);
            prof[i] = Math.min(v, 1) * 150;
        }
        return prof;
    }

    function selfTest() {
        // Each case gets two slightly different "bands" (jitter in blur/offset), like
        // the two halves of a real scan band.
        const cases = [
            ['straight', ['713733788632', () => 1.05, [1.0, 1.1], 12, 0.2]],
            ['curved', ['713733788632', (k) => 1.05 + 0.0006 * k, [1.1, 1.0], 12, 0.25]],
            ['clean', ['036000291452', () => 1.3, [0.8, 0.9], 10, 0.1]],
        ];
        const out = {};
        for (const [name, [digits, mwFn, sigmas, xoff, ePx]] of cases) {
            const profs = sigmas.map(s => synthProfile(digits, mwFn, s, xoff, ePx));
            const r = decodeJoint(profs, { mwMin: 0.9, mwMax: 1.6 });
            out[name] = r ? r.digits + ' ratio=' + r.ratio.toFixed(3) : 'FAIL';
        }
        return out;
    }

    window.UpcWaveform = { decodeProfile, decodeJoint, extractProfile, reverseProfile, synthProfile, selfTest, fullCost, refine, buildModules, guardCost, segCost, cdfFor, gridPos };
})();
