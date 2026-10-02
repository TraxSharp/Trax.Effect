using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Trax.Core.Decisions;
using Trax.Effect.Configuration.TraxEffectBuilder;
using Trax.Effect.Data.Decisions;
using Trax.Effect.Data.JunctionEvents;
using Trax.Effect.Data.Services.DataContextLoggingProvider;
using Trax.Effect.Extensions;
using Trax.Effect.Services.Decisions;
using Trax.Effect.Services.JunctionEvents;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Data.Extensions;

/// <summary>
/// Provides extension methods for configuring Trax.Effect.Data services in the dependency injection container.
/// </summary>
/// <remarks>
/// The ServiceExtensions class contains utility methods that simplify the registration
/// of Trax.Effect.Data services with the dependency injection system.
///
/// These extensions enable:
/// 1. Easy configuration of data context logging
/// 2. Consistent service registration across different applications
/// 3. Integration with the Trax.Effect configuration system
///
/// By using these extensions, applications can easily configure and use the
/// Trax.Effect.Data system with minimal boilerplate code.
/// </remarks>
public static class ServiceExtensions
{
    /// <summary>
    /// Registers an <see cref="ILoggerProvider"/> that stores the host's <see cref="ILogger"/> messages
    /// in the <c>trax.log</c> table, readable through <c>IDataContext.Logs</c>.
    /// Requires a data provider (<c>UsePostgres()</c>, <c>UseSqlite()</c>, or <c>UseInMemory()</c>) to have been configured first.
    /// </summary>
    /// <param name="configurationBuilder">
    /// The effect builder with a data provider configured. This method is only available
    /// after calling <c>UsePostgres()</c>, <c>UseSqlite()</c>, or <c>UseInMemory()</c>, which promotes the builder
    /// to <see cref="TraxEffectBuilderWithData"/>.
    /// </param>
    /// <param name="minimumLogLevel">The minimum log level to capture (defaults to Information if not specified)</param>
    /// <param name="blacklist">
    /// Logger categories not to store: an exact category name, or a pattern in which <c>*</c> matches
    /// any run of characters.
    /// </param>
    /// <returns>The configuration builder for method chaining</returns>
    /// <remarks>
    /// It is a sink for application logging, not a trace of the data context: every
    /// <see cref="ILogger"/> category at or above <paramref name="minimumLogLevel"/> and not
    /// blacklisted is stored, whatever wrote it. It does not record SQL or transaction boundaries.
    /// EF Core's own command log (<c>Microsoft.EntityFrameworkCore.Database.Command</c>) is always
    /// skipped, because writing a row would log another one.
    ///
    /// Entries are queued in memory (4096, oldest dropped when full) and written in batches by
    /// <see cref="DataContextLoggingProvider"/>, which stores what is queued when the host stops.
    ///
    /// Example usage:
    /// ```csharp
    /// services.AddTrax(trax => trax
    ///     .AddEffects(effects => effects
    ///         .UsePostgres(connectionString)
    ///         .AddDataContextLogging(
    ///             minimumLogLevel: LogLevel.Information,
    ///             blacklist: ["Microsoft.EntityFrameworkCore.*"]
    ///         )
    ///     )
    /// );
    /// ```
    ///
    /// Calling this method before a data provider is a compile-time error that says so: it is
    /// defined on <see cref="TraxEffectBuilderWithData"/>, and the earlier stage binds to an
    /// overload in <see cref="BuilderOrderExtensions"/> that only carries the instruction.
    /// </remarks>
    public static TraxEffectBuilderWithData AddDataContextLogging(
        this TraxEffectBuilderWithData configurationBuilder,
        LogLevel? minimumLogLevel = null,
        List<string>? blacklist = null
    )
    {
        // Create and register the logging configuration
        var credentials = new DataContextLoggingProviderConfiguration
        {
            MinimumLogLevel = minimumLogLevel ?? LogLevel.Information,
            Blacklist = blacklist ?? [],
        };

        configurationBuilder
            .ServiceCollection.AddSingleton<IDataContextLoggingProviderConfiguration>(credentials)
            .AddSingleton<ILoggerProvider, DataContextLoggingProvider>();

        return configurationBuilder;
    }

    /// <summary>
    /// Records every decision a train makes (each question it asks a decider, the answer it acts
    /// on, any shadow's answer, and the track it takes) in <c>trax.decision</c> against the run,
    /// and makes a requeued run replay its original's decisions.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="DecisionJournal"/> as the <see cref="IDecisionObserver"/> and
    /// <see cref="IDecisionReplay"/> that a train's <c>Decide</c>, <c>Switch</c>, <c>Gate</c> and
    /// <c>Scale</c> steps find in the container. Each decision is written, through a data context
    /// of its own, before the train acts on it, and a decision that cannot be written fails its
    /// step, classified transient. Each run's row is marked <c>DecisionsRecorded</c>. A run queued
    /// with <c>ReplayDecisionsOf</c> loads, when it starts, the answers of that run and, for
    /// questions it never reached, of the runs it replayed in turn; it fails, classified
    /// permanent, when a run in that chain does not exist, belongs to another train, or ran
    /// without recording its decisions. Calling this more than once registers it once.
    ///
    /// <para>The journal is told about each decision alongside every other
    /// <see cref="IDecisionObserver"/>: one the host registered before this call, and the one
    /// <c>AddJunctionEvents</c> adds. It is told first, because the decision is not acted on unless it
    /// is written. An observer registered as <see cref="IDecisionObserver"/> after <c>AddTrax</c>
    /// replaces them all, as the container would; register it before.</para>
    /// </remarks>
    public static TraxEffectBuilderWithData AddDecisionRecording(
        this TraxEffectBuilderWithData configurationBuilder
    )
    {
        var services = configurationBuilder.ServiceCollection;

        services.TryAddSingleton<DecisionJournal>();
        // Beside any other observer (a host's own, junction events), never in place of one.
        DecisionObservers.Add(
            services,
            typeof(DecisionJournal),
            sp => sp.GetRequiredService<DecisionJournal>(),
            ServiceLifetime.Singleton
        );
        services.TryAddSingleton<IDecisionReplay>(sp => sp.GetRequiredService<DecisionJournal>());
        services.TryAddSingleton<IDecisionRunRecorder>(sp =>
            sp.GetRequiredService<DecisionJournal>()
        );

        return configurationBuilder;
    }

    /// <summary>
    /// Publishes each step of every run, live, and records it in <c>trax.junction_run</c>: each
    /// junction as it starts and ends, each question a routing step asks (<c>Decide</c>,
    /// <c>Switch</c>, <c>Gate</c>, <c>Scale</c>) and the answer the run acts on, and each track it
    /// takes. Off unless this is called.
    /// </summary>
    /// <remarks>
    /// <para><b>What is published.</b> Each step is a <see cref="TrainLifecycleEventMessage"/> with a
    /// junction event type (<c>JunctionStarted</c>, <c>JunctionCompleted</c>, <c>JunctionFailed</c>,
    /// <c>JunctionCancelled</c>, <c>Decided</c>, <c>DecisionRefused</c>, <c>Routed</c>) and the step
    /// in <see cref="TrainLifecycleEventMessage.Junction"/>. It goes to the host's
    /// <see cref="IJunctionEventHandler"/>s on the run's path, and over the transport
    /// <c>UseBroadcaster</c> configured, when there is one, to the junction event handlers of other
    /// hosts. It never reaches an <see cref="ITrainEventHandler"/>, and the SignalR sink sends it to
    /// clients only when configured with <c>WithJunctionEvents()</c>.</para>
    ///
    /// <para><b>What is never published or stored:</b> a junction's input or output, the train's
    /// input or output, a failure's message, or the state, instructions or criteria of a question.
    /// A failed junction is described by its exception's type and its failure class. A question's
    /// answer is summarised (the option, score or probability of yes, and the confidence), and left
    /// out entirely, track included, for a question about a type marked <c>[TraxSensitive]</c>.
    /// The junctions a track runs are still named in their own steps.</para>
    ///
    /// <para><b>What it costs a run.</b> Nothing it does can fail a run or change a junction's
    /// result: a failure to store, broadcast or hand out a step is logged and swallowed. A step is
    /// stored by a background writer, so the run never waits on the database; the shipped
    /// transports queue rather than wait too. Local handlers run on the run's path and must return
    /// quickly. A junction skipped because an earlier one failed is not a step.</para>
    ///
    /// <para><b>Attempt.</b> A run of a manifest carries which attempt it is: 1 plus the
    /// manifest's failed runs since its last completed or cancelled one, read once when the run
    /// begins. A run with no manifest carries none. A failure to read it is logged, and the run's
    /// events carry none.</para>
    ///
    /// <para><b>Attempt.</b> A run of a manifest carries which attempt it is: 1 plus the
    /// manifest's failed runs since its last completed or cancelled one, read once when the run
    /// begins. A run with no manifest carries none. A failure to read it is logged, and the run's
    /// events carry none.</para>
    ///
    /// <para><b>Retention.</b> A step's row is deleted with its run's metadata row, by the foreign
    /// key's cascade, so every existing delete of metadata removes it.</para>
    ///
    /// <para>Calling this more than once registers it once. Its decision observer is told alongside
    /// any other, after the ones that are required, such as <c>AddDecisionRecording</c>'s.</para>
    /// </remarks>
    public static TraxEffectBuilderWithData AddJunctionEvents(
        this TraxEffectBuilderWithData configurationBuilder
    )
    {
        var services = configurationBuilder.ServiceCollection;

        if (services.Any(d => d.ServiceType == typeof(JunctionEventPublisher)))
            return configurationBuilder;

        services.AddSingleton<JunctionEventPublisher>();
        services.AddSingleton<JunctionRunWriter>();
        services.AddSingleton<IJunctionRunSink>(sp => sp.GetRequiredService<JunctionRunWriter>());
        services.AddSingleton<IRunAttempts, RunAttempts>();
        services.AddSingleton<IRunAttempts, RunAttempts>();
        // Stopping the host drains the steps still queued for the database.
        services.AddHostedService(sp => sp.GetRequiredService<JunctionRunWriter>());

        DecisionObservers.Add(
            services,
            typeof(JunctionEventDecisionObserver),
            _ => new JunctionEventDecisionObserver(),
            ServiceLifetime.Singleton
        );

        return configurationBuilder;
    }
}
