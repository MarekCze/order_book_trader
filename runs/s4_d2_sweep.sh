#!/usr/bin/env bash
set -u
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
ROOT=/mnt/d/Projects/Trading/order_book_trader
DATA=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OF="dotnet $ROOT/src/OrderFlow.Backtest/bin/Release/net8.0/orderflow.dll"
# isolate S4: disable the other active setups so S4 isn't starved of the one-position slot
ISO="--set Detectors:Setup1:Enabled=false --set Detectors:Setup2:Enabled=false --set Detectors:Setup5:Enabled=false"

summ() {  # $1=workdir $2=label(DDF/PRM)
  python3 - "$1" "$2" <<'PY'
import sqlite3, sys, glob, os
wd, label = sys.argv[1], sys.argv[2]
cand=t=0; net=0.0; wins=0; mfe=[]; mae=[]
for db in sorted(glob.glob(os.path.join(wd,"j-*.db"))):
    c=sqlite3.connect(db)
    cand += c.execute("SELECT COUNT(*) FROM candidates WHERE setup=4").fetchone()[0]
    for npl, mf, ma in c.execute("SELECT net_pnl, mfe_ticks, mae_ticks FROM candidates WHERE setup=4 AND disposition='Traded'"):
        t+=1; net+=npl; wins+= (1 if npl>0 else 0)
        if mf is not None: mfe.append(mf)
        if ma is not None: mae.append(ma)
    c.close()
hr=(wins/t*100) if t else 0
amfe=(sum(mfe)/len(mfe)) if mfe else 0; amae=(sum(mae)/len(mae)) if mae else 0
print(f"{label:14s} cand={cand:6d} trades={t:4d} wins={wins:4d} hit={hr:4.1f}% net=${net:10.2f} avgMFE={amfe:4.1f}t avgMAE={amae:4.1f}t")
PY
}

run() { ddf=$1; prm=$2
  W=$ROOT/runs/artifacts/s4-d2/d${ddf}_p${prm}; rm -rf "$W"; mkdir -p "$W"
  for d in 01 02 03 04 05; do
    $OF replay "$DATA/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$W/j-$d.db" \
      --set Storage:SqlitePath="$W/state.db" $ISO \
      --set Detectors:Setup4:DepthDeclineFraction=$ddf --set Detectors:Setup4:PullRatioMin=$prm \
      >/dev/null 2>&1 || echo "  d$ddf/p$prm 06-$d EXIT=$?"
  done
  summ "$W" "DDF$ddf/PRM$prm"
}

echo "=== S4 D2 fine-grid (S4 isolated; D3 MinAlignedDelta=default 100) ==="
echo "    rulebook default is DDF0.40/PRM1.5 (=0 cand). cliff between x0.8 and x0.6."
for ddf in 0.34 0.30 0.26 0.22; do
  for prm in 1.2 1.0 0.8; do run $ddf $prm; done
done
echo "=== done ==="
