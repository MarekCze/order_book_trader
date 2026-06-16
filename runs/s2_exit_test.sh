#!/usr/bin/env bash
set -u
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
ROOT=/mnt/d/Projects/Trading/order_book_trader
DATA=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OF="dotnet $ROOT/src/OrderFlow.Backtest/bin/Release/net8.0/orderflow.dll"
ISO="--set Detectors:Setup1:Enabled=false --set Detectors:Setup4:Enabled=false --set Detectors:Setup5:Enabled=false"
GLOBAL="--set Risk:OpenExcludeMinutes=0 --set Risk:MaxAttemptsPerLoi=1000 --set Risk:ConsecutiveStopOutsToKillLevel=1000"
# P3g B-chain (the larger relaxed sample) + full exit; sweep the single-target distance
BCHAIN="--set Detectors:Setup2:ClimaxVolumePercentile=0.55 --set Detectors:Setup2:ClimaxShareBeyondLevel=0.30 --set Detectors:Setup2:SupplyDepthIncrease=0.10 --set Detectors:Setup2:StackedImbalanceMinLen=2 --set Detectors:Setup2:ImbalanceRatio=2.0 --set Detectors:Setup2:SweepZoneMaxTicks=6"
P2="--set Detectors:Setup2:T1ExitFraction=1.0"

summ() { python3 - "$1" "$2" <<'PY'
import sqlite3, sys, glob, os
wd, label = sys.argv[1], sys.argv[2]
t=wins=0; net=0.0; mfe=[]; mae=[]; ex={}
for db in sorted(glob.glob(os.path.join(wd,"j-*.db"))):
    c=sqlite3.connect(db)
    for npl,mf,ma,er in c.execute("SELECT net_pnl,mfe_ticks,mae_ticks,exit_reason FROM candidates WHERE setup=2 AND disposition='Traded'"):
        t+=1; net+=npl; wins+=(1 if npl>0 else 0); ex[er]=ex.get(er,0)+1
        if mf is not None: mfe.append(mf)
        if ma is not None: mae.append(ma)
    c.close()
hr=(wins/t*100) if t else 0
aw=(sum(x for x in [n for n in []]) if False else 0)
awl=[p for p in []]
exs=", ".join(f"{k}:{v}" for k,v in sorted(ex.items()))
amfe=(sum(mfe)/len(mfe)) if mfe else 0; amae=(sum(mae)/len(mae)) if mae else 0
print(f"T1R={label:5s} trades={t:3d} wins={wins:2d} hit={hr:4.1f}% net=${net:9.2f} avgMFE={amfe:4.1f}t avgMAE={amae:4.1f}t exits[{exs}]")
PY
}
run() { r=$1
  W=$ROOT/runs/artifacts/s2-exit/r$r; rm -rf "$W"; mkdir -p "$W"
  for d in 01 02 03 04 05; do
    $OF replay "$DATA/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$W/j-$d.db" \
      --set Storage:SqlitePath="$W/state.db" $ISO $GLOBAL $BCHAIN $P2 \
      --set Detectors:Setup2:T1RMultiple=$r >/dev/null 2>&1 || echo "  r$r 06-$d EXIT=$?"
  done
  summ "$W" "$r"
}
echo "=== S2 exit-geometry test (P3g sample, full exit; sweep T1RMultiple). n tiny (~6) ==="
for r in 1.0 1.5 2.0 2.5; do run $r; done
echo "=== done ==="
