using Trax.Effect.Configuration.TraxEffectBuilder;
using Microsoft.Extensions.Logging;

namespace Trax.Effect.Extensions;

public static class TraxEffectConfigurationBuilderExtensions
{
    public static TraxEffectConfigurationBuilder SetEffectLogLevel(
        this TraxEffectConfigurationBuilder builder,
        LogLevel logLevel
    )
    {
        builder.LogLevel = logLevel;
        return builder;
    }
}
