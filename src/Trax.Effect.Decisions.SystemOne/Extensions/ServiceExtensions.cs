using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Decisions;
using Trax.Effect.Configuration.TraxEffectBuilder;

namespace Trax.Effect.Decisions.SystemOne.Extensions;

/// <summary>
/// Registers <see cref="SystemOneDecider"/> on the effect builder.
/// </summary>
public static class ServiceExtensions
{
    /// <summary>
    /// Answers every train's decisions through a typed decision model that speaks the System One
    /// request format (Jev, d1, Laya, Kev and others), by registering a
    /// <see cref="SystemOneDecider"/> as the <see cref="IDecider"/>.
    /// </summary>
    /// <remarks>
    /// The options are checked here, so a missing endpoint, an unpinned model or plain HTTP to a
    /// remote host stops the host from starting rather than failing the first decision. To put
    /// the model in front of a larger one, register a <see cref="CascadingDecider"/> as the
    /// <see cref="IDecider"/> instead, built from the <see cref="SystemOneDecider"/> registered
    /// here.
    /// <code>
    /// services.AddTrax(trax => trax.AddEffects(effects => effects
    ///     .UsePostgres(connectionString)
    ///     .AddDecisionRecording()
    ///     .AddSystemOneDecider(o =>
    ///     {
    ///         o.Endpoint = new Uri("https://api.typesafe.ai/v1/systemone");
    ///         o.Model = "jev-1.13.0";
    ///         o.ApiKey = configuration["Jev:ApiKey"];
    ///     })));
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentException">The configured options are not usable.</exception>
    public static TBuilder AddSystemOneDecider<TBuilder>(
        this TBuilder configurationBuilder,
        Action<SystemOneOptions> configure
    )
        where TBuilder : TraxEffectBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SystemOneOptions();
        configure(options);

        var decider = new SystemOneDecider(options);

        configurationBuilder.ServiceCollection.AddSingleton(decider);
        configurationBuilder.ServiceCollection.AddSingleton<IDecider>(sp =>
            sp.GetRequiredService<SystemOneDecider>()
        );

        return configurationBuilder;
    }

    /// <summary>
    /// Answers every train's decisions with Nimble, Bespoke Labs' open-weights typed decision
    /// model: a local Ollama running <c>nimble:9b</c> by default, or Bespoke's hosted API when an
    /// API key is given. Registers a <see cref="SystemOneDecider"/> as the <see cref="IDecider"/>.
    /// </summary>
    /// <remarks>
    /// <code>
    /// effects.AddNimbleDecider();                                   // local Ollama
    /// effects.AddNimbleDecider(o =&gt; o.ApiKey = configuration["Nimble:ApiKey"]);   // hosted
    /// </code>
    /// The local default needs <c>ollama pull nimble:9b</c> on the machine. The options are checked
    /// here, so an unpinned model or plain HTTP to a remote host stops the host from starting.
    /// </remarks>
    /// <exception cref="ArgumentException">The configured options are not usable.</exception>
    public static TBuilder AddNimbleDecider<TBuilder>(
        this TBuilder configurationBuilder,
        Action<NimbleOptions>? configure = null
    )
        where TBuilder : TraxEffectBuilder
    {
        var options = new NimbleOptions();
        configure?.Invoke(options);

        var decider = new SystemOneDecider(options.ToSystemOne());

        configurationBuilder.ServiceCollection.AddSingleton(decider);
        configurationBuilder.ServiceCollection.AddSingleton<IDecider>(sp =>
            sp.GetRequiredService<SystemOneDecider>()
        );

        return configurationBuilder;
    }
}
