#!/usr/bin/env bash
# Item 4 — threshold-scaling sweep for Setups 1 and 4 ONLY.
# Applies a single multiplicative factor to each setup's volume/ratio thresholds at the
# binding walls the funnel telemetry identified (S1 A4 = StallVolumeMultiple; S4 D2 =
# DepthDeclineFraction + PullRatioMin) and reports how the A4/D2 passed counts and the
# per-setup candidate counts respond. Goal: find where they BEGIN to fire — not to optimize.
# Diagnostic only: ephemeral state, no config files edited (all via --set).
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
DIR=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OUT=runs/artifacts/sweep
rm -rf "$OUT"; mkdir -p "$OUT"
RAW="$OUT/funnel_lines.txt"
: > "$RAW"

# Defaults: S1 StallVolumeMultiple=3.0 ; S4 DepthDeclineFraction=0.40 ; S4 PullRatioMin=1.5
for f in 1.0 0.8 0.6 0.4; do
  svm=$(awk "BEGIN{printf \"%.4g\", 3.0*$f}")
  ddf=$(awk "BEGIN{printf \"%.4g\", 0.40*$f}")
  prm=$(awk "BEGIN{printf \"%.4g\", 1.5*$f}")
  echo ">>> factor=$f  S1.StallVolumeMultiple=$svm  S4.DepthDeclineFraction=$ddf  S4.PullRatioMin=$prm"
  for d in 01 02 03 04 05; do
    dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- \
      replay "$DIR/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$OUT/j.db" \
      --set Detectors:Setup1:StallVolumeMultiple=$svm \
      --set Detectors:Setup4:DepthDeclineFraction=$ddf \
      --set Detectors:Setup4:PullRatioMin=$prm 2>&1 \
      | grep -E "Funnel \[.* (AbsorptionFade|LvnVacuum) " \
      | sed "s/^/factor=$f day=$d /" >> "$RAW"
  done
done

echo "===== SWEEP SUMMARY (summed over the 5-day week, both directions) ====="
python3 - "$RAW" <<'PY'
import re, sys, collections
rows = collections.defaultdict(lambda: collections.defaultdict(int))
pat_f = re.compile(r"factor=(\S+)")
pat_setup = re.compile(r"(AbsorptionFade|LvnVacuum)")
pat_cand = re.compile(r"candidates ([\d,]+)")
pat_a4 = re.compile(r"A4 ([\d,]+)/([\d,]+)")
pat_d2 = re.compile(r"D2 ([\d,]+)/([\d,]+)")
def num(s): return int(s.replace(",", ""))
for line in open(sys.argv[1]):
    f = pat_f.search(line).group(1)
    setup = pat_setup.search(line).group(1)
    cand = pat_cand.search(line)
    rows[f][f"{setup}_cand"] += num(cand.group(1)) if cand else 0
    if setup == "AbsorptionFade":
        m = pat_a4.search(line)
        if m: rows[f]["A4_pass"] += num(m.group(1)); rows[f]["A4_eval"] += num(m.group(2))
    else:
        m = pat_d2.search(line)
        if m: rows[f]["D2_pass"] += num(m.group(1)); rows[f]["D2_eval"] += num(m.group(2))
print(f"{'factor':>6} | {'S1 A4 pass/eval':>20} {'S1 cand':>8} | {'S4 D2 pass/eval':>22} {'S4 cand':>8}")
print("-"*72)
for f in ["1.0","0.8","0.6","0.4"]:
    r = rows[f]
    a4 = f"{r['A4_pass']:,}/{r['A4_eval']:,}"
    d2 = f"{r['D2_pass']:,}/{r['D2_eval']:,}"
    print(f"{f:>6} | {a4:>20} {r['AbsorptionFade_cand']:>8,} | {d2:>22} {r['LvnVacuum_cand']:>8,}")
PY
