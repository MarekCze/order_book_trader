# Permissive "many entrances" run — results

**Date:** 2026-06-14 · **Data:** 5 real ES sessions 2026-06-01…05 · **Profile:**
`runs/permissive_run.sh` (binding-wall conditions opened; filter-5 re-entry caps lifted;
context conditions and global filters 1–4/6 at rulebook defaults). **No appsettings defaults
changed** — everything is a per-run `--set` override. **P&L here is noise by construction**;
the goal was volume + stress-testing the path, not a profitable configuration.

## Headline

Opening the binding walls floods **candidates** but barely moves **completed trades** — and
it pinpoints the real ceiling on trade frequency.

| | Default week (iteration 2) | Permissive week |
|---|---:|---:|
| Candidates | 94 | **70,133** (~750×) |
| Completed trades | 16 | **48** (3×) |
| Hit rate | 31% | 8.3% |
| Net P&L | −$6,162 | −$23,286 |
| Expectancy/trade | −$385 | −$485 |
| Profit factor | 0.12 | 0.05 |

**Two findings:**

1. **The throttle on trade frequency is the portfolio rule, not the setups.** Of 70,133
   candidates, **66,494 (95%) were blocked `PositionOpen`** — the hard-coded
   one-position-at-a-time rule (filter 5). Loosening signal thresholds 750×'d the candidate
   stream but only 3×'d completed trades. "Many entrances" is **architecturally gated**, not
   threshold-gated; reaching it would require a code change to the portfolio risk model
   (concurrent positions / per-setup exposure), which is out of this run's config-only scope.

2. **Loosening the signal thresholds destroys entry quality.** Hit rate fell 31% → 8.3%,
   profit factor 0.12 → 0.05, and **47 of 48 exits were stops** (1 Target). The strict
   rulebook thresholds are *quality filters*, not merely frequency throttles — removing them
   produces low-conviction entries that stop out. This is a point *for* the rulebook's
   selectivity, not against it.

## Blocked candidates (by risk filter, week)

| Filter | Count | Note |
|---|---:|---|
| **PositionOpen** | **66,494** | one position at a time (hard-coded) — the dominant ceiling |
| Spread | 3,222 | spread ≠ 1 tick at the trigger instant (hard equality, can't relax) |
| Session | 342 | first 2 min / outside RTH |
| Location | 2 | > 4 ticks from an LOI |

## Trades by setup

| Setup | Trades | Net | Hit rate | Exits |
|---|---:|---:|---:|---|
| LvnVacuum (S4) | 30 | −$14,652 | 3.3% | 29 Stop, 1 Target |
| DeltaDivergenceFade (S5) | 13 | −$5,870 | 23.1% | 13 Stop |
| StopRunFade (S2) | 5 | −$2,764 | 0% | 5 Stop |
| AbsorptionFade (S1) | **0** | $0 | — | — |

- **S4 dominates** the trade stream (30 of 48) — it both floods the most candidates (e.g.
  46,159 on day 5) and sits first among the loosened setups in detector step order, so it
  wins most free position slots.
- **S1 still produces 0 trades even with A4 opened** — A5/A6 block downstream (A5 passes
  ~1 of thousands; A6's structural `delta ≥ 0` + ≥2-bucket requirement). Confirms the
  iteration-1 verdict that **S1 is structural, not a threshold-calibration problem.**
- Per-day trades: 2 / 5 / 9 / 3 / 29 — day 5 (the busiest, ~13M events) produced 29, with
  S4-long alone taking 20.

## MAE / MFE (48 trades, ticks)

| Metric | n | min | p25 | median | p75 | p90 | max | mean |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| MAE | 48 | 2 | 3 | 5 | 5 | 5 | 7 | 4.48 |
| MFE | 48 | 0 | 0 | 1 | 5 | 11.3 | 20 | 3.42 |

Most trades go adverse quickly (MAE ≈ stop distance) with little favorable excursion — the
signature of low-quality, near-random entries.

## Process note — a maximally-open profile is degenerate

The first attempt set S4 D2/D3 to *always-pass* (`DepthDeclineFraction=0`, `PullRatioMin=0`,
`MinAlignedDeltaContracts=-1000000`). That emitted a candidate on essentially **every event
D1 held** (millions/day): day 1's journal hit 68 MB and the run did not finish — it was
killed. The profile was moderated to the iteration-1 sweep's *firing* levels
(`DepthDeclineFraction=0.24`, `PullRatioMin=0.9`, `MinAlignedDeltaContracts=0`, etc.), which
keeps candidate volume in the tens-of-thousands/week and the run tractable (~6 min for the
week). The exact overrides are in `runs/permissive_run.sh`.

## Conclusion

"Many trade entrances" cannot be produced by loosening detector thresholds alone: the
one-position-at-a-time portfolio rule caps completed trades to a few dozen per week regardless
of candidate volume, and loosening signal quality only makes those few trades worse. The
levers that matter next are therefore (a) the portfolio exposure model (a code change, if more
concurrency is genuinely wanted) and (b) entry *quality*, not entry *frequency*. No defaults
were changed; this remains a diagnostic.
