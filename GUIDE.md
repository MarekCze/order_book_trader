# Running on Real Historical Data

How to replay a real Databento market-data file through the pipeline and validate the
results. As of M1.5 the output is replay statistics on the console (the trade journal
arrives in M4, backtest reports in M6) — the main purpose of this run is to **validate the
DBN decoder against real data**, which has so far only been tested against synthetic
files. Treat this as a gate before relying on any downstream results.

## 1. Prerequisites

- .NET 8 SDK (`dotnet --version` → 8.x)
- A Databento historical export (see below)

Build once from the repo root:

```bash
dotnet build -c Release
```

## 2. Getting the data

The bot reads **DBN files** (Databento Binary Encoding), Databento's default export
format, either raw (`.dbn`) or zstd-compressed (`.dbn.zst`) — compression is detected by
file content, not extension, so both work as-is. **Do not export CSV/JSON/Parquet; the
decoder only reads DBN.**

When requesting the data (web portal or API), select:

| Setting | Value |
|---|---|
| Dataset | `GLBX.MDP3` (CME Globex) |
| Schema | `mbp-10` |
| Symbol | `ES.c.0` (front month via continuous symbology) |
| Encoding | DBN, zstd compression |
| Time range | One full session is ideal; start small (e.g. one RTH hour) if cost matters |

Notes:

- A single ES RTH session of mbp-10 is single-digit millions of records — small enough
  to validate in seconds.
- DBN versions 1–3 are all supported.
- If using the Databento API for download tooling, the key must come from an environment
  variable (e.g. `DATABENTO_API_KEY`) — never commit keys to the repo (CLAUDE.md rule).
- Keep data files out of git: `data/`, `*.dbn`, and `*.dbn.zst` are already in
  `.gitignore`. A `data/` directory in the repo root is the conventional spot.

## 3. Run the replay

```bash
dotnet run --project src/OrderFlow.Backtest -c Release -- \
    replay data/es-session.dbn.zst --stats
```

Options:

- `--tick-size 0.25` — override the instrument tick size (defaults to ES = 0.25 from
  `src/OrderFlow.Backtest/appsettings.json`).

Expected output shape:

```
File:     data/es-session.dbn.zst
DBN:      v2, dataset GLBX.MDP3, schema mbp-10, symbols [ES.c.0]
Window:   2026-01-05T14:30:00.0000000Z -> 2026-01-05T21:00:00.0000000Z
Tick:     0.25

Event counts by kind:
  BookChanged          x,xxx,xxx
  Trade                  xxx,xxx

Instrument 12345:
  trades                  xxx,xxx
  volume                x,xxx,xxx
  buy volume              xxx,xxx  (aggressor bought)
  sell volume             xxx,xxx  (aggressor sold)
  session high            xxxx.xx
  session low             xxxx.xx
  spread ticks    min 1 / max x (x,xxx,xxx two-sided samples)
  final book      10 bid / 10 ask levels, BBO xxxx.xx x xxxx.xx

Replayed x,xxx,xxx events (x non-MBP-10 records skipped) in x.xxs = xxx,xxx events/s
```

## 4. Validation checklist

Compare the output against an independent reference (the Databento portal's session
summary, or your charting platform's daily stats for the same contract and window):

1. **Trade count and total volume** match the reference values for the time range.
2. **Session high / low** match exactly (these come from `T` records, so any price-field
   misalignment shows up here immediately).
3. **Buy volume + sell volume ≈ total volume.** A large residual means many trades carry
   side `N`, which would matter for delta features in M2.
4. **Min spread = 1 tick**, and the overwhelming majority of two-sided samples should be
   at 1 tick for ES in RTH. A nonsensical min/max spread is the clearest symptom of a
   wrong level-array offset in the decoder.
5. **Final book BBO** is a plausible price near the session close.
6. **Skipped / ignored counts are explainable.** Skipped rtypes like `0x16`
   (symbol mapping) or `0x17` (system) in small numbers are normal. Thousands of
   "Ignored 'N'/'F'/unknown actions" are not — report that.
7. **No `DBN format error`** over the full file.
8. **Throughput**: a full RTH session should replay in well under the 5-minute target
   (expect tens of seconds).
9. **Determinism**: run the replay twice and diff the output (excluding the final
   timing line) — it must be byte-identical:

   ```bash
   dotnet run --project src/OrderFlow.Backtest -c Release -- replay data/es-session.dbn.zst --stats | grep -v "events/s" > /tmp/run1.txt
   dotnet run --project src/OrderFlow.Backtest -c Release -- replay data/es-session.dbn.zst --stats | grep -v "events/s" > /tmp/run2.txt
   diff /tmp/run1.txt /tmp/run2.txt && echo OK
   ```

## 5. Known decoder assumptions to watch

These were implemented from the documented DBN layout but have not yet been confirmed
against a real file (full list in the M1.5 PR description):

- Empty level slots are assumed to be `UNDEF_PRICE` (i64::MAX) with zero size/count.
- `T` (trade) records are assumed to carry the book state *immediately after* the trade.
- Snapshot-flagged records are assumed to form one contiguous run at stream start.

If any checklist item above fails, suspect one of these first.

## 6. Troubleshooting

| Symptom | Likely cause |
|---|---|
| `Missing DBN magic bytes` | Not a DBN file — re-export as DBN (CSV/JSON/Parquet are not supported) |
| `schema raw=N` (not `mbp-10`) in the header line | Wrong schema exported — request `mbp-10`; an `mbo` file (schema 0) will decode 0 events since all its records are rtype `0xA0` |
| Every record skipped, all counted under one rtype | Schema mismatch (see above) — the skipped rtype tells you what the file actually contains |
| `DBN format error: ... truncated` | Incomplete download — re-fetch the file |
| Spread/high/low look insane | Decoder layout bug — capture the output and the file's first records, and file an issue |

## 7. What you do NOT get yet

No trades are simulated and nothing is persisted. Detectors, risk rules, fill simulation
and the SQLite/CSV/Parquet trade journal arrive in M4; per-setup expectancy reports and
equity curves in M6. Until then, replay output is diagnostic only.
