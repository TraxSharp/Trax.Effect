-- See the Postgres migration of the same name.
ALTER TABLE metadata ADD COLUMN failure_class TEXT NOT NULL DEFAULT 'unclassified';
