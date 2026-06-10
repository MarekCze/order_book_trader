# Order Flow Trading Bot

Rule-based automated futures trading bot for CME ES, reconstructing the limit order book
from Databento MBO data and running deterministic order-flow setup detectors against it.

- Product spec: [`order-flow-setups-rulebook.md`](order-flow-setups-rulebook.md)
- Architecture & roadmap: [`CLAUDE.md`](CLAUDE.md)

## Build & test

```bash
dotnet build
dotnet test
```

## CLI

```bash
# Replay a Databento DBN mbo file (zstd or raw) and print sanity statistics
dotnet run --project src/OrderFlow.Backtest -- replay data/es-session.dbn.zst --stats

# Generate a deterministic synthetic MBO file (for benchmarks / smoke tests)
dotnet run --project src/OrderFlow.Backtest -- synth /tmp/synth.dbn.zst --events 1000000 --seed 42
```

## Solution layout

| Project | Role |
|---|---|
| `OrderFlow.Domain` | Pure logic, zero dependencies: primitives, market events, order book |
| `OrderFlow.Application` | Pipeline orchestration (Channels), book builder stage |
| `OrderFlow.Infrastructure` | DBN decoder/writer, config |
| `OrderFlow.Backtest` | CLI host (`orderflow`), stats, synthetic data generator |
| `OrderFlow.Tests` | xUnit: golden book tests, property tests, codec round-trips |
