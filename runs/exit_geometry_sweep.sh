#!/usr/bin/env bash
# Stage 1 — exit-geometry sweep (config-only, no code, no default changes).
# DEFAULT entry conditions (so we measure on the real setups — in practice S5, the only one
# that trades at default); only the exit geometry varies, applied to S1/S2/S5 (S4 is
# single-target and has none of these knobs, so the overrides are no-ops for it).
# For each profile: run the 5-day week (chained state), merge journals, run the M6 report,
# and print one comparison row vs the iteration-2 baseline (16 trades, -$6,162).
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
DIR=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OUT=runs/artifacts/exitgeo
rm -rf "$OUT"; mkdir -p "$OUT"

# Apply a list of "field=value" exit knobs to S1, S2 and S5.
profile_overrides() {
  local out=()
  for kv in "$@"; do
    out+=(--set "Detectors:Setup1:$kv" --set "Detectors:Setup2:$kv" --set "Detectors:Setup5:$kv")
  done
  printf '%s\n' "${out[@]}"
}

run_profile() {
  local name="$1"; shift
  local pdir="$OUT/$name"; mkdir -p "$pdir"
  local state="$pdir/state.db"
  mapfile -t ov < <(profile_overrides "$@")
  for d in 01 02 03 04 05; do
    dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- \
      replay "$DIR/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$pdir/j-$d.db" \
      --set Storage:SqlitePath="$state" "${ov[@]}" >/dev/null 2>&1
  done
  python3 runs/merge_journals.py "$pdir/week.db" "$pdir"/j-0*.db >/dev/null
  local rep
  rep=$(dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- \
        report "$pdir/week.db" --out "$pdir/report.md" --csv-dir "$pdir/csv" 2>&1 | grep "Report:")
  # avg win + target-exit count straight from the merged journal
  local extra
  extra=$(sqlite3 "$pdir/week.db" "SELECT
      'avgWin $'||COALESCE(ROUND(AVG(CASE WHEN net_pnl>0 THEN net_pnl END),1),0)||
      ' | targets '||SUM(exit_reason='Target')||
      ' | T1hit '||SUM(t1_filled=1)
    FROM candidates WHERE disposition='Traded';")
  printf '%-22s %s | %s\n' "$name" "${rep#Report:  }" "$extra"
}

echo "===== EXIT-GEOMETRY SWEEP (default entry; exit knobs on S1/S2/S5) ====="
echo "baseline iter2 was: 16 trades, 5W/11L, net -\$6161.50, expectancy -\$385.09, maxDD \$6161.50"
echo "------------------------------------------------------------------------"
run_profile "P0-baseline"
run_profile "P1-earlyT1-T2x2"  T1RMultiple=0.5 T1ExitFraction=0.5 T2RCap=2.0 BreakevenOffsetTicks=0
run_profile "P2-single-1R"     T1RMultiple=1.0 T1ExitFraction=1.0
run_profile "P3-single-0.5R"   T1RMultiple=0.5 T1ExitFraction=1.0
run_profile "P4-lockrunner"    T1RMultiple=1.0 T1ExitFraction=0.5 T2RCap=1.5 BreakevenOffsetTicks=-1
echo "------------------------------------------------------------------------"
echo "(report.md + csv per profile under $OUT/<profile>/)"
