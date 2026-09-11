# Trax.Effect

The effect layer: execution metadata, data contexts, effect and junction providers, the
state machine engine, and the Postgres / Sqlite / InMemory data providers. It sits directly
above `Trax.Core` and below everything else in the workspace, so a change here reaches every
other Trax repo through a published package.

This file is the entry point. It routes; it does not restate the rules.

## Architecture decisions

`docs/adr/` records **why** things are the way they are. A documentation page says what the
rule is; an ADR says whether it is a deliberate constraint or an accident, so you can tell
which ones are safe to change. Read the relevant one before proposing to change a rule, and
if your work contradicts one, say so rather than silently overriding it.

| Working on | Read first |
| --- | --- |
| a schema change | [0001](./docs/adr/0001-schema-changes-are-hand-written-sql.md), hand-written SQL journaled by DbUp, no EF migrations |
| anything that creates a table | [0002](./docs/adr/0002-framework-tables-are-migrated-domain-tables-are-bootstrapped.md), which half of the split you are in |
| a new data model | [0003](./docs/adr/0003-a-model-and-its-persistent-mapping-are-a-pair.md), and [0001](./docs/adr/0001-schema-changes-are-hand-written-sql.md) for the migration it needs |
| a table for a feature package | `Trax.Docs/adr/0009`, the DDL ships in the core provider set or it never runs |

Decisions binding more than one repo live in the central corpus at `Trax.Docs/adr/`, whose
index lists them by repo. Eight of them name `effect`: executable guards, exact version
pinning, the dependency direction, the three test conventions (FluentAssertions, no
`[Ignore]`, no fixed delays), the canonical train name being the interface FullName, and
the documentation lints. In a workspace checkout the index is at
`../Trax.Docs/adr/README.md`; that path does not resolve on GitHub, because it crosses a
repository boundary.

## When your change makes a decision

Most changes do not. When one does (reversing it would cost something real, a future reader
would ask why it is like this, and there were genuine alternatives), it takes five steps and
the build enforces four. The `adr-guard` job runs on every pull request.

| | Step | Enforced |
| --- | --- | --- |
| 1 | Notice you made a decision, and write the ADR | no, this is the human step |
| 2 | Tag it `areas`, and add it to `docs/adr/README.md` | yes |
| 3 | Say where it stands in `## Status` and record it in `## Changelog` | yes |
| 4 | Give it `## Exemplars`: guards, `**Enforced elsewhere:**`, or `**Unenforced:**` with a reason | yes |
| 5 | Have each guard you named cite the ADR back, in its docstring and its failure message | yes |

Step 1 is the only one you have to remember, because no test can detect a decision you chose
not to record. The format is
[`.claude/skills/recording-decisions/ADR-FORMAT.md`](./.claude/skills/recording-decisions/ADR-FORMAT.md).

## Guards

`tests/Trax.Effect.Tests.Meta/` holds the convention guards. Eleven of the thirteen are
shared with other repos and enforce workspace-wide rules: nine appear in all eight code
repos, `TraxPinLockstepTests` in five and `BuilderPartialSplitTests` in three. Only
`MigrationsIntegrityTests` and `ModelPersistentPairingTests` are unique to this repo.

The census (every guard credited to an ADR or explicitly opted out) is **not** switched on
here yet. Trax.Docs runs it over its own guards; this repo will once the shared copies carry
citations of the central ADRs they enforce.

`tests/Trax.Effect.StateMachine.Persistence.Integration/MigrationSchemaTests.cs` is the
model-versus-DDL drift guard and needs a live Postgres. `docker compose up -d` provides one.

## Running the tests

```bash
docker compose up -d          # Postgres and RabbitMQ for the integration suites
dotnet test
```
