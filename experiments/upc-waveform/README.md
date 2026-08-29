# UPC waveform decoder

Reads barcodes that are too **blurry** for binarizing decoders (zxing-wasm included),
the way commercial engines do: treat the scan band as a 1D analog waveform and decode
it by model fitting instead of thresholding. Since 2026-08-29 the decoder **ships** in
the app (`wwwroot/js/upc-waveform.js`, run off-thread by `wwwroot/js/upc-worker.js`)
as a companion to zxing-wasm, targeting the close-held regime: webcams cannot focus
close, so a barcode held near the lens is large but heavily defocused — big modules,
soft edges, exactly what binarizers can't read and this model can.

## How it works

- `locate()` finds the code in the band with TWO candidate generators and returns up
  to 3 ranked extents: edge-energy runs (precise when the code is sharp) and
  brightness-crossing chains (a defocused code has weaker gradients than sharp
  background clutter — measured on a real frame where the energy winner was an
  office chair — but nothing else oscillates ~24-60 times about its local mean).
  Both are filtered by crossing count, so codeless frames refuse in microseconds.
- `extractProfile()` averages the band into sub-pixel profiles (red channel — colored
  inks stay dark), rescaled so any code lands at ~1.2 px/module, normalized against a
  **linear white baseline anchored in the quiet zones** (a rolling local max dips over
  ink-dense digit runs and biases decoding toward sparser wrong digits — measured),
  with the illumination ramp divided out. Anchor ranges straddle the located edges
  with a 93rd-percentile white estimate, so moderate extent error or dark clutter in
  an anchor range cannot poison the baseline.
- The printed code is modeled as bars + ink spread convolved with a **Gaussian** edge
  response on a curved module grid (quadratic + cubic). Per-segment amplitude fits are
  **clamped to the frame's global ink amplitude** so wrong templates can't amplify
  their way into matching.
- Physics (blur σ, ink spread) is fitted per frame on **known structure only** —
  guard patterns extended with their structurally-determined neighbor modules, plus
  the digit-agnostic free-digit fit — then **frozen** for every candidate (per-candidate
  physics lets wrong strings hide behind smear).
- Digits decode by ML template matching with beam search + forced check digit, then
  ICM repairs: singles, adjacent pairs, and **distant cousin pairs** (one-bar-shift
  digit confusions are the degeneracy family under blur; two of them at distant
  positions cancel in the check digit, so no chain of improving single moves connects
  them).
- Verification is joint across three sub-bands, then the winner must pass:
  - `mwPx >= 2` (below ~2 px/module a single frame is information-ambiguous — proven
    on a real 1 px/module frame where a wrong checksum-valid code fit better than the
    truth),
  - runner-up ratio <= 0.85,
  - **cousin margin**: every one-bar-shift substitution of the winner is scored
    (checksum-free); if any comes within 8%, the frame doesn't determine those
    digits and the decode is refused (misreads measure <= 0.99 — the confusion fits
    BETTER; honest decodes measure >= 1.09). This is what makes confident misreads
    not ship: in the synthetic suite the model's best guess is wrong on the two
    hardest frames, and this gate refuses both.

The live scanner (`app.js` scanner.runWasm) feeds the worker the scan-line band
whenever the worker is idle; results join zxing's double-read confirmation pool, so
acceptance still requires two agreeing reads across frames/methods.

## Lab

`lab/closeblur.js` (no deps) is the regression suite: synthetic close-held frames —
blur up to ~1 module, noise, lighting gradient, reversed codes, clutter, plus
must-refuse cases (no code; 1 px/module). Current state: decodes 6/8, refuses the
other 2 honestly (σ/mw ≥ ~1.1 — even the true code cannot beat its cousins there,
measured with forced scoring). `node harness.js self` runs the module self-tests;
`node harness.js real <frame.png>` (needs `npm i pngjs`) decodes a saved scanner
frame through the same entry point the worker uses. debug*.js / fitnorm.js are the
instrumented probes that drove the model fixes. Never commit real frames — they
show the user's surroundings.

## History

Built 2026-08-28 while chasing Dynamsoft-class reading of a ~100 px barcode in a
1080p webcam frame. That regime (1.05 px/module) proved information-ambiguous under
any single-frame 1D model: the honest best fit was a wrong code, so the decoder was
archived instead of shipped. The close-held investigation (2026-08-29) found the real
failure mode was defocus, not size — large blurry codes are information-RICH, and the
decoder ships for that regime with the ambiguity gates above. Remaining levers for
the tiny-code regime: per-row 2D rectification and true multi-frame fusion.
