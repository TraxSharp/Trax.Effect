using System.Text.Json.Nodes;
using FluentAssertions;
using Trax.Effect.StateMachine.Persistence.Integration.Fixtures;

namespace Trax.Effect.StateMachine.Persistence.Integration;

public enum GateState
{
    A,
    B,
}

public enum GateTrigger
{
    Go,
}

/// <summary>A declarative machine whose first <see cref="Configure"/> blocks until released.</summary>
public sealed class SlowToBuildMachine : Machine<GateState, GateTrigger>
{
    public readonly ManualResetEventSlim Release = new(false);
    public readonly ManualResetEventSlim Entered = new(false);

    protected override void Configure(IMachineBuilder<GateState, GateTrigger> m)
    {
        Entered.Set();
        Release.Wait(TimeSpan.FromSeconds(10));
        m.Id("slow").Version(1).StartsAt(GateState.A, () => new JsonObject());
        m.In(GateState.A).Context().On(GateTrigger.Go).To(GateState.B);
        m.In(GateState.B).Context();
    }
}

public class EffectAndHandshakeDefectTests
{
    [Test]
    public async Task An_effect_that_returns_a_null_receipt_must_not_run_again()
    {
        var key = $"idem:{Guid.NewGuid()}";
        var calls = 0;
        Func<Task<string>> effect = () =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string>(null!);
        };

        await new IdempotentEffect(TestDb.NewClaims()).RunOnce(key, effect, TimeSpan.Zero);
        // Complete wrote receipt = NULL, which the ledger reads as "claimed, in flight". Once the lease has
        // passed, the next caller reclaims the key and delivers again.
        await new IdempotentEffect(TestDb.NewClaims()).RunOnce(key, effect, TimeSpan.Zero);

        calls.Should().Be(1);
    }

    [Test]
    public void SchemaHash_must_not_read_null_while_another_request_is_computing_it()
    {
        var machine = new SlowToBuildMachine();

        var first = Task.Run(() => machine.SchemaHash);
        machine.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        // A second request arrives while the first is still building the machine.
        var second = machine.SchemaHash;
        machine.Release.Set();

        first.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        first.Result.Should().NotBeNull();
        second.Should().Be(first.Result, "a null hash turns the schema-mismatch check off");
    }
}
