using FluentAssertions;
using Trax.Effect.StateMachine.Persistence.Integration.Fakes;

namespace Trax.Effect.StateMachine.Persistence.Integration;

/// <summary>
/// The runtime self-check: a machine replays its committed differential corpus through its own engine, and a
/// host aggregates the results at startup (<see cref="SnapshotSelfCheck.Run"/>). Proves a machine with a
/// matching corpus reports no diffs, a tampered one reports them, a machine with no corpus is skipped (not a
/// failure), and the aggregation is machine-prefixed. These are DB-free but live in this assembly to reuse the
/// declarative turnstile fake; the fixture's Postgres is available but unused.
/// </summary>
public class SnapshotSelfCheckTests
{
    // A corpus the declarative turnstile reproduces exactly: Locked + Coin(quarter) -> Unlocked{paidWith}.
    private const string MatchingCorpus = """
        {"cases":[{"given":{"machine":"declarative-turnstile","version":1,"state":"Locked","context":{}},"when":{"trigger":"Coin","input":{"coin":"quarter"}},"expect":{"outcome":"transitioned","wire":"{\"machine\":\"declarative-turnstile\",\"version\":1,\"state\":\"Unlocked\",\"context\":{\"paidWith\":\"quarter\"}}"}}]}
        """;

    // Same case, but the golden expects the wrong wire — the engine's real result diverges from it.
    private const string TamperedCorpus = """
        {"cases":[{"given":{"machine":"declarative-turnstile","version":1,"state":"Locked","context":{}},"when":{"trigger":"Coin","input":{"coin":"quarter"}},"expect":{"outcome":"transitioned","wire":"WRONG"}}]}
        """;

    /// <summary>The declarative turnstile, but shipping an (injectable) corpus so SelfCheck has something to replay.</summary>
    private sealed class CorpusTurnstile(string? corpus) : Machine<TurnstileState, TurnstileTrigger>
    {
        protected override void Configure(IMachineBuilder<TurnstileState, TurnstileTrigger> m) =>
            DeclarativeTurnstileMachine.ConfigureTurnstile(m);

        public override string? Corpus { get; } = corpus;
    }

    [Test]
    public void SelfCheck_returns_no_diffs_when_the_engine_reproduces_the_corpus()
    {
        new CorpusTurnstile(MatchingCorpus).SelfCheck().Should().BeEmpty();
    }

    [Test]
    public void SelfCheck_reports_a_diff_when_the_engine_diverges_from_the_corpus()
    {
        new CorpusTurnstile(TamperedCorpus)
            .SelfCheck()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Locked + Coin");
    }

    [Test]
    public void SelfCheck_is_empty_for_a_machine_that_ships_no_corpus()
    {
        new DeclarativeTurnstileMachine().SelfCheck().Should().BeEmpty();
        new CorpusTurnstile(null).SelfCheck().Should().BeEmpty();
    }

    [Test]
    public void Run_aggregates_diffs_across_machines_prefixed_by_name_and_skips_corpus_less_ones()
    {
        var machines = new IMachine[]
        {
            new DeclarativeTurnstileMachine(), // no corpus -> skipped
            new CorpusTurnstile(MatchingCorpus), // agrees -> no diff
            new CorpusTurnstile(TamperedCorpus), // diverges -> one diff
        };

        SnapshotSelfCheck
            .Run(machines)
            .Should()
            .ContainSingle()
            .Which.Should()
            .StartWith("declarative-turnstile: ");
    }

    [Test]
    public void Run_is_empty_when_no_machine_ships_a_corpus()
    {
        SnapshotSelfCheck
            .Run(new IMachine[] { new DeclarativeTurnstileMachine() })
            .Should()
            .BeEmpty();
    }
}
