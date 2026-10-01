using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;

namespace Trax.Effect.Decisions.SystemOne;

/// <summary>
/// Answers a train's questions through the System One request format: one state and a set of
/// typed questions in, typed answers with calibrated probabilities out. Jev introduced the format;
/// d1, Laya, Kev, OpenDecider and other typed decision models accept it, hosted or self-hosted.
/// </summary>
/// <remarks>
/// A choice is sent as <c>choice</c>, a scale as <c>score</c> and a yes/no as <c>noul</c>, with
/// each option's description as its criterion. Every answer carries the model and version the
/// response names, so a decision can be traced to the model that made it.
///
/// <para>A throttled, unavailable or slow model is retried, honouring <c>Retry-After</c>. When
/// retries run out the failure is classified transient; a request the model refuses (bad input, a
/// bad key) is classified permanent and not retried. Cancelling the train cancels the request.</para>
/// </remarks>
public sealed class SystemOneDecider : IDecider, IDisposable
{
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    private readonly SystemOneOptions _options;

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly SemaphoreSlim? _slots;

    /// <summary>Creates a decider with an HTTP client of its own.</summary>
    /// <exception cref="ArgumentException">The options are not usable.</exception>
    public SystemOneDecider(SystemOneOptions options)
        : this(
            options,
            new HttpClient(
                new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }
            )
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            ownsHttp: true
        ) { }

    /// <summary>Creates a decider that sends through <paramref name="http"/>, which it does not dispose.</summary>
    /// <exception cref="ArgumentException">The options are not usable.</exception>
    public SystemOneDecider(SystemOneOptions options, HttpClient http)
        : this(options, http, ownsHttp: false) { }

    private SystemOneDecider(SystemOneOptions options, HttpClient http, bool ownsHttp)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);

        if (options.Problems().ToList() is { Count: > 0 } problems)
            throw new ArgumentException(
                $"SystemOneDecider cannot be used: {string.Join(" ", problems)}",
                nameof(options)
            );

        _options = options;
        _http = http;
        _ownsHttp = ownsHttp;
        _slots = options.MaxConcurrentRequests is { } limit
            ? new SemaphoreSlim(limit, limit)
            : null;
    }

    /// <inheritdoc />
    public async Task<DecisionResult> Decide(
        DecisionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Questions.Count > _options.MaxQuestions)
            throw Refused(
                request,
                $"it asks {request.Questions.Count} questions in one request, and the model "
                    + $"accepts at most {_options.MaxQuestions}. Split the Decide",
                FailureClass.Permanent
            );

        string body;

        try
        {
            body = Request(request).ToJsonString();
        }
        catch (ArgumentException tooMany)
        {
            throw Refused(request, tooMany.Message, FailureClass.Permanent);
        }

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string failure;

            // A slot is held for one attempt, not across the wait before a retry, and the wait for
            // it does not count against the attempt's timeout.
            if (_slots is not null)
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.AttemptTimeout);

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                if (_options.ApiKey is { } key)
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                using var response = await _http
                    .SendAsync(message, timeout.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return Read(
                        await response
                            .Content.ReadAsStringAsync(timeout.Token)
                            .ConfigureAwait(false),
                        request
                    );

                var status = (int)response.StatusCode;
                var detail = await Detail(response, timeout.Token).ConfigureAwait(false);
                failure = $"the model answered {status} {response.ReasonPhrase}{detail}";

                if (!IsRetryable(response.StatusCode))
                    throw Refused(request, failure, FailureClass.Permanent);

                retryAfter = RetryAfter(response.Headers.RetryAfter);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failure =
                    $"the model did not answer within {_options.AttemptTimeout.TotalSeconds:0.#}s";
            }
            catch (HttpRequestException e)
            {
                failure = $"the model could not be reached: {e.Message}";
            }
            finally
            {
                _slots?.Release();
            }

            if (attempt >= _options.MaxAttempts)
                throw Refused(
                    request,
                    $"{failure}, after {attempt} attempt{(attempt == 1 ? "" : "s")}",
                    FailureClass.Transient
                );

            var delay = retryAfter ?? _options.RetryDelay * Math.Pow(2, attempt - 1);
            await Task.Delay(Min(delay, TimeSpan.FromSeconds(30)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _slots?.Dispose();

        if (_ownsHttp)
            _http.Dispose();
    }

    private JsonObject Request(DecisionRequest request)
    {
        var questions = new JsonObject();

        foreach (var question in request.Questions)
            questions[question.Key] = question switch
            {
                ChoiceQuestion choice => new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = choice.Instructions,
                    ["criteria"] = new JsonObject(
                        Limit(choice, choice.Options)
                            .Select(o =>
                                KeyValuePair.Create(o.Name, (JsonNode?)(o.Description ?? o.Name))
                            )
                    ),
                },
                ScoreQuestion score => new JsonObject
                {
                    ["type"] = "score",
                    ["instructions"] = score.Instructions,
                    ["criteria"] = new JsonArray(
                        Limit(score, score.Levels)
                            .Select(l => (JsonNode?)(l.Description ?? l.Name))
                            .ToArray()
                    ),
                },
                YesNoQuestion yesNo => new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = yesNo.Instructions,
                    ["criteria"] = new JsonObject
                    {
                        ["true"] = yesNo.Yes ?? "Yes",
                        ["false"] = yesNo.No ?? "No",
                    },
                },
                _ => throw new NotSupportedException(
                    $"SystemOneDecider cannot ask a {question.GetType().Name}."
                ),
            };

        return new JsonObject
        {
            ["model"] = _options.Model,
            ["state"] = request.State is string text
                ? JsonValue.Create(text)
                : JsonSerializer.SerializeToNode(request.State, StateJson),
            ["questions"] = questions,
        };
    }

    private IReadOnlyList<Criterion> Limit(Question question, IReadOnlyList<Criterion> criteria) =>
        criteria.Count <= _options.MaxOptions
            ? criteria
            : throw new ArgumentException(
                $"the question '{question.Key}' offers {criteria.Count} options, and the model "
                    + $"accepts at most {_options.MaxOptions}"
            );

    /// <summary>
    /// Reads the answers out of a response. An answer that cannot be read is left out, so the
    /// train fails on it as unanswered rather than acting on a guess.
    /// </summary>
    private static DecisionResult Read(string json, DecisionRequest request)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        var model = root?["model"]?.GetValue<string>();
        var answers = new Dictionary<string, Answer>();

        if (root?["answers"] is not JsonObject given)
            return new DecisionResult(answers);

        foreach (var question in request.Questions)
            if (given[question.Key] is JsonObject node && Answer(node) is { } answer)
                answers[question.Key] = answer with { Model = model };

        return new DecisionResult(answers);
    }

    private static Answer? Answer(JsonObject node)
    {
        try
        {
            return (string?)node["type"] switch
            {
                "choice" => new ChoiceAnswer(
                    node["choice"]!.GetValue<string>(),
                    node["confidence"]!.GetValue<double>(),
                    node["probabilities"]
                        ?.AsObject()
                        .ToDictionary(p => p.Key, p => p.Value!.GetValue<double>())
                ),
                "score" => new ScoreAnswer(
                    node["score"]!.GetValue<double>(),
                    node["confidence"]!.GetValue<double>(),
                    node["probabilities"]
                        ?.AsObject()
                        .OrderBy(p => int.Parse(p.Key, CultureInfo.InvariantCulture))
                        .Select(p => p.Value!.GetValue<double>())
                        .ToList()
                ),
                "noul" => new YesNoAnswer(node["noul"]!.GetValue<double>()),
                _ => null,
            };
        }
        catch (Exception e)
            when (e is NullReferenceException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header) =>
        header?.Delta
        ?? (header?.Date is { } date ? Max(date - DateTimeOffset.UtcNow, TimeSpan.Zero) : null);

    /// <summary>
    /// What the model said about a failed request: its <c>detail</c> and <c>request_id</c> when
    /// the body carries them, as Nimble's errors do, otherwise the start of the body. The request
    /// id is what the model's provider asks for when a failure is reported to them.
    /// </summary>
    private static async Task<string> Detail(HttpResponseMessage response, CancellationToken ct)
    {
        string text;

        try
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }

        if (string.IsNullOrWhiteSpace(text))
            return "";

        try
        {
            if (JsonNode.Parse(text) is JsonObject body)
            {
                var detail = body["detail"] is JsonValue value
                    ? value.ToString()
                    : body["detail"]?.ToJsonString();
                var id = body["request_id"]?.ToString();

                if (detail is not null || id is not null)
                    return (detail is null ? "" : $": {Clip(detail)}")
                        + (id is null ? "" : $" (request {id})");
            }
        }
        catch (JsonException)
        {
            // Not JSON; the text itself is the detail.
        }

        return $": {Clip(text)}";
    }

    private static string Clip(string text) => text.Length > 300 ? text[..300] + "…" : text;

    /// <summary>
    /// A failure carrying its class, which Trax keeps when it records the run's failure.
    /// </summary>
    private static DecisionServiceException Refused(
        DecisionRequest request,
        string reason,
        FailureClass failureClass
    )
    {
        var exception = new DecisionServiceException(
            $"The decision model could not answer train '{request.Train}': {reason}."
        );

        exception.Data["TrainExceptionData"] = new TrainExceptionData
        {
            TrainName = request.Train,
            TrainExternalId = "",
            Type = nameof(DecisionServiceException),
            Junction = nameof(SystemOneDecider),
            Message = exception.Message,
            FailureClass = failureClass,
        };

        return exception;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>
/// The decision model could not answer. Its failure class says whether trying again later can help.
/// </summary>
public sealed class DecisionServiceException(string message) : Exception(message);
