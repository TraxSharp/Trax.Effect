---
authors: [Theauxm]
areas: [platform]
status: accepted
---

# A service train does its work in Junctions(), and its Run cannot be overridden

`ServiceTrain.Run(input, ct)` is a sealed override, and `Run(input, metadata)` and
`Run(input, metadata, ct)` are not virtual. A service train's work is its `Junctions()` chain.
`Run` owns what wraps that chain: the metadata row, the lifecycle hooks, the typed input, the
failure classification and the outcome write on a token the caller cannot cancel
([0005](./0005-a-trains-outcome-is-recorded-on-an-uncancellable-token.md)). An override that
does its own work, or forgets to call `base.Run`, runs untracked while every surface still reports
the train as Trax's, and the declared chain the mediator validates at startup
(`Trax.Docs/adr/0016`) stops describing what the train does.

## Status

**Accepted.**

## Considered options

**Leave the overloads virtual and document the rule.** What was in place. Nothing in Trax needed
the extension point, and a rule enforced only by documentation is enforced by nobody.

**Seal plain `Train.Run` in Trax.Core as well.** Left for later. `Train.Run` is the extension
point `ServiceTrain` itself overrides, so it cannot be sealed on `Train`, and the base class has
other consumers whose `Run` overrides have not been surveyed. Doing it means a Core release that
has to move in lockstep with Effect.

## Consequences

**Binary-safe for published Trax.** Before sealing, no published Trax assembly overrode any of
the three overloads. The check covered every Trax package version in the local NuGet cache, 213
assemblies, reading method definitions from metadata, plus a source scan of every Trax repo's
`main`, tests and samples included. A caller compiled against the virtual overloads still binds,
because a call through `callvirt` to a method that is no longer virtual is valid.

**A consumer that overrides `Run` breaks, by design.** Their code stops compiling against this
version, and a consumer assembly already compiled with such an override fails to load against it.
It is a minor release, not a major one: the override was never a supported way to write a train,
and the break points at the replacement, which is `Junctions()` and the lifecycle hooks.

`NewMonad` stays a protected virtual on `ServiceTrain`, because the published Effect overrides it
from Core's base, and a train that overrides it again still runs its chain through `Run`.

## Exemplars

- `ServiceTrainRunIsSealedTests` fails if any `Run` reachable on `ServiceTrain` can be overridden,
  and pins the cancellation-token overload as a sealed override.
- The committed public API baseline (`Trax.Effect.received.txt`) records the modifiers.

Not covered: plain `Train<TIn, TOut>.Run` in Trax.Core stays virtual, so a train that does not
derive from `ServiceTrain` can still override it.

## Changelog

- **2026-09-27**: Recorded.
