# Exit-geometry sweep — results (Stage 1, config-only)

**Date:** 2026-06-14 · **Data:** 5 real ES sessions 2026-06-01…05, **default entry config**
(so the trades are the real setups — in practice S5, the only one that trades at default).
Only the exit geometry varies, applied to S1/S2/S5 via `--set` (`runs/exit_geometry_sweep.sh`).
No appsettings defaults changed. P&L is small-sample; the comparison is what matters.

## Profiles vs baseline

| Profile | T1 R / frac / T2cap / BE | Trades | Net | Expectancy | Avg win | Targets hit | maxDD |
|---|---|---:|---:|---:|---:|---:|---:|
| **P0 baseline** | 1.0 / 0.5 / 3.0 / 0–1 | 16 | −$6,161 | −$385 | $173.5 | 0 | $6,161 |
| P1 early-T1 + T2×2 | 0.5 / 0.5 / 2.0 / 0 | 22 | −$9,370 | −$426 | $142.3 | 1 | $9,370 |
| **P2 single @1R** ✅ | 1.0 / **1.0** / — / — | 16 | **−$4,599** | **−$287** | **$486.0** | 5 | **$4,932** |
| P3 single @0.5R | 0.5 / 1.0 / — / — | 22 | −$8,808 | −$400 | $236.0 | 6 | $8,808 |
| P4 lock-profit runner | 1.0 / 0.5 / 1.5 / −1 | 16 | −$5,099 | −$319 | $386.0 | 2 | $5,099 |

## What this shows

**The T1-50%-then-breakeven mechanic was destroying expectancy.** In the baseline, the 5
winners banked only the T1 half (+1R on 50% ≈ **+0.5R**, avg win $173.5) while the runner sat
at a breakeven stop that always caught it — because **T2 (3R) was never reached**.

**P2 — take the full position at the 1R target — is the clear winner.** Same 16 trades, same
5 winners, but each now banks the **full 1R** (avg win $173.5 → **$486**, ×2.8), so expectancy
improves **−$385 → −$287 (~25%)** and maxDD drops to the lowest of the set. P2 gives up nothing
versus baseline because the runner never reached T2 anyway — confirmed by the 5 "Target" exits
(the same 5 that used to merely hit T1). Payoff ratio rises 0.27 → 0.76.

P1/P3 (earlier/smaller T1) are **worse** — a smaller target takes more trades but banks too
little per win. P4 (lock +1 tick after T1, closer 1.5R T2) helps but less than P2.

## But the strategy is still net-negative — and exit geometry can't fix that

Even P2's improved payoff (0.76) needs a **~57% hit rate** to break even; the actual hit rate
is **31%** (and only ~31% of trades reach 1R at all — avg MFE ≈ 3 ticks). So:

> **Exit geometry is worth ~25% of the loss; the remaining gap is entry quality.** The wins are
> few and small because the *entries* rarely produce favorable excursion — not because the exits
> are mis-set. The next lever is entry selectivity/quality, not exits.

A **trailing-stop runner (the planned Stage 2 code change) is NOT worth building** on this data:
the MFE distribution (6% of trades reach 3R, 19% reach 2R) means a trailing runner would capture
almost nothing beyond what P2's flat 1R target already banks. The exit improvement is fully
achievable in config.

## Recommendation

- **Exit fix = adopt a P2-style geometry** (take the position off at the 1R target rather than
  scaling out 50% and parking the runner at breakeven behind an unreachable 3R T2). Cheapest as
  config: `T1ExitFraction=1.0` (with `T1RMultiple=1.0`) for S1/S2/S5 — or, if a scaled exit is
  still wanted, at minimum move the post-T1 stop to lock profit (P4) rather than breakeven.
- **Do not build the trailing-stop code** — the data says it won't pay.
- **Next real lever: entry quality** (separate workstream), since even the best exit stays
  net-negative at a 31% hit rate.

No defaults changed; per-profile reports under `runs/artifacts/exitgeo/<profile>/`.
