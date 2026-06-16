#!/usr/bin/env bash
set -u
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
ROOT=/mnt/d/Projects/Trading/order_book_trader
DATA=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OF="dotnet $ROOT/src/OrderFlow.Backtest/bin/Release/net8.0/orderflow.dll"
ISO="--set Detectors:Setup1:Enabled=false --set Detectors:Setup4:Enabled=false --set Detectors:Setup5:Enabled=false"
# relax the global filters that block 95% of S2 candidates, to FORCE a measurable sample
# (spread filter conflicts with S2's premise; lift session/attempt caps). Measurement device only.
GLOBAL="--set Risk:OpenExcludeMinutes=0 --set Risk:MaxAttemptsPerLoi=1000 --set Risk:ConsecutiveStopOutsToKillLevel=1000"

summ() { python3 - "$1" "$2" <<'PY'
import sqlite3, sys, glob, os
wd, label = sys.argv[1], sys.argv[2]
cand=t=wins=0; net=0.0; mfe=[]; mae=[]
for db in sorted(glob.glob(os.path.join(wd,"j-*.db"))):
    c=sqlite3.connect(db)
    cand+=c.execute("SELECT COUNT(*) FROM candidates WHERE setup=2").fetchone()[0]
    for npl,mf,ma in c.execute("SELECT net_pnl,mfe_ticks,mae_ticks FROM candidates WHERE setup=2 AND disposition='Traded'"):
        t+=1; net+=npl; wins+=(1 if npl>0 else 0)
        if mf is not None: mfe.append(mf)
        if ma is not None: mae.append(ma)
    c.close()
hr=(wins/t*100) if t else 0
amfe=(sum(mfe)/len(mfe)) if mfe else 0; amae=(sum(mae)/len(mae)) if mae else 0
edge=("MFE>MAE" if amfe>amae else "MFE<MAE") if t else "-"
print(f"{label:18s} cand={cand:5d} trades={t:4d} wins={wins:3d} hit={hr:4.1f}% net=${net:9.2f} avgMFE={amfe:4.1f}t avgMAE={amae:4.1f}t [{edge}]")
PY
}
run() { name=$1; shift
  W=$ROOT/runs/artifacts/s2-unblock/$name; rm -rf "$W"; mkdir -p "$W"
  for d in 01 02 03 04 05; do
    $OF replay "$DATA/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$W/j-$d.db" \
      --set Storage:SqlitePath="$W/state.db" $ISO $GLOBAL "$@" >/dev/null 2>&1 || echo "  $name 06-$d EXIT=$?"
  done
  summ "$W" "$name"
}
B() { echo "--set Detectors:Setup2:ClimaxVolumePercentile=$1 --set Detectors:Setup2:ClimaxShareBeyondLevel=$2 --set Detectors:Setup2:SupplyDepthIncrease=$3 --set Detectors:Setup2:StackedImbalanceMinLen=$4 --set Detectors:Setup2:ImbalanceRatio=$5 --set Detectors:Setup2:SweepZoneMaxTicks=$6"; }
echo "=== S2 measurement run (B-chain loosened + global filters relaxed; spread up to 8t) ==="
run P2g_moderate   $(B 0.70 0.40 0.20 2 2.5 6)
run P3g_aggressive $(B 0.55 0.30 0.10 2 2.0 6)
echo "=== done ==="
