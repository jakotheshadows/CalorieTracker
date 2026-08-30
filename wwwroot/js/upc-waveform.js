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

    // Memoized: fullCost rebuilds the same strings hundreds of thousands of times
    // during verification (measured), and the pool of distinct codes is tiny.
    const MODCACHE = new Map();
    const buildModules = (d12) => {
        let m = MODCACHE.get(d12);
        if (m !== undefined) return m;
        m = '101';
        for (let i = 0; i < 6; i++) m += L[+d12[i]];
        m += '01010';
        for (let i = 6; i < 12; i++) m += R[+d12[i]];
        m += '101';
        if (MODCACHE.size > 4000) MODCACHE.clear();
        MODCACHE.set(d12, m);
        return m;
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
    // Edge response for blur s px, optionally with a motion GHOST of span g px: the
    // camera integrating over a hand movement produces a two-impulse kernel (each
    // bar edge appears twice, offset by g) — measured at up to ~8px on real webcam
    // frames, and no single-Gaussian shape can honestly represent it.
    const cdfFor = (s, g) => {
        const k = CDF_N / (2 * CDF_R);
        const base = (x) => {
            const u = (x / s + CDF_R) * k;
            if (u <= 0) return 0;
            if (u >= CDF_N) return 1;
            const i = u | 0;
            return CDF_T[i] + (CDF_T[i + 1] - CDF_T[i]) * (u - i);
        };
        const f = !g ? base : (x) => 0.5 * (base(x - g / 2) + base(x + g / 2));
        // How far a bar edge's response reaches; segCost prunes bars beyond this.
        // Ghost-free cut matches the pre-ghost constant exactly (behavior parity).
        f.cut = g ? Math.min(12, 2.5 * s + g / 2 + 1) : Math.min(9, 2.5 * s + 1);
        return f;
    };
    const cdfOf = (p, mw) => cdfFor(p.sigmaM * mw, (p.gh || 0) * mw);

    // Cost of matching modules valStr starting at startModule against the profile.
    // Amplitude and offset are fitted per segment (absorbs local lighting); lower =
    // better. requireEvidence changes the no-bar-correlation semantics: instead of
    // the cheap residual-variance fallback (right for DECODING — a slight model
    // mismatch at 1px modules must not veto a true grid), absence of bars is charged
    // like the missing ink it is. Localization uses need this: with the cheap
    // fallback, flat label regions score better than real guards, and every
    // structure-evidence ranking built on guardCost silently inverts (measured on
    // real frames: blank regions and text gaps out-scored the actual barcode).
    let SEG_TV = new Float64Array(4096);
    function segCost(profile, pos, mw, cdf, e, startModule, valStr, shift, requireEvidence) {
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
        // Scratch reuse: this allocation sat in the hottest loop of the whole module
        // (millions of calls per decode); workers are single-threaded, so one shared
        // buffer is safe.
        if (SEG_TV.length < ib - ia + 1) SEG_TV = new Float64Array((ib - ia + 1) * 2);
        const tv = SEG_TV;
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
        // Demand evidence only where the template PREDICTS visible modulation under
        // the current physics: at sigma >= ~0.7 modules a 1-module alternation (the
        // middle guard) is legitimately erased, its fitted amplitude is trend-noise
        // with arbitrary sign, and a hard veto there randomly rejects TRUE grids
        // (measured: g = -149 at an exact true grid). Templates that keep real
        // modulation (outer guards + quiet zones, digit windows at decodable blur)
        // still veto flat regions and text gaps as intended.
        if (requireEvidence && amp && g < 0.25 * amp) {
            const tsd = Math.sqrt(Math.max(0, stt / n - (st / n) * (st / n)));
            if (tsd >= 0.12) return 0.2 * amp * amp; // asserted bars are not there
        }
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
        const cdf = cdfOf(p, p.mw);
        const pos = gridPos(p);
        // Extended guard templates: the modules flanking each guard are fixed by UPC
        // structure (every L digit starts 0 and ends 1; every R digit starts 1 and
        // ends 0), so model them. Without the flanking bars the physics fit absorbs
        // their unmodeled ink bleeding into the window by inflating blur + ink spread.
        const a = segCost(profile, pos, p.mw, cdf, p.e, 0, '1010', 0, true);
        if (!isFinite(a)) return Infinity;
        const b = segCost(profile, pos, p.mw, cdf, p.e, 44, '1010101', 0, true);
        const c = segCost(profile, pos, p.mw, cdf, p.e, 91, '0101', 0, true);
        if (!isFinite(b) || !isFinite(c)) return Infinity;
        // Real labels often print text close to the code, so keep the windows tight
        // and cap the term: it should veto misaligned grids, not dominate ranking.
        const qz = quietCost(profile, pos, -3.5, -1) + quietCost(profile, pos, 96, 98.5);
        if (!isFinite(qz)) return Infinity;
        return a + b + c + Math.min(qz, 3000);
    }

    function fullCost(profile, digits, p) {
        const cdf = cdfOf(p, p.mw);
        return segCost(profile, gridPos(p), p.mw, cdf, p.e, 0, buildModules(digits));
    }

    // Summed cost of the best-fitting pattern in each of the 12 digit windows —
    // a digit-agnostic measure of how well a grid + physics explains the code.
    // Structure evidence only (never decodes digits), so absent bars are charged
    // as absent, not excused (see segCost's requireEvidence).
    // stride 2 = every other digit window: still 6 windows (42 modules) of evidence
    // on top of the guards — plenty for physics estimation at half the cost. Full
    // stride stays the default everywhere rankings between grids are compared.
    function freeDigitFit(profile, g, stride) {
        const cdf = cdfOf(g, g.mw);
        const pos = gridPos(g);
        let sum = 0;
        for (let d = 0; d < 12; d += (stride || 1)) {
            const startModule = d < 6 ? 3 + 7 * d : 50 + 7 * (d - 6);
            const table = d < 6 ? L : R;
            let best = Infinity;
            for (let digit = 0; digit < 10; digit++) {
                const c = segCost(profile, pos, g.mw, cdf, g.e, startModule, table[digit], 0, true);
                if (c < best) best = c;
            }
            sum += best;
        }
        return sum;
    }

    // Coordinate-descent refinement of parameters p against a cost function.
    function refine(costFn, p, passes, lockGh) {
        let cur = { ...p };
        let curCost = costFn(cur);
        if (cur.r === undefined) cur.r = 0;
        if (cur.gh === undefined) cur.gh = 0;
        const moves = [
            ['x0', [-0.5, -0.25, 0.25, 0.5]],
            ['mw', [-0.02, -0.01, 0.01, 0.02]],
            ['q', [-0.00015, 0.00015]],
            ['r', [-0.000003, 0.000003]],
            ['sigmaM', [-0.2, -0.1, 0.1, 0.2]],
            ['e', [-0.1, -0.05, 0.05, 0.1]],
            // NO gh moves: the ghost span is set by DISCRETE sweep only (fitPhysics).
            // As a descent dimension it is a second smear knob that every fit walks
            // up (the guard/free-digit/full-code objectives all prefer over-smear),
            // eroding digit tables and margins across the whole single-frame path.
        ];
        for (let pass = 0; pass < (passes || 3); pass++) {
            let improved = false;
            for (const [key, deltas] of moves) {
                // Ghost span is chosen by DISCRETE sweep only (see fitPhysics): as a
                // free descent dimension it is a second smear knob that the guard +
                // free-digit objective greedily walks up, re-creating the over-blur
                // disease the sigma sweep exists to prevent (measured: sigma 1.45
                // fitted on a true-0.69 frame once gh could drift).
                if (lockGh && key === 'gh') continue;
                for (const d of deltas) {
                    const t = { ...cur, [key]: cur[key] + d };
                    if (t.sigmaM < 0.4 || t.sigmaM > 2.6 || t.e < 0 || t.e > 0.4 || t.mw < 0.8 || t.mw > 1.6 || t.gh < 0 || t.gh > 2.2) continue;
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
        const cdf = cdfOf(g, g.mw);
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
        // e seeds MUST include 0: with e=0.15 the widened middle-guard template
        // decorrelates from zero-ink-spread data and the evidence veto rejects the
        // TRUE grid (measured: middle guard 7543 at e=0.15 vs 1155 at e=0).
        // Seed spacing is NOT a fast-mode knob: a sharp code's alignment basin is
        // narrower than a 0.75-sample dx step (measured: coarse steps decode blurry
        // cases fine and lose the sharp ones).
        for (const sigmaM of [0.9, 1.3])
            for (const e of [0, 0.2])
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
            let g = refine((p) => guardCost(profile, p), { ...seed }, 3, true);
            let ctx = null;
            for (let iter = 0; iter < (o.fast ? 2 : 3); iter++) {
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
    function geoRefine(profile, digits, seed, phys, passes) {
        let cur = { x0: seed.x0, mw: seed.mw, q: seed.q || 0, r: seed.r || 0, ...phys };
        let curCost = fullCost(profile, digits, cur);
        for (let pass = 0; pass < (passes || 4); pass++) {
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

    // Frame physics estimation on KNOWN structure only (guards + free digits, both
    // evidence-aware): sweep blur sigma AND motion-ghost span explicitly with a
    // geometry refit at each combination — sigma/ghost couple with geometry, and
    // coordinate descent alone walks to over-blurred local minima. A ghost is the
    // camera integrating over hand movement (each edge printed twice); no Gaussian
    // can represent that shape, so it is its own dimension.
    const PHYS_GEO_MOVES = [
        ['x0', [-0.5, -0.25, 0.25, 0.5]],
        ['mw', [-0.02, -0.01, 0.01, 0.02]],
        ['q', [-0.00015, 0.00015]],
        ['r', [-0.000003, 0.000003]],
    ];
    function fitPhysics(prof, seed0, sigmas, passes, deadline, withGhost) {
        // Full-stride objective: a half-stride variant was measured to land on
        // physics that halves the clean-case decision margin — not worth the ms.
        const cost1 = (p) => guardCost(prof, p) + freeDigitFit(prof, p);
        // The ghost dimension is for BURST per-frame physics only: the guard +
        // free-digit objective genuinely prefers over-smeared combos, and a
        // single-frame fit given the ghost knob degrades across the board
        // (measured); the burst's cross-frame agreement is what keeps it honest.
        let sweepBest = null;
        for (const sig of sigmas) {
            for (const gh of withGhost ? [0, 0.5, 1.0, 1.5] : [0]) {
                if (deadline && Date.now() >= deadline && sweepBest) break;
                let cur = { ...seed0, sigmaM: sig, e: 0.1, gh };
                let curCost = cost1(cur);
                for (let pass = 0; pass < passes; pass++) {
                    let imp = false;
                    for (const [key, deltas] of PHYS_GEO_MOVES)
                        for (const d of deltas) {
                            const t = { ...cur, [key]: cur[key] + d };
                            const c = cost1(t);
                            if (c < curCost) { cur = t; curCost = c; imp = true; }
                        }
                    if (!imp) break;
                }
                if (!sweepBest || curCost < sweepBest.cost) sweepBest = { ...cur, cost: curCost };
            }
        }
        return refine(cost1, sweepBest, 2, true);
    }
    const clampSig = (s) => Math.max(0.4, Math.min(2.6, s));
    const physBracket = (fitted) => [
        { sigmaM: clampSig(fitted.sigmaM * 0.75), e: fitted.e, gh: fitted.gh || 0 },
        { sigmaM: clampSig(fitted.sigmaM), e: fitted.e, gh: fitted.gh || 0 },
        { sigmaM: clampSig(fitted.sigmaM * 1.25), e: fitted.e, gh: fitted.gh || 0 },
    ];

    // Decode across several band profiles of the same barcode — sub-bands of one
    // frame, or one profile per frame of a BURST (o.perProfilePhys): a wrong string
    // can fit one noisy band; only the true one fits all of them. Per-profile
    // geometry refinement doubles as cross-frame registration (hand drift moves
    // x0/mw between frames), and per-profile physics absorbs frame-to-frame blur
    // changes. Returns {digits, ratio, cousinRatio} or null; ratio near 1 = unsure.
    function decodeJoint(profiles, opts) {
        const o = opts || {};
        // Candidate generation on a few profiles (all when small); collect neutral
        // guard-anchored seeds. o.envs supplies a per-profile extent override.
        const pool = new Map();
        const seedPool = [];
        const genIdx = o.genIdx || profiles.map((_, i) => i);
        if (o.candidates) {
            // Verification-only mode: candidate strings come from a stronger external
            // searcher (the full single-frame pipeline on the sharpest frame); this
            // call only has to JUDGE them across all profiles. Seeds are synthesized
            // from the extent estimates — geometry refinement polishes per profile.
            for (const d of o.candidates) pool.set(d, null);
            for (let i = 0; i < profiles.length; i++) {
                const env = (o.envs && o.envs[i]) || o.env;
                if (!env) continue;
                const spanMw = (env.xr - env.xl) / 95;
                for (const f of [0.96, 1.0, 1.05])
                    seedPool.push({ x0: env.xl + 1, mw: spanMw * f, q: 0, gc: seedPool.length });
            }
        } else {
            for (const gi of genIdx) {
                if (o.deadline && Date.now() >= o.deadline && pool.size > 0) break;
                const prof = profiles[gi];
                const genOpts = o.envs ? { ...o, env: o.envs[gi] } : o;
                const gen = decodeProfile(prof, genOpts);
                if (!gen) continue;
                for (const [digits, seed] of gen.candMap) if (!pool.has(digits)) pool.set(digits, seed);
                seedPool.push(...gen.guardGrids.slice(0, 4));
            }
        }
        if (pool.size === 0) return null;
        // Non-generation profiles still need amp (segCost's evidence clamp) — attach
        // it the same way decodeProfile does, from their own envelope region.
        for (let i = 0; i < profiles.length; i++) {
            const prof = profiles[i];
            if (prof.amp) continue;
            const env = (o.envs && o.envs[i]) || o.env;
            const ia = env ? Math.round(env.xl / STEP) : 0;
            const ib = env ? Math.round(env.xr / STEP) : prof.length;
            const inside = [...prof.slice(Math.max(0, ia), Math.min(prof.length, ib))].sort((a, b) => a - b);
            if (inside.length > 20) prof.amp = inside[Math.floor(inside.length * 0.95)];
        }
        // Dedupe seeds by mw cluster across profiles, keep a few diverse basins.
        const buildSeeds = () => {
            const seedBuckets = new Map();
            for (const s of seedPool) {
                const b = Math.round(s.mw / 0.05);
                if (!seedBuckets.has(b) || s.gc < seedBuckets.get(b).gc) seedBuckets.set(b, s);
            }
            return [...seedBuckets.values()].sort((u, v) => u.gc - v.gc).slice(0, 4);
        };
        let seeds = buildSeeds();

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
        let fitNorm, PHYSLIST;
        let weights = profiles.map(() => 1);
        if (o.physList) {
            // Physics pre-fitted by the caller (scanBurst fits every frame once, for
            // both reference-frame selection and judging) — profiles arrive already
            // quality-sorted with aligned weights.
            PHYSLIST = o.physList;
            weights = o.weights || weights;
            fitNorm = 0;
        } else if (o.perProfilePhys) {
            // Burst mode: every profile is a different FRAME with its own blur (focus
            // hunting, hand motion), so each gets its own frozen physics bracket. A
            // coarser sigma grid keeps N-frame fitting affordable.
            const fits = profiles.map((prof) =>
                fitPhysics(prof, seed0, [0.6, 0.9, 1.3, 1.8, 2.4], 2, o.deadline, true));
            PHYSLIST = fits.map(physBracket);
            // Frames are NOT equal: a badly-ghosted frame contributes model-misfit
            // noise that can swamp the clean frames' discrimination (measured: a
            // burst refused while its best single frame decoded). Weight each frame
            // by relative fit quality and lead with the cleanest one (the reference
            // profile drives the cheap pre-rank and repair screening).
            const bestFit = Math.min(...fits.map(f => f.cost));
            weights = fits.map(f => Math.max(0, Math.min(1, bestFit / Math.max(f.cost, 1e-9))));
            const order = profiles.map((_, i) => i).sort((a, b) => weights[b] - weights[a]);
            const reorder = (arr) => order.map(i => arr[i]);
            profiles = reorder(profiles);
            PHYSLIST = reorder(PHYSLIST);
            weights = reorder(weights);
            const envsL = o.envs ? reorder(o.envs) : null;
            // LATE generation on the two cleanest frames: the initial generation
            // spread is picked blind (quality is unknowable before physics fitting)
            // and can land entirely on ghosted frames whose digit tables never emit
            // the truth — while a clean frame's tables do (measured: the burst's
            // forced-truth score won decisively, it just was never generated).
            const generated = new Set(genIdx.map(gi => order.indexOf(gi)));
            let grew = false;
            if (!o.candidates)
                for (const i of [0, 1]) {
                    if (i >= profiles.length || generated.has(i)) continue;
                    if (o.deadline && Date.now() >= o.deadline) break;
                    const genOpts = envsL ? { ...o, env: envsL[i] } : o;
                    const gen = decodeProfile(profiles[i], genOpts);
                    if (!gen) continue;
                    for (const [digits, seed] of gen.candMap) if (!pool.has(digits)) pool.set(digits, seed);
                    seedPool.push(...gen.guardGrids.slice(0, 4));
                    grew = true;
                }
            if (grew) seeds = buildSeeds();
            fitNorm = 0;
        } else {
            const fitted = fitPhysics(profiles[0], seed0,
                o.fast ? [0.5, 0.8, 1.1, 1.4, 1.8, 2.2]
                    : [0.5, 0.65, 0.8, 0.95, 1.1, 1.3, 1.55, 1.8, 2.1, 2.4],
                o.fast ? 2 : 3, o.deadline);
            const shared = refine(
                (p) => profiles.reduce((s, prof) => s + guardCost(prof, p) + freeDigitFit(prof, p), 0),
                fitted, 3, true);
            // Fit quality normalized by ink amplitude: a real barcode's guards + free
            // digits fit to within noise, while barcode-less texture leaves residuals
            // on the order of the amplitude itself.
            let amp2 = 0;
            for (const prof of profiles) amp2 += (prof.amp || 1) * (prof.amp || 1);
            fitNorm = shared.cost / amp2;
            const PHYS = physBracket(shared);
            PHYSLIST = profiles.map(() => PHYS);
        }
        // Blur/ink physics is a property of the FRAME, not of reading direction —
        // export it so the caller's reversed pass can skip re-fitting it.
        if (o.physOut) { o.physOut.physList = PHYSLIST; o.physOut.weights = weights; }

        // Joint score: per profile, refine geometry once under that profile's center
        // physics (best over seeds), then rescore that geometry under the bracket
        // ends. Fewer seeds per profile in burst mode — N frames buy redundancy.
        const seedsJ = profiles.length > 4 ? seeds.slice(0, 2) : seeds;
        const jointOf = new Map();
        // Geometry cache: the grid (x0/mw/curvature) is a property of the PHYSICAL
        // code in each profile, not of the candidate string — candidates differ only
        // in digits. The first candidate scored on a profile pays for the full
        // multi-seed 4-pass descent; every later candidate warm-starts from that
        // fitted grid and only polishes (measured: verification was ~95% of decode
        // time, almost all of it re-deriving the same grid per candidate).
        const geoCache = profiles.map(() => null);
        const jointScore = (digits) => {
            if (jointOf.has(digits)) return jointOf.get(digits);
            let s = 0;
            for (let i = 0; i < profiles.length; i++) {
                const prof = profiles[i], PH = PHYSLIST[i], w = weights[i];
                if (w < 0.25) continue; // junk frame: no discrimination left in it
                let best = null;
                if (geoCache[i]) {
                    // Full descent depth from the warm grid: shallow 2-pass polish
                    // was measured to under-fit every candidate equally, compressing
                    // the winner/runner-up ratios the accept gates depend on.
                    best = geoRefine(prof, digits, geoCache[i], PH[1]);
                } else {
                    for (const seed of seedsJ) {
                        const r = geoRefine(prof, digits, seed, PH[1]);
                        if (!best || r.cost < best.cost) best = r;
                    }
                    geoCache[i] = best.geo;
                }
                s += w * (best.cost
                    + fullCost(prof, digits, { ...best.geo, ...PH[0] })
                    + fullCost(prof, digits, { ...best.geo, ...PH[2] }));
            }
            jointOf.set(digits, s);
            return s;
        };
        // cheap pre-rank on the first profile / its center physics only — same
        // warm-start scheme, primed by the first pool entry (best generation beam)
        const cheap = new Map();
        let cheapGeo = null;
        const cheapScore = (digits) => {
            if (!cheap.has(digits)) {
                let best = null;
                if (cheapGeo) {
                    best = geoRefine(profiles[0], digits, cheapGeo, PHYSLIST[0][1], 2);
                } else {
                    for (const seed of seeds.slice(0, 2)) {
                        const r = geoRefine(profiles[0], digits, seed, PHYSLIST[0][1]);
                        if (!best || r.cost < best.cost) best = r;
                    }
                    cheapGeo = best.geo;
                }
                cheap.set(digits, best.cost);
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
            // that lead toward the truth. Fast mode screens on one band — screening
            // only orders proposals; the joint verify still judges on all bands.
            const screenIdx = !o.fast && profiles.length > 1 ? [0, profiles.length - 1] : [0];
            const screenProfs = screenIdx.map(i => profiles[i]);
            const staticScore = (digits) => {
                if (screenGeos === null)
                    screenGeos = screenIdx.map(i =>
                        geoRefine(profiles[i], best, seeds[0], PHYSLIST[i][1]).geo);
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
            // The exhaustive adjacent-pair family is ~800 strings whose static screens
            // dominated fast-mode repair time; the cousin-pair family below already
            // covers the physically-real blur confusions, so fast mode skips this.
            if (!o.fast)
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
        if (o.returnTop) out.finalists = sorted.slice(0, o.returnTop).map(([d]) => d);
        if (o.debug) {
            out.top = sorted.slice(0, 8).map(([d, c]) => d + ':' + Math.round(c));
            out.inPool = o.mustScore ? o.mustScore.map(d => pool.has(d)) : undefined;
            out.phys = PHYSLIST[0][1];
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
        // 4 sub-reads cover the box span at 1.2 px/module scale; more is oversampling.
        const sub = Math.max(1, Math.min(4, Math.round(sc)));
        const redBox = (x, y) => {
            if (sub === 1) return redAt(x, y);
            let s = 0;
            for (let k = 0; k < sub; k++) s += redAt(x + (k - (sub - 1) / 2) * (sc * STEP / sub), y);
            return s / sub;
        };
        const n = Math.round((xb - xa) / (STEP * sc));
        const prof = new Float64Array(n);
        const rows = y2 - y1;
        // Vertical stride: bars are vertical, so rows are redundant samples of the
        // same waveform — ~32 of them average the noise down as far as it goes.
        // (This loop was measured at up to a third of a whole fast decode.)
        const ystep = Math.max(1, Math.floor(rows / 32));
        for (let i = 0; i < n; i++) {
            let sum = 0, m = 0;
            for (let y = y1; y < y2; y += ystep) {
                sum += redBox(xa + i * STEP * sc + shear * ((y - y1) / rows - 0.5), y);
                m++;
            }
            prof[i] = sum / m;
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
                // 2.5x median: tried at 2.0x to cut merged code+text spans at their
                // quiet-zone seam, but that also dissolves REAL codes into sub-floor
                // fragments (a clean, decodable candidate on a real frame vanished);
                // the evidence-aware pre-rank now handles merged-span demotion instead.
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

    // Vertical extent of the BARS within the band: rows whose darkness pattern
    // along x correlates with the band's central column profile (bars repeat down
    // their whole height; the printed digit line under the code, white label above
    // it, and glare stripes do not). Averaging non-bar rows into the profiles reads
    // to the model as extreme blur and poisons decoding — measured on a real frame
    // where a code SHARPER than previously-decoded ones produced pure garbage
    // because its bars spanned only ~60% of the band height.
    function barRows(img, loc, y1, y2) {
        const W = img.width, data = img.data;
        const xl = Math.max(0, loc.xl), xr = Math.min(W - 1, loc.xr);
        const n = xr - xl + 1;
        const rows = y2 - y1;
        if (n < 30 || rows < 32) return [y1, y2];
        const stripH = Math.min(48, rows);
        const sy1 = y1 + ((rows - stripH) >> 1);
        const ref = new Float64Array(n);
        for (let y = sy1; y < sy1 + stripH; y++) {
            const row = y * W;
            for (let i = 0; i < n; i++) ref[i] += data[(row + xl + i) * 4];
        }
        let refMean = 0;
        for (let i = 0; i < n; i++) { ref[i] /= stripH; refMean += ref[i]; }
        refMean /= n;
        let refVar = 0;
        for (let i = 0; i < n; i++) refVar += (ref[i] - refMean) * (ref[i] - refMean);
        if (refVar < 1e-6) return [y1, y2];
        const slope = loc.slope || 0;
        const midY = (y1 + y2) / 2;
        const corr = new Float64Array(rows);
        for (let y = y1; y < y2; y++) {
            const row = y * W;
            const shift = slope * (y - midY);
            let m = 0;
            const vals = new Float64Array(n);
            for (let i = 0; i < n; i++) {
                const x = Math.max(0, Math.min(W - 1, Math.round(xl + i + shift)));
                vals[i] = data[(row + x) * 4];
                m += vals[i];
            }
            m /= n;
            let cv = 0, vv = 0;
            for (let i = 0; i < n; i++) {
                cv += (vals[i] - m) * (ref[i] - refMean);
                vv += (vals[i] - m) * (vals[i] - m);
            }
            corr[y - y1] = vv > 1e-6 ? cv / Math.sqrt(vv * refVar) : 0;
        }
        // Expand from the band center while rows still look like bars (small gaps
        // tolerated: a glare line can cross the bars without ending them).
        const c0 = Math.floor(rows / 2);
        let top = c0, bot = c0;
        for (let y = c0 - 1, gap = 0; y >= 0; y--) {
            if (corr[y] >= 0.5) { top = y; gap = 0; }
            else if (++gap > 4) break;
        }
        for (let y = c0 + 1, gap = 0; y < rows; y++) {
            if (corr[y] >= 0.5) { bot = y; gap = 0; }
            else if (++gap > 4) break;
        }
        // Mean row-correlation over the result: the VERTICAL SELF-SIMILARITY of the
        // region. Bars repeat down their whole height (near 1); text lines do not
        // (each line differs from the column mean). This is the cheap bars-vs-text
        // discriminator — orientation coherence fails at it (a line of text has
        // mostly vertical strokes too), measured on real frames.
        let mc = 0, span;
        if (bot - top < 24) {
            for (let y = 0; y < rows; y++) mc += corr[y];
            mc /= rows;
            span = [y1, y2];
        } else {
            for (let y = top; y <= bot; y++) mc += corr[y];
            mc /= bot - top + 1;
            span = [y1 + top, y1 + bot + 1];
        }
        span.push(mc);
        return span;
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
        const ry1 = loc.by1 === undefined ? y1 : loc.by1;
        const ry2 = loc.by2 === undefined ? y2 : loc.by2;
        const H = ry2 - ry1;
        const a = ry1 + ((H / 6) | 0), b = ry2 - ((H / 6) | 0);
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
        const g = refine(cost, best, 3, true);
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
        const deadline = o.budgetMs ? Date.now() + o.budgetMs : Infinity;
        // Rank candidates by crossings x vertical self-similarity — the same score
        // that picks the box on the main thread. quickFit's structural COST was used
        // for ranking here and measured to invert on real frames (a text line fits
        // the guard model cheaper than a heavily defocused true code), sending whole
        // decode budgets into print; it stays only as the per-candidate extent
        // repair, run lazily on the candidates actually decoded.
        for (const c of cands) {
            if (Date.now() >= deadline) break;
            const br = barRows(img, c, y1, y2);
            c.by1 = br[0];
            c.by2 = br[1];
            c.corr = br[2] === undefined ? 0.5 : br[2];
        }
        if (cands.length > 1)
            cands.sort((u, w) => w.crossings * Math.max(0.15, w.corr) - u.crossings * Math.max(0.15, u.corr));
        // fast mode: the caller (live scanner) already tracked and verified ONE
        // region over several ticks — spend the whole small budget on it.
        const maxCands = o.fast ? 1 : 3;
        if (cands.length > maxCands) cands.length = maxCands;
        // Extent repair on whatever will actually be decoded: crossing-span edges
        // routinely grab adjacent print or slide under lighting gradients (a
        // ±6-module extent error poisons the decode windows and wastes the whole
        // budget). Adopt the fitted position only when it moves meaningfully —
        // small corrections are as likely to be fit noise.
        for (const c of cands) {
            if (Date.now() >= deadline) break;
            const q = quickFit(img, c, y1, y2);
            if (q.x0 !== undefined && Math.abs(q.x0 - c.xl) > 3.5 * c.mwEst) {
                c.xl = Math.round(q.x0);
                c.xr = Math.round(q.x0 + 95 * c.mwEst);
            }
        }
        const maxRatio = o.maxRatio === undefined ? 0.85 : o.maxRatio;
        // Misreads measure cousin <= 0.99 (a confusion fits BETTER than the winner,
        // worst observed 0.992); legitimate decodes cluster from ~1.07 up, with real
        // mass at 1.07-1.10 (three knife-edge refusals observed at 1.069-1.078).
        // 1.05 keeps a ~6% margin over the worst misread and stops refusing truths.
        const minCousin = o.minCousin === undefined ? 1.05 : o.minCousin;
        let lastRefused = null;

        for (const loc of cands) {
            if (loc.mwEst < minMw || loc.mwEst > 16) continue;
            const scale = loc.mwEst / 1.2;
            const margin = 14 * loc.mwEst;
            const xa = Math.max(0, loc.xl - margin);
            const xb = Math.min(img.width, loc.xr + margin);
            // Three overlapping sub-bands within the BAR rows only (see barRows):
            // decodeJoint's cross-band agreement is the within-frame defense against
            // a wrong code fitting one noisy band.
            const ry1 = loc.by1 === undefined ? y1 : loc.by1;
            const ry2 = loc.by2 === undefined ? y2 : loc.by2;
            const H = ry2 - ry1;
            const bands = H >= 24
                ? [[ry1, ry1 + (H * 2 / 3) | 0], [ry1 + (H / 6) | 0, ry2 - (H / 6) | 0], [ry2 - (H * 2 / 3) | 0, ry2]]
                : [[ry1, ry2]];
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
            const bandMid = (ry1 + ry2) / 2;
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
            const jointOpts = {
                grids: o.grids || (o.fast ? 4 : 6),
                verify: o.verify || (o.fast ? 8 : undefined),
                repairIters: o.fast ? 2 : 6,
                fast: o.fast,
                // fast: generate candidates from the middle sub-band only (usually the
                // cleanest rows); all three bands still JUDGE every candidate, which is
                // where the misread protection lives.
                genIdx: o.fast && profiles.length === 3 ? [1] : undefined,
                env, deadline, returnTop: o.returnTop, debug: o.debug, mustScore: o.mustScore,
            };
            // fast: cap the forward pass at ~65% of what's left, so an upside-down
            // code doesn't starve its own reversed pass out of the budget. The
            // reversed pass reuses the forward pass's fitted physics (blur is a
            // property of the frame, not of reading direction), so it runs on a
            // smaller share.
            const physOut = {};
            const fwdOpts = o.fast && isFinite(deadline)
                ? { ...jointOpts, physOut, deadline: Math.min(deadline, Date.now() + (deadline - Date.now()) * 0.65) }
                : { ...jointOpts, physOut };
            const fwd = decodeJoint(profiles, fwdOpts);
            // Only pay for the reversed pass when forward isn't decisively clean.
            let better = fwd, other = null;
            if ((!fwd || fwd.ratio > maxRatio * 0.85) && Date.now() < deadline) {
                const revOpts = { ...jointOpts, env: envRev };
                if (physOut.physList) revOpts.physList = physOut.physList;
                const rev = decodeJoint(profiles.map(reverseProfile), revOpts);
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
                out.rev = better !== fwd;
                if (o.returnTop) out.finalists = better.finalists;
                if (o.debug) { out.top = better.top; out.inPool = better.inPool; out.phys = better.phys; }
                return out;
            }
            if ((o.debug || o.returnTop) && better && !lastRefused) {
                // Lab/hybrid visibility: keep the first refused decode to surface if
                // nothing is accepted — but keep trying later candidates like live.
                lastRefused = { digits: better.digits, ratio: effRatio, cousinRatio: better.cousinRatio, mwPx: loc.mwEst, refused: true };
                lastRefused.rev = better !== fwd;
                if (o.returnTop) lastRefused.finalists = better.finalists;
                lastRefused.top = better.top; lastRefused.inPool = better.inPool; lastRefused.phys = better.phys;
            }
            if (Date.now() >= deadline) break;
        }
        return lastRefused;
    }

    // MULTI-FRAME FUSION entry point: decode a burst of bands as ONE joint problem.
    // Each band is a frame's detected code region with its own metadata:
    //   { img: {width,height,data}, xl, xr, mwEst, slope, by1, by2 }  (y's band-relative)
    // One profile is extracted per frame; decodeJoint fuses them with per-frame
    // frozen physics (blur differs frame to frame — focus hunting, hand motion,
    // varying ghost offsets) and per-profile geometry (hand drift registration).
    // A single frame at sigma/mw ~1.5 is information-ambiguous; N frames of the
    // same code under DIFFERENT noise are not — the true code is the only string
    // that keeps explaining every frame.
    function scanBurst(bands, opts) {
        const o = opts || {};
        const minMw = o.minMwPx === undefined ? 2.05 : o.minMwPx;
        if (!bands || !bands.length) return null;
        const mws = bands.map(b => b.mwEst).sort((a, b) => a - b);
        const mwMed = mws[Math.floor(mws.length / 2)];
        if (mwMed < minMw || mwMed > 16) return null;
        const deadline = o.budgetMs ? Date.now() + o.budgetMs : Infinity;
        // Slightly looser gates than single-frame (0.88/1.03 vs 0.85/1.05): a thin
        // margin summed over N quality-weighted frames is statistically far more
        // reliable than the same margin from one frame, misreads still measure
        // ratio >= 0.97 or cousin <= 0.99, and each gate backstops the other.
        const maxRatio = o.maxRatio === undefined ? 0.88 : o.maxRatio;
        const minCousin = o.minCousin === undefined ? 1.03 : o.minCousin;

        const profiles = [], envs = [], envsRev = [];
        for (const b of bands) {
            const img = b.img;
            const scale = b.mwEst / 1.2;
            const margin = 14 * b.mwEst;
            const xa = Math.max(0, b.xl - margin);
            const xb = Math.min(img.width, b.xr + margin);
            const anchors = [
                [Math.max(xa, b.xl - 8 * b.mwEst), b.xl + 5 * b.mwEst],
                [b.xr - 5 * b.mwEst, Math.min(xb, b.xr + 8 * b.mwEst)],
            ];
            const ry1 = Math.max(0, b.by1), ry2 = Math.min(img.height, b.by2);
            if (ry2 - ry1 < 12) continue;
            const h = ry2 - ry1;
            const a = ry1 + ((h / 6) | 0), c = ry2 - ((h / 6) | 0);
            const prof = extractProfile(img, xa, xb, a, c, (b.slope || 0) * (c - a), scale, anchors);
            profiles.push(prof);
            envs.push({ xl: (b.xl - xa) / scale, xr: (b.xr - xa) / scale });
            envsRev.push({ xl: (xb - b.xr) / scale, xr: (xb - b.xl) / scale });
        }
        if (!profiles.length) return null;

        // HYBRID: search with the strongest single-frame machinery, judge with the
        // burst. The full scanBand pipeline (sub-bands, extent refinement, repairs,
        // cousin-pair moves) finds the truth far more reliably than any slimmed-down
        // burst generation (measured both ways), while the burst's quality-weighted
        // N-frame joint is the far stronger JUDGE — so the CLEANEST frame proposes
        // finalists and every frame votes on them.
        //
        // Frame quality = physics-fit residual on known structure. (High-frequency
        // energy was tried and is exactly wrong: ghost-doubled edges ADD high
        // frequencies, so it ranks the most motion-smeared frame "sharpest".)
        const usable = [];
        for (let i = 0; i < profiles.length; i++) {
            if (usable.length >= 2 && Date.now() >= deadline) break;
            const prof = profiles[i], env = envs[i];
            const inside = [...prof.slice(Math.max(0, Math.round(env.xl / 0.25)), Math.min(prof.length, Math.round(env.xr / 0.25)))].sort((a, b) => a - b);
            if (inside.length > 20) prof.amp = inside[Math.floor(inside.length * 0.95)];
            const seed0 = { x0: env.xl + 1, mw: (env.xr - env.xl) / 95, q: 0, r: 0 };
            const fit = fitPhysics(prof, seed0, [0.6, 0.9, 1.3, 1.8, 2.4], 2, deadline, true);
            usable.push({ prof, env: envs[i], envRev: envsRev[i], band: bands[i], fit });
        }
        const bestFit = Math.min(...usable.map(u => u.fit.cost));
        for (const u of usable) {
            u.w = Math.max(0, Math.min(1, bestFit / Math.max(u.fit.cost, 1e-9)));
            // Decodability is set by how much fine structure SURVIVED, not by how
            // well the smear was modeled: a heavily-ghosted frame fits its (ghost-
            // aware) physics beautifully and would rank "best" by residual alone.
            u.sigEff = Math.sqrt(u.fit.sigmaM * u.fit.sigmaM + (u.fit.gh || 0) * (u.fit.gh || 0) / 4);
        }
        usable.sort((a, b) => b.w - a.w);

        // Up to three reference frames, sharpest-first: a single reference is
        // brittle — its search can miss the truth another frame's search finds.
        const refOrder = usable.slice().sort((a, b) => a.sigEff - b.sigEff).slice(0, 3);
        let lastRefused = null;
        for (let ri = 0; ri < refOrder.length; ri++) {
            if (ri > 0 && Date.now() >= deadline) break;
            const refBand = refOrder[ri].band;
            // Fractional budget so one leaky stage cannot eat the whole cycle; the
            // judge needs the remainder.
            const searchBudget = deadline === Infinity ? undefined
                : Math.max(3000, Math.min(10000, (deadline - Date.now()) * 0.4));
            const s1 = scanBand(refBand.img, 0, refBand.img.height, {
                returnTop: 10, maxRatio: 1.01, minCousin: 0,
                budgetMs: searchBudget,
                minMwPx: o.minMwPx, grids: o.grids,
            });
            if (!s1 || !s1.finalists || !s1.finalists.length) continue;
            // A gate-clean single-frame decode needs no further arbitration — the
            // burst must never do WORSE than its best frame alone. The judge handles
            // the frames where single-frame search is uncertain.
            if (s1.digits && s1.ratio <= 0.85 && s1.cousinRatio >= 1.05) {
                const out = { digits: s1.digits, ratio: s1.ratio, cousinRatio: s1.cousinRatio, mwPx: mwMed, frames: 1 };
                if (o.debug) { out.top = s1.top; out.phys = s1.phys; }
                return out;
            }

            // Judge the finalists across every frame, in the search's orientation.
            const P = usable.map(u => (s1.rev ? reverseProfile(u.prof) : u.prof));
            const E = usable.map(u => (s1.rev ? u.envRev : u.env));
            const judged = decodeJoint(P, {
                physList: usable.map(u => physBracket(u.fit)),
                weights: usable.map(u => u.w),
                candidates: s1.finalists, verify: s1.finalists.length,
                repairIters: 2, envs: E, deadline,
                debug: o.debug, mustScore: o.mustScore,
            });
            if (!judged) continue;
            if (judged.ratio <= maxRatio && judged.cousinRatio >= minCousin) {
                const out = { digits: judged.digits, ratio: judged.ratio, cousinRatio: judged.cousinRatio, mwPx: mwMed, frames: profiles.length };
                if (o.debug) { out.top = judged.top; out.inPool = judged.inPool; out.phys = judged.phys; }
                return out;
            }
            if (o.debug && !lastRefused) {
                lastRefused = { digits: judged.digits, ratio: judged.ratio, cousinRatio: judged.cousinRatio, mwPx: mwMed, frames: profiles.length, refused: true };
                lastRefused.top = judged.top; lastRefused.inPool = judged.inPool; lastRefused.phys = judged.phys;
            }
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

    const api = { scanBand, scanBurst, locate, quickFit, barRows, decodeProfile, decodeJoint, extractProfile, reverseProfile, synthProfile, selfTest, fullCost, refine, buildModules, guardCost, segCost, cdfFor, gridPos };
    // Page, Web Worker, and Node-vm lab all load this file; attach wherever exists.
    if (typeof globalThis !== "undefined") globalThis.UpcWaveform = api;
    if (typeof window !== "undefined") window.UpcWaveform = api;
})();
