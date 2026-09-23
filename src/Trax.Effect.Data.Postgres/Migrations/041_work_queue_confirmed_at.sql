-- Two-phase enqueue: an entry is dispatchable only once confirmed.
--
-- Normally confirmed_at is stamped at insert, so behaviour is unchanged. A train that defers
-- promotion is committed with a null value, its OnQueue hook runs, and a second commit stamps
-- it. A crash in between leaves a detectable unconfirmed row instead of an orphaned side-effect.
--
-- Existing rows are backfilled from created_at so nothing already queued stops being claimable.
ALTER TABLE trax.work_queue ADD COLUMN IF NOT EXISTS confirmed_at timestamptz NULL;

UPDATE trax.work_queue SET confirmed_at = created_at WHERE confirmed_at IS NULL;

-- Dispatch reads queued + confirmed ordered by priority and age; keep that covered.
CREATE INDEX IF NOT EXISTS ix_work_queue_dispatchable
    ON trax.work_queue (priority DESC, created_at ASC)
    WHERE confirmed_at IS NOT NULL;

-- Finds rows stranded unconfirmed by a crash.
CREATE INDEX IF NOT EXISTS ix_work_queue_unconfirmed
    ON trax.work_queue (created_at)
    WHERE confirmed_at IS NULL;
