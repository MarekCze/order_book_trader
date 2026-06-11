# Tuning log

Append-only record of calibration runs. Each entry: date, command line, config delta
vs appsettings defaults, and headline funnel numbers. Newest at the bottom.

---

## RUN A — filters-open week diagnostic (2026-06-11, iteration 1 / item 1)

**Command** (per day 2026-06-01..05, chained shared state):
```
orderflow replay <day>.mbp-10.dbn.zst --trade --journal j-<dd>.db \
  --set Risk.AtrPercentileMin=0 --set Risk.AtrPercentileMax=1.0 \
  --set Storage:SqlitePath=state.db
# then merge_journals.py + orderflow report
```

**Config delta vs defaults:** `Risk.AtrPercentileMin 0.20→0`, `Risk.AtrPercentileMax 0.95→1.0`
(volatility band fully opened). Storage path set so ATR/POC state accumulates across days.

**Headline:** 216 candidates over the week, **0 completed trades, $0.00 net.** The
fill/bracket/T1/T2/exit path still has not executed.

**Per-day block breakdown (band wide open):**
| Day | Candidates | Blocked by |
|---|---|---|
| 06-01 | 28 | Volatility 28 |
| 06-02 | 61 | Volatility 61 |
| 06-03 | 42 | Volatility 40, Location 1, Session 1 |
| 06-04 | 62 | Volatility 61, Location 1 |
| 06-05 | 23 | **None 1 (approved!)**, PositionOpen 2, Session 20 |

**Root cause (key finding):** opening the band changed nothing on days 1–4 — they still
block 100% on `Volatility`. With the band at [0,1] the only way to hit the Volatility
filter is the **null-percentile path** (`RiskManager` line 118 blocks when
`ctx.AtrPercentile is null`). So the real blocker is *the ATR percentile being unavailable*,
not the band. The trailing ATR baseline only becomes available on **day 5** (after 4 prior
sessions of history) — at which point a candidate finally passed the filters (block=None)
and reserved exposure (the 2 PositionOpen blocks), but its entry never filled before data
ended, so still 0 trades.

**Conclusion:** RUN A's intended smoke test (exercise fills/brackets) is blocked by the
null-percentile volatility gate, exactly the problem item 3 targets. Item 3's "pass-through
when baseline < 10 sessions" rule would let all 5 days through and should produce the first
real fills. Re-run RUN A after item 3 lands.

---

## RUN A2 — regime-gate week, default config (2026-06-11, iteration 1 / after item 3)

**Command** (per day 2026-06-01..05, chained shared state, **no band override** — the
regime-gate redesign is the unblock):
```
orderflow replay <day>.mbp-10.dbn.zst --trade --journal j-<dd>.db --set Storage:SqlitePath=state.db
# then merge_journals.py + orderflow report
```

**Config delta vs defaults:** none (item 3 changed the defaults: regime ATR 30-min sampled
at context, pass-through when baseline < `MinBaselineSessions`=10). Only Storage path set.

**Headline:** with < 10 sessions of baseline the gate is pass-through every day (logged:
10/18/3/38/3 candidates/day). **First end-to-end trades ever: 16 trades, 5W/11L, net
−$6,161.50, expectancy −$385/trade, hit rate 31%, maxDD $6,161.50.** P&L is smoke-test
output, not evaluation.

**Smoke-test coverage (the point of this run):** the full path executed — entry fills, T1
partial exits + breakeven stop moves (the 5 wins are T1-then-breakeven at +$173.50),
full stop-outs (the 11 losses at −$639), MAE/MFE tracking, net-of-commission P&L, and the
M6 markdown/CSV report.

**Per-setup:** all 16 trades were **Setup 5 (delta-divergence fade)**. Exit reasons: 16/16
**Stop** (no Target/TimeStop/Invalidation reached). Setups 1, 2, 4 still produced ~0
tradeable candidates (S1/S4 ~0 contexts, S2 1 expired) — motivates item 4's S1/S4 sweep.

**Observations for later (not acted on — diagnostics only):** every exit is a stop; no trade
reached T2; Setup 5 short dominates (14 of 16). Suggests T1/stop geometry and Setup-5
selectivity are the next things to look at after the funnel telemetry (item 2) exists.

---

## Iteration 2 — detector validation tooling (2026-06-11)

**What landed (code, no journal schema change, no detector behavior change):**

1. **Per-condition funnel telemetry** (iteration 1's item 2, now the critical path): a
   shared `ConditionFunnel` records passed/evaluated per rulebook condition, in chain
   order, for every detector instance — S1 `A1 → A2rdy → A2 → A3 → A4 → A5 → A6` (A2rdy =
   the session delta-distribution readiness gate, counted separately because it silences
   the setup without any rulebook condition failing), S2 `B1B2 → B3 → B4 → B5`, S4
   `D1lvn → D1room → D2 → D3` (D1 split so "no LVN near" is distinguishable from "no room
   to the HVN"), S5 `E1 → E2 → E3 → E4`. Printed in the replay funnel lines; because
   guards short-circuit, the first condition whose passed count collapses is the binding
   constraint.
2. **`orderflow inspect-trade <journal.db> <id> [--data file.dbn.zst]`** (item 2b tooling):
   prints the journaled row, the full F1–F36 snapshot at trigger, and the Setup-5 E1–E4
   journal evidence; with `--data` it replays the file and reconstructs the
   detector-internal context the journal does not carry — H1/H2 swing sample with
   cumDeltas, E1–E4 verdicts at context/trigger, swing/divergence-sample counts, and a
   ±60 s price/delta path in 10 s buckets around the entry.
3. **Swing-pivot counters** (item 2c): the feature engine counts confirmed swing
   highs/lows; replay prints `Swings [instr]: confirmed highs N, lows N
   (Features:SwingPullbackTicks=4)`. The value in effect is the default **4 ticks**
   (appsettings; never overridden in any run so far).

**Synthetic shakeout** (8M-event `synth` file, ~37 min of RTH): telemetry immediately
attributes the silence per setup — S1 dies at **A3** (0/17,901 — no 45 s stall on the
synthetic walk), S2 at **B3** (0/243 climax), S4 at **D2** (0/96,472 pulling), while S5
filters genuinely: `E1 72/89 → E2 23/72 → E3 21/23 → E4 12/211` → 12 candidates (all
blocked on Spread — the synthetic book is wide). Journals remain byte-identical across
re-runs (sha256-verified).

**Blocked in this environment — needs the data restored:** the container has no DBN files,
no `DATABENTO_API_KEY`, and the RUN A2 journals were ephemeral. The following audit steps
from the iteration-2 plan are specified but NOT yet executed:

- **(2a)** MAE/MFE distributions for the 16 RUN A2 trades, split by T1-reached, plus
  time-in-trade and price-path class. After re-running RUN A2 (same commands as above):
  `SELECT t1_filled, COUNT(*), AVG(mae_ticks), AVG(mfe_ticks) FROM candidates WHERE
  disposition='Traded' GROUP BY t1_filled;` then per-trade
  `inspect-trade j-<dd>.db <id> --data <day>.mbp-10.dbn.zst` for the path class.
- **(2b)** written audit of 5 randomly sampled S5 trades (E1/E2/E3 human verdicts) — the
  `inspect-trade --data` output is designed to answer exactly this per trade.
- **(2c)** swings per session on the real week — read the new `Swings [...]` line from
  each day's replay; flag if it is tens per session.
- The funnel-telemetry week re-run (why S1/S2/S4 are silent on REAL data, and whether
  S5's conditions filter or pass everything) — read the funnel lines from the RUN A2
  command set; no config delta needed.

**Confirmed (item 5):** the orphaned `--set`/tuning-reference commit (`f27fd79`) and the
iteration-1 commit (`16102cc`) are both reachable from `origin/main` (merged via PR #8).

**Not touched (per plan item 4):** Setup 5 geometry (offsets, breakeven, T1/T2) and all
detector thresholds are unchanged; the S1/S4 threshold-scaling sweep (item 3) waits for
the funnel re-run on real data.
