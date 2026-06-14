#!/usr/bin/env bash
# Deliberately-permissive "many entrances" run.
# Opens each silent setup's BINDING-WALL conditions only (S1 A4/A5/A6, S2 B3/B5, S4 D2/D3) and
# lifts the filter-5 re-entry caps, so the bot fires a large stream of trades. Context conditions
# (A1/A2, B1/B2, D1, E*) and global filters 1/2/3/4/6 stay at rulebook defaults; S5 is untouched
# (it already trades). This is a volume / plumbing diagnostic — P&L is noise by construction.
# No appsettings defaults are changed; everything is a per-run --set override.
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
DIR=/mnt/d/Projects/Trading/databento/data/GLBX.MDP3/mbp-10/ES
OUT=runs/artifacts/permissive
rm -rf "$OUT"; mkdir -p "$OUT"
STATE="$OUT/state.db"

# Binding-walls-open profile, MODERATED to the iteration-1 sweep's firing levels so the run is
# tractable. A maximally-open profile (D2/D3 set to always-pass) was degenerate: it emitted a
# candidate on essentially every event D1 held (millions/day), flooding the journal (68 MB for
# one day, no end). These levels open each binding wall clearly while keeping candidate volume
# in the tens-of-thousands/week. (See runs/tuning-iteration-1-results.md for the funnel/sweep basis.)
OVERRIDES=(
  # S1 — A4 volume-without-progress, A5 replenishment, A6 exhaustion-drop
  --set Detectors:Setup1:StallVolumeMultiple=0.5
  --set Detectors:Setup1:ReplenishRatioMin=0.5
  --set Detectors:Setup1:RefreshCountMin=1
  --set Detectors:Setup1:ExhaustionDropRatio=0.3
  # S2 — B3 climax, B5 supply confirm
  --set Detectors:Setup2:ClimaxVolumePercentile=0.3
  --set Detectors:Setup2:ClimaxShareBeyondLevel=0.3
  --set Detectors:Setup2:SupplyDepthIncrease=0.1
  --set Detectors:Setup2:StackedImbalanceMinLen=2
  # S4 — D2 depth-decline + pull-ratio (sweep firing level), D3 aggressor alignment
  --set Detectors:Setup4:DepthDeclineFraction=0.24
  --set Detectors:Setup4:PullRatioMin=0.9
  --set Detectors:Setup4:MinAlignedDeltaContracts=0
  # Filter 5 — lift per-LOI attempt cap + dead-level rule so a level can re-fire
  --set Risk:MaxAttemptsPerLoi=1000
  --set Risk:ConsecutiveStopOutsToKillLevel=1000
)

for d in 01 02 03 04 05; do
  echo "===================== DAY 2026-06-$d ====================="
  dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- \
    replay "$DIR/2026-06-$d.mbp-10.dbn.zst" --trade --journal "$OUT/j-$d.db" \
    --set Storage:SqlitePath="$STATE" "${OVERRIDES[@]}" 2>&1 \
    | grep -E "Funnel|setup [0-9]|Vol gate|Swings"
done

echo "===================== WEEK REPORT ====================="
python3 runs/merge_journals.py "$OUT/week.db" "$OUT"/j-0*.db
dotnet run --project src/OrderFlow.Backtest -c Release --no-build -- report "$OUT/week.db" \
  --out "$OUT/week.report.md" --csv-dir "$OUT/csv" --title "Permissive run — binding walls open"
echo "===== REPORT ====="; cat "$OUT/week.report.md"
