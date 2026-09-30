# Trax.Effect

[![Build](https://github.com/TraxSharp/Trax.Effect/actions/workflows/nuget_release.yml/badge.svg?branch=main)](https://github.com/TraxSharp/Trax.Effect/actions/workflows/nuget_release.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/Trax.Effect)](https://www.nuget.org/packages/Trax.Effect)
[![codecov](https://codecov.io/gh/TraxSharp/Trax.Effect/branch/main/graph/badge.svg)](https://codecov.io/gh/TraxSharp/Trax.Effect)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/TraxSharp/Trax.Effect/blob/main/LICENSE)
[![Docs](https://img.shields.io/badge/docs-traxsharp.net-blue)](https://traxsharp.net/docs/effect)

> Part of [Trax](https://github.com/TraxSharp): business logic you can call, schedule, or serve as an API, with every
> run recorded in your Postgres. [Docs](https://traxsharp.net/docs) · [Getting started](https://traxsharp.net/docs/getting-started) · [All repos](https://github.com/TraxSharp)

Trax.Effect adds run records, dependency injection and storage providers to Trax trains, plus the portable state-machine engine. Its `ServiceTrain` runs the same chain as a [Trax.Core](https://github.com/TraxSharp/Trax.Core) `Train`, resolves junctions from DI, and writes a metadata row for every run. Trax.Mediator, Trax.Scheduler and the layers above it all run trains through it.

## Install

```bash
dotnet add package Trax.Effect
dotnet add package Trax.Effect.Data.Postgres   # or Trax.Effect.Data.Sqlite, or Trax.Effect.Data.InMemory
```

Install one storage package, since runs are recorded only through one: Postgres for production, SQLite for a single process, InMemory for tests.

## Example

Adapted from the game server sample:

```csharp
builder.Services.AddTrax(trax => trax
    .AddEffects(effects => effects
        .UsePostgres(connectionString)
        .SaveTrainParameters()
        .AddJunctionLogger()));

builder.Services.AddScopedTraxRoute<IRecalculateLeaderboardTrain, RecalculateLeaderboardTrain>();

public interface IRecalculateLeaderboardTrain
    : IServiceTrain<RecalculateLeaderboardInput, RecalculateLeaderboardOutput>;

public class RecalculateLeaderboardTrain
    : ServiceTrain<RecalculateLeaderboardInput, RecalculateLeaderboardOutput>,
        IRecalculateLeaderboardTrain
{
    protected override Task<Either<Exception, RecalculateLeaderboardOutput>> Junctions() =>
        Chain<AggregateScoresJunction>()
            .Chain<RankPlayersJunction>()
            .Resolve();
}

public class RankPlayersJunction(ILogger<RankPlayersJunction> logger)
    : Junction<RecalculateLeaderboardInput, RecalculateLeaderboardOutput> { /* ... */ }
```

Inject `IRecalculateLeaderboardTrain` and call `Run(input)`. Each run writes a row to `trax.metadata`: when it started,
how it ended, and on failure the junction that threw and its exception. `SaveTrainParameters()` adds the input and output.

## License

MIT. There is no commercial edition, and there will not be one.

Trax is an independent open-source project and is not affiliated with the Utah Transit Authority, Trax Retail, or any
other organization using the Trax name.
