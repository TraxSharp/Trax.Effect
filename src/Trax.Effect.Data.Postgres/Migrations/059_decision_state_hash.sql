-- The hash of the state each recorded question was asked about (SHA-256, lowercase hex), as
-- Trax.Core reports it. A requeued run's replay hands it back, and Trax.Core replays an answer only
-- into a state that hashes the same. Rows written before this column have none, and their answers
-- are asked afresh rather than replayed.
ALTER TABLE trax.decision ADD COLUMN IF NOT EXISTS state_hash text;
