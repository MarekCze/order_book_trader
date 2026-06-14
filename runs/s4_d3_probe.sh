#!/usr/bin/env bash
set -u
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
ROOT=/mnt/d/Projects/Trading/order_book_trader
DATA=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OF="dotnet $ROOT/src/OrderFlow.Backtest/bin/Release/net8.0/orderflow.dll"
ISO="--set Detectors:Setup1:Enabled=false --set Detectors:Setup2:Enabled=false --set Detectors:Setup5:Enabled=false"
# fix D2 at a firing point (DDF0.30/PRM0.8); probe whether stronger confirming aggressor flow (D3)
# carves out a sub-population where MFE > MAE.
D2="--set Detectors:Setup4:DepthDeclineFraction=0.30 --set Detectors:Setup4:PullRatioMin=0.8"

summ() { python3 - "$1" "$2" <<'PY'
import sqlite3, sys, glob, os
wd, label = sys.argv[1], sys.argv[2]
cand=t=0; net=0.0; wins=0; mfe=[]; mae=[]
for db in sorted(glob.glob(os.path.join(wd,"j-*.db"))):
    c=sqlite3.connect(db)
    cand += c.execute("SELECT COUNT(*) FROM candidates WHERE setup=4").fetchone()[0]
    for npl, mf, ma in c.execute("SELECT net_pnl, mfe_ticks, mae_ticks FROM candidates WHERE setup=4 AND disposition='Traded'"):
        t+=1; net+=npl; wins+=(1 if npl>0 else 0)
        if mf is not None: mfe.append(mf)
        if ma is not None: mae.append(ma)
    c.close()
hr=(wins/t*100) if t else 0
amfe=(sum(mfe)/len(mfe)) if mfe else 0; amae=(sum(mae)/len(mae)) if mae else 0
edge="MFE>MAE" if amfe>amae else "MFE<MAE"
print(f"{label:18s} cand={cand:6d} trades={t:4d} wins={wins:3d} hit={hr:4.1f}% net=${net:10.2f} avgMFE={amfe:4.1f}t avgMAE={amae:4.1f}t [{edge}]")
PY
}
run() { mad=$1
  W=$ROOT/runs/artifacts/s4-d3/mad${mad}; rm -rf "$W"; mkdir -p "$W"
  for d in 01 02 03 04 05; do
    $OF replay "$DATA/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$W/j-$d.db" \
      --set Storage:SqlitePath="$W/state.db" $ISO $D2 \
      --set Detectors:Setup4:MinAlignedDeltaContracts=$mad >/dev/null 2>&1 || echo "  mad$mad 06-$d EXIT=$?"
  done
  summ "$W" "MinAlignedDelta$mad"
}
echo "=== S4 D3 probe (D2 fixed DDF0.30/PRM0.8; sweep MinAlignedDeltaContracts) ==="
for mad in 100 200 400 800 1500; do run $mad; done
echo "=== done ==="
