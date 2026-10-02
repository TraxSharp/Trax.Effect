---
authors: [Theauxm]
areas: [platform, data-model]
status: accepted
---

# A recorded answer replays only into the same state, and only while it is fresh

`AddDecisionRecording` stores, with each answer in `trax.decision`, the hash of the state the
question was asked about (`state_hash`, from `DecisionMade.StateHash`), and hands it back with the
answer so Trax.Core replays it only into a state that hashes the same (`core/0004`). It also
replays an answer only while it is younger than `ReplayAnswersFor`, 24 hours by default, measured
from when a decider gave it. An answer outside either is asked afresh.

## Status

**Accepted.** Narrows the replay of `Trax.Docs/adr/0041`, which replayed a recorded answer into any
later repeat of the run, whatever its state and however long ago it was given.

## Why this is written down

Because a replay is unattended, and both limits are easy to drop as "it was answered already".
A run that waits in a dead letter for a week and is then requeued acts on a judgement made about
the world as it was a week ago; so does a chain of requeues, each of which replays the answer and
records a new row for it. The bound therefore counts from the row a decider wrote, following the
replayed rows back to it, so replaying an answer never makes it younger. A row whose answering run
is no longer in the chain cannot be dated and is asked afresh.

## Considered options

**No age bound, state hash only.** Rejected: a state that hashes the same can still be judged
differently later (a policy, a model, a fraud signal outside the state), and a decision is about
the world, not only about its input.

**A bound per train or per question.** Not taken now: one host-wide bound covers the case, and a
finer one can be added without changing what is stored.

**Report an aged-out answer in `ReplayRefused`.** Not possible from the journal: Trax.Core writes
that field only for answers it was handed. The journal leaves the answer out and logs the reason at
Information.

## Consequences

Rows written before `state_hash` existed have none and are never replayed, so the first requeue
after upgrading asks afresh. A requeue more than a day after the original asks afresh unless the
host raises the bound. Both apply to every path that names a run to replay: a manual requeue, a
dead letter's requeue and a manifest's retry.

## Exemplars

- `DecisionRecordingTests` pins that the hash is stored, that a changed state and an aged answer
  are asked afresh, that a replay does not refresh an answer's age, and that a row with no hash is
  asked afresh.

Not covered: Postgres and Sqlite pin only that the hash is stored; the age and state rules are the
same code for every provider and are exercised on the in-memory one.

## Changelog

- **2026-10-02**: Recorded.
