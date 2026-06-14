# Task 002 — Setup 5 gate-threshold sweep (calibrate the flow-exhaustion gates)

**Status:** 🕒 DEFERRED (later date). Do **not** start before more data exists — see Dependencies.
**Type:** Calibration / response-surface mapping (config-only; no code change expected).
**Depends on / follows:** `tasks/001-s5-flow-exhaustion-gate.md` (gates implemented, commit 6987ece);
`runs/s5-flow-gate-results.md`, `runs/tp-target-sweep-results.md`.

## Why

The two S5 flow-exhaustion gates shipped with **uncalibrated placeholder thresholds**, and the
TP-target sweep was done on top of them at those placeholders. The TP sweep already found a clean
1.5R target optimum; the gate thresholds themselves have **never been swept**. This task maps how
each gate parameter trades off filtered losers vs lost winners, to find sensible values (and
eventually candidate defaults) — but only once there is enough data to make the answer mean
something (the 5-day n made the +$388 result heavily overfit; see the data-sizing discussion).

Current defaults (placeholders, in `appsettings.json` → `Detectors:Setup5`):
`MaxTriggerFlowZ=10.0`, `FlowClimaxWindowSeconds=10`, `ExhaustionDropRatio=0.70`,
`ExhaustionBucketSeconds=10`, `ExhaustionLookbackBuckets=6`.

## Scope — parameters and suggested ranges

Sweep each via `--set` on ephemeral state, with **both gates enabled** and the TP target fixed at
its swept optimum (`T1ExitFraction=1.0`, `T1RMultiple≈1.5`) so the gate effect is isolated from
exit geometry. Start 1-D (hold the others at default), then 2-D for the interacting pair.

- **Option A — `MaxTriggerFlowZ`** (block while with-move flow z ≥ this): `{4, 6, 8, 10, 12, 15, 20}`.
  Lower = stricter (blocks more). The Day-1 cascade was +13–21σ; ~10 caught it.
- **Option A — `FlowClimaxWindowSeconds`**: `{10, 30}` (must be a configured `Features:FlowWindowsSeconds`).
- **Option B — `ExhaustionDropRatio`** (last bucket must drop ≥ this below trailing peak):
  `{0.5, 0.6, 0.7, 0.8, 0.9}`. Higher = stricter (demands more deceleration).
- **Option B — `ExhaustionBucketSeconds`**: `{5, 10, 20, 30}` (smaller buckets resolve faster
  pushes; the sub-second cascade is invisible to 10s buckets — relevant to whether B can ever
  catch what A catches).
- **Option B — `ExhaustionLookbackBuckets`** (2-D with bucket size): `{3, 6, 9, 12}`.

## Method

Reuse the existing sweep pattern (`runs/tp_sweep.sh` / `runs/p2_gated_run.sh`): per value, replay
the day set chained on `Storage:SqlitePath`, then aggregate trades / wins / hit / net / avg
win-loss / exit-reason from the journals (the python summarizer in those scripts). One sweep
script per parameter; write the response surface to `runs/s5-gate-threshold-sweep-results.md`.

## Dependencies (why deferred)

1. **More data first.** On 5 days S5 makes ~8–16 trades; a multi-parameter gate sweep there is
   pure curve-fitting (some value wins by chance). Per the data-sizing analysis, a credible gate
   calibration needs on the order of **~200+ S5 trades (~1 year of ES, spread across regimes)**,
   with an out-of-sample hold-out. Running the sweep on 5 days is **diagnostic-only** (it shows
   the *shape* of each response — smooth ramp vs cliff — not a trustworthy value).
2. **Degrees of freedom.** This adds ~5 tunable knobs on top of the TP target; each one tuned on
   thin data compounds overfit. Decide up front which to actually tune vs leave at default.

## Acceptance criteria

1. A `runs/`-style response-surface writeup per parameter (and the bucket×lookback 2-D grid),
   with the data window and n stated, and whether each response is a smooth ramp or a cliff.
2. A recommended value/range per parameter **with explicit out-of-sample validation** before any
   `appsettings` default is changed. No silent default changes.
3. Determinism preserved; sweeps are `--set`-only on ephemeral state; gates remain off by default
   unless a separate, signed-off decision flips them.

## Open questions (resolve when picked up)

- Joint vs sequential: sweep gate thresholds with TP fixed at 1.5R, or re-sweep TP jointly once
  gates move? (Recommend: fix TP, sweep gates 1-D, then one joint pass around the joint optimum.)
- Is Option B worth calibrating at all, or does Option A (instantaneous z) dominate? The cascade
  is sub-second; B at 10s buckets may be redundant with A — the `ExhaustionBucketSeconds` sweep
  answers this.
- Which parameters to freeze at default to limit degrees of freedom.

## Not in scope

- The S1/S2/S4 overhauls (`tasks/revive-other-setups/`).
- Flipping any gate on by default (separate, data-backed decision).
