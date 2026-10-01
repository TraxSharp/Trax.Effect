namespace Trax.Effect.Decisions.SystemOne;

/// <summary>
/// Where and how <see cref="SystemOneDecider"/> reaches a typed decision model.
/// </summary>
public sealed class SystemOneOptions
{
    /// <summary>
    /// The model's System One endpoint, for example <c>https://api.typesafe.ai/v1/systemone</c>
    /// for Jev, or a self-hosted Laya or Kev at <c>http://localhost:8080/v1/systemone</c>.
    /// Required. Must be HTTPS unless it is a loopback address.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The bearer token, or null for an endpoint that needs none. Never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The model, pinned to a version, for example <c>jev-1.13.0</c>. Required. A confidence
    /// threshold tuned against one version does not carry over to the next, so a floating alias
    /// such as <c>jev-latest</c> is refused unless <see cref="AllowFloatingModel"/> is set.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Accepts a floating model alias. Off by default.</summary>
    public bool AllowFloatingModel { get; set; }

    /// <summary>How long one attempt may take before it is abandoned and retried. Defaults to ten seconds.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times a request is tried, counting the first, when the model is throttled,
    /// unavailable or slow. Defaults to three.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// The wait before the first retry, doubling after each, unless the model says how long to
    /// wait. Defaults to half a second.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The most options one question may offer. The System One format allows 255.</summary>
    public int MaxOptions { get; set; } = 255;

    /// <summary>
    /// The most questions one request may carry. Nimble accepts 64, so a <c>Decide</c> asking more
    /// is refused before it is sent rather than answered with a 422.
    /// </summary>
    public int MaxQuestions { get; set; } = 64;

    /// <summary>
    /// How many requests may be in flight at once, or null for no limit. Hosted Nimble allows 8
    /// per organisation; a limit here makes a busy host queue its decisions instead of being
    /// answered 429.
    /// </summary>
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>
    /// The settings, checked: what is wrong with them, or nothing.
    /// </summary>
    internal IEnumerable<string> Problems()
    {
        if (Endpoint is null)
            yield return "Endpoint is required.";
        else if (!Endpoint.IsAbsoluteUri)
            yield return $"Endpoint '{Endpoint}' is not an absolute URL.";
        else if (Endpoint.Scheme != Uri.UriSchemeHttps && !Endpoint.IsLoopback)
            yield return $"Endpoint '{Endpoint}' is not HTTPS. Only a loopback address may use HTTP, "
                + "because the request carries the train's state and the API key.";

        if (string.IsNullOrWhiteSpace(Model))
            yield return "Model is required, pinned to a version (for example jev-1.13.0).";
        else if (!AllowFloatingModel && IsFloating(Model))
            yield return $"Model '{Model}' is a floating alias. Pin a version, because confidence "
                + "thresholds are tuned against one, or set AllowFloatingModel.";

        if (AttemptTimeout <= TimeSpan.Zero)
            yield return "AttemptTimeout must be positive.";

        if (MaxAttempts < 1)
            yield return "MaxAttempts must be at least 1.";

        if (RetryDelay < TimeSpan.Zero)
            yield return "RetryDelay cannot be negative.";

        if (MaxOptions is < 2 or > 255)
            yield return "MaxOptions must be between 2 and 255.";

        if (MaxQuestions < 1)
            yield return "MaxQuestions must be at least 1.";

        if (MaxConcurrentRequests is < 1)
            yield return "MaxConcurrentRequests must be at least 1, or null for no limit.";
    }

    private static bool IsFloating(string model) =>
        model.EndsWith("latest", StringComparison.OrdinalIgnoreCase) || !model.Any(char.IsDigit);
}
