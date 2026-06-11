# Tuning reference — entry/exit thresholds

Every rulebook threshold is a **named config value**, never a literal in code (per
`CLAUDE.md`). The setups themselves are fixed; only the numbers below are adjustable.

**Source of truth** is the typed options classes — this doc is a catalogue, but the
defaults and meanings ultimately live in code:

| Section | appsettings key | C# options class |
|---|---|---|
| Global risk filters 1–6 | `Risk` | `OrderFlow.Domain.Trading.RiskOptions` |
| Fill model / costs | `Execution` | `OrderFlow.Domain.Trading.ExecutionOptions` |
| Setup 1 — absorption fade | `Detectors:Setup1` | `Setup1Options` |
| Setup 2 — stop-run fade | `Detectors:Setup2` | `Setup2Options` |
| Setup 4 — LVN vacuum | `Detectors:Setup4` | `LvnVacuumOptions` |
| Setup 5 — delta-divergence fade | `Detectors:Setup5` | `Setup5Options` |

> Setup 3 (iceberg follow) is deferred — it needs MBO data.

## How to change a value

**Persistent:** edit `src/OrderFlow.Backtest/appsettings.json` (no recompile — it is copied
to the output on build). Defaults there equal the rulebook's stated ES values.

**Per-run (sweeps, no file edit):** pass `--set Key=Value` to `replay` / `report`
(repeatable). Path separators may be `:` or `.`. Overrides win over the JSON file and the
compiled defaults.

```bash
# widen the volatility filter band and tighten Setup 1's stop, just for this run
orderflow replay ES.dbn.zst --trade \
  --set Risk.AtrPercentileMax=0.99 \
  --set Detectors:Setup1:StopOffsetTicks=2

# disable a whole setup for an A/B run
orderflow replay ES.dbn.zst --trade --set Detectors:Setup5:Enabled=false
```

A malformed override (no `=`) aborts the run with a clear error before any work starts.

All values default to the rulebook; directions are stated for the long side — the short
mirror flips signs, not thresholds.

---

## Global risk filters (`Risk`)

These gate **every** candidate from every setup before entry.

| Key | Default | Meaning |
|---|---|---|
| `OpenExcludeMinutes` | 2 | Filter 1: minutes after the RTH open with no trading. |
| `LoiProximityTicks` | 4 | Filter 2: max ticks from a level of interest. |
| `AtrPercentileMin` | 0.20 | Filter 3: lower edge of the regime-ATR percentile band vs the trailing distribution. |
| `AtrPercentileMax` | 0.95 | Filter 3: upper edge of the ATR band. |
| `AtrSampleAtContext` | true | Filter 3: sample the regime ATR at context formation, not at the signal trigger (decouples the gate from the burst the setup reacts to). |
| `MinBaselineSessions` | 10 | Filter 3: gate stays disabled (pass-through, logged) until the trailing regime-ATR baseline covers this many sessions. |
| `RequiredSpreadTicks` | 1 | Filter 4: required spread (ticks) at signal time. |
| `MaxAttemptsPerLoi` | 3 | Filter 5: max attempts per LOI per session. |
| `ConsecutiveStopOutsToKillLevel` | 2 | Filter 5: consecutive stop-outs that kill a level for the day. |
| `AccountEquity` | 100000 | Filter 6: equity the risk fraction applies to (no compounding in v1). |
| `RiskFractionPerTrade` | 0.005 | Filter 6: risk per trade as a fraction of equity (≤ 0.5%). |
| `TickValue` | 12.50 | Filter 6 + P&L: currency value of one tick per contract (ES $12.50). |
| `MaxPositionContracts` | 10 | Engineering safety cap on position size (not from the rulebook). |

## Fill model / costs (`Execution`)

| Key | Default | Meaning |
|---|---|---|
| `StopSlippageTicks` | 1 | Adverse slippage (ticks) applied to stop-market and market fills. |
| `CommissionPerContractRoundTurn` | 1.40 | All-in round-turn commission per contract; all journal P&L is net of it. |

---

## Setup 1 — absorption fade (`Detectors:Setup1`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch for the setup. |
| `DeclineTicks` | 6 | A1: minimum adverse move toward the level over the decline window. |
| `DeclineWindowSeconds` | 180 | A1: decline lookback ("the last 3 minutes"). |
| `ContextLoiProximityTicks` | 2 | A1: max distance from the LOI that defines level L. |
| `DeltaWindowSeconds` | 60 | A2: flow window the delta is measured over. |
| `DeltaContracts` | 300 | A2: absolute windowed-delta threshold against the fade. |
| `DeltaPercentile` | 0.10 | A2: alternative session-distribution tail. |
| `StallSeconds` | 45 | A3: minimum stall duration with the best holding the level. |
| `MinStallSellPrints` | 1 | A3: minimum continued aggressor prints during the stall. |
| `StallVolumeMultiple` | 3.0 | A4: stall volume at [L, L+1] as a multiple of the 15-min baseline. |
| `ReplenishRatioMin` | 2.5 | A5: traded(L) ÷ max displayed(L) over the replenish window. |
| `RefreshCountMin` | 3 | A5: refresh events at L during the stall. |
| `ExhaustionBucketSeconds` | 10 | A6: exhaustion bucket width. |
| `ExhaustionDropRatio` | 0.70 | A6: required drop of the latest bucket from the stall's peak. |
| `StallAbandonTicks` | 4 | Context reset when price runs this far beyond L (engineering guard). |
| `EntryLimitOffsetTicks` | 1 | Entry: limit at L + offset. |
| `LimitWorkingSeconds` | 30 | Entry: seconds the limit works before the momentum switch. |
| `MomentumMinAdvanceTicks` | 2 | Entry: min advance beyond L for the momentum switch. |
| `MomentumStopOffsetTicks` | 3 | Entry: momentum stop at L + offset. |
| `EntryExpirySeconds` | 90 | Entry: total working time before the setup expires unfilled. |
| `StopOffsetTicks` | 3 | Stop at L − offset (never widened). |
| `T1RMultiple` | 1.0 | T1 at entry + this multiple of R. |
| `T1ExitFraction` | 0.5 | Fraction exited at T1. |
| `BreakevenOffsetTicks` | 1 | After T1: stop to entry − offset. |
| `T2RCap` | 3.0 | T2 cap in R (developing POC caps below it). |
| `TimeStopSeconds` | 300 | Time stop: exit at market if neither T1 nor stop hits. |
| `InvalidationSweepContracts` | 200 | Invalidation: single print/sweep size through L that exits immediately. |
| `InvalidationVanishRatio` | 0.8 | Invalidation: fraction of max displayed size pulled without trading. |

## Setup 2 — stop-run fade (`Detectors:Setup2`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch. |
| `SweepZoneMinTicks` | 1 | B2: min ticks the sweep pokes beyond the reference level. |
| `SweepZoneMaxTicks` | 5 | B2: max sweep-zone width (beyond it is a breakout, not faded). |
| `ClimaxVolumePercentile` | 0.90 | B3: the breaking bar's aggressive volume must reach this session-bar percentile. |
| `ClimaxShareBeyondLevel` | 0.60 | B3: fraction of the bar's aggressive volume executing at/beyond the level. |
| `FollowThroughTicks` | 1 | B4: most a new high may extend before it counts as follow-through. |
| `SupplyDepthIncrease` | 0.50 | B5: required increase in displayed size beyond the sweep. |
| `StackedImbalanceMinLen` | 3 | B5: consecutive diagonal sell imbalances (alternative confirmation). |
| `ImbalanceRatio` | 3.0 | B5: diagonal imbalance ratio. |
| `EntryOffsetTicks` | 2 | Entry: stop this many ticks inside the reference level. |
| `SweepValiditySeconds` | 240 | Entry: cancel the resting stop if untriggered this long after the sweep. |
| `StopAboveSweepTicks` | 1 | Stop: ticks beyond the sweep high against the trade. |
| `T1RMultiple` | 1.0 | T1 at this multiple of R. |
| `T1ExitFraction` | 0.5 | Fraction exited at T1. |
| `BreakevenOffsetTicks` | 0 | After T1: stop to entry ∓ offset. |
| `T2RCap` | 4.0 | T2 cap in R (developing POC caps below it). |
| `ScratchSeconds` | 60 | Scratch: exit if price re-crosses the level and holds this long. |

## Setup 4 — LVN vacuum (`Detectors:Setup4`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch. |
| `LvnProximityTicks` | 3 | D1: max ticks between last price and the LVN it is about to fall through. |
| `HvnRoomTicks` | 8 | D1: required distance from the LVN to the next HVN ("room to pay"). |
| `DepthDeclineFraction` | 0.40 | D2: required fractional decline of displayed size on the pulling side. |
| `PullRatioMin` | 1.5 | D2: pull ratio (F16 cancel ÷ add) above which cancels dominate. |
| `DeltaWindowSeconds` | 30 | D3: flow window the alignment delta is measured over. |
| `MinAlignedDeltaContracts` | 100 | D3: minimum aggressor volume pushing the trade direction. |
| `EntryOffsetTicks` | 1 | Entry: stop-market this many ticks beyond the LVN. |
| `StopOffsetTicks` | 4 | Stop: ticks beyond the LVN against the trade (LVN-zone width). |
| `TargetFrontRunTicks` | 1 | Target: front-run the next HVN by this many ticks (exit 100%). |
| `TimeStopSeconds` | 180 | Time stop: a vacuum that hasn't accelerated by now isn't one. |
| `EntryExpirySeconds` | 60 | Working stop-entry lifetime before cancel (engineering bound). |

## Setup 5 — delta-divergence fade (`Detectors:Setup5`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch. |
| `MinNewExtremeTicks` | 2 | E1: the new 30-min extreme must exceed the prior swing extreme by this. |
| `CloseInExtremeFraction` | 0.667 | E2: the bar printing the extreme closed within this fraction of its range toward it. |
| `LocationProximityTicks` | 4 | E3: max ticks from the new extreme to an LOI. |
| `ImbalanceRatio` | 3.0 | E4: diagonal imbalance ratio. |
| `ImbalanceProximityTicks` | 2 | E4: how close to the extreme the diagonal imbalance must print. |
| `EntryOffsetTicks` | 2 | Entry: limit this many ticks inside the extreme. |
| `EntryExpirySeconds` | 120 | Entry: working limit lifetime before cancel. |
| `StopOffsetTicks` | 2 | Stop: ticks beyond the extreme against the trade. |
| `T1RMultiple` | 1.0 | T1 at this multiple of R. |
| `T1ExitFraction` | 0.5 | Fraction exited at T1. |
| `BreakevenOffsetTicks` | 0 | After T1: stop to entry ∓ offset. |
| `T2RCap` | 3.0 | T2 cap in R (developing POC caps below it). |
| `DeltaBucketSeconds` | 10 | Invalidation bucket width. |
| `InvalidationDeltaContracts` | 150 | Invalidation: adverse aggressor delta in a bucket beyond the prior extreme. |
| `ContextExpirySeconds` | 120 | Context lifetime: drop an un-triggered divergence after this long. |

---

## Feature-engine knobs (`Features`)

Mostly affect what is computed/journalled rather than entry/exit, but a few are referenced
by detectors. See `OrderFlow.Domain.Features.FeatureEngineOptions` for the full list (e.g.
`FlowWindowsSeconds`, `DepthImbalanceLevels`, `SwingPullbackTicks`, `SessionMinSamples`).
`SwingPullbackTicks` in particular defines Setup 5's "swing high/low".

The **regime volatility gate** (filter 3) reads its own ATR series here:

| Key | Default | Meaning |
|---|---|---|
| `RegimeAtrBarSeconds` | 1800 | Bar width of the 30-min regime ATR (separate from F34's 5-min ATR). |
| `RegimeAtrPeriodBars` | 14 | True ranges averaged into the regime ATR. |
| `RegimeAtrLookbackDays` | 20 | Trailing window the regime-ATR percentile ranks against. |
