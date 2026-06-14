#!/usr/bin/env bash
# Experiment — invert trade direction (SL <-> TP). Detection is unchanged (same signals fire),
# but every trade is taken on the OPPOSITE side via the new `Risk:InvertDirection` flag
# (ExecSign mirrors entry side / brackets / R / P&L; default off = byte-identical). Default
# entry + exit config otherwise. Not a fine-tuning run — a "does doing the opposite win?" test.
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
DIR=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
A=runs/artifacts/inverted; rm -rf "$A"; mkdir -p "$A"; STATE="$A/state.db"
for d in 01 02 03 04 05; do
  echo "===== DAY 2026-06-$d ====="
  dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- \
    replay "$DIR/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$A/j-$d.db" \
    --set Storage:SqlitePath="$STATE" --set Risk:InvertDirection=true 2>&1 | grep -E "setup [0-9]"
done
python3 runs/merge_journals.py "$A/week.db" "$A"/j-0*.db
dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- report "$A/week.db" \
  --out "$A/week.report.md" --csv-dir "$A/csv" --title "Inverted direction (SL<->TP) week" 2>&1 | grep "Report:"
