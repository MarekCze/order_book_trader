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

---

## Funnel telemetry on the REAL week (2026-06-11, resolves the blocked iteration-2 audits)

Ran the iteration-2 per-condition telemetry (PR #10) on the real ES week (default config,
chained state) — the run the iteration-2 session could not do (no DBN files there). Week
report reproduces RUN A2 byte-for-byte: **16 trades, 5W/11L, −$6,161.50** → confirms PR #10
added telemetry only, zero behaviour change. Per-condition passed/evaluated (chain order),
representative day; pattern holds all 5 days:

```
S1 AbsorptionFade : A1 → A2rdy → A2 → A3 → A4 0/N → A5 0/0 → A6 0/0   (A4 is an absolute wall)
S2 StopRunFade    : B1B2 → B3 (~1% pass) → B4 (all pass) → B5 ~0/N    (B3 climax, then B5 supply)
S4 LvnVacuum      : D1lvn → D1room → D2 0/N → D3 0/0                  (D2 is an absolute wall)
S5 DeltaDivFade   : E1 (most pass) → E2 (~10% pass) → E3 → E4 → trades (works; E2 is the filter)
```

**Binding constraints (the answer to "why are S1/S4 silent"):**
- **Setup 1 — `A4` (volume-without-progress, `StallVolumeMultiple`=3×) passes 0 of thousands
  of A3-stall events, every day, both sides.** A3 produces plenty of stalls; none carry 3×
  baseline volume at [L, L+1]. (Day 5 also shows A3 0/N — `StallSeconds`=45 vs ~15–24 s max
  stalls that day — but A4 is the dominant wall.)
- **Setup 4 — `D2` (depth-decline `DepthDeclineFraction`=0.40 + `PullRatioMin`=1.5) passes
  0 of hundreds of thousands to millions, every day.** D1 (LVN proximity + HVN room) fires
  constantly; the cancel/pull signal never reaches threshold.
- **Setup 2 —** `B3` (climax ≥ 90th pctl) filters ~99%, then `B5` (supply confirmation)
  takes nearly all the rest → ~1 candidate all week.
- **Setup 5 —** the only setup that trades; `E2` (cum-delta / non-confirming-bar divergence)
  is the selective filter (~10% pass), E1/E3/E4 are permissive.

**Directly sets up item 4** (S1/S4 threshold-scaling sweep): the factors should scale
**S1 `StallVolumeMultiple`** (the A4 wall) and **S4 `DepthDeclineFraction` + `PullRatioMin`**
(the D2 wall) at ×1.0/0.8/0.6/0.4 and watch the A4 / D2 passed counts begin to lift.

---

## Item 4 — S1/S4 threshold-scaling sweep (2026-06-11)

Script: `runs/sweep_s1_s4.sh`. A single multiplicative factor applied per run to S1's A4
threshold (`StallVolumeMultiple`, default 3.0) and S4's D2 thresholds
(`DepthDeclineFraction` 0.40 + `PullRatioMin` 1.5), all via `--set`, ephemeral state,
no config files touched. Summed over the 5-day week, both directions:

| factor | S1.StallVolMult | S1 A4 pass/eval | S1 cand | S4.DDF / PRM | S4 D2 pass/eval | S4 cand |
|---|---|---|---|---|---|---|
| ×1.0 | 3.0 | 0/97,484 | 0 | 0.40 / 1.5 | 0/4,686,141 | 0 |
| ×0.8 | 2.4 | 0/97,484 | 0 | 0.32 / 1.2 | 0/4,686,141 | 0 |
| ×0.6 | 1.8 | 0/97,484 | 0 | 0.24 / 0.9 | 214,868/3,879,650 | 50,910 |
| ×0.4 | 1.2 | 0/97,484 | 0 | 0.16 / 0.6 | 306,312/2,249,518 | 43,819 |

**Setup 4 — D2 begins to fire between ×0.8 and ×0.6** (i.e. `DepthDeclineFraction` ≲ 0.24
and `PullRatioMin` ≲ 0.9). The boundary is a cliff, not a ramp: 0 → 214k passes / 50,910
candidates in one step, then it floods. The rulebook 0.40/1.5 is far on the dead side. So
S4's silence IS a threshold-calibration problem, but the firing region is wildly permissive —
50k candidates/week is not a tradeable setup, just proof the gate opens. Real calibration
needs a much finer grid in [×0.6, ×0.8] plus a tighter D3/location/quality filter.

**Setup 1 — A4 does NOT begin to fire anywhere in ×1.0…×0.4.** A4's evaluated count is
constant (97,484 = the A3 stalls; the multiplier doesn't change upstream) but passed stays
0 even at `StallVolumeMultiple`=1.2. This is NOT a near-miss: lowering the multiplier 2.5×
moves nothing, which means the measured stall volume at [L, L+1] is essentially always far
below baseline — a structural issue (the metric, not the threshold). Probing lower
(×0.2/×0.1) to confirm — see below. A4 likely needs a definition review (what volume
is being accumulated at the level during the stall), not just a threshold change.

**S1 deep probe (×0.2, ×0.1; A4 summed over week):**

| factor | S1.StallVolMult | S1 A4 pass/eval | S1 cand |
|---|---|---|---|
| ×0.2 | 0.6 | 8,430/97,484 | 0 |
| ×0.1 | 0.3 | 30,588/79,306 | 3 |

A4 only **begins to pass at ×0.2** (`StallVolumeMultiple`=0.6 — 5× below the rulebook 3.0),
confirming the stall volume at [L, L+1] is typically a *fraction* of the per-price baseline,
never a multiple. And even with A4 open at ×0.2, **candidates stay 0** — A5 (replenishment)
and A6 (exhaustion) then block; only at ×0.1 do 3 candidates emerge. So **Setup 1 has
several deep walls (A4, then A5/A6), not one tunable threshold.** Conclusion: S1 is not a
calibration miss — its A4 volume metric and the A5/A6 chain need a definition review against
this data before any threshold is meaningful. (Eval drops to 79,306 at ×0.1 because once the
machine arms/trades it spends fewer events in the A3 stall — a second-order effect.)

**Item 4 verdict:** S4 silence is a (steep) threshold-calibration issue with a firing
boundary at ~×0.7; S1 silence is structural (metric/definition), not a threshold. No
defaults changed — diagnostics only, per the iteration's "diagnostics before optimization".

---

## Permissive "many entrances" run (2026-06-14) — full results in `runs/permissive-run-results.md`

**Command:** `runs/permissive_run.sh` (binding-wall conditions opened to sweep firing levels,
filter-5 caps lifted, all via `--set`; context conditions + global filters 1–4/6 default).

**Config delta vs defaults:** S1 `StallVolumeMultiple=0.5,ReplenishRatioMin=0.5,RefreshCountMin=1,
ExhaustionDropRatio=0.3`; S2 `ClimaxVolumePercentile=0.3,ClimaxShareBeyondLevel=0.3,
SupplyDepthIncrease=0.1,StackedImbalanceMinLen=2`; S4 `DepthDeclineFraction=0.24,PullRatioMin=0.9,
MinAlignedDeltaContracts=0`; `Risk:MaxAttemptsPerLoi=1000,ConsecutiveStopOutsToKillLevel=1000`.

**Headline:** 70,133 candidates (vs 94 default) → only **48 trades** (vs 16). **66,494 (95%)
blocked PositionOpen.** Net −$23,285.70, hit rate 8.3%, PF 0.05, 47/48 exits Stop.

**Findings:** (1) The trade-frequency ceiling is the **hard-coded one-position-at-a-time rule**,
not the setups — loosening thresholds 750×'d candidates but only 3×'d trades. "Many entrances"
is architecturally gated (needs a portfolio-model code change), not threshold-gated. (2)
Loosening signal thresholds **destroys entry quality** (hit rate 31%→8%, PF 0.12→0.05) — the
strict rulebook thresholds are quality filters, not just throttles. (3) S1 still trades 0 even
with A4 open (A5/A6 block) — reconfirms S1 is structural. S4 dominates (30/48; flood + detector
order). **Process note:** a maximally-open profile (D2/D3 always-pass) was degenerate
(candidate-per-event, 68 MB/day, killed) → moderated to sweep firing levels. No defaults changed.

---

## Exit-geometry sweep (2026-06-14) — full results in `runs/exit-geometry-results.md`

**Command:** `runs/exit_geometry_sweep.sh` (config-only; default entry config; exit knobs on
S1/S2/S5 via `--set`; no defaults changed). Tests the diagnosis that wins are capped at +0.5R
because T1-50%-then-breakeven + unreachable-3R-T2 → the runner contributes 0.

| Profile | Net | Expectancy | Avg win | Targets |
|---|---:|---:|---:|---:|
| P0 baseline (1R/0.5/3R) | −$6,161 | −$385 | $173.5 | 0 |
| **P2 single @1R (frac=1.0)** | **−$4,599** | **−$287** | **$486** | 5 |
| P4 lock-runner (T2 1.5R, BE −1) | −$5,099 | −$319 | $386 | 2 |
| P1 early-T1 / P3 tiny-T1 | worse (−$9.4k / −$8.8k) | | | |

**Findings:** (1) Taking the **full position at the 1R target** (P2) beats the
scale-out-then-breakeven baseline — same 16 trades/5 winners, but each banks the full 1R →
avg win ×2.8 ($173.5→$486), expectancy +~25% (−$385→−$287), lowest maxDD; payoff 0.27→0.76.
The runner gave up nothing (T2 never reached). (2) Still **net-negative**: P2's 0.76 payoff
needs ~57% hit rate; actual 31% → **entry quality is the binding ceiling, not exits.**
(3) **Trailing-stop (planned Stage 2 code) is NOT worth building** — MFEs (6% reach 3R) mean a
runner captures ~nothing beyond P2's flat 1R. Exit fix is achievable in config.
**Recommendation:** adopt P2-style geometry (config); skip the code change; next lever = entries.

---

## Inverted trade direction (2026-06-14) — full results in `runs/inverted-direction-results.md`

**Command:** `runs/inverted_run.sh` (week, default config, `--set Risk:InvertDirection=true`).
New gated flag `Risk:InvertDirection` (default off, byte-identical, 307 tests pass): mirrors
execution (`ExecSign=−Sign`) — entry side, brackets, R, P&L — while detection is unchanged, so
every trade is taken on the opposite side (old SL level ↔ TP level).

**Result — inverting makes it WORSE:** 35 trades, 4W/31L, net **−$17,219** (vs baseline −$6,162),
hit rate 31%→11%, expectancy −$385→−$492, maxDD $17.4k. Exits: 28 stops, 4 T1-breakeven, 3
targets (≈$0) — not a bug, the inverted trades just lose.

**Finding:** **direction is not the problem — fading is the correct side.** Buying highs /
selling lows gets caught by the same mean-reversion the fades target, so the inverse stops out
more and ~triples the loss. Corroborates that losses come from **entry quality + R:R/costs, not
the side.** No defaults changed.

---

## Entry-quality diagnosis (2026-06-14) — full results in `runs/entry-quality-diagnosis.md`

**Command:** re-ran the week (Release, per-day journals, chained `Storage:SqlitePath`),
reproduced **16 trades / 5W-11L / −$6,161.50** byte-for-byte, then `inspect-trade --data` on
all 16 + SQL fact table. Diagnostics-only; no defaults/thresholds/code changed.

**Findings (n=16, all Setup 5; hypotheses, not proofs):**
- **Outcome is binary, and it's an entry problem not an exit problem.** 11 losers have MFE
  ∈ {0,1,2} ticks (never near +1R); 5 winners have MFE 5–12 (hit T1 fast). No loser reaches
  MFE 3 → **widening the stop only enlarges losses; tightening kills winners. The R≈4t stop is
  correctly sized.** Exits (P2) are already the only lever and can't fix this.
- **S5 fades live climaxes (continuation), not exhaustion.** 6/11 losers stop in <4s (three in
  0.0–0.1s). Day-1 cascade: 4 shorts fired *within one second* at successive new highs of a
  vertical rally, each into **+13–21σ buy flow** (`f8_delta_z_w10`). S5's trigger has no
  stall/deceleration gate (unlike S1's A3/A6). Blocking the 5 Day-1 climax entries alone →
  net −$6,161.50 → **−$3,779 (−39%)**.
- **E4 never required a reclaim:** all 16 triggered on `imbalance-near-extreme` with
  `reclaimed-past-H1=False`; E2 often passed on the weak non-confirming-bar branch.
- Deferred audits answered: 2a MAE/MFE split is clean (t1=1 win / t1=0 loss); 2c swings
  1.4k–5.2k/session, S5 not over-firing by count (E3 gates to 2–13/day) — quality not quantity.

**Top hypothesis for next round (detector change, not a knob tweak):** add a flow-exhaustion /
stall gate to the S5 trigger (e.g. block while `|f8_delta_z_w10|` extreme, or require last
delta bucket to roll over from peak). Secondary: require E4's `reclaimed-past-H1`; add a
re-arm cooldown to kill cascades. No defaults changed.

---

## S5 flow-exhaustion gates — implemented + first results (2026-06-14) — full results in `runs/s5-flow-gate-results.md`

Implemented the top hypothesis above as **two opt-in gates (off by default, byte-identical
baseline, TDD, 318 tests green)**: Option A `Setup5:FlowClimaxGateEnabled` (block while with-move
F8 z ≥ `MaxTriggerFlowZ`) and Option B `Setup5:FlowDecelGateEnabled` (last with-move delta bucket
dropped ≥ `ExhaustionDropRatio` below trailing peak — a Setup 1 A6 analogue). Guards in
`Setup5Guards`, wired in `TryTrigger` after E4 with funnel cols `Eflow`/`Edecel`; z via new
`FeatureEngine.DeltaZScore`. Also fixed a **pre-existing** `TradeSummaryPrinter` crash (NULL
`SUM` on an all-unresolved group → `GetInt64`; COALESCE'd, regression-tested).

**Week results (defaults except the gate flags):**

| Config | Trades | Wins | Hit | Net | Δ |
|---|---:|---:|---:|---:|---:|
| Baseline (off) | 16 | 5 | 31% | −$6,161.50 | — (byte-identical) |
| Flow-Z A (z≥10) | 12 | 4 | 33% | −$4,418.00 | +$1,743.50 (−28%) |
| Decel B (drop≥0.70) | 12 | 5 | 42% | −$3,605.50 | +$2,556.00 (−42%) |
| **Both** | **8** | **4** | **50%** | **−$1,862.00** | **+$4,299.50 (−70%)** |

**Findings:** every gate improves entry quality as predicted; **B is the stronger single gate**
(cut 4 losers, kept all 5 winners); **both together cut the loss 70%, hit rate 31%→50%**. Still
net-negative (n tiny, ≈+0.5R win cap + costs) — gates fix the diagnosed *entry-quality* ceiling;
stacking P2 exits is the separate next lever. **Caveats:** n=8–12, thresholds are uncalibrated
placeholders, gates off by default — a proper threshold sweep is the next step before any default
change.

---

## Gated S5 + P2 exit + TP-target sweep (2026-06-14) — full results in `runs/tp-target-sweep-results.md`

Stacked the two opt-in gates with **P2 exit** (`T1ExitFraction=1.0`, full exit at one target),
then swept the target distance `T1RMultiple` (config-only, ephemeral state; overfitting accepted
per user — goal is the response surface, n=7–10/week).

**Gates × P2 matrix:** baseline+P2 −$4,599 (16t/31%) · flowA+P2 −$3,168 (12t/33%) · decelB+P2
−$2,043 (12t/42%) · **both+P2 −$612 (8t/50%)**. (avg win $486 = full 1R vs $173.5 scale-out;
avg loss −$639.)

**TP-target sweep (both gates + full exit):** clean single peak at the winners' MFE cluster.
0.5R −$2,015 → 1.0R −$612 → 1.25R −$112 → **1.5R (~6t) +$388 (first net-positive ever)** →
cliff at 1.75R −$1,473 (targets stop filling, hit 50%→29%). On the rising limb the same 4
winners hit target (flat Tgt=4); avg win $236→$736. **The 1.5R optimum matches the diagnosis's
winner MFEs {5,7,8,8,12}** — target and entry agree on where the move ends.

**Takeaway:** entry gates (cut continuation losers) + full exit at ~1.5R (bank at winner MFE)
turns the week −$6,161.50 → **+$388**. Proof the setup *can* clear costs when entries and target
align. **Heavily overfit (n tiny, 5 days)** — needs out-of-sample days before trusting; gate
thresholds still placeholders (their sweep is next). No defaults changed.
