# Setup 4 (LVN vacuum) — D2 fine-grid diagnostic (2026-06-14)

Part 1 of `tasks/revive-other-setups/003-s4-lvn-vacuum-calibration-quality.md`. Maps the D2
firing surface AND the resulting entry quality, to decide whether a quality filter is viable.
S4 **isolated** (S1/S2/S5 disabled) so it isn't starved of the one-position slot; D3
`MinAlignedDeltaContracts` held at default 100; config-only via `--set`; `runs/s4_d2_sweep.sh`.
5 ES days 2026-06-01..05. MFE/MAE in ticks (favorable = price vacuums on; stop ≈ R = 5 ticks).

## Surface (DepthDeclineFraction × PullRatioMin)

| DDF \ PRM | 1.2 | 1.0 | 0.8 |
|---|---|---|---|
| 0.34 | 0 cand | 0 cand | 1,228 cand · 37t · hit 8.1% · MFE 3.5 / MAE 4.9 · −$15,236 |
| 0.30 | 0 | 0 | 2,166 cand · 48t · hit 6.2% · MFE 4.5 / MAE 4.9 · −$21,103 |
| 0.26 | 0 | 0 | 4,286 cand · 53t · hit 5.7% · MFE 4.1 / MAE 4.9 · −$23,470 |
| 0.22 | 0 | 0 | 8,107 cand · 53t · hit 5.7% · MFE 4.2 / MAE 4.9 · −$23,470 |

(rulebook default DDF 0.40 / PRM 1.5 = 0 candidates, confirmed.)

## Findings

1. **`PullRatioMin` is the *sole* binding gate — `DepthDeclineFraction` is inert.** Every cell with
   PRM ≥ 1.0 fires **zero** candidates at every DDF (0.34→0.22). Only PRM = 0.8 opens D2. So the
   earlier "×0.7 cliff" was really a *PullRatioMin* cliff; DDF does almost nothing in this range.
   Once PRM=0.8, lowering DDF only inflates candidate count (1,228 → 8,107) — but trades stay
   capped (~37–53, one-position) and **quality is flat**. (DDF 0.26 and 0.22 give identical
   trades/net: the extra candidates just get blocked PositionOpen.)

2. **Where it fires, the entries are negative-edge — this is the decisive result.** Across every
   firing cell: **hit rate 5.7–8.1%**, and critically **avg MFE (3.5–4.5t) < avg MAE (4.9t)** —
   the average S4 entry travels *further against* the position than for it. Net −$15k to −$23k
   over the week on 37–53 trades. The LVN-vacuum thesis (price vacuums through the thin LVN to the
   next HVN) **does not hold on this ES data** — price reverts through the LVN more often than it
   accelerates. n here is decent for S4 (37–53 trades/cell) and the ~6% hit is consistent across
   all firing cells, so this is a robust signal, not small-sample noise.

3. **Implication for a quality filter:** filtering toward profit requires a profitable
   sub-population to exist. When the *signal-level* average is adverse (MFE < MAE) and hit ≈ 6%, a
   D3/location filter would have to discard ~94%+ of entries and still find edge in the remainder —
   a long shot. Before building it, one cheap check remains: does requiring strong confirming
   aggressor flow (tightening D3 `MinAlignedDeltaContracts`) carve out a sub-population where
   MFE > MAE? That probe is running (`runs/s4_d3_probe.sh`). If even strong D3 alignment can't lift
   MFE above MAE, **S4 should be shelved like S1** (documented not-viable on MBP-10 ES), not
   force-calibrated.

## D3 probe — does stronger confirming flow rescue it? (`runs/s4_d3_probe.sh`)

D2 fixed at DDF 0.30 / PRM 0.8 (a firing point); swept D3 `MinAlignedDeltaContracts` (require
this much aligned aggressor delta at the trigger).

| MinAlignedDelta | cand | trades | hit | MFE / MAE | net |
|---|---|---|---|---|---|
| 100 (default) | 2,166 | 48 | 6.2% | 4.5 / 4.9 | −$21,103 |
| 200 | 1,494 | 38 | 5.3% | 4.0 / 4.9 | −$17,344 |
| 400 | 1,235 | 18 | 11.1% | 4.4 / 4.8 | −$6,676 |
| 800 | 7 | 4 | 0.0% | 3.8 / 5.0 | −$2,134 |
| 1500 | 1 | 1 | — | 14.0 / 5.0 | −$533 |

**Tightening D3 does not rescue S4.** MFE stays below MAE at every usable setting; hit rate only
nudges (6%→11% at MAD 400) while still bleeding (−$6.7k). Past that the setup essentially stops
firing — MAD 800 → 7 candidates / 4 trades / 0 wins; MAD 1500 → a single trade (its MFE>MAE is
**n=1 noise**, not signal). There is no setting that both fires and shows positive edge.

## Verdict: SHELVE S4 (like S1) — not viable on this MBP-10 ES data

The LVN-vacuum *signal itself* is negative-edge here (MFE < MAE across the entire firing region),
and the candidate quality filter (D3) has no profitable sub-population to filter toward. Unlike S5
— where real winners existed and just needed protecting — S4 has nothing to build on. **Recommend:
do not build the quality-filter code; leave S4 effectively dormant** (rulebook defaults already
fire 0), and document it. Revisit only with much more data, or with a different detection basis,
if ever.

- **Untested lever, deliberately not pursued:** location tightening (`LvnProximityTicks`,
  `HvnRoomTicks`). With MFE < MAE the *direction* of the entry is wrong (price reverts through the
  LVN), and location tightening doesn't fix a wrong-direction signal — not worth the spend on this
  sample.

## Caveats

- 5 days, one regime. The finding is robust *within* this sample (37–53 trades/cell, consistent
  ~6% hit), but "S4 has no edge" is a statement about these 5 ES days, not a universal law — more
  data (or MNQ) could differ. The recommendation is "shelve now," not "S4 is permanently dead."
- The pessimistic fill model bites S4 hardest (stop entry pays the spread + 1 tick) — intended,
  but it compounds the poor raw edge.
- `PullRatioMin` rests on F16, a documented MBP-10 approximation (traded-vs-cancelled heuristic) —
  its noisiness is a plausible contributor to why the depth-pull signal doesn't separate real
  vacuums from reversion.
