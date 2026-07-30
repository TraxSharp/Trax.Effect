using System.Text.Json.Nodes;
using FluentAssertions;
using Trax.Effect.StateMachine.Tests.Fakes;

namespace Trax.Effect.StateMachine.Tests.UnitTests;

/// <summary>
/// The resolver-boundary helper: load stored JSON, apply a trigger, hand back the serialized
/// successor — a single total operation that never throws.
/// </summary>
public class SnapshotEndpointTests
{
    private static readonly SnapshotEndpoint<TurnstileState, TurnstileTrigger> Endpoint = new(
        TestTurnstile.Machine
    );

    private const string LockedJson =
        "{\"machine\":\"turnstile\",\"version\":1,\"state\":\"Locked\",\"context\":{}}";

    [Test]
    public void Advance_from_null_starts_at_the_initial_snapshot_and_transitions()
    {
        var result = Endpoint.Advance(null, "Coin", new JsonObject { ["coin"] = "quarter" });

        result
            .Should()
            .BeOfType<SnapshotEndpointResult.Ok>()
            .Which.SnapshotJson.Should()
            .Contain("\"state\":\"Unlocked\"");
    }

    [Test]
    public void Advance_re_drives_from_the_stored_snapshot()
    {
        var result = Endpoint.Advance(LockedJson, "Coin", new JsonObject { ["coin"] = "dollar" });

        result
            .Should()
            .BeOfType<SnapshotEndpointResult.Ok>()
            .Which.SnapshotJson.Should()
            .Be(
                "{\"machine\":\"turnstile\",\"version\":1,\"state\":\"Unlocked\",\"context\":{\"paidWith\":\"dollar\"}}"
            );
    }

    [Test]
    public void Advance_surfaces_a_declined_trigger_as_a_rejection()
    {
        Endpoint
            .Advance(LockedJson, "Push")
            .Should()
            .BeOfType<SnapshotEndpointResult.Rejected>()
            .Which.Reason.Should()
            .Be(RejectionReasons.NoTransition);
    }

    [Test]
    public void Advance_surfaces_a_failed_guard_as_a_rejection()
    {
        Endpoint
            .Advance(LockedJson, "Coin", new JsonObject { ["coin"] = "penny" })
            .Should()
            .BeOfType<SnapshotEndpointResult.Rejected>()
            .Which.Reason.Should()
            .Be(RejectionReasons.GuardFailed);
    }

    [Test]
    public void Advance_surfaces_bad_stored_json_as_a_load_error()
    {
        Endpoint
            .Advance("{ not json", "Coin")
            .Should()
            .BeOfType<SnapshotEndpointResult.LoadError>()
            .Which.Code.Should()
            .Be(RehydrationErrorCodes.Malformed);
    }
}
