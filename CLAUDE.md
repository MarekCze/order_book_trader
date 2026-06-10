Project: Order Flow Trading Bot (rule-based v1)
What this project is
An automated futures trading bot that implements the five order flow setups defined in order-flow-setups-rulebook.md (repo root — read it fully before writing any code; it is the product spec). The bot trades CME E-mini S&P 500 futures (ES) by reconstructing the limit order book from market-by-order (MBO) data and running deterministic rule-based detectors against it.
This phase is the rule-based bot ONLY. Part 2 of the rulebook (ML features and labels) is NOT in scope for implementation, with one deliberate exception: the feature engine must compute and journal the features F1–F36 defined there, because the bot's trade journal doubles as the training-label dataset for a later ML phase. Do not build any model training, inference, or Python ML code in this repo.
Operating modes

Backtest (build this first): replay historical Databento DBN files through the full pipeline; a simulator resolves fills.
Live (interface only for now): the execution layer is a port/interface. Implement only the simulator adapter in v1. Do not implement a broker adapter yet.

Architecture (fixed decisions — do not relitigate)

.NET 8, C#, single solution, Clean Architecture:

OrderFlow.Domain — pure logic, zero external dependencies: order book model, market events, feature calculators, setup detector state machines, risk rules, fill model abstractions.
OrderFlow.Application — pipeline orchestration, session lifecycle, journaling.
OrderFlow.Infrastructure — DBN file reader, journal persistence (SQLite + CSV/Parquet export), config.
OrderFlow.Backtest — CLI host: replay engine, fill simulator, results reporting.
OrderFlow.Tests — xUnit.


Pipeline: event-driven via System.Threading.Channels. Data flows: DBN reader → book builder → feature engine → detectors → risk/execution → (simulator | future broker port). Detectors never know whether events are replayed or live.
Each setup is a state machine: Idle → ContextMet → Armed → OrderWorking → InPosition → Closed, with every rulebook condition (A1…E4, invalidations, time stops) implemented as an explicit, individually testable transition guard. One class per setup, sharing a common base.
Determinism is a hard requirement. Replaying the same DBN file with the same config must produce byte-identical journals. No wall-clock time anywhere in Domain/Application — all time comes from event timestamps. No unordered dictionary iteration affecting results. Randomness forbidden.

Data

Input: Databento GLBX.MDP3 dataset, mbo schema, DBN v2/v3 binary files (zstd-compressed .dbn.zst). Symbol: ES front month via continuous symbology.
Implement a native C# DBN decoder for the record types we need (MboMsg, symbol mapping, system/clearing records can be skipped). The DBN format is publicly documented by Databento; records are fixed-size little-endian structs. Prices are fixed-precision int64 (1e-9 units); timestamps are uint64 nanoseconds since UNIX epoch.
Book builder must handle: snapshot records at stream start (flagged), add/cancel/modify/fill/trade actions, and the R (clear/reset) action. Maintain orders by ID; derive MBP aggregates (top 10 levels per side) on demand for the feature engine.

Fill model (conservative by design)

Stop-market entries/exits: filled at trigger price + 1 tick adverse slippage.
Limit orders: filled ONLY when the market trades through the limit price (not on touch). No queue modeling in v1 — this deliberately biases results against us.
Commissions: configurable, default $1.40 round-turn per contract (ES retail all-in approximation). All journal P&L is net.

Rulebook interpretation rules

Every threshold in the rulebook must be a named config value (appsettings.json + typed options), never a literal in code. Defaults = the rulebook's stated values.
Session percentile baselines (e.g., A2, B3): rolling within-session distributions with a minimum sample count of 200 events before any detector may arm; before that, fall back to the prior session's final distribution if available, else detectors stay disabled. Implement this in ONE shared component.
Levels of interest (LOIs) are computed, not hand-marked: prior-day H/L, overnight H/L, prior-session POC/VAH/VAL, session LVNs, naked-POC registry (persisted across sessions in SQLite), and round numbers (multiples of 25.00 for ES). Volume profile math: volume-by-price histogram per session; POC = max bin; value area = smallest contiguous 70% expansion around POC; LVN = local minima below 25% of session mean per-price volume.
RTH calendar: ES regular session 09:30–16:00 ET (handle US DST correctly; store everything internally in UTC).

Journaling (this is a first-class output, not logging)
For every candidate event (detector reached Armed, whether or not it traded), persist: timestamp, setup ID, full F1–F36 feature snapshot at trigger time, the decision taken, and — if traded — entry/exit fills, MAE/MFE in ticks, exit reason (target/stop/invalidation/time/scratch), and net P&L. SQLite for storage, with a CSV and Parquet exporter. This table is the future ML training set; treat its schema as a contract.
Engineering standards

TDD where it pays: the book builder, feature calculators, and every detector transition guard get unit tests. Book builder is tested against small hand-crafted event sequences with asserted book states (golden tests).
No external service calls at runtime. No secrets in the repo, ever. Databento API usage (if any download tooling is added) reads the key from an environment variable only.
Keep PRs scoped to one milestone (see ROADMAP below). Each PR: green tests, a short summary of design decisions made, and any rulebook ambiguities encountered listed explicitly at the top of the PR description — do not silently resolve spec ambiguities.
Performance target: replay one full ES RTH session (tens of millions of MBO events) in under 5 minutes on 4 vCPUs. Prefer structs and spans in the hot path (decoder, book builder); avoid allocations per event. Don't micro-optimize elsewhere.

Roadmap (one PR per milestone)

M0 — Solution scaffold, CI (GitHub Actions: build + test), config system, domain primitives (Price, Tick, Side, timestamps).
M1 — DBN decoder + book builder with golden tests; CLI command to replay a file and print book/trade statistics as a sanity check.
M2 — Feature engine: F1–F15 (book state + flow) with rolling-window infrastructure and session percentile component.
M3 — Feature engine: F16–F36 (liquidity dynamics, footprint bars via volume-bar sampling, profile/LOI computation, naked-POC registry).
M4 — Detector framework + Setup 1 (absorption fade) end-to-end with the fill simulator, risk rules (global filters 1–6), and journaling.
M5 — Setups 2–5.
M6 — Backtest reporting: per-setup expectancy net of costs, hit rate, MAE/MFE distributions, invalidation-vs-stop exit breakdown, equity curve; export to a markdown report + CSVs.