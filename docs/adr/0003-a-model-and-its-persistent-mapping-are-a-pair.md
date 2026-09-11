---
authors: [Theauxm]
areas: [data-model]
status: accepted
---

# A model and its persistent mapping are a pair

A data model is two files in two projects: the provider-agnostic base at
`Trax.Effect/Models/<Entity>/<Entity>.cs`, and the EF mapping at
`Trax.Effect.Data/Models/<Entity>/Persistent<Entity>.cs`. Neither ships without the other,
except where the pairing genuinely does not apply.

## Status

**Accepted.**

## Why this is written down

Because the split is not obvious from either half. Reading `Persistent<Entity>` alone
suggests the mapping could simply carry the properties, and reading the model alone gives
no hint that a second file must move with it. The split is what lets `Trax.Effect` be
referenced without EF Core at all.

## Consequences

**Two legitimate exceptions exist and are listed in the guard**: `Host` is stamped onto
metadata records rather than stored as an entity, and `JunctionMetadata` lives inside the
metadata row's `junctions` JSON column. Both carry their reason in the exceptions set.

**Adding a model is a four-place change**: the base, the persistent mapping, the DbContext,
and a migration in each provider set.

## Exemplars

- `ModelPersistentPairingTests` checks **both** directions: a `Persistent<Entity>` with no
  model, and a model with no `Persistent<Entity>` that is not in the exceptions set. The
  two exceptions carry their reasons inline.

Not covered: the guard matches on file paths and names, so it proves the pair exists and
nothing about whether the mapping is right. A `Persistent<Entity>` that maps half the
model's properties passes.

## Changelog

- **2026-09-11**: Renumbered from 0004 when the feature-tables decision moved to the
  central corpus.
- **2026-09-11**: Recorded.
