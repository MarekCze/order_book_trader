# Task 001 — Add a flow-exhaustion / stall gate to the Setup 5 trigger

**Status:** Proposed — **DESIGN DISCUSSION REQUIRED before implementation.**
**Type:** Detector definition change (not a threshold tweak).
**Source:** `runs/entry-quality-diagnosis.md` (2026-06-14 entry-quality forensic).

---

## Why (context)

The entry-quality diagnosis on the real ES week (2026-06-01..05) found that the 16 traded
Setup 5 (delta-divergence fade) entries fail because of **entry timing, not exits, direction,
or stop width**:

- Outcome is binary: 11 losers have MFE ∈ {0,1,2} ticks (never went favorable); 5 winners have
  MFE 5–12 (hit T1 fast). No loser reaches MFE 3 → the R≈4t stop is correctly sized and exits
  (P2) are already maxed; the defect is upstream.
- **S5 fades live climaxes, not exhaustion.** 6/11 losers stop in <4s (three in 0.0–0.1s). The
  Day-1 cascade: four shorts fired *within one second* at successive new highs of a vertical
  rally, each into **+13σ to +21σ buy flow** (`f8_delta_z_w10`). S5's trigger has **no
  requirement that the aggressive push has stalled or decelerated** — unlike Setup 1, which
  gates on A3 (stall) + A6 (exhaustion).

**Quantified prize:** blocking just the 5 Day-1 climax entries moves the week from −$6,161.50
to **−$3,779 (−39%)**.

## Goal

Add a **flow-exhaustion / stall gate** to the S5 trigger so the fade only fires once aggressive
flow in the move's direction has actually cooled — refusing to fade a still-accelerating push.

This task captures **two candidate mechanisms**. Both should be specified; **which one (or
both, or a combination) ships is to be decided in design discussion** — see Open Questions.

### Option A — Block while short-window flow is still extreme

Gate the trigger on the instantaneous directional flow z-score: do not fire while
`|f8_delta_z_w10|` (the signed flow already in `FeatureSnapshot.DeltaZ`, F8 per-window) is above
a threshold — i.e. the climax is still in progress.

- Direction-aware: for a short (fade of a high) the dangerous case is hot **buy** flow
  (`DeltaZ` strongly positive); for a long, hot **sell** flow (strongly negative). Gate on the
  flow that is *with* the move being faded.
- New config key, e.g. `Setup5:MaxTriggerFlowZ` (default to be chosen; ~10 blocked the entire
  Day-1 cascade on this week). Window choice (w10 vs w30) is itself a discussion point.
- Cheapest to implement: the value already exists per trigger event; it is one extra guard in
  `TryTrigger` before `E4`.
- Risk: a single instantaneous z-score is noisy; it blocked the one Day-1 winner (01-7) too —
  acceptable on net here, but verify it doesn't over-prune the genuine fast fades (01-7 was the
  exact-top entry).

### Option B — Require the last delta bucket to have rolled over from its peak

Gate on **deceleration**, not level: track per-side aggressor delta in short fixed buckets
(e.g. 10s, mirroring the existing post-entry `AccumulateAdverseBucket` /
`Setup5:DeltaBucketSeconds` machinery) over the run into the extreme, and require the most
recent bucket's directional delta to have **dropped by ≥ X% from the peak bucket** before
allowing the trigger (analogous to Setup 1's A6 exhaustion: "sell volume per 10s bucket dropped
≥70% from peak and last bucket's delta ≥ 0").

- New config key(s), e.g. `Setup5:ExhaustionDropRatio` (+ reuse/define a pre-trigger bucket
  window). Default TBD.
- More robust than Option A (measures the climax *turning over*, which is the actual thesis) but
  needs new pre-trigger bucket state in the detector (today buckets exist only post-entry for
  invalidation) and more care for determinism.
- This is the closer structural analogue to S1's A3/A6 and arguably the "correct" fix.

## Where (code references)

- Trigger path & E4: `src/OrderFlow.Domain/Trading/DeltaDivergenceFadeDetector.cs`
  — `TryTrigger` (line ~205), E4 evaluated at line ~213. New gate goes here, before/with E4.
- Guards: `src/OrderFlow.Domain/Trading/Setup5Guards.cs` (E1 l.14, E2 l.19, E3 l.25, E4 l.30) —
  add a new guard method (e.g. `FlowExhaustion(...)`) consistent with the existing static style.
- Options: `src/OrderFlow.Domain/Trading/Setup5Options.cs` — add the new tunable(s) with
  rulebook-style XML-doc; defaults are the values that reproduce *current* behavior only if the
  gate is opt-in (see Open Questions on default-on vs default-off).
- Feature inputs: `FeatureSnapshot.DeltaZ` / `.Delta` (F8 per-window) —
  `src/OrderFlow.Domain/Features/FeatureSnapshot.cs:34-35`; windows in `FeatureEngineOptions`
  (`{10s,30s,60s,300s}`).
- Existing bucket precedent for Option B: `AccumulateAdverseBucket` + `DeltaBucketSeconds`
  (`DeltaDivergenceFadeDetector.cs:266-283`).
- Funnel telemetry: add the new condition to the S5 chain in the `ConditionFunnel` so the gate's
  pass/eval shows up in replay funnel lines (mirror how E1→E2→E3→E4 are counted).

## Engineering constraints (from CLAUDE.md)

- **Every threshold is a named config value** (appsettings + typed `Setup5Options`), never a
  literal. Tunable via `--set Setup5:<Key>=<Value>`.
- **Determinism is hard-required:** no wall-clock, no unordered iteration, no randomness. Same
  DBN + same config ⇒ byte-identical journal. Option B's pre-trigger bucket state must be
  reset/keyed deterministically.
- **TDD:** every transition guard gets unit tests. Add guard tests for the new condition (hot
  flow blocks / cooled flow passes / boundary) and a detector-level test.
- Journal schema is a contract — this change adds **no new journal columns** (the gate uses
  existing F8 features); journaling behavior for blocked candidates is unchanged.

## Acceptance criteria / validation

1. New gate is **off-by-default OR defaults reproduce current behavior** (decision in Open
   Questions); with the gate disabled, the week is **byte-identical** to today (16 trades /
   5W-11L / −$6,161.50) and all existing tests pass.
2. With the gate enabled at the proposed threshold, re-run the week
   (`runs/artifacts/...`, per-day journals, chained `Storage:SqlitePath`) and confirm:
   - the Day-1 17:43:27 cascade entries are blocked (funnel shows them failing the new
     condition), and
   - net P&L improves toward the ~−$3,779 estimate; report the new trade count, hit rate, and
     per-condition funnel.
3. New unit tests cover the guard and the detector transition; full suite green.
4. No `appsettings` *behavioral* defaults changed without explicit sign-off (per the project's
   "diagnostics before optimization" / no-silent-default-changes discipline).

## Open questions (resolve in discussion before coding)

- **A, B, or both?** Ship the cheap instantaneous gate (A), the structural deceleration gate
  (B), or A as a fast guard + B as the primary? (Recommendation leans B as the "correct" fix,
  with A as a possible cheap complement.)
- **Window & threshold:** w10 vs w30 for Option A; bucket size + drop-ratio for Option B; actual
  default values. ~10 for `|DeltaZ_w10|` is the only empirically-anchored number so far (n=16).
- **Default on or off?** A new always-on gate changes defaults (breaks byte-identical baseline);
  an off-by-default opt-in preserves it (cf. how `Risk:InvertDirection` was added gated). Pick
  one consistent with current discipline.
- **Re-arm cooldown (related, possibly separate task):** even with a flow gate, should S5 have a
  per-extreme-sequence cooldown to prevent cascades structurally? Could be folded in or split to
  its own task.
- **Generalize to S1/S2?** The same flow-exhaustion concept may apply to the other fade setups;
  out of scope here unless decided otherwise.

## Not in scope

- Reviving S1 (structural A4/A5/A6) or S4 (D2 calibration) — separate tasks.
- The one-position portfolio rule (trade-concurrency) — separate code change.
- Exit geometry (P2) — already diagnosed; config-only, decided separately.
