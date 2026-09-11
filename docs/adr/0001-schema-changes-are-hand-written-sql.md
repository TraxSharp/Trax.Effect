---
authors: [Theauxm]
areas: [migrations, providers]
status: accepted
---

# Schema changes are hand-written SQL journaled by DbUp

Every Trax table is created by a numbered SQL file at
`Trax.Effect.Data.<Provider>/Migrations/<NNN>_<name>.sql`, embedded via a csproj glob and
applied by DbUp at DI-registration time, inside `UsePostgres(...)` / `UseSqlite(...)`. EF
Core migrations are not used. `SkipMigrations()` opts out for an externally managed schema,
though it is declared only in the Postgres package. `UseSqlite` honours the flag, and a
Sqlite-only host can still set it through the underlying `MigrationsDisabled` property on the
builder. That property is marked `[EditorBrowsable(Never)]`, so the route is undiscoverable
rather than unavailable.

The two provider sets are **independent and numbered separately**. Postgres is at `040`,
Sqlite at `006`, and each must be gapless from `001` in its own folder. A table that must
work on both needs a file in both.

## Status

**Accepted.**

## Considered options

**EF Core migrations.** The obvious path, and rejected for two reasons that matter here.
They are generated from a model diff, so the SQL that runs is whatever the tool emitted,
which is the wrong trade for a library whose schema is a published contract. And they are
per-`DbContext`, while this ships two provider assemblies over one logical schema, so the
generated sets would have to be reconciled by hand anyway.

**A schema-management tool outside the host** (Flyway, an ops-run script). Rejected because
a consumer adding `UsePostgres(...)` expects a working database, not a deployment step. The
migration running inside registration is what makes the samples need nothing but a running
Postgres.

## Consequences

**Applying happens synchronously during service registration**, not at first use. A host
that cannot reach its database fails at startup, which is the intended moment.

**The DDL column names must match the EF `[Column(...)]` names exactly**, because the
stores query by those names and nothing reconciles the two automatically. That drift is
what [0002](./0002-framework-tables-are-migrated-domain-tables-are-bootstrapped.md) is about.

**The runner scans exactly one assembly**, `typeof(AssemblyMarker).Assembly`. There is no
cross-assembly discovery, which is why a feature package's DDL must ship here. That one
binds Trax.Api as well, so it lives in the central corpus as `Trax.Docs/adr/0009`.

## Exemplars

- `MigrationsIntegrityTests` pins the file naming (`NNN_description.sql`), the gapless
  sequence from 001 in each provider folder, and that every file is an embedded resource or
  covered by the wildcard.

Not covered: nothing compares a Postgres migration against its Sqlite counterpart, so the
two sets can drift into different shapes for the same table and no test notices. The contents
are checked further than the naming, though not by this guard: `SqliteMigrationTests.cs` and
`PostgresMigrationTests.cs` assert the tables and indexes a migrated database ends up with,
and `MigrationSchemaTests.cs` compares the model against the DDL for the state machine
tables (see [0002](./0002-framework-tables-are-migrated-domain-tables-are-bootstrapped.md)).

## Changelog

- **2026-09-11**: Corrected two overstatements: SkipMigrations is reachable on Sqlite, and the migration contents are tested further than the ADR claimed.
- **2026-09-11**: Corrected the SkipMigrations claim: it ships only in the Postgres
  package, so a Sqlite-only host cannot call it.
- **2026-09-11**: Recorded.
