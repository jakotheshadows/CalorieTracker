# UPC waveform decoder (experiment — not shipped)

An attempt to read barcodes that are too small/blurry for binarizing decoders
(zxing-wasm included), the way commercial engines like Dynamsoft do: treat the scan
band as a 1D analog waveform and decode it by model fitting instead of thresholding.

## What it does

- Averages the scan band (red channel — colored inks stay dark) into a sub-pixel
  profile, normalized against a rolling white level.
- Models the printed code as bars **with ink spread** convolved with a blur kernel,
  on a module grid with **quadratic + cubic curvature** (curved labels).
- Anchors the grid via guard patterns + **quiet zones**, seeded from a variance
  **envelope** estimate of the code's extent.
- Decodes digits by maximum-likelihood template matching (no binarization), with
  beam search + forced check digit, then **ICM sweeps** (single + adjacent-pair digit
  moves alternated with geometry refits).
- Verifies candidates under **frozen physics** (per-candidate blur must not be a free
  parameter — wrong strings hide behind extra smear) with geometry-only refinement,
  scored jointly across multiple bands.

## Status / findings (2026-08-28)

- Synthetic self-tests pass: decodes 1.05 px/module codes through blur, ink spread,
  and curvature that defeat zxing-wasm (`selfTest()` in the module).
- On the real test frame (Spring Valley bottle, ~100 px barcode, blue ink, glare,
  cylinder curvature, truth `713733788632`): **the model's best checksum-valid
  explanation of the extracted profiles is a wrong code** (`711113388632`, honest
  joint score 2784 vs truth 3415). The information in a single 1080p frame of this
  scene is genuinely ambiguous under a 1D model — this is a modeling ceiling, not a
  search failure (the search *does* find the global-best string).
- Conclusion: shipping this would produce confident misreads on exactly the frames
  it exists for. The missing pieces commercial engines have: per-row 2D rectification
  of label curvature (our 1D shear projection loses the weak-signal mid-region) and
  multi-frame fusion. Either is a substantial project.

## Lab

`lab/` contains Node probes (need `npm i pngjs`, and a `test-frame.png` — a saved
full-resolution scanner frame; not committed since test frames contain the user's
surroundings). `harness.js` runs self-tests + a real-frame decode, `icm2.js` the
joint ICM experiment, `honest.js` the frozen-physics truth-vs-impostor scorer.
Paths inside point at the repo copy of `upc-waveform.js`; adjust as needed.
