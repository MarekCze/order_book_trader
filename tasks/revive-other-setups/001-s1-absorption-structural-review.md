# Task — Setup 1 (Absorption fade): structural metric review of A4 / A5 / A6

**Status:** Proposed — **investigation first; likely a definition change, NOT a threshold tweak.
DESIGN DISCUSSION REQUIRED.**
**Type:** Detector metric/definition review (deepest of the three).
**Source:** `runs/tuning-log.md` (item 4 sweep + S1 deep probe), `runs/tuning-iteration-1-results.md`.

## Why / where it dies

S1 thesis: a passive bid absorbs aggressive selling at an LOI; when sellers exhaust, price
reverts up. On real data the funnel shows S1 produces thousands of A3 stalls but **A4 passes
0 of them, every day, both sides** — and lowering the threshold barely helps:

| factor | `StallVolumeMultiple` | A4 pass / eval (week) | candidates |
|---|---|---|---|
| ×1.0 | 3.0 | 0 / 97,484 | 0 |
| ×0.4 | 1.2 | 0 / 97,484 | 0 |
| ×0.2 | 0.6 | 8,430 / 97,484 | 0 (A5/A6 then block) |
| ×0.1 | 0.3 | 30,588 / 79,306 | 3 |

A4 (volume-without-progress) only *begins* to pass at **5× below** the rulebook value, and even
then **A5 (replenishment) and A6 (exhaustion) block** — only 3 candidates emerge at ×0.1. This
is not a calibration miss: the measured stall volume at `[L, L+1]` is **typically a fraction of
the per-price baseline, never a multiple.** S1 has **several deep walls**, so threshold tuning
is meaningless until the metrics are reviewed against this data.

## The investigation (do this before proposing any change)

For each guard, determine **what is actually being measured vs what the rulebook intends**, on
real MBP-10 data:

- **A4 — volume-without-progress** (`StallVolumeMultiple`=3.0, `Setup1Options.cs:45`;
  `MinStallSellPrints`=1, l.42). Rulebook: "volume at `[L, L+1]` ≥ 3× the 15-min per-price
  baseline." Audit: what volume is accumulated at the level during the stall window, and what is
  the baseline it's compared to? Hypothesis: either the accumulation window/levels are wrong, or
  the per-price baseline is computed over a span that makes 3× unreachable at a single level.
  Use `inspect-trade --data` on A3-stall events to dump the actual numbers.
- **A5 — replenishment** (`ReplenishRatioMin`=2.5, `RefreshCountMin`=3, l.48/53). This is the
  F17 replenishment ratio (traded vs displayed at a price). Audit whether the refresh-count and
  ratio are observable at MBP-10 granularity during a real stall.
- **A6 — exhaustion** (`ExhaustionBucketSeconds`=10, `ExhaustionDropRatio`=0.70, l.56/59):
  "sell volume per 10s bucket dropped ≥70% from peak and last bucket delta ≥ 0." (Note: this is
  the same exhaustion concept proposed for S5 in `tasks/001-s5-flow-exhaustion-gate.md` Option
  B — findings should be shared.)
- **A3 — stall** (`StallSeconds`=45, l.38): some days max stall is ~15–24s, so A3 itself can be
  0; secondary to A4 but in scope.

**Deliverable of the investigation phase:** a short `runs/`-style write-up answering, per guard:
is the metric (a) correctly computed but the market just doesn't do this at a single price under
MBP-10, (b) mis-computed/mis-windowed and fixable, or (c) not computable from MBP-10 as written?
Outcome (a)/(c) is a legitimate conclusion — document and possibly **disable S1 with a rationale**
rather than force it.

## Where (code references)

- Detector: `src/OrderFlow.Domain/Trading/AbsorptionFadeDetector.cs` (state machine A1→A6).
- Guards: `src/OrderFlow.Domain/Trading/AbsorptionGuards.cs` (the A1…A6 guard methods).
- Options: `src/OrderFlow.Domain/Trading/Setup1Options.cs` (all thresholds listed above).
- Feature inputs: F15 (volume-at-price ratio), F17 (replenishment), F8 (delta) — in
  `FeatureSnapshot` / `FeatureEngine`; confirm each guard reads the intended feature.
- Tooling: `runs/sweep_s1_s4.sh` (already sweeps `StallVolumeMultiple`); funnel telemetry chain
  `A1→A2rdy→A2→A3→A4→A5→A6`.

## Acceptance criteria

1. Investigation write-up exists and classifies each of A4/A5/A6 as fixable / market-reality /
   not-MBP-computable, with `inspect-trade` evidence.
2. **If** a metric fix is agreed: it is implemented behind named config, unit-tested (TDD),
   byte-identical when the fix is off / defaults unchanged, and re-run on the week with funnel +
   entry-quality forensic (MAE/MFE, continuation-vs-reversal) — a setup that fires only into
   continuation is **not** accepted.
3. **If** the conclusion is "not computable from MBP-10": document it (CLAUDE.md / rulebook
   annotation) and keep S1 effectively dormant rather than mis-tuned.

## Open questions (discuss before coding)

- Is the per-price baseline for A4 the right denominator, or should "volume without progress"
  be measured over a band of levels / a different window?
- Should A5/A6 be reconsidered together with the S5 exhaustion gate (shared guard)?
- Acceptable to conclude S1 is structurally unsupported on MBP-10 and shelve it?

## Not in scope

- Setup 3 (iceberg) — permanently deferred (needs MBO). Don't conflate A5/F17 work with it.
- Threshold-only sweeps as a "fix" — proven futile here.
