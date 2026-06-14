#!/usr/bin/env bash
set -u
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
ROOT=/mnt/d/Projects/Trading/order_book_trader
DATA=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OF="dotnet $ROOT/src/OrderFlow.Backtest/bin/Release/net8.0/orderflow.dll"
P2="--set Detectors:Setup5:T1ExitFraction=1.0 --set Detectors:Setup5:T1RMultiple=1.0"

summarize() {  # $1=workdir $2=label
  python3 - "$1" "$2" <<'PY'
import sqlite3, sys, glob, os
wd, label = sys.argv[1], sys.argv[2]
t=w=0; net=0.0; wins=[]; losses=[]; exits={}
for db in sorted(glob.glob(os.path.join(wd,"j-*.db"))):
    c=sqlite3.connect(db); cur=c.cursor()
    for npl, er in cur.execute("SELECT net_pnl, exit_reason FROM candidates WHERE disposition='Traded'"):
        t+=1; net+=npl
        if npl>0: w+=1; wins.append(npl)
        else: losses.append(npl)
        exits[er]=exits.get(er,0)+1
    c.close()
hr = (w/t*100) if t else 0
aw = (sum(wins)/len(wins)) if wins else 0
al = (sum(losses)/len(losses)) if losses else 0
exs = ", ".join(f"{k}:{v}" for k,v in sorted(exits.items()))
print(f"{label:18s} trades={t:3d} wins={w:2d} hit={hr:4.1f}% net=${net:9.2f} avgWin=${aw:7.2f} avgLoss=${al:8.2f} | exits[{exs}]")
PY
}

run() { name=$1; shift
  W=$ROOT/runs/artifacts/p2-gated/$name; rm -rf "$W"; mkdir -p "$W"
  for d in 01 02 03 04 05; do
    $OF replay "$DATA/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$W/j-$d.db" \
      --set Storage:SqlitePath="$W/state.db" "$@" >/dev/null 2>&1 || echo "  06-$d EXIT=$?"
  done
  summarize "$W" "$name"
}

echo "=== gated setups WITH P2 exit (full exit at 1R) ==="
run baseline_p2 $P2
run flowA_p2    $P2 --set Detectors:Setup5:FlowClimaxGateEnabled=true
run decelB_p2   $P2 --set Detectors:Setup5:FlowDecelGateEnabled=true
run both_p2     $P2 --set Detectors:Setup5:FlowClimaxGateEnabled=true --set Detectors:Setup5:FlowDecelGateEnabled=true
echo "=== done ==="
