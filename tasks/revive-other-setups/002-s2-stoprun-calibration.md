# Task — Setup 2 (Stop-run fade): B3 / B5 calibration review

**Status:** Proposed — calibration + possible definition review. **DESIGN DISCUSSION REQUIRED
before changing defaults.**
**Type:** Threshold calibration with quality validation.
**Source:** `runs/tuning-log.md` (funnel on real week), `runs/permissive-run-results.md`.

## Why / where it dies

S2 thesis: a sweep 1–5 ticks above an obvious high (PDH/ONH/old swing) fills breakout buyers and
triggers stops; with no follow-through they are trapped and their forced exits fuel a reversal —
fade it. On real data the funnel is:

```
S2 StopRunFade : B1B2 (sweeps exist) → B3 (~1% pass) → B4 (all pass) → B5 (~0/N) → ~1 candidate/week
```

Two sequential walls:
- **B3 — climax** (`ClimaxVolumePercentile`=0.90, `ClimaxShareBeyondLevel`=0.60;
  `Setup2Options.cs:25/29`): the breaking bar's aggressive buy volume must be ≥ 90th percentile
  with ≥ 60% executing at/above the level. Passes ~1%.
- **B5 — supply confirmation** (`SupplyDepthIncrease`=0.50, or stacked sell imbalance
  `StackedImbalanceMinLen`=3 / `ImbalanceRatio`=3.0; l.42/45/48): offers must stack ≥ 50% above
  the sweep (or a 3:1 stacked sell imbalance). Takes nearly all of B3's survivors.

So S2 isn't structurally dead like S1 — sweeps and follow-through exist; the **climax + supply
confirmation are jointly too strict** for this week's microstructure, yielding ~1 candidate.

## The investigation

1. **Quantify each wall independently.** Extend/parameterize a sweep (cf. `runs/sweep_s1_s4.sh`)
   over `ClimaxVolumePercentile`, `ClimaxShareBeyondLevel`, `SupplyDepthIncrease`,
   `StackedImbalanceMinLen`, `ImbalanceRatio` via `--set`, ephemeral state. Find the level at
   which B3 and B5 each begin to pass and how candidate count scales — is there a clean firing
   region or a cliff/flood (as S4 has)?
2. **Definition check on B3:** the climax percentile is over the per-bar aggressive-buy
   distribution. Confirm the percentile baseline has enough sample and is measured over the right
   bar definition (footprint/volume bar) — a 90th-pctl gate on a thin distribution can be
   near-impossible. Use `inspect-trade --data` on B1B2 sweep contexts to read the actual climax
   numbers vs the threshold.
3. **Definition check on B5:** is the supply/depth-increase observable at top-10 MBP after a
   sweep, or does the relevant stacking happen beyond the visible book?

## Where (code references)

- Detector: `src/OrderFlow.Domain/Trading/StopRunFadeDetector.cs` (B1→B5 state machine).
- Guards: `src/OrderFlow.Domain/Trading/Setup2Guards.cs`.
- Options: `src/OrderFlow.Domain/Trading/Setup2Options.cs` (thresholds above; entry stop H−2
  l.54, stop sweep+1 l.60, T2≤4R l.73, scratch 60s l.79).
- Feature inputs: F25 (bar delta / pctl), F23/F24 (diagonal / stacked imbalance), depth features
  for supply stacking. Confirm guards read the intended features.
- Tooling: funnel chain `B1B2→B3→B4→B5`; `runs/sweep_s1_s4.sh` pattern.

## Acceptance criteria

1. A `runs/`-style write-up: per-wall firing curves, whether B3/B5 are calibratable or
   definitionally too strict on MBP-10, and a recommended candidate threshold set (if any).
2. **If** thresholds are loosened: re-run the week with funnel + the **entry-quality forensic**
   (MAE/MFE, continuation-vs-reversal, time-in-trade). The S2-specific risk: fading a *real*
   breakout. A "scratch if reclaim above H holds 60s" already exists (`ScratchSeconds`) — verify
   it actually protects the loosened entries. Entries that fire only into continuation are not
   accepted.
3. Determinism preserved; byte-identical when defaults unchanged; any default change only with
   sign-off + supporting week report. TDD on any changed guard.

## Open questions (discuss before coding)

- Is the B3 climax-percentile baseline well-formed (sample size, bar definition), or is the 90th
  pctl effectively unreachable here? Calibrate vs redefine?
- Is B5 supply stacking visible in top-10 MBP post-sweep, or is it a partial-observability gap
  (like F16 pull-ratio) that should be documented as approximate?
- Target candidate frequency: how many S2 trades/week is "revived" vs "flooded"?

## Not in scope

- S1 / S4 (separate tasks). The one-position portfolio rule (concurrency) — separate.
