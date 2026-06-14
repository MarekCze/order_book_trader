#!/usr/bin/env bash
set -u
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
ROOT=/mnt/d/Projects/Trading/order_book_trader
DATA=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OF="dotnet $ROOT/src/OrderFlow.Backtest/bin/Release/net8.0/orderflow.dll"
# both gates ON, full exit at the single target; sweep the target distance T1RMultiple
GATES="--set Detectors:Setup5:FlowClimaxGateEnabled=true --set Detectors:Setup5:FlowDecelGateEnabled=true --set Detectors:Setup5:T1ExitFraction=1.0"

summarize() {
  python3 - "$1" "$2" <<'PY'
import sqlite3, sys, glob, os
wd, label = sys.argv[1], sys.argv[2]
t=w=0; net=0.0; wins=[]; losses=[]; exits={}
for db in sorted(glob.glob(os.path.join(wd,"j-*.db"))):
    c=sqlite3.connect(db)
    for npl, er in c.execute("SELECT net_pnl, exit_reason FROM candidates WHERE disposition='Traded'"):
        t+=1; net+=npl
        (wins if npl>0 else losses).append(npl)
        exits[er]=exits.get(er,0)+1
    c.close()
w=len(wins); hr=(w/t*100) if t else 0
aw=(sum(wins)/len(wins)) if wins else 0; al=(sum(losses)/len(losses)) if losses else 0
exs=", ".join(f"{k}:{v}" for k,v in sorted(exits.items()))
print(f"T1R={label:5s} trades={t:3d} wins={w:2d} hit={hr:4.1f}% net=${net:9.2f} avgWin=${aw:7.2f} avgLoss=${al:8.2f} | exits[{exs}]")
PY
}

run() { r=$1
  W=$ROOT/runs/artifacts/tp-sweep/r$r; rm -rf "$W"; mkdir -p "$W"
  for d in 01 02 03 04 05; do
    $OF replay "$DATA/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$W/j-$d.db" \
      --set Storage:SqlitePath="$W/state.db" $GATES --set Detectors:Setup5:T1RMultiple=$r >/dev/null 2>&1 || echo "  r$r 06-$d EXIT=$?"
  done
  summarize "$W" "$r"
}

echo "=== TP-target sweep (both gates + full exit; sweep T1RMultiple) ==="
for r in 0.5 0.75 1.0 1.25 1.5 1.75 2.0 2.25 2.5 2.75 3.0; do run $r; done
echo "=== done ==="
