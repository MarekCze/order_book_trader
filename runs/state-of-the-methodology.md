# State of the methodology on ES MBP-10 — milestone summary (2026-06-14)

A consolidated decision record after the entry-quality diagnosis, the S5 flow-exhaustion gates +
exit/TP work, and the S1/S2/S4 overhaul diagnostics. Synthesizes the per-run docs in `runs/`.

**Data under test:** Databento `GLBX.MDP3`, `mbp-10`, symbol **`ES.c.0`** (E-mini S&P 500 continuous
front month — verified in the DBN metadata; prices ~7600, $12.50/tick), **5 RTH days 2026-06-01..05**,
one regime. All P&L net of $1.40/contract round-turn ($14 per 10-lot). All results below are
config-only via `--set`; **no `appsettings` defaults changed; one code addition (the S5 gates) ships
off-by-default and byte-identical.**

## TL;DR — the four active setups (S3 deferred, needs MBO)

| Setup | Verdict | Headline evidence | Disposition |
|---|---|---|---|
| **S5** delta-divergence fade | ✅ **Viable** | gated+P2+~1.5R target → **+$388/wk** (from −$6,161 baseline); winners' MFE {5,7,8,8,12}t | gates implemented, **off by default** |
| **S2** stop-run fade | 🟡 **Promising, throttled** | **avg MFE 8–12t > avg MAE 5.3t**; +$290 at 2.5R (relaxed, n=6) | parked for data; not shelved |
| **S1** absorption fade | ⛔ **Unmeasurable on MBP-10** | A4 passes 0 even 5× below rulebook; needs order-level reload/queue data | shelved → wants MBO |
| **S4** LVN vacuum | ⛔ **Negative-edge** | **avg MFE < avg MAE**, ~6% hit, −$15k..−$23k; D3 doesn't rescue | shelved |

## Per-setup detail

### S5 — delta-divergence fade — VIABLE (the one that works)
- **Diagnosis** (`runs/entry-quality-diagnosis.md`): the 16 baseline trades (5W/11L, −$6,161.50,
  31% hit) failed on **entry timing, not exits/direction/stop**. Losers had MFE 0–2t (never went
  green); 6/11 stopped in <4s. The Day-1 cascade fired 4 fade-shorts in one second into +13–21σ buy
  flow — fading a live climax.
- **Fix** (`runs/s5-flow-gate-results.md`, commit `6987ece`): two opt-in flow-exhaustion gates
  (A: block while with-move flow z extreme; B: require the with-move delta bucket to roll over from
  its peak). Both off by default (byte-identical; 318 tests green). Both gates → −$1,862 (8t/50%),
  a 70% loss cut, hit 31%→50%.
- **Exits** (`runs/tp-target-sweep-results.md`, commit `c805b37`): full exit (P2) + target sweep →
  clean single peak at **1.5R (~6t) = +$388**, cliff beyond. The optimum matches the winners' MFE
  cluster — entry and target agree on where the move ends.
- **Status:** first net-positive config this project produced. **Heavily overfit (n=8–16, 5 days),
  all thresholds placeholders, gates off by default.** Not an established edge.

### S2 — stop-run fade — PROMISING, parked (`runs/s2-loosen-results.md`, commits `1cd2d6b`, `45e992f`)
- Loosening B3/B5 lifts candidates 1→232 but trades stay ~0 — ~95% die at **global filters**
  (AttemptsExhausted/Session/**Spread**). The spread filter is exact-1-tick (`RiskManager.cs:157`)
  and S2's sweep premise occurs in *wide-spread* books → a structural frequency ceiling.
- With Session/Attempts relaxed to force a sample: **avg MFE 8–12t > avg MAE 5.3t** — favorable
  directional edge (opposite of S4). Exit test: net crosses **+$290 at 2.5R** (same shape as S5,
  optimum further out). 
- **Three throttles, not a wrong thesis:** over-tight B3/B5 conjunction; the spread filter; and
  entry-stop **expiry** (price doesn't reclaim to H−2 in time). **n=3–6 with non-deployable relaxed
  filters → suggestive only.** Revisit with more data + exit-geometry + an entry-fill fix (limit
  entry / longer validity).

### S1 — absorption fade — UNMEASURABLE on MBP-10 (`runs/tuning-log.md`, item-4 sweep)
- A4 ("volume-without-progress ≥ 3× baseline") passes **0** even at 5× below the rulebook value;
  A5/A6 also block. Stall volume at a single price is always a *fraction* of baseline. Clean
  absorption detection (a hidden order soaking aggression and reloading) needs **order-level MBO** —
  the same gap that defers S3. Not "theory wrong," but not testable at this schema. Left dormant.

### S4 — LVN vacuum — NEGATIVE-EDGE (`runs/s4-d2-sweep-results.md`, commits `0443ad4`, `d490781`)
- `PullRatioMin` is the sole binding D2 gate (`DepthDeclineFraction` inert). Where it fires:
  hit ~6%, **avg MFE 3.5–4.5t < avg MAE 4.9t**, net −$15k..−$23k over 37–53 trades/cell. Price
  reverts through the LVN more than it vacuums through it. D3 (aggressor-alignment) tightening
  doesn't rescue it. A momentum entry paying the spread on a mean-reverting instrument, on an
  approximate (F16) signal — shelved.

## The through-line (the real finding)

The setups that survive read **aggregate flow vs price structure** — cumulative-delta divergence at
a swing extreme (S5), trapped-trader reversal after a sweep (S2). The setups that fail need **fine
liquidity reading at a level** — passive absorption (S1), depth-pull vacuums (S4) — exactly what
MBP-10 (top-10 depth, no queue/order-ID) approximates worst and what moves fastest in a deep,
liquid index future like ES. **On ES MBP-10, the methodology's aggregate-flow expressions show edge;
its micro-liquidity expressions want MBO or don't hold.** Two further structural facts: the
one-position-at-a-time rule caps trade frequency, and the exact-1-tick spread filter caps S2.

## The ceiling: data, not ideas

Every open question is now **sample-size-bound**: is S5's +$388 real, is S2's edge real, what are
the right gate/target thresholds. The 5-day sample (8–16 S5 trades; 3–6 relaxed S2 trades) is
exhausted — going further on it only deepens overfit.

- **~50 ES days spread across regimes (~$90)** → re-run the S5 (gated+TP) and S2 (loosened+exit)
  stacks out-of-sample; first real read on whether the edges survive.
- **~1 year (~250 sessions, ~$450)** → enough S5 trades (~200–400) to distinguish a real edge from
  the ~46–57% breakeven band, with an out-of-sample hold-out.
- (Cost extrapolated linearly from $9/5 days — confirm Databento bulk pricing.)

## Recommended path forward

1. **Acquire ~50 ES days (spread, not consecutive); re-run the stacks out-of-sample.** Gate every
   further tuning decision on it.
2. If S5/S2 edges survive: S5 gate-threshold sweep (`tasks/002-s5-gate-threshold-sweep.md`,
   deferred), then S2 exit-geometry + entry-fill work (`tasks/revive-other-setups/002`).
3. **S1:** revisit only with MBO. **S4:** shelved. **S3:** still deferred (MBO).
4. Optional later: relax the one-position portfolio rule (code change) if multi-position concurrency
   is wanted; backtest MNQ as a second instrument.

## Artifact index
- Results: `runs/entry-quality-diagnosis.md`, `s5-flow-gate-results.md`, `tp-target-sweep-results.md`,
  `s4-d2-sweep-results.md`, `s2-loosen-results.md`; running log `runs/tuning-log.md`.
- Sweep scripts: `runs/{p2_gated_run,tp_sweep,s4_d2_sweep,s4_d3_probe,s2_loosen_sweep,s2_unblock_run,s2_exit_test}.sh`.
- Tasks: `tasks/001-s5-flow-exhaustion-gate.md` (done), `tasks/002-s5-gate-threshold-sweep.md`
  (deferred), `tasks/revive-other-setups/{000-overview,001-s1,002-s2,003-s4}.md`.
- Key code: `Setup5Guards.cs` / `DeltaDivergenceFadeDetector.cs` (gates), `Setup5Options.cs`
  (knobs), `RiskManager.cs:157` (exact-1-tick spread filter), `TradeSummaryPrinter.cs` (NULL-SUM fix).
