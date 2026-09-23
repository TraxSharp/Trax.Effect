-- See the Postgres migration of the same name.
ALTER TABLE work_queue ADD COLUMN confirmed_at TEXT NULL;

UPDATE work_queue SET confirmed_at = created_at WHERE confirmed_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_work_queue_unconfirmed
    ON work_queue (created_at)
    WHERE confirmed_at IS NULL;
