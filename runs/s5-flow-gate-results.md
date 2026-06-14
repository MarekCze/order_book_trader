# Setup 5 flow-exhaustion gates — first results (2026-06-14)

Implements `tasks/001-s5-flow-exhaustion-gate.md`: two independent, **opt-in (off by default)**
gates on the S5 trigger that refuse to fade while the move is still being pushed — the
continuation failure mode found in `runs/entry-quality-diagnosis.md`. Code is TDD'd; with both
gates off the week is **byte-identical** to baseline (16 trades / −$6,161.50, verified).

- **Option A — flow-climax gate** (`Setup5:FlowClimaxGateEnabled`): block while the with-the-move
  F8 flow z-score (`MaxTriggerFlowZ`, window `FlowClimaxWindowSeconds`) is ≥ threshold.
- **Option B — deceleration gate** (`Setup5:FlowDecelGateEnabled`): require the last completed
  with-the-move delta bucket to have dropped ≥ `ExhaustionDropRatio` below the trailing peak
  (`ExhaustionBucketSeconds`, `ExhaustionLookbackBuckets`) — a Setup 1 A6 analogue.

## Results (5 real ES days 2026-06-01..05, chained state; defaults except the gate flags)

| Config | Trades | Wins | Hit rate | Net | vs baseline |
|---|---:|---:|---:|---:|---:|
| Baseline (both off) | 16 | 5 | 31% | −$6,161.50 | — (byte-identical) |
| Flow-Z gate A (z≥10) | 12 | 4 | 33% | −$4,418.00 | +$1,743.50 (−28%) |
| Decel gate B (drop≥0.70) | 12 | 5 | 42% | −$3,605.50 | +$2,556.00 (−42%) |
| **Both gates** | **8** | **4** | **50%** | **−$1,862.00** | **+$4,299.50 (−70%)** |

Headline numbers via SQL over the per-day journals (`disposition='Traded'`); artifacts under
`runs/artifacts/gate-verify/` (gitignored).

## Findings

- **Every gate improves entry quality in the predicted direction.** The diagnosis said S5 fades
  live climaxes; gating on flow exhaustion removes losers while largely keeping winners.
- **Option B (deceleration) is the stronger single gate** — it cut 4 losers while **keeping all
  5 winners** (hit rate 31%→42%), vs Option A which also dropped one winner (the Day-1 exact-top
  entry that fired into a 20σ climax).
- **Both gates together** cut the loss **70%** and lifted hit rate to **50%** (8 trades). They
  are complementary: A catches the instantaneous sub-second cascade (the Day-1 4-in-1-second
  spike); B catches slower pushes that decelerate over 10s buckets.
- **Still net-negative.** At n=8–12 with $14/trade commissions and the ≈+0.5R win cap, even a
  50% hit rate doesn't clear costs. The gates address *entry quality* (the diagnosed ceiling);
  combining with P2 exit geometry (full exit at 1R) is the natural next stack and is a separate,
  config-only lever.

## Caveats (do not over-read)

- **n is tiny** (16 → 8 traded). These are directional signals, not statistically established.
- **Thresholds are uncalibrated placeholders** (z≥10 from the diagnosis tail; drop≥0.70 and
  lookback=6 borrowed from Setup 1 / first guess). A proper sweep of `MaxTriggerFlowZ`,
  `ExhaustionDropRatio`, `ExhaustionBucketSeconds`, `ExhaustionLookbackBuckets` is the next step
  before considering any default change.
- **Off by default** — no behavioural default changed; baseline remains byte-identical. Enable
  per-run via `--set Detectors:Setup5:FlowClimaxGateEnabled=true` /
  `--set Detectors:Setup5:FlowDecelGateEnabled=true`.

## Engineering notes

- Both gates are pure guards in `Setup5Guards` (`FlowNotClimaxing`, `FlowDecelerated`), wired in
  `DeltaDivergenceFadeDetector.TryTrigger` after E4, with funnel columns `Eflow`/`Edecel`.
  Option B accumulates with-the-move aggressor delta on a fixed absolute time grid (zero-filled
  across gaps → session boundaries self-clear → pass-through), tracked across context churn
  (not in `OnReset`). Flow z-score exposed via `FeatureEngine.DeltaZScore` (same source the
  journal records as `f8_delta_z_w*`).
- **Pre-existing bug fixed along the way:** `TradeSummaryPrinter` crashed (`GetInt64` on a NULL
  `SUM`) when a setup/direction group consisted entirely of unresolved candidates (an emitted
  candidate left open at data-end). The decel gate surfaced it by shifting which candidates
  trade. Fixed by `COALESCE`-ing the boolean SUMs; regression test added.
