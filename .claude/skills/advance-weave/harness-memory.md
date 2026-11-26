# Weave harness memory

Why each change to `advance-weave`, `verify-weave`, the gate or a friction-removing script was made, and whether it worked (`agent-harness/skills/autonomous-dev` §8.1). Organised by root cause. Read whole every run; check every pending attempt whose check date has passed.

## RC-1 Runs end early instead of using their budget
Status: open
Inherited: agent-harness RC-1 and envoylet RC-1 — runs stopped after housekeeping or "not enough time for the full gate"; the shared rule became firm progress rule 2 (use the whole budget; push a sub-step instead of stopping).
- A1 · PR (this file) · 2026-11-25 · claude-opus-5-5
  Change: none to the rules yet; recorded so later runs can measure it. Why: inherited only, not yet observed here. Predict: the next 3 runs of this task each merge or push a product change and report no early stop.
  Observed: 2026-11-26 run from 04:51 UTC: released 0.6.0 (#597) and fixed the owner's flaky test (#598) with no early stop; one run, so still pending (check 2026-11-27)

## RC-2 Hosted CI and Codex review never arrive, so merges stall
Status: open
Inherited: envoylet RC-2 (settled) — hosted Actions often cannot start; waiting on them blocked merges; the rule became "the full local gate plus an independent review agent stand in".
- A1 · PR #577 · 2026-11-25 · (owner instruction, recorded by claude-opus-5-5)
  Change: advance-weave "Test evidence for merging": all-green hosted checks on the head **or** the full local gate is enough. Why: hosted checks here usually do run and finish green, while the full mailbox `npm test` takes over 10 minutes in the sandbox. Predict: no PR in the next 2 days waits a whole run only for evidence.
  Observed: pending (check 2026-11-27)

## RC-3 The sandbox's default Node is older than the repo needs, and setup costs time
Status: open
Inherited: envoylet RC-3 — the cloud image's default Node 22 did not match the documented Node 24; runs lost time installing it.
- A1 · advance-weave Setup (before this file) · 2026-11 · unknown
  Change: use `/opt/node24/bin` when present, otherwise the checked official tarball. Why: the default `node` is v22 on the 2026-11 image. Predict: setup takes under 2 minutes in every run.
  Observed: 2026-11-25 `/opt/node24/bin/node` is v24.22.3 while the default is v22.23.5; setup took about 1 minute → kept

## RC-4 A long full `npm test` makes a run's own tool call time out
Status: open
- A1 · PR (this file) · 2026-11-25 · claude-opus-5-5
  Change: none yet; noted. Why: the full mailbox-worker `npm test` ran past the 10-minute foreground tool limit on 2026-11-25 and was lost; it worked when started in the background with output to a log. Predict: if two more runs hit the same timeout, advance-weave Setup gains "run the full mailbox `npm test` in the background with a log" (check 2026-11-27).
  Observed: pending (check 2026-11-27); 2026-11-26 the full mailbox `npm test` was again run in the background with a log (about 20 minutes beside other test loops on 2 CPUs), no timeout
