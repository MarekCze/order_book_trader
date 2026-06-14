# Task — Setup 4 (LVN vacuum): D2 fine-grid calibration + a quality filter

**Status:** ⛔ INVESTIGATED → SHELVED (2026-06-14). Part 1 diagnostic done
(`runs/s4-d2-sweep-results.md`): S4's LVN-vacuum entries are **negative-edge** on the 5-day ES
sample (hit ~6%, avg MFE < avg MAE across the entire firing region; D3 tightening doesn't rescue
it). No quality-filter sub-population to build toward — **the quality-filter code was NOT built.**
Like S1, documented not-viable on this MBP-10 ES data; leave dormant (rulebook defaults fire 0).
Revisit only with much more data or a different detection basis. **Parts 1–3 below are the
original plan, retained for that future revisit.**
**Type:** Threshold calibration + quality-gate design.
**Source:** `runs/tuning-log.md`, `runs/permissive-run-results.md`, `runs/s4-d2-sweep-results.md`.

## Why / where it dies

S4 thesis: when resting liquidity ahead of price evaporates, price travels fast through the thin
LVN to the next HVN — follow the vacuum (momentum, pays the spread). On real data D1 (LVN
proximity + HVN room) fires constantly; **D2 (the depth-pull signal) passes 0 at the rulebook
values** — but unlike S1, D2 *does* open, on a **cliff**:

| factor | `DepthDeclineFraction` / `PullRatioMin` | D2 pass / eval (week) | candidates |
|---|---|---|---|
| ×1.0 | 0.40 / 1.5 | 0 / 4,686,141 | 0 |
| ×0.8 | 0.32 / 1.2 | 0 / 4,686,141 | 0 |
| ×0.6 | 0.24 / 0.9 | 214,868 / 3,879,650 | **50,910** |
| ×0.4 | 0.16 / 0.6 | 306,312 / 2,249,518 | 43,819 |

The firing boundary is ~×0.7: **0 → ~51k candidates/week in one step.** The rulebook 0.40/1.5 is
far on the dead side; just below the cliff it **floods** (50k candidates is not a tradeable
setup). In the permissive run S4 dominated 30/48 trades with garbage quality. So S4 needs **both**
a finer calibration **and** a tight quality filter — opening the gate alone produces noise.

## The work (two parts)

### Part 1 — Fine-grid D2 calibration

Sweep `DepthDeclineFraction` and `PullRatioMin` independently on a **fine grid inside [×0.6,
×0.8]** (e.g. DDF ∈ {0.40,0.36,0.32,0.28,0.24}, PRM ∈ {1.5,1.3,1.1,0.9}) via `--set`, ephemeral
state, both sides, summed over the week. Map the candidate-count surface to find a region that
fires *some* but not *floods*. Note the F16 pull-ratio is a documented MBP-10 approximation
(traded-vs-cancelled heuristic) — factor its noisiness into how literally to trust PRM.

### Part 2 — A quality filter (the actual point)

A bare D2 opening yields tens of thousands of low-quality candidates, so add discriminators so
only *genuine* vacuums fire. Candidate filters to evaluate (design discussion picks which):
- Tighten **D3 — aggressor alignment** (`MinAlignedDeltaContracts`, S4 delta window
  `DeltaWindowSeconds`=30) so the move has real flow behind it.
- Tighten **location/room**: `LvnProximityTicks`=3, `HvnRoomTicks`=8 — require thinner LVN
  (F32 lvn_depth_ratio) and clearer room to the HVN (F33).
- A **flow / continuation-quality gate** analogous to the S5 work
  (`tasks/001-s5-flow-exhaustion-gate.md`) — but inverted: S4 is a *continuation* setup, so it
  wants flow *confirming* the break, the opposite of the fades. Make sure the quality gate is
  direction-correct for a momentum setup.

### Part 3 — Validate as entries, not just counts

Run any firing configuration through the **entry-quality forensic** (`runs/entry-quality-diagnosis.md`
method): MAE/MFE, did the price actually vacuum to the HVN (favorable MFE) vs stall/reverse,
time-in-trade, fill realism (S4 pays the spread via a stop entry — the pessimistic fill model
bites hardest here). A flood of candidates with poor MFE is **not** a revived setup.

## Where (code references)

- Detector: `src/OrderFlow.Domain/Trading/LvnVacuumDetector.cs` (D1→D4 state machine; single
  100% target front-running the HVN, no T1/runner).
- Guards: `src/OrderFlow.Domain/Trading/LvnVacuumGuards.cs`.
- Options: `src/OrderFlow.Domain/Trading/LvnVacuumOptions.cs` — `DepthDeclineFraction`=0.40 (l.29),
  `PullRatioMin`=1.5 (l.32), `LvnProximityTicks`=3 (l.16), `HvnRoomTicks`=8 (l.20),
  `DeltaWindowSeconds`=30 (l.37), entry/stop/target offsets (l.47/52/55), time stop 180s (l.58).
- Feature inputs: F16 (pull ratio, approximate), F21 (depth change), F22 (vanish), F32 (LVN depth
  ratio), F33 (HVN distance), F8 (delta) — confirm guards read intended features.
- Tooling: `runs/sweep_s1_s4.sh` (already sweeps DDF+PRM by a single factor — extend to a 2-D
  fine grid); funnel chain `D1lvn→D1room→D2→D3`.

## Acceptance criteria

1. `runs/`-style write-up with the 2-D D2 firing surface and a recommended (DDF, PRM) region.
2. A designed-and-justified quality filter; with it, candidate count is in a tradeable range
   (not tens of thousands) **and** the entry-quality forensic shows favorable MFE (genuine
   vacuums), not noise.
3. Determinism preserved; byte-identical when defaults unchanged; default changes only with
   sign-off + week report. TDD on new/changed guards (incl. the quality filter).
4. **Process guard:** a maximally-open profile (D2/D3 always-pass) is degenerate
   (candidate-per-event, ~68 MB/day journal — observed, killed). Keep sweeps moderated to firing
   levels; never ship an always-pass gate.

## Open questions (discuss before coding)

- How much to trust `PullRatioMin` given F16 is a heuristic MBP-10 approximation?
- Which quality filter(s) — D3 strength, location tightening, a confirming-flow gate — and what
  target candidate frequency counts as "revived"?
- Does the pessimistic spread-paying fill model make S4 structurally unprofitable even when it
  fires correctly? Validate fills explicitly.

## Not in scope

- S1 / S2 (separate tasks). One-position portfolio rule (concurrency) — separate.
