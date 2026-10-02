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
/// d1, Laya, Kev, Nimble, OpenDecider and other typed decision models accept it, hosted or
/// self-hosted.
/// </summary>
/// <remarks>
/// A choice is sent as <c>choice</c>, a scale as <c>score</c> and a yes/no as <c>noul</c>, with
/// each option's description as its criterion. Every answer carries the model and version the
/// response names, so a decision can be traced to the model that made it.
///
/// <para>A choice or score answer without a <c>confidence</c>, which the format allows, takes the
/// probability of the chosen option or level instead; one with neither is left out. A score's
/// probabilities must name every level, from 0, or the answer is left out rather than shifted onto
/// the wrong levels.</para>
///
/// <para>A throttled, unavailable or slow model, a connection that is refused, reset or times out,
/// or a model that answers with something other than a System One response, is retried with a
/// jittered, doubling wait, honouring <c>Retry-After</c> up to
/// <see cref="SystemOneOptions.MaxRetryDelay"/>. When retries run out, or the model asks for a
/// longer wait than that, the failure is classified transient. A request the model refuses (bad
/// input, a bad key, a method or version it does not implement, a redirect), one that cannot be
/// sent at all, and an endpoint that cannot be reached as configured (its name does not resolve,
/// or its TLS handshake fails) are classified permanent and not retried. Cancelling the train
/// cancels the request.</para>
/// </remarks>
public sealed class SystemOneDecider : IDecider, IDisposable
{
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    private readonly SystemOneOptions _options;

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly SemaphoreSlim? _slots;

    private int _disposed;

    /// <summary>
    /// Creates a decider with an HTTP client of its own, which does not follow redirects: the
    /// request goes to the endpoint that was configured and nowhere else.
    /// </summary>
    /// <exception cref="ArgumentException">The options are not usable.</exception>
    public SystemOneDecider(SystemOneOptions options)
        : this(
            options,
            new HttpClient(
                new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    AllowAutoRedirect = false,
                }
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

        try
        {
            options.Check(nameof(options));
        }
        catch
        {
            if (ownsHttp)
                http.Dispose();
            throw;
        }

        _options = options.Copy();
        _http = http;
        _ownsHttp = ownsHttp;
        _slots = _options.MaxConcurrentRequests is { } limit
            ? new SemaphoreSlim(limit, limit)
            : null;
    }

    /// <summary>The checked copy of the options this decider was built with.</summary>
    internal SystemOneOptions Options => _options;

    /// <summary>How the decider waits before a retry. Replaced in tests to record the waits.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    /// <summary>A number in [0, 1) that spreads retries out. Replaced in tests to fix it.</summary>
    internal Func<double> Jitter { get; set; } = Random.Shared.NextDouble;

    /// <inheritdoc />
    public async Task<DecisionResult> Decide(
        DecisionRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

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
        catch (Unsendable unsendable)
        {
            throw Refused(request, unsendable.Message, FailureClass.Permanent);
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

                if (_options.Bearer is { } key)
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

                // Not followed, even by a client that would: the endpoint is the one configured.
                if (status is >= 300 and < 400)
                    throw Refused(
                        request,
                        $"the model answered {status} {response.ReasonPhrase}, a redirect, and "
                            + "redirects are not followed: the endpoint is the one configured",
                        FailureClass.Permanent
                    );

                failure =
                    $"the model answered {status} {response.ReasonPhrase} ({Classify(status)})"
                    + RequestId(response);

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
            catch (HttpRequestException e) when (CannotReach(e.HttpRequestError))
            {
                // A name that does not resolve or a certificate that is not trusted is a
                // configuration to fix, not a blip to wait out.
                throw Refused(
                    request,
                    $"the model's endpoint cannot be reached as configured: {e.Message}",
                    FailureClass.Permanent
                );
            }
            catch (HttpRequestException e)
            {
                failure = $"the model could not be reached: {e.Message}";
            }
            catch (MalformedResponse e)
            {
                failure =
                    $"the model answered with something that is not a System One response: {e.Message}";
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

            if (retryAfter > _options.MaxRetryDelay)
                throw Refused(
                    request,
                    $"{failure}, and it asked to be retried in {Seconds(retryAfter.Value)}, "
                        + $"longer than MaxRetryDelay ({Seconds(_options.MaxRetryDelay)})",
                    FailureClass.Transient
                );

            await Wait(Delay(attempt, retryAfter), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The container can hold this under both its own type and IDecider, and disposes each.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _slots?.Dispose();

        if (_ownsHttp)
            _http.Dispose();
    }

    /// <summary>
    /// The wait before the retry after <paramref name="attempt"/>: what the model asked for, with
    /// a little added so a fleet of callers does not return at the same instant, or else the
    /// doubling <see cref="SystemOneOptions.RetryDelay"/>, jittered and capped.
    /// </summary>
    private TimeSpan Delay(int attempt, TimeSpan? retryAfter)
    {
        var ceiling = _options.MaxRetryDelay.TotalMilliseconds;

        if (retryAfter is { } asked)
        {
            // Never earlier than asked; the extra stays under the cap, which asked is within.
            var extra = Math.Min(asked.TotalMilliseconds * 0.1, 1000) * Jitter();
            return TimeSpan.FromMilliseconds(Math.Min(asked.TotalMilliseconds + extra, ceiling));
        }

        // Math.Min with the ceiling also absorbs an infinite product, so this cannot overflow.
        var backoff = Math.Min(
            _options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 62)),
            ceiling
        );

        return TimeSpan.FromMilliseconds(backoff / 2 + backoff / 2 * Jitter());
    }

    private JsonObject Request(DecisionRequest request)
    {
        var questions = new JsonObject();

        foreach (var question in request.Questions)
        {
            if (questions.ContainsKey(question.Key))
                throw new Unsendable(
                    $"it asks the question '{question.Key}' more than once. Each question needs "
                        + "a distinct key"
                );

            questions[question.Key] = question switch
            {
                ChoiceQuestion choice => new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = choice.Instructions,
                    ["criteria"] = new JsonObject(
                        Distinct(choice, Limit(choice, choice.Options))
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
                _ => throw new Unsendable(
                    $"the question '{question.Key}' is a {question.GetType().Name}, which the "
                        + "System One format cannot ask"
                ),
            };
        }

        return new JsonObject
        {
            ["model"] = _options.Model,
            ["state"] = State(request.State),
            ["questions"] = questions,
        };
    }

    private static JsonNode? State(object state)
    {
        if (state is string text)
            return JsonValue.Create(text);

        try
        {
            return JsonSerializer.SerializeToNode(state, StateJson);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new Unsendable($"its state cannot be written as JSON: {e.Message}");
        }
    }

    private IReadOnlyList<Criterion> Limit(Question question, IReadOnlyList<Criterion> criteria) =>
        criteria.Count <= _options.MaxOptions
            ? criteria
            : throw new Unsendable(
                $"the question '{question.Key}' offers {criteria.Count} options, and the model "
                    + $"accepts at most {_options.MaxOptions}"
            );

    private static IReadOnlyList<Criterion> Distinct(
        Question question,
        IReadOnlyList<Criterion> options
    )
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in options)
            if (!seen.Add(option.Name))
                throw new Unsendable(
                    $"the question '{question.Key}' offers the option '{option.Name}' more than "
                        + "once. Each option needs a distinct name"
                );

        return options;
    }

    /// <summary>
    /// Reads the answers out of a response. An answer that cannot be read is left out, so the
    /// train fails on it as unanswered rather than acting on a guess. A body that is not a System
    /// One response at all is thrown as <see cref="MalformedResponse"/>.
    /// </summary>
    private static DecisionResult Read(string json, DecisionRequest request)
    {
        var answers = new Dictionary<string, Answer>();

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                throw new MalformedResponse("the body is not a JSON object");

            var model = root["model"] switch
            {
                null => null,
                JsonValue value when value.GetValueKind() == JsonValueKind.String =>
                    value.GetValue<string>(),
                _ => throw new MalformedResponse("its 'model' is not a string"),
            };

            // A response without its answers is not a System One response, however it is
            // otherwise shaped; read as answering nothing, it would fail the train as unanswered
            // instead of being retried.
            if (root["answers"] is not JsonObject given)
                throw new MalformedResponse("it has no 'answers' object");

            foreach (var question in request.Questions)
                if (given[question.Key] is JsonObject node && Answer(node, question) is { } answer)
                    answers[question.Key] = answer with { Model = model };
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new MalformedResponse(e.Message);
        }

        return new DecisionResult(answers);
    }

    private static Answer? Answer(JsonObject node, Question question)
    {
        try
        {
            var type = node["type"]?.GetValue<string>();

            return question switch
            {
                ChoiceQuestion when type == "choice" => Choice(node),
                ScoreQuestion score when type == "score" => Score(node, score.Levels.Count),
                YesNoQuestion when type == "noul" => new YesNoAnswer(
                    node["noul"]!.GetValue<double>()
                ),
                _ => null,
            };
        }
        catch (Exception e)
            when (e
                    is NullReferenceException
                        or InvalidOperationException
                        or FormatException
                        or ArgumentException
            )
        {
            return null;
        }
    }

    /// <summary>
    /// A choice, whose confidence is the chosen option's probability when the model gives none.
    /// </summary>
    private static ChoiceAnswer? Choice(JsonObject node)
    {
        var choice = node["choice"]!.GetValue<string>();
        var probabilities = node["probabilities"]
            ?.AsObject()
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<double>());

        double? confidence =
            node["confidence"] is { } given ? given.GetValue<double>()
            : probabilities?.TryGetValue(choice, out var chosen) == true ? chosen
            : null;

        return confidence is { } c ? new ChoiceAnswer(choice, c, probabilities) : null;
    }

    /// <summary>
    /// A score, whose probabilities must be keyed by every level from 0 and nothing else, and
    /// whose confidence is the probability of the level nearest the score when the model gives
    /// none.
    /// </summary>
    private static ScoreAnswer? Score(JsonObject node, int levels)
    {
        var score = node["score"]!.GetValue<double>();
        List<double>? probabilities = null;

        if (node["probabilities"] is { } given)
        {
            var byLevel = given.AsObject();

            if (byLevel.Count != levels)
                return null;

            probabilities = new List<double>(levels);

            for (var level = 0; level < levels; level++)
                if (byLevel[level.ToString(CultureInfo.InvariantCulture)] is { } p)
                    probabilities.Add(p.GetValue<double>());
                else
                    return null;
        }

        double? confidence =
            node["confidence"] is { } stated ? stated.GetValue<double>()
            : probabilities is null ? null
            : probabilities[
                Math.Clamp((int)Math.Round(score, MidpointRounding.AwayFromZero), 0, levels - 1)
            ];

        return confidence is { } c ? new ScoreAnswer(score, c, probabilities) : null;
    }

    /// <summary>
    /// A failure to connect that trying again will not cure: the endpoint's name does not
    /// resolve, its TLS handshake fails (an untrusted or mismatched certificate), or a proxy
    /// refuses the credentials. A refused or reset connection, or one that times out, may be a
    /// server restarting, and is retried.
    /// </summary>
    private static bool CannotReach(HttpRequestError error) =>
        error
            is HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.UserAuthenticationError;

    /// <summary>
    /// Throttled, timed out or unavailable. A 501 or 505 says the server will never handle the
    /// request, so it is not retried.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (
            (int)status >= 500
            && status
                is not HttpStatusCode.NotImplemented
                    and not HttpStatusCode.HttpVersionNotSupported
        );

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header) =>
        header?.Delta
        ?? (header?.Date is { } date ? Max(date - DateTimeOffset.UtcNow, TimeSpan.Zero) : null);

    /// <summary>
    /// What a failed status means, in a few words. The response body is never quoted: a server's
    /// validation errors can echo the request back, and the request carries the train's state,
    /// while this message is stored on the run and shown wherever its failure is.
    /// </summary>
    private static string Classify(int status) =>
        status switch
        {
            400 or 422 => "the request was refused as invalid",
            401 or 403 => "the API key was refused",
            402 => "the account cannot pay for the request",
            404 or 405 => "the endpoint does not answer System One requests",
            408 or 504 => "the model timed out",
            413 => "the request is too large",
            429 => "the model is throttling requests",
            501 or 505 => "the server does not implement the request",
            529 => "the model is overloaded",
            >= 500 => "the model's server failed",
            _ => "the request was refused",
        };

    /// <summary>
    /// The id the provider gave the request, from its <c>x-typesafe-request-id</c> header, which is
    /// what it asks for when a failure is reported to it; empty when there is none.
    /// </summary>
    private static string RequestId(HttpResponseMessage response)
    {
        if (
            !response.Headers.TryGetValues("x-typesafe-request-id", out var values)
            || values.FirstOrDefault() is not { } id
        )
            return "";

        // An id, not a message: anything longer or stranger than one is not repeated.
        return
            id.Length is > 0 and <= 128
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? $" (request {id})"
            : "";
    }

    private static string Seconds(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.#}s");

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

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>A request that cannot be sent as it is; never worth retrying.</summary>
    private sealed class Unsendable(string message) : Exception(message);

    /// <summary>A successful status whose body is not a System One response.</summary>
    private sealed class MalformedResponse(string message) : Exception(message);
}

/// <summary>
/// The decision model could not answer. Its failure class says whether trying again later can help.
/// </summary>
public sealed class DecisionServiceException(string message) : Exception(message);
