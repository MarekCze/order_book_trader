# Order-flow trading methodology

A plain-language explanation of the entire trading methodology this bot implements, from the
core idea down to each rule. Grounded in the product spec (`order-flow-setups-rulebook.md`)
and how it is implemented in the codebase.

## 1. The core idea

Every trade in a futures market is a collision between **aggressors** (market orders that
cross the spread — measured as *delta* = buy volume − sell volume) and **passive liquidity**
(resting limit orders sitting in the book as displayed depth). Price moves when aggressors
overwhelm the passive side; price stalls when passive liquidity absorbs them.

The whole methodology is one bet repeated five ways: **at a meaningful price level, watch who
is winning that aggressor-vs-passive exchange, and position with the winner at the moment the
loser is exhausted or trapped.**

- Three setups **fade exhaustion** — aggressors push to an extreme, fail, and reverse
  (Setups 1, 2, 5).
- One setup **follows continuation** — liquidity vanishes ahead of price and it accelerates
  through the gap (Setup 4).
- One setup (3, **deferred** — needs order-level MBO data) **follows a committed hidden
  player**.

It is a deterministic, rule-based system: it replays historical market data event-by-event,
and the same data always produces the same trades. The trade journal it produces doubles as
the labelled training set for a future ML phase.

## 2. What the bot sees — the data

- Input is **Databento MBP-10**: market-by-price, top 10 levels of book depth per side, with
  trades arriving inline (aggressor side known). There is no order-by-ID tracking and no
  queue position at this schema level — that is why Setup 3 and features F7/F19/F20 are
  deferred/null.
- A **book-state tracker** ingests each record and republishes two clean event types
  downstream: `BookChanged` (new top-10 snapshot + what caused it) and `Trade` (price, size,
  aggressor side, resulting book).
- Everything runs on **event timestamps**, never wall-clock. RTH = 09:30–16:00 ET; the
  17:00–18:00 ET maintenance/pre-open window is excluded (books there are legitimately
  crossed).

## 3. Where trades are allowed — Levels of Interest (LOIs)

Order-flow signals "in the middle of nowhere" are noise. A setup is only valid **near a
pre-computed LOI**. LOIs are computed, not hand-marked:

- Prior-day high/low, overnight high/low
- Prior-session POC / VAH / VAL (from a volume-by-price profile: POC = max-volume bin; value
  area = smallest contiguous 70% around POC)
- Session **LVNs** (low-volume nodes: local minima < 25% of session mean per-price volume)
  and **HVNs** (high-volume nodes)
- A **naked-POC registry** (untouched prior POCs, persisted across sessions)
- Round numbers (multiples of 25.00 for ES)

LVNs and HVNs matter specifically for Setup 4 (price vacuums *through* a thin LVN *toward* a
thick HVN).

## 4. The five setups

Each setup is a **state machine**:
`Idle → ContextMet → Armed → OrderWorking → InPosition → Closed`, where every rulebook
condition is an explicit, individually testable guard. Long version stated below; the short is
the sign-mirror.

### Setup 1 — Absorption fade
**Thesis:** a passive player is absorbing aggressive selling at a level; when sellers exhaust,
price reverts up.
- **Context:** A1 price declined ≥ 6 ticks in 3 min and is within 2 ticks of an LOI
  (level `L`); A2 selling is genuinely aggressive (`delta(60s) ≤ −300` or below the 10th
  session percentile).
- **Signal (all required):** A3 price stalls — best bid hasn't broken `L` for ≥ 45 s despite
  continued sell prints; A4 **volume without progress** — volume at `[L, L+1]` ≥ 3× the
  15-min per-price baseline; A5 **replenishment** — the bid keeps refilling
  (`traded(L)/max displayed(L) ≥ 2.5`, ≥ 3 refreshes); A6 **exhaustion** — sell volume per
  10 s bucket has dropped ≥ 70% from its peak and the last bucket's delta ≥ 0.
- **Entry/stop/targets:** buy limit at `L+1` (escalate to a momentum buy-stop at `L+3` if
  price pushes through first); hard stop `L−3`; T1 at +1R (take 50%, stop to breakeven),
  T2 ≤ +3R at opposing structure; 5-minute time stop.
- **Invalidation:** the bid at `L` is pulled (> 80% disappears untraded), or a ≥ 200-contract
  sweep trades through `L`.

### Setup 2 — Stop-run fade (trapped traders)
**Thesis:** a sweep of an obvious level fills breakout buyers and triggers stops; with no
follow-through, those traders are trapped and their forced exits fuel the reversal.
- **Context:** B1 a reference high `H` exists (PDH/ONH/old swing high); B2 price poked 1–5
  ticks above `H` (the "sweep zone" — > 6 ticks is a real breakout, not faded).
- **Signal:** B3 **climax** — the breaking bar's aggressive buy volume ≥ 90th percentile,
  ≥ 60% executing at/above `H`; B4 **no follow-through** within 90 s; B5 **supply confirms** —
  offers stack ≥ 50% above the sweep (or a 3:1 stacked sell imbalance); B6 **reclaim** —
  price returns to `H−1`.
- **Entry/stop/targets:** sell stop at `H−2`; stop 1 tick above the sweep high; T1 −1R,
  T2 ≤ 4R; **scratch** if price reclaims above `H` and holds 60 s (thesis wrong even if the
  stop survives).

### Setup 3 — Iceberg follow *(deferred — requires MBO)*
**Thesis:** a hidden order that keeps reloading reveals a committed large player; trade their
side using their price as the risk anchor. Detection needs order-by-ID refresh timing /
size-stability (C1/C3), which MBP-10 cannot provide. The framework reserves its slot so
journals stay schema-stable when MBO is adopted.

### Setup 4 — LVN vacuum continuation
**Thesis:** when resting liquidity ahead of price evaporates, price travels fast through the
thin zone to the next high-volume area.
- **Context:** D1 price within 3 ticks above a profile LVN, with the next HVN ≥ 8 ticks away
  ("room to pay").
- **Signal:** D2 **pulling** — displayed bid size in the 5 levels below has dropped ≥ 40% in
  30 s with cancels > adds (pull ratio > 1.5); D3 **aggressor alignment** — `delta(30s) ≤
  −100`; D4 **trigger** — best bid ticks through the LVN.
- **Entry/stop/targets:** sell stop 1 tick below the LVN (momentum entry, pays the spread);
  stop above the LVN zone; single target front-running the next HVN (no runners); 3-minute
  time stop.

### Setup 5 — Delta divergence fade
**Thesis:** price makes a new extreme but aggressive flow doesn't confirm — the extreme is
being absorbed; fade it.
- **Signal:** E1 a new 30-min high `H2` exceeds the prior swing high `H1` by ≥ 2 ticks; E2
  **divergence** — `cumDelta` at `H2` is below `cumDelta` at `H1` (or the bar making `H2` has
  delta ≤ 0 despite closing strong); E3 **location** — `H2` within 4 ticks of an LOI; E4
  **trigger** — a 3:1 diagonal sell imbalance within 2 ticks of `H2`, or price falls back
  below `H1`.
- **Entry/stop/targets:** sell limit at `H2−2`; stop 2 ticks above `H2`; T1 1R, T2 ≤ 3R;
  invalidation if a 10 s bucket prints `delta ≥ +150` above `H1` (real buyers showed up).

## 5. The global filters — every candidate must clear all six

Independent of which setup fired, a candidate is gated by six portfolio-level filters before
entry:

1. **Session** — RTH only, skip the first 2 minutes after the open and ±10 min around
   scheduled high-impact news.
2. **Location** — must be within 4 ticks of an LOI.
3. **Volatility regime** — the ATR percentile must sit inside a [20th, 95th] band (skip dead
   markets where absorption is noise and news-driven markets where levels don't hold). *This
   was redesigned in tuning iteration 1* — see §9.
4. **Spread** — must be exactly 1 tick (a wide spread means a stressed book and adverse
   passive fills).
5. **Exposure** — one position at a time across all setups; max 3 attempts per LOI per
   session; two consecutive stop-outs kill that level for the day.
6. **Risk sizing** — fixed-fractional ≤ 0.5% of equity; size = risk budget ÷ (stop distance
   in ticks × tick value), capped.

## 6. Execution — a deliberately pessimistic fill model

The simulator is biased *against* the strategy so backtests don't flatter:
- **Stop / market orders** fill at trigger + 1 tick of adverse slippage.
- **Limit orders** fill *only when the market trades through* the price (never on touch; no
  queue-position credit).
- **Commissions** are charged ($1.40 round-turn/contract default); **all journal P&L is net.**

Trade management is mechanical: T1 takes a partial and moves the stop to breakeven; T2 caps
the runner; time stops and invalidations force exits. Every exit is tagged with a reason
(Target / Stop / Invalidation / TimeStop / SessionEnd / Scratch).

## 7. The journal — a first-class output

For **every candidate** that reaches Armed (whether it traded or was blocked), the bot
persists: timestamp, setup, the full **F1–F36 feature snapshot** at trigger time, the risk
decision, and — if traded — fills, MAE/MFE in ticks, exit reason, and net P&L. This is both
the audit trail and the future ML training set (the plan is *meta-labeling*: the hand-coded
detectors propose candidates, a model later learns *which* to take).

## 8. Why it is built this way

Three design commitments hold the whole thing together: **determinism** (same data →
byte-identical journal — no wall-clock, no randomness, no unordered iteration), **every
threshold is a named config value** (nothing hard-coded; defaults equal the rulebook's stated
ES numbers, all tunable via `--set`), and **the live broker is just an interface** — v1 only
implements the simulator adapter.

## 9. What is actually true empirically (from the tuning work)

The methodology is sound on paper; calibration against five real ES days revealed where
reality bites (full detail in `runs/tuning-iteration-1-results.md`):

- **Filter 3 (volatility) was a silent kill-switch** — the original "5-min ATR sampled at the
  trigger" blocked ~100% of candidates because the setups *react to* volatility bursts. It was
  redesigned to a **30-min regime ATR sampled when context forms**, with pass-through until a
  10-session baseline exists. That produced the **first end-to-end trades**.
- **Only Setup 5 actually trades** on this data so far. The per-condition funnel shows why the
  others are silent: **Setup 1 dies structurally at A4** (stall volume at the level is a
  *fraction* of baseline, never the required 3×), **Setup 4 dies at D2** (the depth-pull
  thresholds are far too strict — it is calibratable but on a cliff), and **Setup 2** is
  throttled by its climax (B3) and supply (B5) conditions.

So the unifying methodology is real and fully implemented, but only one of the four active
expressions of it currently clears its own signal conditions on live ES data — and the
calibration work to change that is deliberately the *next* phase, not done yet.

---

*See also: `order-flow-setups-rulebook.md` (the product spec, incl. the F1–F36 ML feature
table), `CLAUDE.md` (architecture and engineering rules), `docs/tuning-reference.md` (every
tunable threshold), and `runs/tuning-log.md` / `runs/tuning-iteration-1-results.md` (calibration
findings).*
