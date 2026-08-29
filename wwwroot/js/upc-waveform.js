// UPC-A waveform decoder: reads blurry barcodes that binarizing decoders cannot.
// Approach (what commercial engines do for defocused/tiny 1D): average the scan band
// into a 1D analog profile, model the printed code as bars-with-ink-spread convolved
// with a blur kernel, anchor a (possibly curved) module grid on the guard patterns,
// decode digits by maximum-likelihood template matching, and verify checksum-valid
// candidates by full-code fit. No thresholding anywhere.
//
// Live entry point is scanBand(img, y1, y2): locates the code in a horizontal band,
// rescales it to the decoder's ~1.2 px/module sweet spot, and decodes both
// orientations. It refuses codes under minMwPx (default 2 px/module): below that a
// single frame is information-ambiguous (a wrong checksum-valid code can fit the
// pixels better than the truth — measured on a real 1 px/module frame), and a
// confident misread is worse than no read. The target regime is CLOSE-HELD codes on
// webcams that cannot focus close: large modules, heavy defocus blur.
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

    // Digit pairs whose bar patterns differ by one bar edge shifted one module
    // (adjacent 2-bit flips): under blur these are THE confusable pairs, and a
    // decode is only trustworthy if the data separates the winner from every
    // per-position cousin substitution.
    const COUSINS = (() => {
        const out = [];
        for (let a = 0; a < 10; a++) {
            const cs = [];
            for (let b = 0; b < 10; b++) {
                if (b === a) continue;
                const diff = [];
                for (let k = 0; k < 7; k++) if (L[a][k] !== L[b][k]) diff.push(k);
                if (diff.length === 2 && diff[1] - diff[0] === 1) cs.push(b);
            }
            out.push(cs);
        }
        // 1 and 7 differ by two NON-adjacent flips, so the shift rule misses them —
        // but it is the classic blur confusion and digit 1's only rival; without it
        // every '1' in a winner would go entirely uncertified by this sweep.
        out[1].push(7);
        out[7].push(1);
        return out;
    })();

    // Gaussian edge response: CDF of N(0, s^2), via a lookup table (the hot loop
    // calls this constantly). Defocused webcam edges have long tails; the earlier
    // quadratic sigmoid clipped at +/-2s mis-ranked digits under heavy blur.
    const CDF_N = 256, CDF_R = 4;
    const CDF_T = (() => {
        const t = new Float64Array(CDF_N + 1);
        for (let i = 0; i <= CDF_N; i++) {
            const z = -CDF_R + (2 * CDF_R * i) / CDF_N;
            t[i] = 0.5 * (1 + Math.tanh(0.7978845608 * z * (1 + 0.044715 * z * z)));
        }
        return t;
    })();
    const cdfFor = (s) => {
        const k = CDF_N / (2 * CDF_R);
        const f = (x) => {
            const u = (x / s + CDF_R) * k;
            if (u <= 0) return 0;
            if (u >= CDF_N) return 1;
            const i = u | 0;
            return CDF_T[i] + (CDF_T[i + 1] - CDF_T[i]) * (u - i);
        };
        // How far a bar edge's response reaches; segCost prunes bars beyond this.
        f.cut = Math.min(9, 2.5 * s + 1);
        return f;
    };

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
        // Bars are ordered and non-nested, so a two-pointer sweep visits only the
        // few bars near each sample instead of all of them (this is the hot loop).
        const cut = cdf.cut || 3;
        let lo = 0;
        for (let i = ia; i <= ib; i++) {
            const x = i * STEP;
            while (lo < bars.length && bars[lo][1] + cut < x) lo++;
            let v = 0;
            for (let b = lo; b < bars.length; b++) {
                const ba = bars[b][0];
                if (ba - cut > x) break;
                v += cdf(x - ba) - cdf(x - bars[b][1]);
            }
            v = Math.min(v, 1);
            tv[i - ia] = v;
            st += v; so += profile[i]; stt += v * v; sto += v * profile[i]; n++;
        }
        const denom = stt - st * st / n;
        let g = denom > 1e-9 ? (sto - st * so / n) / denom : 0;
        // Ink darkness is a property of the frame, not of a segment: clamp the fitted
        // amplitude to the profile's global bar amplitude (profile.amp, set during
        // candidate generation). Without this, an over-blurred wrong template can
        // "amplify" its way into matching any short window it likes.
        const amp = profile.amp;
        if (amp && g > 0) g = Math.max(0.35 * amp, Math.min(1.4 * amp, g));
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
        // Extended guard templates: the modules flanking each guard are fixed by UPC
        // structure (every L digit starts 0 and ends 1; every R digit starts 1 and
        // ends 0), so model them. Without the flanking bars the physics fit absorbs
        // their unmodeled ink bleeding into the window by inflating blur + ink spread.
        const a = segCost(profile, pos, p.mw, cdf, p.e, 0, '1010');
        if (!isFinite(a)) return Infinity;
        const b = segCost(profile, pos, p.mw, cdf, p.e, 44, '1010101');
        const c = segCost(profile, pos, p.mw, cdf, p.e, 91, '0101');
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

    // Summed cost of the best-fitting pattern in each of the 12 digit windows —
    // a digit-agnostic measure of how well a grid + physics explains the code.
    function freeDigitFit(profile, g) {
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
                    if (t.sigmaM < 0.4 || t.sigmaM > 2.6 || t.e < 0 || t.e > 0.4 || t.mw < 0.8 || t.mw > 1.6) continue;
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
    // With ctxDigits (a current 12-digit hypothesis), each digit is scored IN CONTEXT:
    // the window extends 2 modules into the neighbors and the template renders their
    // modules from the hypothesis. Under heavy blur neighbor ink bleeds well into a
    // digit's own window, and context-free windows systematically mis-rank digits.
    function digitTable(profile, g, keep, ctxDigits) {
        const cdf = cdfFor(g.sigmaM * g.mw);
        const pos = gridPos(g);
        const ctxMods = ctxDigits ? buildModules(ctxDigits) : null;
        const perDigit = [];
        for (let d = 0; d < 12; d++) {
            const startModule = d < 6 ? 3 + 7 * d : 50 + 7 * (d - 6);
            const table = d < 6 ? L : R;
            const cands = [];
            for (let digit = 0; digit < 10; digit++) {
                let bc = Infinity;
                for (const sf of [-0.15, 0, 0.15]) {
                    const valStr = ctxMods
                        ? ctxMods.slice(startModule - 2, startModule) + table[digit] + ctxMods.slice(startModule + 7, startModule + 9)
                        : table[digit];
                    const c = segCost(profile, pos, g.mw, cdf, g.e, ctxMods ? startModule - 2 : startModule, valStr, sf * g.mw);
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
        const thr = maxAct * 0.22;
        // longest active run, tolerating quiet gaps inside the code (dense digit runs
        // under Gaussian-tailed blur can sit locally flat for several px)
        let bestL = -1, bestR = -1, curL = -1, gap = 0;
        const maxGap = Math.round(8 / STEP);
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
    // opts.env overrides the variance-envelope extent estimate — scanBand already
    // localized the code in the image, and findEnvelope's activity window splits
    // heavily-blurred codes that locate() handles fine.
    function decodeProfile(profile, opts) {
        const o = opts || {};
        const env = o.env || findEnvelope(profile);
        if (!env) return null;
        const spanMw = (env.xr - env.xl) / 95;
        if (spanMw < 0.7 || spanMw > 3.0) return null;

        // Global bar amplitude (95th percentile of barness inside the code): the
        // frame-level ink darkness that segCost clamps its per-segment fits to.
        const ia = Math.round(env.xl / STEP), ib = Math.round(env.xr / STEP);
        const inside = [...profile.slice(Math.max(0, ia), Math.min(profile.length, ib))].sort((a, b) => a - b);
        if (inside.length > 20) profile.amp = inside[Math.floor(inside.length * 0.95)];

        // Seed grids: jitter around the envelope estimate, ranked by guard cost.
        // Asymmetric ranges: the envelope's left edge triggers early on the label
        // boundary (so the true x0 sits to the right), and printed glyphs flanking the
        // code inflate the span (so the true mw sits below the span estimate).
        const shortlist = [];
        for (const sigmaM of [0.9, 1.3])
            for (const e of [0.15, 0.3])
                for (let dm = -0.18; dm <= 0.05; dm += 0.01)
                    for (let dx = -2; dx <= 10; dx += 0.5)
                        for (const q of [-0.0002, 0, 0.0002]) {
                            const p = { x0: env.xl + dx, mw: spanMw + dm, q, sigmaM, e };
                            if (p.mw < 0.7) continue;
                            const c = guardCost(profile, p);
                            if (isFinite(c)) shortlist.push({ ...p, gc: c });
                        }
        if (shortlist.length === 0) return null;
        if (o.deadline && Date.now() >= o.deadline) return null;
        shortlist.sort((u, v) => u.gc - v.gc);

        // Re-rank the guard survivors by "free-digit fit" (see freeDigitFit above):
        // a misaligned grid's windows straddle real bar boundaries and fit nothing
        // well, so this finds the true grid even when flanking print fools guards.
        for (const g of shortlist.slice(0, 150)) g.fd = g.gc + freeDigitFit(profile, g);
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
            if (o.deadline && Date.now() >= o.deadline) break;
            // Physics pre-fit on the known structure (guards + quiet zones), so the
            // first digit table already uses the frame's real blur/ink-spread instead
            // of the seed's coarse guess — with heavy defocus the guess mis-ranks
            // digits badly enough that the truth never enters the beams.
            let g = refine((p) => guardCost(profile, p), { ...seed }, 3);
            let ctx = null;
            for (let iter = 0; iter < 3; iter++) {
                const perDigit = digitTable(profile, g, 4, ctx);
                if (!perDigit) break;
                noteCandidates(perDigit, g);
                // refit the grid against the current best full template
                const best11 = perDigit.slice(0, 11).map(pd => pd[0].digit).join('');
                const bestStr = best11 + forcedCheckDigit(best11);
                ctx = bestStr;
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
        return { cost: curCost, geo: cur };
    }

    // Decode across several band profiles of the same barcode. A wrong string can fit
    // one noisy band; only the true one fits all of them — the within-frame version of
    // multi-frame fusion. Returns {digits, ratio} or null; ratio near 1 = unsure.
    function decodeJoint(profiles, opts) {
        const o = opts || {};
        // Candidate generation on each profile; collect neutral guard-anchored seeds.
        const pool = new Map();
        const seedPool = [];
        for (const prof of profiles) {
            if (o.deadline && Date.now() >= o.deadline && pool.size > 0) break;
            const gen = decodeProfile(prof, o);
            if (!gen) continue;
            for (const [digits, seed] of gen.candMap) if (!pool.has(digits)) pool.set(digits, seed);
            seedPool.push(...gen.guardGrids.slice(0, 4));
        }
        if (pool.size === 0) return null;
        // Dedupe seeds by mw cluster across profiles, keep a few diverse basins.
        const seedBuckets = new Map();
        for (const s of seedPool) {
            const b = Math.round(s.mw / 0.05);
            if (!seedBuckets.has(b) || s.gc < seedBuckets.get(b).gc) seedBuckets.set(b, s);
        }
        const seeds = [...seedBuckets.values()].sort((u, v) => u.gc - v.gc).slice(0, 4);

        // Estimate the FRAME's physics (blur + ink spread), then freeze it for every
        // candidate. Blur varies hugely with distance/focus, so a fixed value cannot
        // work; but it is a property of the frame, not of a candidate — fitting it
        // per-candidate lets wrong strings hide behind extra smear. Fit it on the
        // KNOWN structure (guards + quiet zones, summed over bands, amplitude-clamped)
        // so no candidate string can bias it; verify under a frozen bracket around it.
        const seed0 = { x0: seeds[0].x0, mw: seeds[0].mw, q: seeds[0].q || 0, r: seeds[0].r || 0 };
        // Guards alone (15 modules) let noise inflate the blur estimate; the free-digit
        // fit adds the other 84 modules of real bars without committing to any digits.
        // (Fitting on a concrete candidate string was tried instead and is WORSE: its
        // wrong digits poison the fit unpredictably.) Blur and geometry couple (extra
        // blur absorbs misregistration), so coordinate descent over both walks to
        // over-blurred local minima: sweep sigma explicitly with a geometry-only refit
        // at each value, then polish at the winner.
        const physCost1 = (p) => guardCost(profiles[0], p) + freeDigitFit(profiles[0], p);
        const geoMoves = [
            ['x0', [-0.5, -0.25, 0.25, 0.5]],
            ['mw', [-0.02, -0.01, 0.01, 0.02]],
            ['q', [-0.00015, 0.00015]],
            ['r', [-0.000003, 0.000003]],
        ];
        let sweepBest = null;
        for (const sig of [0.5, 0.65, 0.8, 0.95, 1.1, 1.3, 1.55, 1.8, 2.1, 2.4]) {
            if (o.deadline && Date.now() >= o.deadline && sweepBest) break;
            let cur = { ...seed0, sigmaM: sig, e: 0.1 };
            let curCost = physCost1(cur);
            for (let pass = 0; pass < 3; pass++) {
                let imp = false;
                for (const [key, deltas] of geoMoves)
                    for (const d of deltas) {
                        const t = { ...cur, [key]: cur[key] + d };
                        const c = physCost1(t);
                        if (c < curCost) { cur = t; curCost = c; imp = true; }
                    }
                if (!imp) break;
            }
            if (!sweepBest || curCost < sweepBest.cost) sweepBest = { ...cur, cost: curCost };
        }
        const fitted = refine(
            (p) => profiles.reduce((s, prof) => s + guardCost(prof, p) + freeDigitFit(prof, p), 0),
            sweepBest, 3);
        // Fit quality normalized by ink amplitude: a real barcode's guards + free
        // digits fit to within noise, while barcode-less texture leaves residuals on
        // the order of the amplitude itself. Callers use this to bail out early.
        let amp2 = 0;
        for (const prof of profiles) amp2 += (prof.amp || 1) * (prof.amp || 1);
        const fitNorm = fitted.cost / amp2;
        const clampS = (s) => Math.max(0.4, Math.min(2.6, s));
        const PHYS = [
            { sigmaM: clampS(fitted.sigmaM * 0.75), e: fitted.e },
            { sigmaM: clampS(fitted.sigmaM), e: fitted.e },
            { sigmaM: clampS(fitted.sigmaM * 1.25), e: fitted.e },
        ];

        // Joint score: per profile, refine geometry once under the center physics
        // (best over seeds), then rescore that geometry under the bracket ends.
        const jointOf = new Map();
        const jointScore = (digits) => {
            if (jointOf.has(digits)) return jointOf.get(digits);
            let s = 0;
            for (const prof of profiles) {
                let best = null;
                for (const seed of seeds) {
                    const r = geoRefine(prof, digits, seed, PHYS[1]);
                    if (!best || r.cost < best.cost) best = r;
                }
                s += best.cost;
                s += fullCost(prof, digits, { ...best.geo, ...PHYS[0] });
                s += fullCost(prof, digits, { ...best.geo, ...PHYS[2] });
            }
            jointOf.set(digits, s);
            return s;
        };
        // cheap pre-rank on the first profile / center physics only
        const cheap = new Map();
        const cheapScore = (digits) => {
            if (!cheap.has(digits)) {
                let best = Infinity;
                for (const seed of seeds.slice(0, 2)) {
                    const c = geoRefine(profiles[0], digits, seed, PHYS[1]).cost;
                    if (c < best) best = c;
                }
                cheap.set(digits, best);
            }
            return cheap.get(digits);
        };

        const ranked = [...pool.keys()].sort((a, b) => cheapScore(a) - cheapScore(b)).slice(0, o.verify || 14);
        let best = null;
        for (const digits of ranked) {
            const s = jointScore(digits);
            if (!best || s < jointOf.get(best)) best = digits;
            if (o.deadline && Date.now() >= o.deadline) break;
        }

        // Hill-climb repairs: single and adjacent-pair substitutions (check digit
        // forced). Pairs matter because digit patterns overlap under blur, so errors
        // come in runs that no single substitution can escape. Moves are screened with
        // a static full-code cost on the current best geometry (very cheap), and only
        // the most promising are joint-verified.
        const dl = o.deadline || Infinity;
        for (let iter = 0; iter < (o.repairIters === undefined ? 4 : o.repairIters); iter++) {
            if (Date.now() >= dl) break;
            let improved = false;
            let screenGeos = null;
            // Screen repairs on two independent bands (top and bottom) with geometry
            // refit to the current best: one band's noise can mis-rank the repairs
            // that lead toward the truth.
            const screenProfs = profiles.length > 1 ? [profiles[0], profiles[profiles.length - 1]] : [profiles[0]];
            const staticScore = (digits) => {
                if (screenGeos === null)
                    screenGeos = screenProfs.map(prof =>
                        geoRefine(prof, best, seeds[0], PHYS[1]).geo);
                let s = 0;
                for (let i = 0; i < screenProfs.length; i++) s += fullCost(screenProfs[i], digits, screenGeos[i]);
                return s;
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
            // Blur flips digits independently at DISTANT positions too (each a local
            // one-bar-shift cousin confusion), and no chain of improving single moves
            // connects them (a lone fix breaks the forced check digit, so it screens
            // badly even when correct). Enumerate all distant cousin-substitution
            // PAIRS of the current best — the exact two-flip degeneracy family.
            for (let p = 0; p < 11; p++)
                for (const c1 of COUSINS[+best[p]])
                    for (let q = p + 1; q < 11; q++)
                        for (const c2 of COUSINS[+best[q]]) {
                            const chars = best.slice(0, 11).split('');
                            chars[p] = String(c1);
                            chars[q] = String(c2);
                            const d11 = chars.join('');
                            repairs.add(d11 + forcedCheckDigit(d11));
                        }
            // Also combine the best local alternatives of any kind, found by LOCAL
            // in-context window cost (check-digit-free).
            if (screenGeos === null) staticScore(best); // materialize screenGeos
            const tbl = digitTable(profiles[0], screenGeos[0], 10, best);
            if (tbl) {
                const alts = [];
                for (let p = 0; p < 11; p++) {
                    const cur = tbl[p].find(c => c.digit === +best[p]);
                    const base = cur ? cur.cost : tbl[p][tbl[p].length - 1].cost;
                    for (const c of tbl[p]) {
                        if (c.digit === +best[p]) continue;
                        alts.push({ p, digit: c.digit, delta: c.cost - base });
                    }
                }
                alts.sort((u, v) => u.delta - v.delta);
                const topAlts = alts.slice(0, 6);
                for (let i = 0; i < topAlts.length; i++)
                    for (let j = i + 1; j < topAlts.length; j++) {
                        const a = topAlts[i], b = topAlts[j];
                        if (a.p === b.p) continue;
                        const chars = best.slice(0, 11).split('');
                        chars[a.p] = String(a.digit);
                        chars[b.p] = String(b.digit);
                        const d11 = chars.join('');
                        repairs.add(d11 + forcedCheckDigit(d11));
                    }
            }

            const screened = [...repairs].map(c => [c, staticScore(c)]).sort((u, v) => u[1] - v[1]);
            for (const [cand] of screened.slice(0, 14)) {
                if (jointScore(cand) < jointOf.get(best)) { best = cand; improved = true; }
            }
            if (!improved) break;
        }

        // Lab diagnostics: force named strings (e.g. the known truth) through the
        // full joint scorer so their standing is visible.
        if (o.mustScore) for (const d of o.mustScore) jointScore(d);

        const sorted = [...jointOf.entries()].sort((u, v) => u[1] - v[1]);
        const top = sorted[0];
        const second = sorted.find(s => s[0] !== top[0]);

        // Cousin margin: score every one-bar-shift substitution of the winner
        // (checksum-free — this measures pure visual information, and real misreads
        // are checksum-valid anyway). If any cousin comes close, the frame does not
        // actually determine those digits. Past the deadline it cannot be computed,
        // and an uncertified winner must not be accepted: report zero margin.
        let cousinRatio = Date.now() >= dl ? 0 : Infinity;
        if (cousinRatio > 0)
            for (let p = 0; p < 12; p++)
                for (const cd of COUSINS[+top[0][p]]) {
                    const alt = top[0].slice(0, p) + cd + top[0].slice(p + 1);
                    const cs = jointScore(alt);
                    // Guard the top[1]=0 perfect-fit edge: a cousin also at 0 means
                    // indistinguishable (refuse); any positive cousin cost means
                    // infinitely separated.
                    const r = top[1] > 0 ? cs / top[1] : (cs > 0 ? Infinity : 0);
                    if (r < cousinRatio) cousinRatio = r;
                }

        const out = {
            digits: top[0],
            cost: top[1],
            // No measured runner-up must fail CLOSED (ratio 1 = maximally unsure),
            // not open: "no competition found" is degeneracy, not confidence.
            ratio: second ? top[1] / second[1] : 1,
            cousinRatio,
            fitNorm,
            nCands: pool.size,
        };
        if (o.debug) {
            out.top = sorted.slice(0, 8).map(([d, c]) => d + ':' + Math.round(c));
            out.inPool = o.mustScore ? o.mustScore.map(d => pool.has(d)) : undefined;
            out.phys = PHYS[1];
        }
        return out;
    }

    // Extract a barness profile from ImageData: red channel (colored inks stay dark),
    // rows averaged with shear, normalized against a linear white baseline.
    // scale (default 1) shrinks the code into the decoder's ~1 px/module sweet spot:
    // one profile sample covers STEP*scale real pixels (box-averaged, so a close-held
    // 8 px/module code decodes with the exact machinery tuned on tiny codes).
    // anchors (optional): two real-x ranges [[a1,b1],[a2,b2]] known to be quiet-zone
    // white; the baseline is fitted through them instead of the window ends (the ends
    // often contain neighboring label print, which would drag the baseline down).
    function extractProfile(img, xa, xb, y1, y2, shear, scale, anchors) {
        const W = img.width;
        const data = img.data;
        const sc = scale || 1;
        const redAt = (x, y) => {
            const xi = Math.max(0, Math.min(W - 2, Math.floor(x)));
            const fx = x - xi;
            const row = y * W;
            return data[(row + xi) * 4] * (1 - fx) + data[(row + xi + 1) * 4] * fx;
        };
        const sub = Math.max(1, Math.round(sc));
        const redBox = (x, y) => {
            if (sub === 1) return redAt(x, y);
            let s = 0;
            for (let k = 0; k < sub; k++) s += redAt(x + (k - (sub - 1) / 2) * (sc * STEP / sub), y);
            return s / sub;
        };
        const n = Math.round((xb - xa) / (STEP * sc));
        const prof = new Float64Array(n);
        const rows = y2 - y1;
        for (let i = 0; i < n; i++) {
            let sum = 0;
            for (let y = y1; y < y2; y++)
                sum += redBox(xa + i * STEP * sc + shear * ((y - y1) / rows - 0.5), y);
            prof[i] = sum / rows;
        }
        // White baseline: linear fit through the robust white level of two known-white
        // regions. A rolling local max was used here before, but under heavy blur it
        // dips over ink-dense digit runs, compressing their contrast and systematically
        // biasing the model toward sparser (wrong) digit patterns.
        const toIdx = (x) => Math.max(0, Math.min(n - 1, Math.round((x - xa) / (STEP * sc))));
        const endN = Math.max(8, Math.round(n * 0.14));
        const ranges = anchors
            ? anchors.map(([a, b]) => [toIdx(a), Math.max(toIdx(a) + 4, toIdx(b))])
            : [[0, endN], [n - endN, n]];
        // High percentile: an anchor range may be mostly bars or dark clutter when
        // the located extent errs — any white minority must still set the baseline.
        const whiteOf = ([from, to]) => {
            const seg = [...prof.slice(from, to)].sort((a, b) => a - b);
            return seg[Math.floor(seg.length * 0.93)];
        };
        const wL = whiteOf(ranges[0]), wR = whiteOf(ranges[1]);
        const cL = (ranges[0][0] + ranges[0][1]) / 2, cR = (ranges[1][0] + ranges[1][1]) / 2;
        // Divide out the illumination ramp as well as subtracting it: ink contrast is
        // multiplicative in illumination, and the decoder fits one amplitude per
        // window, so a lighting gradient otherwise leaves a systematic misfit ramp.
        const bMean = (wL + wR) / 2;
        const out = new Float64Array(n);
        for (let i = 0; i < n; i++) {
            const base = Math.max(0.3 * bMean, wL + (wR - wL) * ((i - cL) / (cR - cL)));
            out[i] = Math.max(0, (base - prof[i]) * (bMean / base));
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

    // Find the barcode's horizontal extent in a band of ImageData. Returns up to 3
    // ranked candidates {xl, xr, mwEst, crossings, slope} in native px, or null.
    // slope is the bar lines' dx/dy tilt — a hand-held code is rarely upright, and
    // averaging straight down a tilted code smears columns by rows*tan(theta) px,
    // which destroys more information than the defocus itself. Only needs to be in
    // the ballpark: decodeProfile re-derives the precise extent after rescaling.
    function locate(img, y1, y2) {
        const W = img.width, data = img.data;
        if (W < 60 || y2 - y1 < 4) return null;
        const rows = y2 - y1;
        // Row-average over a NARROW central strip, not the full band: tilt smears the
        // full-band mean by slope*height px, hiding tilted codes from detection; a
        // ~60-row strip keeps that under a bar-group width while still averaging
        // noise well below the crossing hysteresis.
        const stripH = Math.min(64, rows);
        const sy1 = y1 + ((rows - stripH) >> 1), sy2 = sy1 + stripH;
        const v = new Float64Array(W);
        for (let y = sy1; y < sy2; y++) {
            const row = y * W;
            for (let x = 0; x < W; x++) v[x] += data[(row + x) * 4];
        }
        for (let x = 0; x < W; x++) v[x] /= stripH;

        // A barcode's signature is OSCILLATION: dozens of bar/space alternations of
        // the row-averaged brightness about its local mean. Edge ENERGY cannot find a
        // defocused code — a sharp background object (door frame, chair mesh) carries
        // far more of it than blurred bars, which is exactly how a real close-held
        // frame failed. So: collect amplitude-hysteresis crossing positions across
        // the whole band, then cluster them into dense chains — each chain is a
        // candidate extent. Isolated edges give a handful of crossings; small clutter
        // clusters give chains too narrow to be a code; blank regions give none.
        const halfS = 20;
        const xs = [];
        // Seed the rolling window with the state for x = -1, i.e. v[0..halfS-1]:
        // the first iteration adds v[halfS] itself. (Seeding through halfS double-
        // counted that sample FOREVER — a dark v[halfS] biased every local mean in
        // the band by more than the hysteresis threshold.)
        let acc = 0, n = 0;
        for (let x = 0; x < Math.min(W, halfS); x++) { acc += v[x]; n++; }
        let state = 0;
        for (let x = 0; x < W; x++) {
            const lo = x - halfS - 1, hi = x + halfS;
            if (lo >= 0) { acc -= v[lo]; n--; }
            if (hi < W) { acc += v[hi]; n++; }
            const d = v[x] - acc / n;
            if (d > 1.5) { if (state < 0) xs.push(x); state = 1; }
            else if (d < -1.5) { if (state > 0) xs.push(x); state = -1; }
        }
        if (xs.length < 16) return null;
        const crossStats = (xl, xr) => {
            let c = 0, first = -1, last = -1;
            for (const x of xs) if (x >= xl && x <= xr) { c++; if (first < 0) first = x; last = x; }
            return { c, first, last };
        };
        // Module width from the CROSSING SPAN, not the padded extent: the outermost
        // crossings sit at roughly modules 1 and 94, so span/92 is ~unbiased, while
        // extent/95 runs 10-25% high (blur tails, margins) — enough to sneak
        // sub-floor codes past the >=2 px/module ambiguity gate (measured).
        const mwOf = (st) => (st.last - st.first) / 92;

        const cands = [];

        // Generator 1 — edge-energy run (precise when the code is SHARP): the classic
        // strongest-run detector. When the code is defocused, a sharp background
        // object wins this run instead — but then it fails the crossings filter below
        // and simply drops out, costing nothing.
        {
            const g = new Float64Array(W);
            for (let x = 1; x < W - 1; x++) g[x] = Math.abs(v[x + 1] - v[x - 1]);
            const half = 15;
            const e = new Float64Array(W);
            let acc2 = 0;
            for (let x = 0; x < Math.min(W, 2 * half + 1); x++) acc2 += g[x];
            for (let x = half; x < W - half - 1; x++) {
                e[x] = acc2 / (2 * half + 1);
                acc2 += g[x + half + 1] - g[x - half];
            }
            let mx = 0;
            for (let x = 0; x < W; x++) if (e[x] > mx) mx = e[x];
            if (mx >= 4) {
                const thr = mx * 0.3, maxGap = 24;
                let bestL = -1, bestR = -1, curL = -1, gap = 0;
                for (let x = 0; x < W; x++) {
                    if (e[x] >= thr) {
                        if (curL < 0) curL = x;
                        gap = 0;
                    } else if (curL >= 0 && ++gap > maxGap) {
                        const r = x - gap;
                        if (r - curL > bestR - bestL) { bestL = curL; bestR = r; }
                        curL = -1;
                    }
                }
                if (curL >= 0 && (W - 1 - gap) - curL > bestR - bestL) { bestL = curL; bestR = W - 1 - gap; }
                if (bestL >= 0) {
                    // Hysteresis extension: blur pushes parts of the code (wide-space
                    // digit runs) under the strong threshold.
                    const lowThr = mx * 0.1, lowGap = 64;
                    for (let x = bestL - 1, g2 = 0, last = bestL; ; x--) {
                        if (x < 0 || ++g2 > lowGap) { bestL = last; break; }
                        if (e[x] >= lowThr) { last = x; g2 = 0; }
                    }
                    for (let x = bestR + 1, g2 = 0, last = bestR; ; x++) {
                        if (x >= W || ++g2 > lowGap) { bestR = last; break; }
                        if (e[x] >= lowThr) { last = x; g2 = 0; }
                    }
                    const width = bestR - bestL + 1;
                    const st = crossStats(bestL, bestR);
                    if (width >= 140 && width <= 1600 && st.c >= 16)
                        cands.push({ xl: bestL, xr: bestR, mwEst: mwOf(st), crossings: st.c });
                }
            }
        }

        // Generator 2 — crossing chains (finds DEFOCUSED codes that generator 1
        // cannot): split the crossing sequence into chains wherever the spacing
        // jumps well past the median — separate objects sit far apart, bars do not.
        {
            const spacings = [];
            for (let i = 1; i < xs.length; i++) spacings.push(xs[i] - xs[i - 1]);
            const median = [...spacings].sort((a, b) => a - b)[Math.floor(spacings.length / 2)];
            const splitAt = Math.max(50, 3 * median);
            const chains = [];
            let start = 0;
            for (let i = 1; i <= xs.length; i++) {
                if (i === xs.length || xs[i] - xs[i - 1] > splitAt) {
                    chains.push([start, i - 1]);
                    start = i;
                }
            }
            const chainCands = [];
            const emit = (a, b) => {
                const count = b - a + 1;
                if (count < 16) return; // a UPC has ~59 runs; severe defocus keeps ~24
                const wch = xs[b] - xs[a];
                const m = wch * 0.05 + 5; // outer bars extend slightly past their crossings
                const xl = Math.max(0, Math.round(xs[a] - m));
                const xr = Math.min(W - 1, Math.round(xs[b] + m));
                const width = xr - xl + 1;
                if (width < 140 || width > 1600) return;
                chainCands.push({ xl, xr, mwEst: wch / 92, crossings: count });
            };
            for (const [a, b] of chains) {
                emit(a, b);
                // A chain can be a code MERGED with neighboring print across one
                // moderate gap — and a merged extent decodes to garbage at the wrong
                // scale. Legit internal gaps and merge seams overlap in size, so emit
                // BOTH readings: the chain and its stricter sub-chains; the caller's
                // decodability pre-rank picks the real one.
                let s = a, split = false;
                for (let i = a + 1; i <= b; i++)
                    if (xs[i] - xs[i - 1] > 2.5 * median) {
                        if (i - 1 > s) emit(s, i - 1);
                        s = i;
                        split = true;
                    }
                if (split && b > s) emit(s, b);
            }
            chainCands.sort((u, w) => w.crossings - u.crossings);
            cands.push(...chainCands);
        }

        // The generators usually agree on a code-bearing region within a few px;
        // without dedup the same extent is decoded twice (seconds each). Dedup only
        // SIMILAR-width overlaps: a sub-chain nested inside a wider merged chain is a
        // different reading of the scene, not a duplicate. Keep the higher crossing
        // count; ties keep the earlier entry.
        const merged = [];
        for (const c of cands) {
            const cw = c.xr - c.xl;
            const i = merged.findIndex(mc => {
                const mw2 = mc.xr - mc.xl;
                const ov = Math.min(mc.xr, c.xr) - Math.max(mc.xl, c.xl);
                return ov > 0.6 * Math.min(mw2, cw) && Math.max(mw2, cw) < 1.35 * Math.min(mw2, cw);
            });
            if (i < 0) merged.push(c);
            else if (c.crossings > merged[i].crossings) merged[i] = c;
        }
        if (!merged.length) return null;
        // Provisional order (most barcode-like first); scanBand re-ranks the top few
        // with a decodability pre-score before spending decode seconds.
        merged.sort((u, w) => w.crossings - u.crossings);

        // Bar tilt per candidate, from the structure tensor of 2D gradients over the
        // full band: even when blur erases individual modules, dozens of parallel
        // edges keep a razor-sharp dominant ORIENTATION. phi is the gradient (bar
        // normal) direction; the bar lines' dx/dy slope follows as -tan(phi).
        for (const c of merged.slice(0, 4)) {
            let jxx = 0, jxy = 0, jyy = 0;
            const x0 = Math.max(1, c.xl), x1 = Math.min(W - 2, c.xr);
            for (let y = y1 + 2; y < y2 - 2; y += 2) {
                const row = y * W;
                for (let x = x0; x <= x1; x += 2) {
                    const ix = data[(row + x + 1) * 4] - data[(row + x - 1) * 4];
                    const iy = data[(row + W + x) * 4] - data[(row - W + x) * 4];
                    jxx += ix * ix;
                    jxy += ix * iy;
                    jyy += iy * iy;
                }
            }
            const phi = jxx + jyy > 1 ? 0.5 * Math.atan2(2 * jxy, jxx - jyy) : 0;
            c.slope = Math.max(-0.5, Math.min(0.5, -Math.tan(phi)));
        }
        return merged.slice(0, 4);
    }

    // Cheap candidate refinement (~150ms): fit a coarse guard + free-digit grid
    // sweep on the candidate's center-band profile, polish it, and return both a
    // decodability score and a REFINED extent. Serves two needs at once: ranking
    // (a merged or clutter extent fits no plausible UPC grid at its implied scale
    // and scores far worse than a real code), and edge repair (crossing spans grab
    // adjacent print, and a ±6-module extent error poisons the decode windows —
    // measured to turn an otherwise-clean decode into garbage).
    function quickFit(img, loc, y1, y2) {
        const scale = loc.mwEst / 1.2;
        const margin = 14 * loc.mwEst;
        const xa = Math.max(0, loc.xl - margin), xb = Math.min(img.width, loc.xr + margin);
        const anchors = [
            [Math.max(xa, loc.xl - 8 * loc.mwEst), loc.xl + 5 * loc.mwEst],
            [loc.xr - 5 * loc.mwEst, Math.min(xb, loc.xr + 8 * loc.mwEst)],
        ];
        const H = y2 - y1;
        const a = y1 + ((H / 6) | 0), b = y2 - ((H / 6) | 0);
        const prof = extractProfile(img, xa, xb, a, b, (loc.slope || 0) * (b - a), scale, anchors);
        const envXl = (loc.xl - xa) / scale, envXr = (loc.xr - xa) / scale;
        const ia = Math.round(envXl / STEP), ib = Math.round(envXr / STEP);
        const inside = [...prof.slice(Math.max(0, ia), Math.min(prof.length, ib))].sort((u, v) => u - v);
        if (inside.length < 20) return { pre: Infinity };
        prof.amp = inside[Math.floor(inside.length * 0.95)];
        if (prof.amp < 5) return { pre: Infinity };
        const spanMw = (envXr - envXl) / 95;
        const cost = (p) => guardCost(prof, p) + freeDigitFit(prof, p);
        let best = null;
        for (const sigmaM of [0.8, 1.2])
            for (let dm = -0.15; dm <= 0.051; dm += 0.05)
                for (let dx = -2; dx <= 8; dx += 2) {
                    const p = { x0: envXl + dx, mw: spanMw + dm, q: 0, sigmaM, e: 0.15 };
                    if (p.mw < 0.7) continue;
                    const c = cost(p);
                    if (!best || c < best.cost) { best = { ...p, cost: c }; }
                }
        if (!best) return { pre: Infinity };
        const g = refine(cost, best, 3);
        const out = { pre: g.cost / (prof.amp * prof.amp) };
        // Only the fitted POSITION is trustworthy: the fit's module width runs
        // ~10% high under heavy blur (free-digit windows prefer stretched grids),
        // while the crossing-span mwEst is ~unbiased. Report x0; the caller pairs
        // it with the original module width.
        const mwReal = g.mw * scale;
        if (mwReal > 0.75 * loc.mwEst && mwReal < 1.3 * loc.mwEst)
            out.x0 = xa + g.x0 * scale;
        return out;
    }

    // Live entry point: locate + rescale + joint-decode one horizontal band.
    // Tries the top locate() candidates (a defocused code can rank behind sharp
    // background clutter) and both orientations (a hand-held code is upside down
    // half the time). Returns {digits, ratio, mwPx} or null. Refuses below minMwPx.
    function scanBand(img, y1, y2, opts) {
        const o = opts || {};
        // mwEst is the ~unbiased crossing-span estimate; 2.05 buys a small buffer
        // over the 2.0 px/module single-frame ambiguity floor (see file header).
        const minMw = o.minMwPx === undefined ? 2.05 : o.minMwPx;
        const cands = locate(img, y1, y2);
        if (!cands) return null;
        // Refine every candidate before committing decode seconds: crossing counts
        // alone rank a merged code+print chain above the clean code inside it, and
        // crossing-span edges routinely grab adjacent print (a ±6-module extent
        // error poisons the decode windows). Adopt the fitted position only when it
        // moves meaningfully — small corrections are as likely to be fit noise.
        for (const c of cands) {
            const q = quickFit(img, c, y1, y2);
            c.pre = q.pre;
            if (q.x0 !== undefined && Math.abs(q.x0 - c.xl) > 3.5 * c.mwEst) {
                c.xl = Math.round(q.x0);
                c.xr = Math.round(q.x0 + 95 * c.mwEst);
            }
        }
        if (cands.length > 1) cands.sort((u, w) => u.pre - w.pre);
        const deadline = o.budgetMs ? Date.now() + o.budgetMs : Infinity;
        const maxRatio = o.maxRatio === undefined ? 0.85 : o.maxRatio;
        // Misreads measure cousin <= 0.99 (a confusion fits BETTER than the winner);
        // legitimate decodes measure >= 1.09. 1.08 splits with margin on both sides.
        const minCousin = o.minCousin === undefined ? 1.08 : o.minCousin;
        let lastRefused = null;

        for (const loc of cands) {
            if (loc.mwEst < minMw || loc.mwEst > 16) continue;
            const scale = loc.mwEst / 1.2;
            const margin = 14 * loc.mwEst;
            const xa = Math.max(0, loc.xl - margin);
            const xb = Math.min(img.width, loc.xr + margin);
            // Three overlapping sub-bands: decodeJoint's cross-band agreement is the
            // within-frame defense against a wrong code fitting one noisy band.
            const H = y2 - y1;
            const bands = H >= 24
                ? [[y1, y1 + (H * 2 / 3) | 0], [y1 + (H / 6) | 0, y2 - (H / 6) | 0], [y2 - (H * 2 / 3) | 0, y2]]
                : [[y1, y2]];
            // Quiet-zone anchors for the white baseline: ranges STRADDLING the located
            // edges. The mandated quiet zone sits just outside the true edge, but the
            // estimate can err a few modules either way (and print/clutter may sit
            // further out), so a wide straddle + the 93rd-percentile white estimate
            // (which tolerates a mostly-dark range) stays anchored on quiet-zone white
            // regardless of moderate extent error.
            const anchors = [
                [Math.max(xa, loc.xl - 8 * loc.mwEst), loc.xl + 5 * loc.mwEst],
                [loc.xr - 5 * loc.mwEst, Math.min(xb, loc.xr + 8 * loc.mwEst)],
            ];
            // Follow the bars' measured tilt: each sub-band's sampling path shifts by
            // slope px per row, so a tilted code averages ALONG its bars instead of
            // smearing across them. Every sub-band shears around the FULL band's
            // center row (via the off shift): sub-bands sit at different heights, and
            // shearing each around its own center would displace the three profiles
            // against each other, breaking decodeJoint's cross-band agreement.
            const bandMid = (y1 + y2) / 2;
            const slope = loc.slope || 0;
            const profiles = bands.map(([a, b]) => {
                const off = slope * ((a + b) / 2 - bandMid);
                const anch = anchors.map(([p, q]) => [p + off, q + off]);
                return extractProfile(img, xa + off, xb + off, a, b, slope * (b - a), scale, anch);
            });
            // Hand the located extent (in profile coordinates) to candidate generation;
            // its own variance envelope splits heavily-blurred codes.
            const env = { xl: (loc.xl - xa) / scale, xr: (loc.xr - xa) / scale };
            const envRev = { xl: (xb - loc.xr) / scale, xr: (xb - loc.xl) / scale };
            const jointOpts = { grids: o.grids || 6, verify: o.verify, repairIters: 6, env, deadline, debug: o.debug, mustScore: o.mustScore };
            const fwd = decodeJoint(profiles, jointOpts);
            // Only pay for the reversed pass when forward isn't decisively clean.
            let better = fwd, other = null;
            if ((!fwd || fwd.ratio > maxRatio * 0.85) && Date.now() < deadline) {
                const rev = decodeJoint(profiles.map(reverseProfile), { ...jointOpts, env: envRev });
                better = !fwd ? rev : !rev ? fwd : (fwd.cost <= rev.cost ? fwd : rev);
                other = better === fwd ? rev : fwd;
            }
            // The losing orientation is a runner-up too: a wrong winner can have a
            // comfortable within-pool ratio while the other orientation's best sits
            // within a few percent of its cost. Fold it into the ratio gate.
            let effRatio = better ? better.ratio : 1;
            if (better && other && other.digits !== better.digits)
                effRatio = Math.max(effRatio, better.cost / other.cost);
            if (better && effRatio <= maxRatio && better.cousinRatio >= minCousin) {
                const out = { digits: better.digits, ratio: effRatio, cousinRatio: better.cousinRatio, mwPx: loc.mwEst };
                if (o.debug) { out.top = better.top; out.inPool = better.inPool; out.phys = better.phys; }
                return out;
            }
            if (o.debug && better && !lastRefused) {
                // Lab visibility: keep the first refused decode to surface if nothing
                // is accepted — but keep trying later candidates exactly like live.
                lastRefused = { digits: better.digits, ratio: effRatio, cousinRatio: better.cousinRatio, mwPx: loc.mwEst, refused: true };
                lastRefused.top = better.top; lastRefused.inPool = better.inPool; lastRefused.phys = better.phys;
            }
            if (Date.now() >= deadline) break;
        }
        return lastRefused;
    }

    function selfTest() {
        // Each case gets two slightly different "bands" (jitter in blur/offset), like
        // the two halves of a real scan band. Cases live at ~1.2 px/module — the
        // virtual regime scanBand rescales every real code into — with blur up to
        // ~0.8 modules; heavier blur is where the honesty gates refuse by design
        // (see the close-blur suite in experiments/upc-waveform/lab).
        const cases = [
            ['straight', ['713733788632', () => 1.2, [0.85, 0.95], 12, 0.15]],
            ['curved', ['713733788632', (k) => 1.2 + 0.0005 * k, [0.95, 0.85], 12, 0.15]],
            ['clean', ['036000291452', () => 1.3, [0.8, 0.9], 10, 0.1]],
        ];
        const out = {};
        for (const [name, [digits, mwFn, sigmas, xoff, ePx]] of cases) {
            const profs = sigmas.map(s => synthProfile(digits, mwFn, s, xoff, ePx));
            const r = decodeJoint(profs, {});
            out[name] = r ? r.digits + ' ratio=' + r.ratio.toFixed(3) : 'FAIL';
        }
        return out;
    }

    const api = { scanBand, locate, quickFit, decodeProfile, decodeJoint, extractProfile, reverseProfile, synthProfile, selfTest, fullCost, refine, buildModules, guardCost, segCost, cdfFor, gridPos };
    // Page, Web Worker, and Node-vm lab all load this file; attach wherever exists.
    if (typeof globalThis !== "undefined") globalThis.UpcWaveform = api;
    if (typeof window !== "undefined") window.UpcWaveform = api;
})();
