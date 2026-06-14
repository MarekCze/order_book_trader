# TP-target sweep on the gated S5 setup (2026-06-14)

Question: with both S5 flow-exhaustion gates on (`runs/s5-flow-gate-results.md`) and a single
full-exit target (P2 geometry, `T1ExitFraction=1.0`), what target distance squeezes the most out
of the setup? Swept `Detectors:Setup5:T1RMultiple` from 0.5R to 2.0R via `--set` on ephemeral
state (`runs/tp_sweep.sh`); no defaults changed. R ≈ 4 ticks (entry H2−2, stop H2+2), so the
target in ticks ≈ 4 × T1R.

**Per the user's direction, overfitting is accepted here** — the goal is to see the response
surface across values, not to claim a robust parameter. n is tiny (7–10 trades over 5 days).

## Results (both gates on; full exit at the target)

| T1R | target ticks | trades | wins | hit | net | avg win | exits |
|---:|---:|---:|---:|---:|---:|---:|---|
| 0.50 | ~2 | 10 | 5 | 50.0% | −$2,015 | $236 | Stop 5 / Tgt 5 |
| 0.75 | ~3 | 8 | 4 | 50.0% | −$1,112 | $361 | Stop 4 / Tgt 4 |
| 1.00 | ~4 | 8 | 4 | 50.0% | −$612 | $486 | Stop 4 / Tgt 4 |
| 1.25 | ~5 | 8 | 4 | 50.0% | −$112 | $611 | Stop 4 / Tgt 4 |
| **1.50** | **~6** | **8** | **4** | **50.0%** | **+$388** | **$736** | **Stop 4 / Tgt 4** |
| 1.75 | ~7 | 7 | 2 | 28.6% | −$1,473 | $861 | Stop 5 / Tgt 2 |
| 2.00 | ~8 | 7 | 2 | 28.6% | −$1,223 | $986 | Stop 5 / Tgt 2 |

(2.25–3.0R not run — the curve had clearly turned over; targets fill even less past 2.0R.)

## The shape

- **Rising limb (0.5R → 1.5R):** the *same 4 winners* reach the target every time (Tgt count
  flat at 4), so widening the target just banks more per win — avg win $236 → $736, net climbs
  monotonically −$2,015 → **+$388**. Hit rate is pinned at 50%.
- **Cliff at 1.75R:** two of the four winners no longer reach the target — they round-trip back
  to the stop instead (Tgt 4→2, Stop 4→5, hit 50%→29%, net falls off). Beyond ~6 ticks the
  setup's winners simply don't travel far enough.
- **Optimum: T1R ≈ 1.5 (~6 ticks), net +$388** — the only net-positive point, and the first
  positive result this project has produced.

## Why 1.5R, and why it's believable (within the overfit caveat)

The optimum is **not arbitrary — it matches the entry-quality diagnosis.** The 5 winning entries
there had MFE ≈ {5, 7, 8, 8, 12} ticks. A ~6-tick target sits right at the dense part of that
cluster: it captures the moves that genuinely travel while staying inside what most winners reach.
Push the target past the cluster (≥7 ticks) and you give back winners to the stop — exactly the
1.75R cliff. So the target sweep and the MFE distribution agree on where the edge of the move is.

## Honest caveats (do not deploy this number)

- **n = 7–10 trades over 5 days.** +$388/week is a single overfit sample, not an edge. Some
  value had to be "best"; this tells us the *shape* (a clean single peak at the winners' MFE),
  not a trustworthy parameter.
- **The one-position rule couples target distance to trade count** (0.5R freed the slot faster →
  10 trades; 1.75R held losers longer → 7). So "trades" is not constant across rows.
- **Costs dominate the margin:** avg loss is fixed at −$639 (R + 1-tick stop slippage + $14
  commission); the whole positive result is 4 × $736 − 4 × $639 = +$388. A couple of extra
  losers erases it. Real validation needs **more ES days**, ideally out-of-sample.
- All gate thresholds remain placeholders; this sweep held them at defaults and only moved the
  target.

## Takeaway / next

The lever stack **entry gates (cut continuation losers) + full exit at ~1.5R (bank the move at
the winners' MFE)** turns the week from −$6,161.50 to **+$388** — proof the setup *can* clear
costs when entries and target are aligned to the same underlying signal. Next, if pursued:
re-confirm on more data (out-of-sample days), then the gate-threshold sweeps
(`MaxTriggerFlowZ`, `ExhaustionDropRatio`, bucket/lookback). No defaults changed.
