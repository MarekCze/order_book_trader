# Order Flow Setup Rulebook & ML Feature Specification

**Scope:** CME futures with a centralized limit order book (ES/MES, NQ/MNQ, CL, ZN). All rules assume access to market-by-price (MBP-10) depth at minimum; iceberg and queue features require market-by-order (MBO) data.

**Conventions used throughout:**

- `tick` = minimum price increment of the instrument (ES = 0.25)
- `delta(t, w)` = aggressive buy volume − aggressive sell volume over window `w` ending at time `t`
- `cumDelta` = session cumulative delta
- `BBO` = best bid and offer; `spread` = ask − bid in ticks
- `depth(side, k)` = sum of displayed size on `side` across the top `k` levels
- `traded(p)` = total volume executed at price `p` within the current event window
- `displayed(p)` = displayed size at price `p` at observation time
- Levels of interest (LOI) = pre-marked prices: prior day high/low, overnight high/low, prior POC, naked POCs, VAH/VAL, session LVNs, round numbers
- All thresholds below are **starting parameters for ES**, to be recalibrated per instrument and regime. They are deliberately explicit so they can be coded, tested, and falsified — not because the specific numbers are sacred.

### v1 data constraint (MBP-10)

- v1 runs on the Databento **MBP-10** schema; trades with aggressor side are included inline (action `T` records).
- **Setup 3 is out of scope for v1** (requires MBO); all conditions referencing individual order tracking (C1, C3) are deferred.
- Features **F7, F19, F20 are recorded as null in v1**; **F16** uses a trade-vs-cancel attribution heuristic (a displayed-size decrease is classified as traded if a matching trade record printed at that price in the same window, cancelled otherwise); everything else in Parts 1 and 2 is computable from MBP-10 as written.

---

## Global filters (apply to every setup)

1. **Session filter:** Trade only during regular trading hours (RTH), excluding the first 2 minutes after the open and the 10 minutes around scheduled high-impact releases (FOMC, CPI, NFP).
2. **Location filter:** No setup is valid unless price is within 4 ticks of a pre-marked LOI. Order flow signals in the middle of nowhere are ignored.
3. **Volatility filter:** Skip if the ATR is below the 20th percentile of its trailing 20-day distribution (dead market → absorption signals are noise) or above the 95th percentile (news-driven market → levels don't hold).
   > **Implementation annotation (tuning iteration 1, 2026-06-11):** the original "5-minute ATR sampled at signal time" was self-defeating — the setups react to volatility bursts, so the trigger-time 5-min ATR sat in the top tail and the gate blocked nearly every candidate. Redesigned to a **regime gate decoupled from the signal burst**: a **30-minute ATR** (`Features:RegimeAtr*`) sampled **at the moment the context forms**, not at the trigger (`Risk:AtrSampleAtContext`), ranked against the trailing 20-day distribution. The gate is **disabled entirely (pass-through, logged)** until the trailing baseline holds at least `Risk:MinBaselineSessions` (default 10) sessions, since a percentile over fewer sessions is statistically meaningless. The [20th, 95th] band keys (`Risk:AtrPercentileMin/Max`) are unchanged. F34 keeps its rulebook definition (5-min ATR) for the ML journal — the regime gate is a separate ATR series.
4. **Spread filter:** Spread must equal 1 tick at signal time. A wide spread means the book is stressed and passive fills are adversely selected.
5. **One position at a time. Max 3 attempts per LOI per session.** Two consecutive stop-outs at the same level = that level is dead for the day.
6. **Risk:** Fixed fractional risk per trade ≤ 0.5% of account; position size = risk budget ÷ (stop distance in ticks × tick value).

---

## Setup 1 — Absorption fade

**Thesis:** A passive participant is absorbing aggressive flow at a level; when the aggressors exhaust, price reverts away from the level.

**Direction:** Long version stated; short is the mirror.

### Context conditions (all required)

- A1. Price has declined ≥ 6 ticks over the last 3 minutes and is within 2 ticks of an LOI (the "level" `L`).
- A2. Selling is genuinely aggressive: `delta(t, 60s) ≤ −300` contracts (ES), or below the 10th percentile of the rolling session distribution.

### Signal conditions (all required)

- A3. **Price stalls:** the best bid has not ticked below `L` for ≥ 45 seconds despite continued sell prints.
- A4. **Volume without progress:** volume traded at the bid within `[L, L+1 tick]` over the stall window ≥ 3× the average per-price volume of the prior 15 minutes.
- A5. **Replenishment:** displayed bid size at `L` has refreshed to ≥ 60% of its pre-hit size at least 3 times during the stall (i.e., `traded(L) / max(displayed(L)) ≥ 2.5`).
- A6. **Exhaustion trigger:** aggressive sell volume per 10-second bucket has fallen ≥ 70% from its peak bucket during the stall, AND the last 10-second bucket has `delta ≥ 0`.

### Entry

- Buy limit at `L + 1 tick`, working for a maximum of 30 seconds after A6 fires. If unfilled in 30s and price is ≥ `L + 2`, switch to buy stop-market at `L + 3` (momentum confirmation entry). If neither fills within 90s, cancel — setup expired.

### Stop

- Hard stop at `L − 3 ticks` (one tick beyond the absorption price plus buffer). Never widened.

### Targets / management

- T1 at `entry + 1R` (R = stop distance): exit 50%, move stop to entry −1 tick.
- T2 at the nearest opposing structure (developing POC, VWAP, or prior bounce high), capped at `entry + 3R`.
- **Time stop:** if neither T1 nor stop is hit within 5 minutes, exit at market — absorption trades work fast or not at all.

### Invalidation (exit immediately at market, even before stop)

- The absorbing bid at `L` is pulled (displayed size drops > 80% without trading).
- A single sell print or sweep ≥ 200 contracts trades through `L`.

---

## Setup 2 — Stop-run fade (trapped traders)

**Thesis:** A sweep of an obvious level fills breakout buyers and triggered stops; absence of follow-through traps them, and their forced exits fuel the reversal.

**Direction:** Short version stated (fading a swept high); long is the mirror.

### Context conditions

- B1. A reference high `H` exists: prior day high, overnight high, or a swing high ≥ 30 minutes old, visible on a 5-minute chart.
- B2. Price trades above `H` by 1–5 ticks (the "sweep zone"). A break > 6 ticks is a breakout, not a sweep — no trade.

### Signal conditions (all required)

- B3. **Climax:** the bar/event-window that breaks `H` prints aggressive buy volume ≥ 90th percentile of session bars, with ≥ 60% of that volume executing at or above `H`.
- B4. **No follow-through:** within 90 seconds of the sweep high, price has not made a new high by more than 1 tick.
- B5. **Supply confirms:** total displayed offer size in the 3 levels above the sweep high has increased ≥ 50% since the sweep (offers stacking, not pulling), OR a stacked sell imbalance (≥ 3 consecutive diagonal sell imbalances at 3:1) prints in the sweep zone.
- B6. **Reclaim trigger:** last trade price returns to `H − 1 tick`.

### Entry

- Sell stop-market at `H − 2 ticks` placed when B5 confirms; executes on the reclaim. Cancel if not triggered within 4 minutes of the sweep.

### Stop

- 1 tick above the sweep high.

### Targets / management

- T1 at `entry − 1R`: exit 50%, stop to entry.
- T2: the origin of the breakout leg (the last consolidation before the run at `H`), capped at 4R.
- **Scratch rule:** if price re-crosses above `H` and holds for 60 seconds without hitting the stop, exit at market — the trap thesis is wrong even if the stop survives.

---

## Setup 3 — Iceberg follow *(requires MBO — deferred in v1)*

**Thesis:** A reloading hidden order reveals a committed large participant; trade their side using their level as the risk anchor.

**Direction:** Stated for a bid-side iceberg (long); mirror for offers.

### Detection (defines the iceberg, requires MBO or trade-vs-display reconciliation)

- C1. At a single price `P`: `traded(P) / max(displayed(P)) ≥ 4` within a 3-minute window, with displayed size refreshing within 500 ms of each depletion at least 4 times.
- C2. `P` is within 3 ticks of an LOI.
- C3. The refresh quantity is stable (coefficient of variation of refresh sizes < 0.5) — algorithmic execution signature, distinguishing a true iceberg from coincidental queue joins.

### Entry

- Buy limit at `P + 1 tick` (do not join at `P` — you'd be behind the iceberg's own refreshes and filled only when it breaks). Work the order while C1's refresh behavior continues.

### Stop

- `P − 2 ticks`.

### Targets / management

- T1 at `entry + 1.5R`: exit 50%, stop to entry.
- T2: trail 2 ticks behind each successive defended price if the iceberg "steps" (re-detected one tick higher).
- **Invalidation:** iceberg stops refreshing (displayed at `P` fully depletes and is not replaced within 2 seconds) → exit at market regardless of P&L.

---

## Setup 4 — LVN vacuum continuation (pull-and-thin)

**Thesis:** When resting liquidity ahead of price evaporates, price travels fast through the thin zone to the next high-volume area.

**Direction:** Stated for downside continuation; mirror for upside.

### Context conditions

- D1. Price is within 3 ticks above a profile LVN (volume at the LVN price ≤ 25% of the session mean per-price volume), and the next HVN below is ≥ 8 ticks away (the move must have room to pay).

### Signal conditions (all required)

- D2. **Pulling:** total displayed bid size in the 5 levels below last price has declined ≥ 40% over the last 30 seconds, with cancel volume > new-add volume on the bid side (pull ratio > 1.5).
- D3. **Aggressor alignment:** `delta(t, 30s) ≤ −100` (sellers active into the thinning book).
- D4. **Trigger:** best bid ticks down through the LVN price.

### Entry

- Sell stop-market 1 tick below the LVN, placed when D2+D3 hold. This is a momentum entry — paying the spread is accepted.

### Stop

- 1 tick above the upper edge of the LVN zone (typically 3–4 ticks).

### Targets / management

- Single target: front-run the next HVN by 1 tick, exit 100%. Vacuum trades do not get runners — the move ends when liquidity reappears.
- **Time stop:** 3 minutes. A vacuum that doesn't accelerate immediately isn't a vacuum.

---

## Setup 5 — Delta divergence fade

**Thesis:** Price makes a new extreme but aggressive flow doesn't confirm — the extreme is being absorbed by passive interest; fade it.

**Direction:** Short version stated (divergence at highs); mirror at lows.

### Signal conditions (all required)

- E1. Price prints a new 30-minute high `H2`, exceeding the prior swing high `H1` by ≥ 2 ticks.
- E2. **Divergence:** `cumDelta` at the moment of `H2` is below `cumDelta` at `H1`, OR the bar printing `H2` has bar-delta ≤ 0 despite closing in the top third of its range's price advance.
- E3. **Location:** `H2` is within 4 ticks of an LOI.
- E4. **Trigger:** a diagonal sell imbalance (3:1) prints within 2 ticks of `H2`, or price trades back below `H1`.

### Entry

- Sell limit at `H2 − 2 ticks` after E4; cancel if unfilled within 2 minutes.

### Stop

- 2 ticks above `H2`.

### Targets / management

- T1 at 1R (50% off, stop to entry); T2 at session VWAP or developing POC, capped at 3R.
- **Invalidation:** a 10-second bucket with `delta ≥ +150` printing above `H1` → exit at market (real buyers showed up).

---
---

# Part 2 — ML Feature Specification

This section converts the rulebook into a feature set, labels, and modeling notes for training a model to detect these setups and manage entries/exits on raw order book + trade data.

## 2.0 Design philosophy

Two viable framings:

1. **Supervised setup classifier + meta-label** (recommended first): hand-coded detectors (the rules above) generate candidate events; the model learns *which candidates to take* (meta-labeling, per López de Prado). This dramatically reduces class imbalance and keeps the model's job tractable.
2. **End-to-end policy** (RL or sequence model predicting position): only after framing 1 works. Entry/exit as actions, P&L-derived reward net of costs.

Either way, **features must be stationary**: use ratios, z-scores against rolling session distributions, and tick-denominated distances — never raw prices or raw volumes.

**Sampling:** do not use time bars. Build features on event-driven bars — volume bars (e.g., every 1,000 ES contracts), tick-imbalance bars, or simply per-event snapshots at candidate timestamps. Order flow information density varies enormously with activity; time bars smear it.

## 2.1 Book-state features (snapshot at decision time)

| # | Feature | Definition | Encodes rule |
|---|---|---|---|
| F1 | `spread_ticks` | (ask − bid)/tick | Global filter 4 |
| F2 | `depth_imbalance_k` | (Σbid_k − Σask_k)/(Σbid_k + Σask_k), for k ∈ {1,3,5,10} | A5, B5, D2 |
| F3 | `book_slope_bid/ask` | OLS slope of log(size) vs. level index over top 10 levels | thin vs. thick book shape |
| F4 | `bbo_size_ratio` | displayed(bid₁)/displayed(ask₁), log-transformed | imbalance |
| F5 | `depth_z` | z-score of total top-5 depth vs. trailing 30-min distribution | regime |
| F6 | `level_dist_signed` | signed distance to nearest LOI in ticks, plus one-hot of LOI type (PDH, ONL, nPOC, LVN, …) | Global filter 2, A1, B1, C2, D1, E3 |
| F7 | `queue_position_est` | own-order queue ahead estimate (MBO) — for execution model only *(requires MBO — deferred in v1, journaled as null)* | queue logic |

## 2.2 Flow features (rolling windows w ∈ {10s, 30s, 60s, 300s})

| # | Feature | Definition | Encodes rule |
|---|---|---|---|
| F8 | `delta_w` | aggressive buys − sells over w, z-scored vs. session | A2, D3, E2 |
| F9 | `cum_delta_div` | cumDelta(now) − cumDelta(at last swing extreme), signed by direction of the new extreme | E2 |
| F10 | `trade_intensity_w` | trade count per second over w, z-scored | climax / exhaustion |
| F11 | `aggressor_run_len` | length of the current run of same-side aggressor prints | momentum |
| F12 | `large_print_count_w` | count of single prints ≥ 95th percentile size over w | B3, sweeps |
| F13 | `sweep_flag_w` | 1 if any single order consumed ≥ 2 price levels within w | sweeps, invalidations |
| F14 | `sell_decay` | (peak 10s sell volume during current stall) / (latest 10s sell volume) | A6 exhaustion |
| F15 | `vol_at_price_ratio` | traded volume in [bid, bid+1] over stall window ÷ 15-min per-price mean | A4 |

## 2.3 Liquidity-dynamics features (require depth updates; MBO ideal)

| # | Feature | Definition | Encodes rule |
|---|---|---|---|
| F16 | `pull_ratio_side_w` | cancel volume ÷ add volume per side over w | D2 (pulling), A-invalidation |
| F17 | `replenish_ratio_p` | traded(P) ÷ max(displayed(P)) at the defended price | A5, C1 (iceberg core) |
| F18 | `refresh_count_p` | number of refresh events at P within 3 min | A5, C1 |
| F19 | `refresh_latency_ms` | median ms between depletion and refresh at P *(requires MBO — deferred in v1, journaled as null)* | C1 |
| F20 | `refresh_size_cv` | coefficient of variation of refresh sizes at P *(requires MBO — deferred in v1, journaled as null)* | C3 (algo signature) |
| F21 | `depth_change_dir_w` | %Δ in displayed size, 3 levels beyond the active extreme | B5 (stacking vs. pulling) |
| F22 | `vanish_flag` | 1 if defended-level displayed size dropped >80% without trading | absorption invalidation |

## 2.4 Footprint features (per event-bar)

| # | Feature | Definition | Encodes rule |
|---|---|---|---|
| F23 | `diag_imbalance_count_buy/sell` | count of ≥3:1 diagonal imbalances in bar | B5, E4 |
| F24 | `stacked_imbalance_len` | max consecutive same-side imbalanced levels | B5 |
| F25 | `bar_delta` / `bar_delta_pctl` | bar delta, and its session percentile | E2 |
| F26 | `delta_price_div` | sign(bar return) × sign(bar delta) disagreement flag | absorption-in-hindsight |
| F27 | `extreme_volume_share` | volume in top (bottom) 2 ticks of bar ÷ bar volume | B3 trapped volume |
| F28 | `unfinished_auction_flag` | two-sided volume at bar extreme above threshold | unfinished auction |
| F29 | `poc_drift` | bar-POC migration direction over last 5 bars, in ticks | value migration |

## 2.5 Profile / context features

| # | Feature | Definition | Encodes rule |
|---|---|---|---|
| F30 | `dist_poc`, `dist_vah`, `dist_val` | signed tick distance to session POC / VAH / VAL | targets, location |
| F31 | `dist_naked_poc` | signed tick distance to nearest naked POC | LOI typing |
| F32 | `lvn_depth_ratio` | volume at current price ÷ session mean per-price volume | D1 |
| F33 | `dist_next_hvn` | tick distance to next HVN in trade direction | D1 room-to-pay |
| F34 | `atr5_pctl` | 5-min ATR percentile vs. 20-day distribution | Global filter 3 |
| F35 | `tod_sin/cos`, `rth_minute` | time-of-day encoding, minutes since open | session filter |
| F36 | `news_window_flag` | 1 within ±10 min of scheduled release | session filter |

## 2.6 Composite setup scores (optional engineered meta-features)

These mirror the hand-coded detectors and are legitimate inputs for a meta-labeling model:

- `absorption_score` = f(F8↓, F14↑, F15↑, F17↑, F18↑, price-stall duration)
- `trap_score` = f(F12, F27, F21-stacking, time-since-sweep, failure-to-extend)
- `iceberg_score` = f(F17, F18, F19↓, F20↓, F6 proximity)
- `vacuum_score` = f(F16↑, F8 alignment, F32↓, F33)
- `divergence_score` = f(F9, F25, F23)

## 2.7 Labels

**Candidate generation:** run the rulebook detectors (relaxed thresholds, e.g. 70% of stated values) over historical data to produce candidate events. Each candidate gets features sampled at trigger time.

**Primary label — triple-barrier:** for each candidate, simulate the rulebook's own stop (lower barrier), 2R target (upper barrier), and time stop (vertical barrier). Label:

- `+1` if target hit first
- `−1` if stop hit first
- `0` (or the signed return) if the time barrier hits first

Net all simulated outcomes of costs: spread paid per the entry type (limit vs. stop-market), commissions, and 1 tick of slippage on stop-market exits.

**Meta-label:** binary — "would taking this candidate have been profitable net of costs?" Model output = probability; position size ∝ calibrated probability (or trade only above a threshold chosen on validation data).

**Exit model (separate):** train a second model on open-position states with features F1–F36 plus position-state features (unrealized R, time in trade, distance to target/stop) to predict P(stop hit before target | current state) — this learns the scratch/invalidation rules (A-invalidation, B-scratch, C-invalidation) from data rather than hand-coding them.

## 2.8 Validation and pitfalls

1. **Walk-forward only.** Purged k-fold with embargo if cross-validating (events overlap in time; standard CV leaks).
2. **Leakage checks:** every feature must be computable strictly from data available at the candidate timestamp. Footprint features of the *forming* bar must use the partial bar, not the completed one.
3. **Class imbalance:** even with candidate filtering, expect skew; use the meta-label framing and evaluate with precision/recall at the chosen threshold plus net expectancy — never accuracy.
4. **Regime sensitivity:** retrain/recalibrate thresholds per volatility regime (F34 buckets); a model fit on 2024 chop will fail in a trending repricing.
5. **Latency realism:** simulate detection-to-order latency (≥ 250 ms for retail infrastructure) and queue position for limit entries. Passive fills are adversely selected — model fill probability, not just touch.
6. **Costs dominate.** At 2–4 tick targets, 1 tick of combined spread+slippage+commission consumes 25–50% of gross edge. Any backtest not netting costs per fill type is fiction.
7. **Non-stationarity of microstructure:** exchange matching rules, tick sizes, and participant mix change; date-stamp the data regime and don't pool across structural breaks (e.g., tick size changes, contract roll behavior).
8. **Benchmark honestly:** compare the trained model against (a) the raw rulebook with no ML and (b) random entries with the same exit logic. If the model doesn't beat both net of costs, the features aren't carrying signal.

---

*Parameters in this document are illustrative starting points for ES and must be recalibrated per instrument, session type, and volatility regime. None of this constitutes financial advice; futures trading involves substantial risk of loss.*
