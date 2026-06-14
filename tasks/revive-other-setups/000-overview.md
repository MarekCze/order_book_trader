# Revive the non-firing setups (S1, S2, S4) — overview

**Status:** Proposed — investigation/calibration work; **diagnostics before optimization.**
**Source:** `runs/tuning-log.md`, `runs/tuning-iteration-1-results.md`, `runs/permissive-run-results.md`.

On the real ES week (2026-06-01..05, default config) **only Setup 5 trades**. Setups 1, 2, 4
produce ~0 tradeable candidates. The per-condition funnel telemetry (PR #10) localized exactly
where each dies; this folder turns that into one task per setup.

| Setup | Binding wall | Nature | Task |
|---|---|---|---|
| S1 Absorption fade | **A4** (then A5/A6) | **Structural** — metric/definition, not a threshold | `001-s1-absorption-structural-review.md` |
| S2 Stop-run fade | **B3** then **B5** | Calibration + possible definition | `002-s2-stoprun-calibration.md` |
| S4 LVN vacuum | **D2** (a cliff) | Threshold-calibration **+ quality filter** | `003-s4-lvn-vacuum-calibration-quality.md` |

## Cross-cutting principles (apply to all three)

1. **Loosening thresholds destroys entry quality.** The permissive run opened every wall and got
   70,133 candidates but hit rate collapsed 31%→8% (PF 0.12→0.05) — the strict rulebook
   thresholds are *quality filters*, not just throttles. So reviving a setup is **not** "lower
   the threshold until it fires." Every gate-opening must be paired with a *quality* assessment.
2. **Validate entries the same way S5 was diagnosed.** Any newly-firing setup must be run through
   the entry-quality forensic in `runs/entry-quality-diagnosis.md`: MAE/MFE distribution,
   continuation-vs-reversal classification, time-in-trade, flow z-score at trigger. A setup that
   fires but only into continuation is not "revived." (The S5 lesson — fades fire into live
   climaxes without an exhaustion gate — likely generalizes; see `tasks/001-s5-flow-exhaustion-gate.md`.)
3. **Use the existing tooling, don't rebuild it.** Per-condition funnel lines (replay output),
   `runs/sweep_s1_s4.sh` (multiplicative threshold sweep via `--set`, ephemeral state),
   `runs/merge_journals.py` + `orderflow report` (week rollup), `orderflow inspect-trade <db>
   <id> --data <file>` (per-candidate audit). Run via `--set`; **never edit appsettings for a
   sweep.**
4. **Engineering constraints (CLAUDE.md):** every threshold a named config value; determinism
   (no wall-clock / unordered iteration / randomness; byte-identical replays); TDD on every new
   or changed guard; no journal-schema changes (F7/F19/F20 stay null; Setup 3 stays deferred).
5. **No silent default changes.** Diagnostics and sweeps don't touch defaults. A default change
   ships only with explicit sign-off and the supporting week report.

## Suggested order

1. **S4** first — it is the clearest *calibration* problem with an already-located firing
   boundary (~×0.7); fastest path to a second tradeable setup, and it forces us to build the
   reusable "quality filter after a gate opens" pattern.
2. **S2** second — calibration of B3/B5, moderate scope.
3. **S1** last — the deepest (structural metric review); may conclude that A4/A5/A6 cannot be
   computed meaningfully from MBP-10 as written, which is itself a valid outcome to document.

Each task is self-contained: context, where it dies, the investigation, code references,
acceptance criteria, and open questions for discussion before any code is written.
