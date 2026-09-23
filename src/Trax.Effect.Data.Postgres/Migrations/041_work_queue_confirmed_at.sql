-- Two-phase enqueue: an entry is dispatchable only once confirmed.
--
-- Normally confirmed_at is stamped at insert, so behaviour is unchanged. A train that defers
-- promotion is committed with a null value, its OnQueue hook runs, and a second commit stamps
-- it. A crash in between leaves a detectable unconfirmed row instead of an orphaned side-effect.
--
-- Existing rows are backfilled from created_at so nothing already queued stops being claimable.
--
-- The default is for writers that do not know the column. During a rolling deploy an instance
-- still on the previous version inserts rows without it; with no default those rows would be
-- unconfirmed, and nothing confirms a row it did not stage, so they would never dispatch (the
-- stale-staged sweep would eventually cancel them). Code that knows the column always writes
-- it, as a timestamp or as an explicit NULL when it defers.
--
-- The order matters. DbUp runs these statements without a transaction, so an old instance can
-- insert between any two of them. The column is added bare, not as ADD COLUMN ... DEFAULT now(),
-- because that would stamp every existing row with the migration time instead of its
-- created_at. The default is set next, before the backfill: SET DEFAULT takes an ACCESS
-- EXCLUSIVE lock, which waits for every in-flight inserting transaction, so any row inserted
-- without the default is committed before it and visible to the backfill after it, and every
-- row inserted after it gets now(). Backfilling first and setting the default last would leave
-- a window in which a row slips past the backfill with a NULL.
ALTER TABLE trax.work_queue ADD COLUMN IF NOT EXISTS confirmed_at timestamptz NULL;

ALTER TABLE trax.work_queue ALTER COLUMN confirmed_at SET DEFAULT now();

UPDATE trax.work_queue SET confirmed_at = created_at WHERE confirmed_at IS NULL;

-- Dispatch reads queued + confirmed ordered by priority and age; keep that covered without
-- indexing every row that has ever been dispatched.
CREATE INDEX IF NOT EXISTS ix_work_queue_dispatchable
    ON trax.work_queue (priority DESC, created_at ASC)
    WHERE status = 'queued' AND confirmed_at IS NOT NULL;

-- Finds rows stranded unconfirmed by a crash.
CREATE INDEX IF NOT EXISTS ix_work_queue_unconfirmed
    ON trax.work_queue (created_at)
    WHERE confirmed_at IS NULL;
