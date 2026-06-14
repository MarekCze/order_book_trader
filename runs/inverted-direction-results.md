# Inverted trade direction (SL ↔ TP) — results

**Date:** 2026-06-14 · **Data:** 5 real ES sessions 2026-06-01…05, default entry/exit config.
Experiment (not fine-tuning): take every trade on the **opposite side** so the old stop level
becomes the take-profit and vice versa.

## How it's implemented

New flag **`Risk:InvertDirection`** (default **false** = byte-identical; 307 tests pass with it
off). When true, an `ExecSign = −Sign` mirrors the **execution** only — entry side, stop/target
brackets, R, P&L and MAE/MFE — while **detection is unchanged** (the same signals fire). So at a
new-high divergence the bot now *buys* the high instead of fading it; the protective stop sits
where the take-profit used to be and vice versa. Run via `runs/inverted_run.sh`.

Note: inversion relocates the entry to the mirror side (e.g. S5 buys at `H2+2` instead of selling
at `H2−2`), so this is a genuine opposite-direction backtest with its own fill behaviour — not a
sign-flip of the identical trades.

## Result — inverting makes it worse

| | Baseline (normal) | Inverted |
|---|---:|---:|
| Trades | 16 | 35 |
| Wins / losses | 5 / 11 | 4 / 31 |
| Hit rate | 31% | 11% |
| Net P&L | −$6,162 | **−$17,219** |
| Expectancy/trade | −$385 | **−$492** |
| Max drawdown | $6,162 | $17,393 |

Exit reasons (inverted): 28 full stops (−$639), 4 T1-then-breakeven (+$173.5), 3 targets (≈$0
after costs). It is **not** a degenerate always-lose bug (it does produce T1s and targets) — the
inverted trades simply lose.

## Conclusion

**Direction is not the problem — fading is the correct side.** Buying highs / selling lows
(the inverse) gets caught by exactly the mean-reversion the fade setups are designed to capture,
so the inverted (momentum-at-extremes) trades stop out *more* (hit rate 31% → 11%), and roughly
**triple the loss**. This corroborates the earlier finding: the losses come from **entry
quality/timing and the R:R + cost structure, not from being on the wrong side.** Flipping the
side just pays the spread/commission to be wrong in the other direction.

No defaults changed (flag is off by default); reproduce with `--set Risk:InvertDirection=true`.
Artifacts under `runs/artifacts/inverted/`.
