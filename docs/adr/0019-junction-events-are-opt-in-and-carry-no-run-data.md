---
authors: [Theauxm]
areas: [platform, data-model]
status: accepted
---

# Junction events are opt-in, carry no run data, and are kept as long as their run

A run's steps (each junction starting and ending, each question a routing step asks, each track it
takes) are published live and stored in `trax.junction_run` only on a host that calls
`AddJunctionEvents()`. They ride the existing broadcaster transport inside
`TrainLifecycleEventMessage`, under event types of their own and a `Junction` payload, and reach
only `IJunctionEventHandler`s. They carry names, positions, times, states, a failed junction's
failure class and exception type, and a question's answer summary, and never anything the run was
given or produced. Their rows go when the run's metadata row goes.

## Status

**Accepted.**

## Why this is written down

Because every one of these is the easy thing to loosen, and each loosening is a leak.

**Opt-in.** Every junction becomes up to two messages and two writes. A host that never shows a
timeline should not pay for one, and a consumer that subscribed to train events years ago should
not start receiving a new kind of event because a package was upgraded.

**A sibling event, not a new train event.** Before this, `TrainEventReceiverService` handed every
message to every `ITrainEventHandler`, and the shipped handlers treat an event type they do not know
differently: Trax.Api's GraphQL handler logs a warning per message, and the SignalR sink forwarded
anything that was not a data-change signal to every connected client. Adding `JunctionStarted` as
one more train event type would have reached both. So the receiver routes a junction event only to
`IJunctionEventHandler`s, the SignalR sink sends one only when configured with
`WithJunctionEvents()`, and nothing that handled train events before sees one now. They share the
transport so they keep their order relative to the run's own events, and the RabbitMQ queue drops
them first when it is full, so a busy run's steps never cost another run its outcome.

**What a step carries.** A junction's input and output, the train's input and output, a failure's
message and the state a decider was shown can all hold user data, and a step goes to other
processes and, through the SignalR sink, to browsers. Train events already keep a failure reason
out of the default SignalR payload (`0016`) and present it by exception type in the API. A step
carries the exception's type and its failure class and nothing else about the failure. A question's
answer is new information that train events never carried, so `[TraxSensitive]` on the enum or
marker type a routing step asks about withholds it, and the track taken with it, from both the
event and the row (`0010` is extended to cover types for this purpose only). The mark is honoured
wherever the question's key is built from a marked type: a closed form of a marked generic type, a
type nested in one, a type that takes one as an argument, and a type that inherits the mark. It
fails closed: a key that shares a name with a marked type is withheld too.

**What reaches a browser.** The SignalR sink sends every train's events to every connected client,
so its junction payload carries a question's key and whether it was replayed, but not its answer
or confidence. A host that wants answers in front of every client says so with
`WithJunctionAnswers()`, and a host that redacts its train events can shape junction events the
same way with `WithJunctionProjection()`. When the sink's queue is full it gives up junction events
before a train's own events, as the RabbitMQ queue does.

**Retention.** A step belongs to its run. The foreign key cascades, as `trax.decision`'s does, so
every existing delete of metadata, the scheduler's cleanup and manifest pruning included, removes
the steps without knowing the table exists.

## Considered options

**A new train event type with a nullable payload, delivered to every handler.** Rejected for the
reasons above: it changes what existing handlers receive.

**A separate message type and transport (its own exchange).** Rejected: a second RabbitMQ
broadcaster and receiver for the same payloads, and the steps would lose their order relative to
the run's own events.

**Writing a step on the run's path.** Rejected: a database that is slow or down would stall every
junction for its connection timeout. A background writer queues steps (4096), writes them in order
and in batches through a context of its own (never the run's tracked entities, so it never commits
a run's writes mid-run), and drops a step, counted and logged, rather than hold up a run.

## Consequences

**The stored timeline can trail the live one.** A late subscriber subscribes first, reads
`JunctionRuns.ForRun(id)`, and merges by position. A dropped step is missing, never wrong.

**Only `EffectJunction`s are steps.** A plain `Junction` in a service train runs no junction effects
and reports nothing. A junction skipped because an earlier one failed is not a step.

**Routing steps are told by their question.** Trax.Core reports a decision to the observer without
naming the step that asked, so a step is a `Choice` (Decide, Switch), `Score` (Scale) or `YesNo`
(Gate), and each track taken adds a `Route`.

**Withholding an answer does not hide the path.** The junctions a track runs are named in their own
steps. A host whose track choice is itself secret should not turn junction events on.

**Decision observers compose.** Trax.Core finds one `IDecisionObserver` in the container. Trax now
registers a composite that tells every observer registered before `AddTrax` and every one Trax adds
(decision recording, junction events): required ones first, so a decision that could not be
recorded is not reported as made, then best-effort ones.

**A manifest's run carries its attempt.** When the run begins, one query on a context of its own
counts the manifest's failed runs since its last completed or cancelled one, skipping the dispatch
attempts Trax.Scheduler requeued (`DispatchRequeued`), and the run's steps and rows carry 1 plus that.
A run with no manifest carries none rather than 1, because it is no attempt of anything, and a query
that fails leaves the attempt out and the run alone.

**Publishing never fails a run.** A store, transport or handler failure is logged and swallowed.
Local handlers run on the run's path and must return quickly.

## Exemplars

- `JunctionEventsTests` pins the order, what a step carries and never carries, the withheld answer,
  that it is off by default, that a failing handler or transport does not change the run, and that
  decision observers compose.
- `JunctionEventRoutingTests` pins that a junction event reaches junction event handlers only.
- `SignalRJunctionEventTests` pins that the SignalR sink sends steps only when asked, through the
  train filter it already applies, without answers unless asked for them, through a host's own
  projection when it has one, and that its full queue gives up steps first.
- `PostgresJunctionRunTests` and `SqliteJunctionRunTests` pin that the rows read back in order and
  go with their run.

Not covered: nothing stops a future field on `JunctionEventPayload` from carrying run data; the
tests check the fields that exist against known secrets, not the shape of every future field.

## Changelog

- **2026-10-02**: Recorded.
- **2026-10-02**: A manifest's run carries its attempt.
- **2026-10-02**: Sensitive question types are matched in every form of their key; the SignalR payload leaves answers out unless asked, and its queue drops steps first.
