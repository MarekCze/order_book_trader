Project: Order Flow Trading Bot (rule-based v1)
What this project is
An automated futures trading bot that implements the order flow setups defined in order-flow-setups-rulebook.md (repo root — read it fully before writing any code; it is the product spec). The bot trades CME E-mini S&P 500 futures (ES) by tracking top-10 limit order book depth from market-by-price (MBP-10) data and running deterministic rule-based detectors against it.
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


Pipeline: event-driven via System.Threading.Channels. Data flows: DBN reader → book state tracker → feature engine → detectors → risk/execution → (simulator | future broker port). Detectors never know whether events are replayed or live.
Each setup is a state machine: Idle → ContextMet → Armed → OrderWorking → InPosition → Closed, with every rulebook condition (A1…E4, invalidations, time stops) implemented as an explicit, individually testable transition guard. One class per setup, sharing a common base.
Determinism is a hard requirement. Replaying the same DBN file with the same config must produce byte-identical journals. No wall-clock time anywhere in Domain/Application — all time comes from event timestamps. No unordered dictionary iteration affecting results. Randomness forbidden.

Data

Input: Databento GLBX.MDP3 dataset, mbp-10 schema, DBN binary files (zstd-compressed .dbn.zst). Symbol: ES front month via continuous symbology.
Each MBP-10 record represents one market event affecting the top 10 levels and carries BOTH the causing event (action, side, price, size) AND the resulting book state (10 levels per side with price, size, and order count). Trades arrive inline as records with action T, where side is the aggressor side — there is no separate trades feed, and no order-by-ID tracking exists in this schema.
The decoder must handle: the MBP-10 record struct (verify the exact field layout against Databento's DBN documentation — prices are fixed-precision int64 1e-9 units, timestamps uint64 UTC nanoseconds), symbol mapping records, and the R (clear) action at stream boundaries. Skip record types we don't need without crashing.
The "book builder" is now a book state tracker: it ingests each record, retains the current 10-level state it carries, and publishes two derived event types downstream — BookChanged (new top-10 state plus what caused it) and Trade (price, size, aggressor side, plus the book state immediately after). Queue/order-level information does not exist at this schema level.

Fill model (conservative by design)

Stop-market entries/exits: filled at trigger price + 1 tick adverse slippage.
Limit orders: filled ONLY when the market trades through the limit price (not on touch). No queue modeling in v1 — this deliberately biases results against us.
Commissions: configurable, default $1.40 round-turn per contract (ES retail all-in approximation). All journal P&L is net.

Rulebook interpretation rules

Every threshold in the rulebook must be a named config value (appsettings.json + typed options), never a literal in code. Defaults = the rulebook's stated values.
Session percentile baselines (e.g., A2, B3): rolling within-session distributions with a minimum sample count of 200 events before any detector may arm; before that, fall back to the prior session's final distribution if available, else detectors stay disabled. Implement this in ONE shared component.
Levels of interest (LOIs) are computed, not hand-marked: prior-day H/L, overnight H/L, prior-session POC/VAH/VAL, session LVNs, naked-POC registry (persisted across sessions in SQLite), and round numbers (multiples of 25.00 for ES). Volume profile math: volume-by-price histogram per session; POC = max bin; value area = smallest contiguous 70% expansion around POC; LVN = local minima below 25% of session mean per-price volume.
RTH calendar: ES regular session 09:30–16:00 ET (handle US DST correctly; store everything internally in UTC).
Maintenance break / pre-open (M2+): treat 17:00–18:00 ET daily as out-of-session. During the CME pre-open (~17:45–18:00 ET) orders rest without matching and the book is legitimately crossed (observed in real data: spreads to -132 ticks and beyond). Feature calculators, session percentile baselines, and detectors must not ingest book states from this window — crossed books there are market reality, not decoder errors.
Setup 3 (iceberg follow) is DISABLED in v1 — its detection conditions C1/C3 require order-level (MBO) data. Implement the detector framework so Setup 3 can be slotted in later, but do not implement it. The replenishment ratio concept (F17) IS still computable from MBP-10 (volume traded at a price vs. its displayed size over a window) and is used by Setup 1's condition A5.
Features F7 (queue position), F19 (refresh latency), and F20 (refresh size CV) are not computable from MBP-10. Journal them as nulls with a schema comment, so the journal schema doesn't change if/when MBO is adopted.
F16 (pull ratio) must be approximated: per-side, per-window, classify each displayed-size decrease at a price as traded (a trade record at that price for at least that size occurred in the same window) or cancelled (otherwise). Document this approximation in code; it is heuristic by nature at this schema level.

Journaling (this is a first-class output, not logging)
For every candidate event (detector reached Armed, whether or not it traded), persist: timestamp, setup ID, full F1–F36 feature snapshot at trigger time, the decision taken, and — if traded — entry/exit fills, MAE/MFE in ticks, exit reason (target/stop/invalidation/time/scratch), and net P&L. SQLite for storage, with a CSV and Parquet exporter. This table is the future ML training set; treat its schema as a contract.
Engineering standards

TDD where it pays: the book state tracker, feature calculators, and every detector transition guard get unit tests. The book state tracker is tested against small hand-crafted event sequences with asserted book states (golden tests).
No external service calls at runtime. No secrets in the repo, ever. Databento API usage (if any download tooling is added) reads the key from an environment variable only.
Keep PRs scoped to one milestone (see ROADMAP below). Each PR: green tests, a short summary of design decisions made, and any rulebook ambiguities encountered listed explicitly at the top of the PR description — do not silently resolve spec ambiguities.
Performance target: replay one full ES RTH session in under 5 minutes on 4 vCPUs. MBP-10 sessions are substantially smaller than MBO (millions rather than tens of millions of records); throughput numbers reported against synthetic MBO volumes in M1 must be re-measured after the MBP-10 migration. Prefer structs and spans in the hot path (decoder, book state tracker); avoid allocations per event. Don't micro-optimize elsewhere.

Roadmap (one PR per milestone)

M0 — Solution scaffold, CI (GitHub Actions: build + test), config system, domain primitives (Price, Tick, Side, timestamps).
M1 — DBN mbp-10 decoder + book state tracker with golden tests; CLI command to replay a file and print book/trade statistics as a sanity check. (Originally built against mbo; migrated in M1.5.)
M2 — Feature engine: F1–F15 (book state + flow) with rolling-window infrastructure and session percentile component.
M3 — Feature engine: F16–F36 (liquidity dynamics, footprint bars via volume-bar sampling, profile/LOI computation, naked-POC registry).
M4 — Detector framework + Setup 1 (absorption fade) end-to-end with the fill simulator, risk rules (global filters 1–6), and journaling.
M5 — Setups 2, 4, 5 (Setup 3 deferred — requires MBO).
M6 — Backtest reporting: per-setup expectancy net of costs, hit rate, MAE/MFE distributions, invalidation-vs-stop exit breakdown, equity curve; export to a markdown report + CSVs.