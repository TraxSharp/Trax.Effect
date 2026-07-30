using FluentAssertions;
using Trax.Effect.StateMachine.Persistence.Integration.Fakes;
using Trax.Effect.StateMachine.Persistence.Integration.Fixtures;
using Trax.Effect.StateMachine.Persistence.Integration.Mutations;

namespace Trax.Effect.StateMachine.Persistence.Integration;

/// <summary>
/// The Trax mutation junctions (the GraphQL resolver logic) against real Postgres, invoked directly the
/// way Trax tests its own junctions — no GraphQL server needed. Proves auth-gating, rejection-as-data,
/// and authoritative re-drive over the store.
/// </summary>
public class SnapshotJunctionTests
{
    private static SnapshotDraftService<TurnstileState, TurnstileTrigger> Service() =>
        TestTurnstile.Service(TestDb.NewStore());

    private static SaveTurnstileSnapshotJunction Save(string? user) =>
        new(Service(), new FakePrincipal(user));

    private static AdvanceTurnstileSnapshotJunction Advance(string? user) =>
        new(Service(), new FakePrincipal(user));

    [Test]
    public async Task Save_persists_a_valid_snapshot()
    {
        var output = await Save("u")
            .Run(
                new SaveSnapshotInput { Id = Guid.NewGuid(), Snapshot = TestTurnstile.InitialJson }
            );

        output.Problem.Should().BeNull();
        output.Snapshot.Should().Contain("\"state\":\"Locked\"");
    }

    [Test]
    public async Task Save_returns_a_problem_when_unauthenticated()
    {
        var output = await Save(null)
            .Run(
                new SaveSnapshotInput { Id = Guid.NewGuid(), Snapshot = TestTurnstile.InitialJson }
            );

        output.Snapshot.Should().BeNull();
        output.Problem!.Code.Should().Be("unauthenticated");
    }

    [Test]
    public async Task Save_returns_a_typed_problem_for_an_invalid_snapshot()
    {
        var invalid =
            "{\"machine\":\"turnstile\",\"version\":1,\"state\":\"Unlocked\",\"context\":{}}";

        var output = await Save("u")
            .Run(new SaveSnapshotInput { Id = Guid.NewGuid(), Snapshot = invalid });

        output.Problem!.Code.Should().Be("invalid-context");
    }

    [Test]
    public async Task Advance_re_drives_the_stored_snapshot()
    {
        var id = Guid.NewGuid();
        await Save("u")
            .Run(new SaveSnapshotInput { Id = id, Snapshot = TestTurnstile.InitialJson });

        var output = await Advance("u")
            .Run(
                new AdvanceSnapshotInput
                {
                    Id = id,
                    Trigger = "Coin",
                    Input = "{\"coin\":\"quarter\"}",
                }
            );

        output.Problem.Should().BeNull();
        output.Snapshot.Should().Contain("\"state\":\"Unlocked\"");
    }

    [Test]
    public async Task Advance_returns_a_typed_rejection_for_an_illegal_trigger()
    {
        var id = Guid.NewGuid();
        await Save("u")
            .Run(new SaveSnapshotInput { Id = id, Snapshot = TestTurnstile.InitialJson });

        var output = await Advance("u").Run(new AdvanceSnapshotInput { Id = id, Trigger = "Push" });

        output.Problem!.Code.Should().Be("no-transition");
    }

    [Test]
    public async Task Advance_on_another_users_draft_is_not_found()
    {
        var id = Guid.NewGuid();
        await Save("owner")
            .Run(new SaveSnapshotInput { Id = id, Snapshot = TestTurnstile.InitialJson });

        var output = await Advance("intruder")
            .Run(
                new AdvanceSnapshotInput
                {
                    Id = id,
                    Trigger = "Coin",
                    Input = "{\"coin\":\"quarter\"}",
                }
            );

        output.Problem!.Code.Should().Be("not-found");
    }

    [Test]
    public async Task Advance_returns_a_problem_for_malformed_trigger_input()
    {
        var id = Guid.NewGuid();
        await Save("u")
            .Run(new SaveSnapshotInput { Id = id, Snapshot = TestTurnstile.InitialJson });

        var output = await Advance("u")
            .Run(
                new AdvanceSnapshotInput
                {
                    Id = id,
                    Trigger = "Coin",
                    Input = "{not json",
                }
            );

        output.Problem!.Code.Should().Be("malformed");
    }

    [Test]
    public async Task Advance_returns_a_problem_when_unauthenticated()
    {
        var output = await Advance(null)
            .Run(new AdvanceSnapshotInput { Id = Guid.NewGuid(), Trigger = "Coin" });

        output.Problem!.Code.Should().Be("unauthenticated");
    }
}
