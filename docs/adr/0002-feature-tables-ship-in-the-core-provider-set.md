---
authors: [Theauxm]
areas: [migrations, providers]
status: accepted
---

# Feature-package tables ship in the core provider migration set

A higher-level feature package does not carry its own migrations. Its table DDL goes into
the core `Trax.Effect.Data.<Provider>` migration set, alongside every other Trax table.
`035_persisted_operations.sql` and `040_state_machine_snapshots.sql` are both in the core
Postgres provider, and `Trax.Effect.StateMachine.Persistence` has no `Migrations` folder at
all.

## Status

**Accepted.**

## Why this is written down

Because it looks backwards. A feature package owning its own schema is the obvious layout,
and someone tidying this up would move the files without anything stopping them.

The reason is mechanical: DbUp is pointed at exactly one assembly
(`typeof(AssemblyMarker).Assembly`, the provider), so a `.sql` file embedded anywhere else
is never discovered. It does not fail. It silently never runs, and the feature looks broken
at query time in a way that points at the wrong place.

## Consequences

**This does not invert the dependency.** The `.sql` is text, and the provider references
nothing from the feature package.

**A host that does not enable the feature carries empty tables.** That is the accepted
price, and it is small: two unused tables against a silently missing one.

## Exemplars

- [Persisted Operations](/docs/persisted-operations) and
  [State Machines](/docs/statemachine) are the two features whose tables ship this way.

**Unenforced:** nothing checks it, and this is the weakest point in the migration story. A
`Migrations/` folder added to a feature package would embed its scripts, run no part of
them, and pass every guard in the repo. A census of embedded `.sql` resources outside the
two provider assemblies would close it, and does not exist.

## Changelog

- **2026-09-11**: Recorded.
