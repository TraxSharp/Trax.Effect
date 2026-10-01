-- Decisions a train made at run time: each question it asked a decider (a typed decision model, a
-- rule table, a cascade of both), the answer it acted on, and the track it took. Written when the
-- run finishes by AddDecisionRecording, and read back to replay a requeued run's decisions so it
-- takes the tracks the original took instead of asking again. The model is
-- Trax.Effect.Models.RecordedDecision, on IDataContext.RecordedDecisions.
--
-- A decision belongs to its run and goes with it: the cascade keeps every existing delete of
-- metadata (cleanup, manifest pruning) working without knowing this table exists.
CREATE TABLE IF NOT EXISTS trax.decision (
    id bigserial PRIMARY KEY,
    metadata_id bigint NOT NULL REFERENCES trax.metadata (id) ON DELETE CASCADE,
    question_key text NOT NULL,
    occurrence integer NOT NULL,
    kind text NOT NULL,
    question jsonb NOT NULL,
    answer jsonb NOT NULL,
    model text,
    decider text,
    replayed boolean NOT NULL DEFAULT false,
    shadows jsonb,
    track text,
    fallback_reason text,
    decided_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_decision_run_question UNIQUE (metadata_id, question_key, occurrence)
);

-- Which run's decisions a run replays. Set by a requeue on the queued entry and carried to the
-- run's metadata at dispatch. Not a foreign key: the original may be deleted before the requeue
-- runs, and a replay that finds nothing recorded asks afresh.
ALTER TABLE trax.work_queue ADD COLUMN IF NOT EXISTS replay_decisions_of bigint;
ALTER TABLE trax.metadata ADD COLUMN IF NOT EXISTS replay_decisions_of bigint;
