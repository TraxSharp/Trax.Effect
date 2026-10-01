namespace Trax.Effect.Decisions.SystemOne;

/// <summary>
/// Where <c>AddNimbleDecider</c> reaches Nimble, Bespoke Labs' open-weights typed decision model:
/// a local Ollama by default, or Bespoke's hosted API when an <see cref="ApiKey"/> is given.
/// </summary>
/// <remarks>
/// Nimble speaks the System One request format, so this resolves to <see cref="SystemOneOptions"/>
/// with Nimble's endpoints, models and limits filled in. Anything left unset takes the default for
/// where it runs.
/// </remarks>
public sealed class NimbleOptions
{
    /// <summary>Ollama's System One endpoint on this machine.</summary>
    public static readonly Uri LocalEndpoint = new("http://localhost:11434/v1/systemone");

    /// <summary>Bespoke Labs' hosted System One endpoint.</summary>
    public static readonly Uri HostedEndpoint = new("https://api.bespokelabs.ai/v1/systemone");

    /// <summary>The Ollama model: Nimble 9B, the open weights.</summary>
    public const string LocalModel = "nimble:9b";

    /// <summary>The hosted model, pinned to its version.</summary>
    public const string HostedModel = "nimble-v3";

    /// <summary>How many requests Bespoke's hosted API accepts at once for one organisation.</summary>
    public const int HostedConcurrentRequests = 8;

    /// <summary>
    /// A Bespoke Labs API key. Setting it selects the hosted API; leaving it null selects a local
    /// Ollama. Never logged.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The endpoint, when it is neither of the defaults: an Ollama on another host, or a gateway in
    /// front of either. Must be HTTPS unless it is a loopback address.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// The model. Defaults to <see cref="LocalModel"/> locally and <see cref="HostedModel"/> hosted.
    /// Pin it: a floating name such as <c>nimble-latest</c> or a bare <c>nimble</c> is refused.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// How many requests may be in flight at once. Defaults to
    /// <see cref="HostedConcurrentRequests"/> hosted and to no limit locally.
    /// </summary>
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>
    /// How long one attempt may take. Defaults to 30 seconds, longer than for a hosted model,
    /// because a local Nimble on a CPU or a small GPU answers in seconds rather than milliseconds.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a request is tried, counting the first. Defaults to three.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The wait before the first retry, doubling after each. Defaults to half a second.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>True when these options reach Bespoke's hosted API.</summary>
    public bool IsHosted => ApiKey is not null;

    internal SystemOneOptions ToSystemOne() =>
        new()
        {
            Endpoint = Endpoint ?? (IsHosted ? HostedEndpoint : LocalEndpoint),
            Model = Model ?? (IsHosted ? HostedModel : LocalModel),
            ApiKey = ApiKey,
            MaxConcurrentRequests =
                MaxConcurrentRequests ?? (IsHosted ? HostedConcurrentRequests : null),
            AttemptTimeout = AttemptTimeout,
            MaxAttempts = MaxAttempts,
            RetryDelay = RetryDelay,
            // Nimble's own limits: 64 questions a request, 2 to 255 options or levels a question.
            MaxQuestions = 64,
            MaxOptions = 255,
        };
}
