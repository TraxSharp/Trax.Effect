-- See the Postgres migration of the same name.
ALTER TABLE work_queue ADD COLUMN subject_key TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_work_queue_subject_busy
    ON work_queue (subject_key)
    WHERE status = 'dispatched';

CREATE INDEX IF NOT EXISTS ix_metadata_active
    ON metadata (id)
    WHERE train_state IN ('pending', 'in_progress');
