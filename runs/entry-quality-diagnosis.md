# Entry-quality diagnosis — why the 16 traded S5 entries fail (2026-06-14)

Forensic on the only setup that trades on the real ES week (2026-06-01..05, default config):
**Setup 5 (delta-divergence fade)**. Diagnostics-only — **no defaults, thresholds, or
detector code changed**. This finishes the iteration-2 audits (2a/2b/2c) that were specified
but never run on real data, plus a continuation-vs-reversal classification.

**Sample validated:** re-ran the week (Release build, per-day journals `j-01..j-05`, chained
`Storage:SqlitePath`); reproduces **16 trades / 5W-11L / −$6,161.50** byte-for-byte. All 16
are Setup 5 (14 short, 2 long). n=16 is small and capped by 5 days of data — **everything
below is a hypothesis to test, not an established fact.** Artifacts under
`runs/artifacts/entry-quality/` (gitignored): journals, per-trade `inspect/t-DD-ID.txt`.

## The 16-trade fact table

Outcome is **binary**: a trade either hits T1 fast (win, banks +$173.50 = T1 50% @1R then
runner stopped at breakeven) or never goes favorable at all (loss, full −$639 stop). MAE/MFE
in ticks (R ≈ 4 ticks; stop = H2±2, entry = H2∓2). `dz` = `f8_delta_z_w10` (directional flow
z-score at trigger; positive = buy-aggression hot).

| trade | dir | outcome | time-in-trade | MAE | MFE | flow dz | net |
|---|---|---|---:|---:|---:|---:|---:|
| 01-1 | S | loss | 2.9s | 3 | 1 | **+13.5** | −639 |
| 01-2 | S | loss | **0.0s** | 3 | 1 | **+21.3** | −639 |
| 01-5 | S | loss | **0.1s** | 3 | 0 | **+20.3** | −639 |
| 01-6 | S | loss | **0.0s** | 3 | 0 | **+20.4** | −639 |
| 01-7 | S | **WIN** | 0.6s | 3 | 5 | **+20.7** | +173.5 |
| 01-9 | L | loss | 19.5s | 3 | 0 | −3.9 | −639 |
| 02-1 | S | **WIN** | 186.6s | 2 | 8 | +4.3 | +173.5 |
| 02-2 | S | **WIN** | 139.1s | 3 | 8 | +0.8 | +173.5 |
| 02-3 | S | loss | 6.3s | 3 | 1 | +3.1 | −639 |
| 02-4 | S | loss | 3.9s | 3 | 0 | −0.1 | −639 |
| 02-5 | S | loss | 1.3s | 3 | 0 | +3.8 | −639 |
| 02-6 | S | loss | 42.7s | 3 | 2 | −1.4 | −639 |
| 02-7 | S | loss | 28.5s | 3 | 1 | +1.5 | −639 |
| 03-3 | L | **WIN** | 18.9s | 3 | 7 | −5.3 | +173.5 |
| 03-4 | S | **WIN** | 56.2s | 2 | 12 | −1.1 | +173.5 |
| 04-1 | S | loss | 1.7s | 3 | 0 | +7.5 | −639 |

## Finding 1 — The losses are continuation, not bad exits. Stop width is NOT the problem.

The MFE distribution is starkly bimodal:

- **11 losers: MFE ∈ {0,1,2}** (median 0). They never came within 2 ticks of a +1R.
- **5 winners: MFE ∈ {5,7,8,8,12}** — all hit T1.

There is **no "shallow reversal that just missed the stop"** — no loser has MFE = 3 (one tick
short of the 4-tick stop). So:

- **Widening the stop cannot help** — losers go ~0 ticks favorable; a wider stop only enlarges
  each loss. **Tightening the stop would kill winners** — winner 01-7 took a 3-tick MAE before
  working; three of five winners touched MAE 2–3. The R≈4t stop is *correctly sized*. The
  earlier exit-geometry work (P2) is the right and only exit lever; it cannot fix this.
- The binding defect is **upstream of exits: the fade fires while the move is still being
  pushed.** Six of eleven losers stop out in **under 4 seconds** (three in 0.0–0.1s) — price
  was driving through the entry the instant it filled.

## Finding 2 — S5 fades into live climaxes. The Day-1 cascade is the smoking gun.

On 2026-06-01 at **17:43:27Z**, four short candidates (01-2/5/6/7) fired **within the same
second** at successive new highs 7625.5 → 7626.75 → 7628.00 → 7628.75 — the detector re-armed
and re-fired a fade at *each new extreme* of a vertical rally. Each stopped in 0.0–0.6s. Three
lost; the one win (01-7) was simply the entry nearest the actual top.

The flow z-score confirms the mechanism: every Day-1 entry fired into **+13.5σ to +21.3σ buy
flow** (`f8_delta_z_w10`) — i.e. fading aggression at its most violent, not its exhaustion.
S5's trigger has **no requirement that the aggressive push has actually stalled or decelerated**
(contrast Setup 1, which gates on A3 stall + A6 exhaustion). E1 (new extreme) + E2 (divergence)
+ E4 (one diagonal imbalance near the extreme) can all be true *mid-acceleration*.

**Quantified:** blocking all five Day-1 entries (a flow-climax gate, e.g. `|f8_delta_z| > ~10`)
removes four losers (−$2,556) and one winner (+$173.5) → net would improve from −$6,161.50 to
**−$3,779** on this week (−39%). flow-z is not a clean *universal* discriminator (Day-2 winners
02-1/02-3 had similar mild +3–4σ), but its extreme tail is uniformly the worst entries.

## Finding 3 — E4 never required a reclaim; E2's weak branch passed many losers.

In the `--data` reconstruction **all 16** trades triggered E4 via `imbalance-near-extreme=True`
with **`reclaimed-past-H1=False`** — i.e. *not one* entry had price actually reclaim back past
the prior swing before entering; every fade was placed at/near the live extreme. E4's reclaim
half is the stronger condition and none of these marginal entries met it. Likewise E2 passed
several entries on the *non-confirming-bar* branch (`close-toward-extreme ≥ 0.67`) rather than a
real cum-delta divergence. (Reconstruction runs on ephemeral feature-state so its E1–E4
verdicts occasionally flip vs the journal — the journaled features are authoritative — but the
H1/H2 cum-delta sample and the reclaim flag are indicative.)

## Deferred iteration-2 audits — answered

- **2a (MAE/MFE split by `t1_filled`):** clean separation. `t1_filled=1` → the 5 winners,
  MFE 5–12. `t1_filled=0` → the 11 losers, MFE 0–2. Confirms Finding 1.
- **2b (per-trade S5 verdicts):** done for all 16 (see `inspect/`). Dominant verdict:
  *premature fade into continuation*; the cleanest losers (Day-1 cascade) fade a 20σ climax.
- **2c (swings/session):** 1,440–5,169 confirmed swings/session. S5 is **not over-firing by
  count** — E3 (location) gates hundreds of E2-passers down to 2–13 candidates/day. The defect
  is the *quality* of those that pass, not the quantity.

## Ranked hypotheses for the next (optimization) round — NOT applied here

1. **Add a flow-exhaustion gate to the S5 trigger (highest leverage; detector change).** Require
   that directional aggression has decelerated before fading — analogous to S1's A6. Concretely:
   block the trigger while short-window flow is still extreme (`|f8_delta_z_w10|` above a
   threshold, ~10 on this week) and/or require the last N-second delta bucket to have rolled
   over from its peak. This is a *new condition/definition*, not a tweak to an existing knob.
   Candidate keys: a new `Setup5:MaxTriggerFlowZ` gate, or a `Setup5:RequireFlowDeceleration`
   guard. Estimated −39% on this week from the Day-1 block alone.
2. **Require the reclaim half of E4 (`reclaimed-past-H1`) instead of the bare imbalance.** None
   of the 16 marginal entries reclaimed; demanding it would force the fade to wait for price to
   actually fail back past the prior swing. Knob: tighten/curtail `E4_Trigger`'s
   `imbalanceNearExtreme` branch (`Setup5Guards.cs:30`).
3. **Throttle re-firing within one extreme sequence (cascade guard).** A per-leg cooldown or
   "one fade per new-extreme run" so a vertical move can't draw 4 stop-outs in one second.
   Knob: new `Setup5:RearmCooldownSeconds` / min-ticks-between-triggers.
4. **Strengthen E2 — prefer the true cum-delta-divergence branch over the non-confirming-bar
   branch.** Knob: raise `Setup5:CloseInExtremeFraction` or gate the bar branch
   (`Setup5Guards.cs:19`).
5. **(Confirm only, n too small)** location/time: winners weakly favor |LOI dist| ≈ 3 and the
   first session hour — not actionable at n=16.

**Bottom line:** entry *timing*, not direction (confirmed: inverting is worse), not exits
(P2 already optimal), not stop width (losers go ~0 ticks favorable). S5 fades moves that have
not yet exhausted. The single highest-leverage change is a flow-exhaustion/stall gate on the
S5 trigger.
