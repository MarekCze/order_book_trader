# Setup 2 (stop-run fade) loosening diagnostic (2026-06-14)

`tasks/revive-other-setups/002`. Question: is S2's silence *over-specified* (calibratable) or
*theory-weak on ES* (shelve like S1/S4)? Config-only, S2 isolated (S1/S4/S5 off), 5 ES days.
Scripts `runs/s2_loosen_sweep.sh` (B-chain ladder) and `runs/s2_unblock_run.sh` (measurement run).
MFE/MAE in ticks; favorable = price reverses back through the level after the sweep.

## Stage 1 — loosen the B-chain only (`s2_loosen_sweep.sh`)

| Profile | ClimaxVolPctl/Share/SupplyInc/StackLen/ImbR/SweepMax | cand | trades | hit | net | MFE/MAE |
|---|---|---|---|---|---|---|
| P0 baseline | 0.90/0.60/0.50/3/3.0/5 | 1 | 0 | — | — | — |
| P1 mild | 0.80/0.50/0.30/2/2.5/5 | 6 | 0 | — | — | — |
| P2 moderate | 0.70/0.40/0.20/2/2.5/6 | 46 | 2 | 0% | −$1,139 | 3.5/7.0 |
| P3 aggressive | 0.55/0.30/0.10/2/2.0/6 | 232 | 4 | 25% | −$1,366 | 6.2/6.5 |

Loosening B3/B5 lifts **candidates** (1→232) but **trades stay near zero** — the wall moved
downstream. P3's 232 candidates break down: **91 AttemptsExhausted, 80 Session, 50 Spread, 6
Expired, 4 Traded.** ~95% die at *global filters*, not the B-chain. The Spread block is structural:
S2's premise (a sweep / stop-run) occurs exactly when the book is stressed and the spread widens
past 1 tick, which the (correct) 1-tick spread filter rejects.

## Stage 2 — measurement run: relax Session + Attempts to force a sample (`s2_unblock_run.sh`)

The spread filter is an **exact** equality (`RiskManager.cs:157`, `SpreadTicks != RequiredSpreadTicks`)
— there is no "max spread" knob, and 1-tick spread is the only tradeable state by design. So we
left it at default (measuring the realistic tradeable subset) and relaxed only `OpenExcludeMinutes`→0,
`MaxAttemptsPerLoi`→1000, `ConsecutiveStopOutsToKillLevel`→1000.

| Profile | cand | trades | hit | net | avg MFE | avg MAE | edge | dispositions |
|---|---|---|---|---|---|---|---|---|
| P2g moderate | 15 | 3 | 33% | +$79 | **11.7t** | 5.3t | **MFE>MAE** | 3 traded / 9 expired / 3 blocked |
| P3g aggressive | 41 | 6 | 33% | −$860 | **8.2t** | 5.3t | **MFE>MAE** | 6 traded / 13 expired / 21 blocked |

## Findings — S2 is NOT theory-weak (contrast S4)

1. **The entries have favorable directional edge: avg MFE 8–12t vs avg MAE 5.3t (MFE > MAE).**
   This is the opposite of S4 (MFE < MAE) and of the tiny B-chain-only samples. When an S2 entry
   does fire, price reverses back through the level meaningfully more than it runs against — the
   stop-run-fade thesis behaves as designed on this data.
2. **Three throttles keep S2 silent, none of them "the theory is wrong":**
   - the over-specified B3/B5 conjunction (needs heavy loosening to make candidates);
   - the spread filter — S2's signal coincides with wide-spread sweeps it structurally can't trade
     (exact 1-tick requirement);
   - **entry-stop expiry** — the sell-stop at H−2 often never fills because price doesn't reclaim
     in time (P3g: 13 of 41 expired). The entry mechanic, not just the signal, throttles it.
3. **Exit geometry under-captures the move (same as S5).** MFE reaches ~1.5–2.4R (R≈5t) but the
   default T1 takes 50% at 1R with a rarely-hit runner — so even favorable trades bank little
   (P2g net +$79 on hit 33%). A fuller/wider target (the S5 P2 / ~1.5R lesson) is the obvious lever.

## Verdict: PROMISING — do not shelve; needs more data + an exit-geometry pass

Unlike S1 (unmeasurable on MBP-10) and S4 (negative-edge), **S2 shows a real favorable-excursion
signature.** But this rests on **n = 3–6 trades** and required relaxing global filters to see at
all, so it is *suggestive, not established*. The realistic path: (a) more ES data to get a true
sample, (b) test capturing the 8–12t MFE with full-exit / wider-target geometry, (c) investigate
the entry-fill/expiry (a limit entry, or longer validity, instead of the H−2 stop). The spread
filter is a hard structural ceiling on S2 frequency regardless.

## Stage 3 — exit-geometry test (`runs/s2_exit_test.sh`)

Applying the S5 lesson: on the P3g sample (6 trades), full exit (`T1ExitFraction=1.0`), sweep the
single-target distance `T1RMultiple`. R≈5t (entry H−2, stop sweep+1).

| Target | trades | hit | net | avg MFE | exits |
|---|---|---|---|---|---|
| 1.0R | 6 | 33% | −$1,235 | 4.8t | Stop 4 / Tgt 2 |
| 1.5R | 6 | 33% | −$710 | 6.3t | Stop 4 / Tgt 2 |
| 2.0R | 6 | 33% | −$235 | 7.7t | Stop 4 / Tgt 2 |
| **2.5R** | 6 | 33% | **+$290** | 9.2t | Stop 4 / Tgt 2 |

**Same shape as S5:** the *same 2 winners* reach the target at every distance (Tgt:2 constant) and
have large favorable runs (MFE ≥10t), so widening the target banks more on them — net climbs
monotonically and crosses **positive at ~2.5R (+$290)**. Confirms the MFE>MAE finding: S2's winners
travel far and the default T1-at-1R geometry under-captures them. (Note S2 winners run *further*
than S5's — optimum ~2.5R vs S5's ~1.5R — consistent with stop-runs producing larger reversals.)

## Caveats

- **n=6, only 2 winners, AND global filters relaxed (non-deployable)** — this is anecdotal-squared.
  The +$290 is *not* a result to trust; the trustworthy part is the *direction* (MFE>MAE; winners
  far enough that a wide target captures them), consistent across Stages 2–3.
- n=3–6; global filters relaxed as a measurement device (not deployable). 5 days, one ES regime.
- The +$79 / −$860 split across P2g/P3g is noise at this n — the **MFE>MAE signature**, not the
  net, is the signal worth carrying forward.
- No defaults or code changed; S2 left at rulebook defaults (fires ~0).
