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
