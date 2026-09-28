-- Nonces a Trax runner has accepted on a signed request (Trax.Scheduler, scheduler/0009). A runner
-- scaled to several instances shares them here, so a request is accepted once across all of them
-- rather than once per process. A feature package's table ships in this set (docs/0009).
--
-- expires_at is Unix seconds: the nonce's signed timestamp plus the runner's allowed clock skew.
-- After it, the timestamp alone refuses the request, so the row can go. The runner inserts with
-- ON CONFLICT and deletes expired rows itself; the index serves that delete.
CREATE TABLE IF NOT EXISTS trax.runner_nonce (
    nonce text PRIMARY KEY,
    expires_at bigint NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_runner_nonce_expires_at
    ON trax.runner_nonce (expires_at);
