using System.Text.Json;
using Trax.Effect.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Trax.Effect.Configuration.TraxEffectConfiguration;

public class TraxEffectConfiguration : ITraxEffectConfiguration
{
    public JsonSerializerOptions SystemJsonSerializerOptions { get; set; } =
        TraxJsonSerializationOptions.Default;

    public JsonSerializerSettings NewtonsoftJsonSerializerSettings { get; set; } =
        TraxJsonSerializationOptions.NewtonsoftDefault;

    public static JsonSerializerOptions StaticSystemJsonSerializerOptions { get; set; } =
        JsonSerializerOptions.Default;

    public bool SerializeStepData { get; set; } = false;

    public LogLevel LogLevel { get; set; } = LogLevel.Debug;
}
