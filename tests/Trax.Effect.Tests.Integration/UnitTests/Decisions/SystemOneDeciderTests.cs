using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Effect.Decisions.SystemOne;

namespace Trax.Effect.Tests.Integration.UnitTests.Decisions;

/// <summary>
/// <see cref="SystemOneDecider"/> speaks the System One request format to a typed decision model.
/// These pin the wire in both directions, what it retries, and what it refuses to send or read.
/// </summary>
public class SystemOneDeciderTests
{
    private static readonly DecisionRequest Ticket = new(
        "TriageTicket",
        new { CustomerId = "cus_1", OrderTotal = 42m },
        [
            new ChoiceQuestion(
                "TicketTrack",
                "Which team should handle this ticket?",
                [new("Refund", "Money back for an order."), new("Escalate", null)]
            ),
            new ScoreQuestion(
                "Urgency",
                "How urgent is it?",
                [new("Low", "Whenever."), new("High", "Now.")]
            ),
            new YesNoQuestion(
                "ContainsThreat",
                "Does it threaten anyone?",
                "It threatens harm.",
                null
            ),
        ]
    );

    private const string Answered = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "TicketTrack": { "type": "choice", "choice": "Refund", "confidence": 0.8,
                             "probabilities": { "Refund": 0.87, "Escalate": 0.13 } },
            "Urgency": { "type": "score", "score": 1.04, "confidence": 0.94,
                         "legend": { "0": "Whenever.", "1": "Now." },
                         "probabilities": { "1": 0.96, "0": 0.04 } },
            "ContainsThreat": { "type": "noul", "noul": 0.03 }
          },
          "usage": { "input_tokens": 212, "output_tokens": 0 }
        }
        """;

    private static SystemOneOptions Options(Action<SystemOneOptions>? configure = null)
    {
        var options = new SystemOneOptions
        {
            Endpoint = new Uri("https://api.example.test/v1/systemone"),
            Model = "jev-1.13.0",
            ApiKey = "sk-test",
            RetryDelay = TimeSpan.Zero,
        };
        configure?.Invoke(options);
        return options;
    }

    [Test]
    public async Task Decide_SendsEachQuestionInTheSystemOneShape()
    {
        var model = new FakeModel(_ => Ok(Answered));
        using var decider = new SystemOneDecider(Options(), new HttpClient(model));

        await decider.Decide(Ticket, CancellationToken.None);

        var sent = model.Requests.Should().ContainSingle().Subject;
        sent.Authorization.Should().Be("Bearer sk-test");
        sent.Body["model"]!.GetValue<string>().Should().Be("jev-1.13.0");
        sent.Body["state"]!["customerId"]!.GetValue<string>().Should().Be("cus_1");

        var questions = sent.Body["questions"]!;
        questions["TicketTrack"]!["type"]!.GetValue<string>().Should().Be("choice");
        questions["TicketTrack"]!["instructions"]!
            .GetValue<string>()
            .Should()
            .Be("Which team should handle this ticket?");
        questions["TicketTrack"]!["criteria"]!
            .ToJsonString()
            .Should()
            .Be("""{"Refund":"Money back for an order.","Escalate":"Escalate"}""");
        questions["Urgency"]!["type"]!.GetValue<string>().Should().Be("score");
        questions["Urgency"]!["criteria"]!.ToJsonString().Should().Be("""["Whenever.","Now."]""");
        questions["ContainsThreat"]!["type"]!.GetValue<string>().Should().Be("noul");
        questions["ContainsThreat"]!["criteria"]!
            .ToJsonString()
            .Should()
            .Be("""{"true":"It threatens harm.","false":"No"}""");
    }

    [Test]
    public async Task Decide_ReadsEachAnswerAndTheModelThatGaveIt()
    {
        using var decider = new SystemOneDecider(
            Options(),
            new HttpClient(new FakeModel(_ => Ok(Answered)))
        );

        var result = await decider.Decide(Ticket, CancellationToken.None);

        result
            .Answers["TicketTrack"]
            .Should()
            .BeEquivalentTo(
                new ChoiceAnswer(
                    "Refund",
                    0.8,
                    new Dictionary<string, double> { ["Refund"] = 0.87, ["Escalate"] = 0.13 }
                )
                {
                    Model = "jev-1.13.0",
                }
            );
        var urgency = result.Answers["Urgency"].Should().BeOfType<ScoreAnswer>().Subject;
        urgency.Score.Should().Be(1.04);
        urgency.Probabilities.Should().Equal(0.04, 0.96);
        result
            .Answers["ContainsThreat"]
            .Should()
            .Be(new YesNoAnswer(0.03) { Model = "jev-1.13.0" });
    }

    [Test]
    public async Task Decide_LeavesOutAnAnswerItCannotRead()
    {
        // The train then fails on an unanswered question rather than acting on a guess.
        using var decider = new SystemOneDecider(
            Options(),
            new HttpClient(
                new FakeModel(_ =>
                    Ok(
                        """{"model":"jev-1.13.0","answers":{"TicketTrack":{"type":"choice","confidence":0.9}}}"""
                    )
                )
            )
        );

        var result = await decider.Decide(Ticket, CancellationToken.None);

        result.Answers.Should().BeEmpty();
    }

    [Test]
    public async Task Decide_RetriesAThrottledOrUnavailableModel()
    {
        var model = new FakeModel(attempt =>
            attempt switch
            {
                1 => Status(HttpStatusCode.TooManyRequests),
                2 => Status(HttpStatusCode.ServiceUnavailable),
                _ => Ok(Answered),
            }
        );
        using var decider = new SystemOneDecider(Options(), new HttpClient(model));

        var result = await decider.Decide(Ticket, CancellationToken.None);

        result.Answers.Should().HaveCount(3);
        model.Requests.Should().HaveCount(3);
    }

    [Test]
    public async Task Decide_WhenRetriesRunOut_FailsTransiently()
    {
        var model = new FakeModel(_ => Status(HttpStatusCode.BadGateway));
        using var decider = new SystemOneDecider(
            Options(o => o.MaxAttempts = 2),
            new HttpClient(model)
        );

        var decide = () => decider.Decide(Ticket, CancellationToken.None);

        var failure = (await decide.Should().ThrowAsync<DecisionServiceException>()).Which;
        failure.Message.Should().Contain("502").And.Contain("after 2 attempts");
        ((TrainExceptionData)failure.Data["TrainExceptionData"]!)
            .FailureClass.Should()
            .Be(FailureClass.Transient);
        model.Requests.Should().HaveCount(2);
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.UnprocessableEntity)]
    public async Task Decide_ARefusedRequest_FailsPermanentlyWithoutRetrying(HttpStatusCode status)
    {
        var model = new FakeModel(_ => Status(status, """{"error":"bad criteria"}"""));
        using var decider = new SystemOneDecider(Options(), new HttpClient(model));

        var decide = () => decider.Decide(Ticket, CancellationToken.None);

        var failure = (await decide.Should().ThrowAsync<DecisionServiceException>()).Which;
        failure.Message.Should().Contain(((int)status).ToString());
        ((TrainExceptionData)failure.Data["TrainExceptionData"]!)
            .FailureClass.Should()
            .Be(FailureClass.Permanent);
        model.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task Decide_ASlowAttempt_IsAbandonedAndRetried()
    {
        var model = new FakeModel(attempt => Ok(Answered), hang: attempt => attempt == 1);
        using var decider = new SystemOneDecider(
            Options(o => o.AttemptTimeout = TimeSpan.FromMilliseconds(50)),
            new HttpClient(model)
        );

        var result = await decider.Decide(Ticket, CancellationToken.None);

        result.Answers.Should().HaveCount(3);
        model.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task Decide_CancellingTheTrain_CancelsTheRequest()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeModel(_ => Ok(Answered), hang: _ => true, onSend: cts.Cancel);
        using var decider = new SystemOneDecider(Options(), new HttpClient(model));

        var decide = () => decider.Decide(Ticket, cts.Token);

        await decide.Should().ThrowAsync<OperationCanceledException>();
        model.Requests.Should().ContainSingle("a cancelled train is not retried");
    }

    [Test]
    public async Task Decide_RefusesMoreOptionsThanTheModelAccepts()
    {
        using var decider = new SystemOneDecider(
            Options(o => o.MaxOptions = 2),
            new HttpClient(new FakeModel(_ => Ok(Answered)))
        );
        var tooMany = Ticket with
        {
            Questions =
            [
                new ChoiceQuestion(
                    "Lane",
                    "Which lane?",
                    [new("A", null), new("B", null), new("C", null)]
                ),
            ],
        };

        var decide = () => decider.Decide(tooMany, CancellationToken.None);

        var failure = (await decide.Should().ThrowAsync<DecisionServiceException>()).Which;
        failure.Message.Should().Contain("offers 3 options");
        ((TrainExceptionData)failure.Data["TrainExceptionData"]!)
            .FailureClass.Should()
            .Be(FailureClass.Permanent);
    }

    [Test]
    public async Task Decide_RefusesMoreQuestionsThanOneRequestMayCarry()
    {
        var model = new FakeModel(_ => Ok(Answered));
        using var decider = new SystemOneDecider(
            Options(o => o.MaxQuestions = 2),
            new HttpClient(model)
        );

        var decide = () => decider.Decide(Ticket, CancellationToken.None);

        var failure = (await decide.Should().ThrowAsync<DecisionServiceException>()).Which;
        failure.Message.Should().Contain("asks 3 questions").And.Contain("at most 2");
        ((TrainExceptionData)failure.Data["TrainExceptionData"]!)
            .FailureClass.Should()
            .Be(FailureClass.Permanent);
        model.Requests.Should().BeEmpty("it is refused before it is sent");
    }

    [Test]
    public async Task Decide_KeepsNoMoreRequestsInFlightThanAllowed()
    {
        var gate = new TaskCompletionSource();
        var model = new FakeModel(_ => Ok(Answered), waitFor: () => gate.Task);
        using var decider = new SystemOneDecider(
            Options(o => o.MaxConcurrentRequests = 2),
            new HttpClient(model)
        );

        var decisions = Enumerable
            .Range(0, 5)
            .Select(_ => decider.Decide(Ticket, CancellationToken.None))
            .ToList();
        await model.InFlightReached(2);
        gate.SetResult();
        await Task.WhenAll(decisions);

        model.MostInFlight.Should().Be(2);
        model.Requests.Should().HaveCount(5);
    }

    [Test]
    public async Task Decide_RetriesAModelThatSaysItIsBusy()
    {
        var model = new FakeModel(attempt =>
            attempt == 1 ? Status((HttpStatusCode)529) : Ok(Answered)
        );
        using var decider = new SystemOneDecider(Options(), new HttpClient(model));

        await decider.Decide(Ticket, CancellationToken.None);

        model.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task Decide_OutOfCredit_FailsPermanentlyWithTheProvidersRequestId()
    {
        var model = new FakeModel(_ =>
            Status(
                HttpStatusCode.PaymentRequired,
                """{"detail":"Insufficient credit","request_id":"req_9f2"}"""
            )
        );
        using var decider = new SystemOneDecider(Options(), new HttpClient(model));

        var decide = () => decider.Decide(Ticket, CancellationToken.None);

        var failure = (await decide.Should().ThrowAsync<DecisionServiceException>()).Which;
        failure
            .Message.Should()
            .Contain("402")
            .And.Contain("Insufficient credit")
            .And.Contain("(request req_9f2)");
        ((TrainExceptionData)failure.Data["TrainExceptionData"]!)
            .FailureClass.Should()
            .Be(FailureClass.Permanent);
        model.Requests.Should().ContainSingle();
    }

    [TestCase(null, "jev-1.13.0", "Endpoint is required")]
    [TestCase("http://models.example.test/v1/systemone", "jev-1.13.0", "is not HTTPS")]
    [TestCase("https://api.example.test/v1/systemone", null, "Model is required")]
    [TestCase("https://api.example.test/v1/systemone", "jev-latest", "floating alias")]
    [TestCase("https://api.example.test/v1/systemone", "jev", "floating alias")]
    public void Constructing_WithUnusableOptions_IsRefused(
        string? endpoint,
        string? model,
        string problem
    )
    {
        var construct = () =>
            new SystemOneDecider(
                Options(o =>
                {
                    o.Endpoint = endpoint is null ? null : new Uri(endpoint);
                    o.Model = model;
                })
            );

        construct.Should().Throw<ArgumentException>().WithMessage($"*{problem}*");
    }

    [Test]
    public void Constructing_ForASelfHostedModelOnLoopback_AllowsHttp()
    {
        using var decider = new SystemOneDecider(
            Options(o => o.Endpoint = new Uri("http://localhost:8080/v1/systemone"))
        );

        decider.Should().NotBeNull();
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Status(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    private sealed record SentRequest(string? Authorization, JsonNode Body);

    /// <summary>A model endpoint that answers by attempt number, and can hang until cancelled.</summary>
    private sealed class FakeModel(
        Func<int, HttpResponseMessage> answer,
        Func<int, bool>? hang = null,
        Action? onSend = null,
        Func<Task>? waitFor = null
    ) : HttpMessageHandler
    {
        private readonly object _lock = new();

        private readonly List<(int Count, TaskCompletionSource Reached)> _watchers = [];

        private int _inFlight;

        public List<SentRequest> Requests { get; } = [];

        public int MostInFlight { get; private set; }

        /// <summary>Completes once this many requests are in flight at the same time.</summary>
        public Task InFlightReached(int count)
        {
            lock (_lock)
            {
                if (_inFlight >= count)
                    return Task.CompletedTask;

                var reached = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _watchers.Add((count, reached));
                return reached.Task;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var sent = new SentRequest(
                request.Headers.Authorization?.ToString(),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!
            );

            int attempt;
            lock (_lock)
            {
                Requests.Add(sent);
                attempt = Requests.Count;
            }
            onSend?.Invoke();

            if (waitFor is not null)
            {
                lock (_lock)
                {
                    _inFlight++;
                    MostInFlight = Math.Max(MostInFlight, _inFlight);
                    foreach (var watcher in _watchers.Where(w => _inFlight >= w.Count))
                        watcher.Reached.TrySetResult();
                }

                await waitFor();

                lock (_lock)
                    _inFlight--;
            }

            if (hang?.Invoke(attempt) == true)
            {
                // Never answers; only cancellation (the train's, or the attempt timeout) ends it.
                var never = new TaskCompletionSource();
                await using var _ = cancellationToken.Register(() =>
                    never.TrySetCanceled(cancellationToken)
                );
                await never.Task;
            }

            return answer(attempt);
        }
    }
}
