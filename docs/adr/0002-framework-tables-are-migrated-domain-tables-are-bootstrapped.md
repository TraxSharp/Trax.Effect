---
authors: [Theauxm]
areas: [migrations, data-model]
status: accepted
---

# Trax's tables are migrated; a consumer's domain tables are bootstrapped

Two different mechanisms, deliberately:

- **Trax's own tables** (the `trax` schema) are created in production only by the shipped
  migrations ([0001](./0001-schema-changes-are-hand-written-sql.md)). Tests may build them
  another way against a throwaway database, and several do.
- **A consumer's domain tables** are created by `EnsureSchemaCreatedAsync`, which runs the
  EF model's create script on a relational provider and falls back to `EnsureCreatedAsync`
  in memory. It is a convenience bootstrap, and it says so: production apps are expected to
  bring their own migrations.

## Status

**Accepted.**

## Why this is written down

Because "never use `EnsureCreated`" is the rule people remember, and it is wrong as stated:
the call is right there in `DomainDataContextServiceCollectionExtensions`. The rule applies
to tables **Trax** owns, where `EnsureCreated` would bypass the DbUp journal, drift from the
migrated schema that production actually runs, and make a feature look like it needs manual
table setup.

For a consumer's own tables Trax has no journal and no authority, so the bootstrap is the
friendliest thing that can be offered, and the docstring is explicit that it is not a
migration system.

## Consequences

**The bootstrap swallows an "already exists" error on its second run.** The create script
carries no `IF NOT EXISTS`, so the steady state is a `DbException` on the first statement,
caught and ignored. That is intended, and it is why this is a bootstrap rather than
something to build on.

**`EnsureCreated` in a test is fine**, and several integration tests use it against
throwaway databases. What is not fine is a Trax table whose only creation path is
`EnsureCreated`, because nothing would then catch the model drifting from the shipped DDL.

## Exemplars

- `MigrationSchemaTests` builds the state machine tables from the **shipped** migrations
  (Postgres `040`, Sqlite `006`) and round-trips through the real stores, so a column added
  to the model without a migration fails when the store queries a column that does not
  exist.

Not covered:

- Only the state machine tables have this round-trip. Every other Trax table is covered by
  the naming and sequence checks alone, so model-versus-DDL drift elsewhere is caught at
  runtime rather than by a test.
- **The suite requires a live Postgres and fails without one rather than skipping.** Its
  `PostgresSetup` is an assembly-wide `[SetUpFixture]` that opens a connection
  unconditionally, so even the Sqlite case fails when Postgres is absent. That contradicts
  `Trax.Docs/adr/0005-a-skipped-test-is-a-runtime-decision.md`, which binds this repo, and
  the reachability-probe pattern the stress fixtures already use is the fix.

## Changelog

- **2026-09-11**: Corrected two overstatements an audit found: Trax tables are built with
  EnsureCreated in several test fixtures, and MigrationSchemaTests fails without Postgres
  rather than skipping.
- **2026-09-11**: Recorded.
