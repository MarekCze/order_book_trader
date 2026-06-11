# Tuning iteration 1 — results (items 1–5)

**Date:** 2026-06-11 · **Instrument/data:** ES front month, 5 real Databento MBP-10 sessions
2026-06-01…05 · **Theme:** *diagnostics before optimization* — **no rulebook defaults were
changed**; every threshold change was a per-run `--set` override.

This document summarises results. The append-only run record is `runs/tuning-log.md`;
reproduction scripts are `runs/sweep_s1_s4.sh` and `runs/merge_journals.py`.

## Summary

| Item | What | Outcome |
|---|---|---|
| 1 | RUN A — filters-open diagnostic | The volatility filter, not the band, blocks everything — via the **null-percentile** path. |
| 2 | Per-condition funnel telemetry | Pinpoints each setup's binding wall on real data: **S1→A4, S4→D2, S2→B3/B5, S5 trades**. |
| 3 | Volatility filter regime-gate redesign | **First end-to-end trades ever** — 16 over the week. |
| 4 | S1/S4 threshold-scaling sweep | **S4 is calibratable; S1 is structural.** |
| 5 | `runs/tuning-log.md` logging convention | Established and used for every run. |

**Headline:** the bot went from **0 trades** (filters blocked 100%) to a working
end-to-end fill→bracket→exit→P&L→report pipeline, and we now know precisely why each silent
setup is silent.

---

## Item 1 — RUN A (filters-open diagnostic)

**Goal:** force the first end-to-end trades by opening the volatility band
(`Risk.AtrPercentileMin=0`, `Risk.AtrPercentileMax=1.0`) across the week, to exercise the
fill/bracket/exit/P&L path that had never run.

**Result — still 0 trades.** 216 candidates over the week, 100% `Volatility` blocks on days 1–4.

| Day | Candidates | Blocked by |
|---|---|---|
| 06-01 | 28 | Volatility 28 |
| 06-02 | 61 | Volatility 61 |
| 06-03 | 42 | Volatility 40, Location 1, Session 1 |
| 06-04 | 62 | Volatility 61, Location 1 |
| 06-05 | 23 | **None 1 (approved)**, PositionOpen 2, Session 20 |

**Finding:** with the band wide open, the *only* way to hit filter 3 is the **null-percentile**
path (`RiskManager` blocks when `AtrPercentile is null`). The trailing ATR baseline was never
ready, so opening the band was never the fix. The baseline only matured on day 5 (after 4
prior sessions), where one candidate finally passed — but its entry never filled. This
directly motivated item 3.

---

## Item 2 — per-condition funnel attrition telemetry

Per-condition `passed/evaluated` counters in guard-chain order for every detector (the
`ConditionFunnel`), printed in the replay funnel lines. Because guards short-circuit, the
first condition whose passed-count collapses is the binding constraint. Run on the real week:

| Setup | Chain (passed/evaluated) | Binding wall |
|---|---|---|
| **S1 AbsorptionFade** | A1 → A2 → A3 → **A4 0/N** → A5 0/0 | **A4** (volume-without-progress) — 0 passes of thousands of stalls, every day |
| **S2 StopRunFade** | B1B2 → B3 ~1% → B4 all → **B5 ~0/N** | B3 (climax) then B5 (supply) → ~1 candidate all week |
| **S4 LvnVacuum** | D1 → **D2 0/N** → D3 0/0 | **D2** (depth-decline + pull ratio) — 0 passes of hundreds of thousands |
| **S5 DeltaDivFade** | E1 → E2 ~10% → E3 → E4 → trades | works; **E2** (divergence) is the selective filter |

Companion tool: `orderflow inspect-trade <journal.db> <id> [--data file]` audits one journaled
candidate (feature snapshot, Setup-5 E1–E4 evidence + reconstruction, ±60 s price path).

---

## Item 3 — volatility filter regime-gate redesign (spec change)

The old "5-min ATR sampled at the signal trigger" was self-defeating: the setups react to
volatility bursts, so the trigger-time reading sat in the top tail and blocked. Redesign of
global filter 3:

- **Regime ATR**: a 30-min ATR series, separate from F34's 5-min ATR (history store is now
  series-keyed `atr5` / `atr30`; F34 keeps its rulebook definition).
- **Sampled at context formation**, not the trigger (`Risk:AtrSampleAtContext`).
- **Pass-through (disabled, logged)** until the trailing baseline holds
  `Risk:MinBaselineSessions` (default 10) sessions — a percentile over fewer is meaningless.
- New keys: `Risk:AtrSampleAtContext`, `Risk:MinBaselineSessions`,
  `Features:RegimeAtr{BarSeconds,PeriodBars,LookbackDays}`. Spec updated in `CLAUDE.md` and a
  rulebook annotation on filter 3.

**Result — first end-to-end trades ever (RUN A2 / iteration-2 week, default config):**

| Metric | Value |
|---|---|
| Trades | 16 (all Setup 5) |
| Win / loss | 5 / 11 (hit rate 31%) |
| Net P&L | −$6,161.50 — **smoke-test output, not evaluation** |
| Expectancy | −$385/trade |
| Max drawdown | $6,161.50 |
| Exits | 16/16 Stop; none reached T2 |

This exercised the full path that had never run: entry fills, **T1 partial exits + breakeven
stop moves** (the 5 wins are T1-then-breakeven at +$173.50), full stop-outs (−$639),
MAE/MFE, net-of-commission P&L, and the M6 markdown/CSV report. With < 10 sessions of
baseline the gate logged pass-through every day.

---

## Item 4 — S1/S4 threshold-scaling sweep (`runs/sweep_s1_s4.sh`)

A single multiplicative factor applied per run to each setup's binding-wall thresholds —
S1 `StallVolumeMultiple` (A4) and S4 `DepthDeclineFraction` + `PullRatioMin` (D2) — summed
over the week, both directions:

| factor | S1 A4 pass/eval | S1 cand | S4 D2 pass/eval | S4 cand |
|---|---|---|---|---|
| ×1.0 | 0/97,484 | 0 | 0/4,686,141 | 0 |
| ×0.8 | 0/97,484 | 0 | 0/4,686,141 | 0 |
| ×0.6 | 0/97,484 | 0 | **214,868**/3,879,650 | **50,910** |
| ×0.4 | 0/97,484 | 0 | 306,312/2,249,518 | 43,819 |
| ×0.2 *(S1 only)* | **8,430**/97,484 | 0 | — | — |
| ×0.1 *(S1 only)* | 30,588/79,306 | **3** | — | — |

**Setup 4 — threshold-calibration.** D2 begins firing between ×0.8 and ×0.6
(`DepthDeclineFraction` ≲ 0.24, `PullRatioMin` ≲ 0.9). It's a **cliff, not a ramp**:
0 → ~51k candidates/week in one step. The rulebook 0.40/1.5 is far on the dead side; the
firing region is wildly permissive → needs a finer grid in [×0.6, ×0.8] plus a tighter
quality/D3 filter, not the raw open gate.

**Setup 1 — structural.** A4 won't pass at all down to ×0.4; it only starts at ×0.2
(`StallVolumeMultiple`=0.6, **5× below** the rulebook 3.0), and even then **0 candidates** —
A5/A6 block downstream; only 3 squeak through at ×0.1. The measured stall volume at [L, L+1]
is consistently a *fraction* of baseline, never a multiple. So S1 needs a **definition review
of the A4 volume metric and the A5/A6 chain**, not a threshold tweak.

---

## Item 5 — logging convention

`runs/tuning-log.md` records every run (command line, config delta vs defaults, headline
funnel numbers, findings), newest at the bottom. Reproduction helpers committed alongside:
`runs/sweep_s1_s4.sh`, `runs/merge_journals.py`. Run artifacts (journals, CSVs, generated
reports) are git-ignored under `runs/artifacts/`.

---

## Overall verdict & deferred work

The volatility filter was the single point of failure (item 3 fixed it → first trades).
With trades flowing and per-condition telemetry in place, the silent setups are now
diagnosed precisely:

| Setup | Status | Next step (optimization — deliberately deferred) |
|---|---|---|
| 1 absorption fade | **structural** | Review the A4 stall-volume metric and the A5/A6 chain definitions against real data. |
| 2 stop-run fade | very selective | B3 climax + B5 supply are the gates; revisit if S1/S4/S5 mature. |
| 4 LVN vacuum | **calibratable** | Fine-grid D2 in [×0.6, ×0.8] + add a quality/D3 filter (current firing region floods). |
| 5 delta-divergence fade | **trades** | Exit geometry: every trade stops out, none reach T2 — review T1/stop/T2 placement. |

All of the above are optimization, intentionally **not** done under this iteration's
"diagnostics before optimization" mandate.

---

### Reproduce

```bash
# Week with funnel telemetry (default config), then a merged week report:
for d in 01 02 03 04 05; do
  orderflow replay 2026-06-$d.mbp-10.dbn.zst --trade --journal j-$d.db --set Storage:SqlitePath=state.db
done
python3 runs/merge_journals.py week.db j-0*.db
orderflow report week.db --out week.report.md --csv-dir csv

# S1/S4 threshold sweep:
bash runs/sweep_s1_s4.sh
```
