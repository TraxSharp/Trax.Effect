-- Filtering runs by how they failed ("the conflicts this hour") pages by id, like every other
-- executions filter. Without an index that is a scan of the whole metadata table and an exact
-- count over it. Unclassified is the default and most rows, and nobody pages through it, so the
-- index covers only the classified ones and stays small.
CREATE INDEX IF NOT EXISTS ix_metadata_failure_class
    ON trax.metadata (failure_class, id DESC)
    WHERE failure_class <> 'unclassified';
