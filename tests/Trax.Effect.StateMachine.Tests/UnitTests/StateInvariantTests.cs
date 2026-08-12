using System.Text.Json.Nodes;
using FluentAssertions;
using static Trax.Effect.StateMachine.Rules;

namespace Trax.Effect.StateMachine.Tests.UnitTests;

/// <summary>
/// A state's declarative validator is its context schema AND its <c>.Requires(...)</c> policy, composed. This
/// is what lets a state demand more than its shape (a complete draft) while sharing one context record across
/// states, and it is what the IR carries as the per-state <c>invariants</c> block.
/// </summary>
public class StateInvariantTests
{
    private enum S
    {
        Draft,
        Done,
    }

    private enum T
    {
        Finish,
    }

    private sealed record DraftContext
    {
        public string Body { get; init; } = "";
    }

    private static BuiltMachine<S, T> Build()
    {
        var m = new MachineBuilder<S, T>();
        m.Id("inv").StartsAt(S.Draft, () => new JsonObject { ["body"] = "" });
        m.In(S.Draft).Context<DraftContext>().On(T.Finish).To(S.Done);
        m.In(S.Done)
            .Context<DraftContext>()
            .Requires(Field((DraftContext d) => d.Body).LengthAtLeast(6));
        return m.Build();
    }

    [Test]
    public void A_requirement_composes_with_the_schema_in_the_state_validator()
    {
        var validate = Build().Definition.ContextValidators[S.Done];

        validate(new JsonObject { ["body"] = "123456" })
            .Should()
            .BeNull("a complete draft is valid in Done");
        validate(new JsonObject { ["body"] = "short" })
            .Should()
            .NotBeNull("Done requires a body of length >= 6");
        validate(new JsonObject { ["body"] = 5 })
            .Should()
            .NotBeNull("the schema is still enforced: body must be a string");
    }

    [Test]
    public void Requirements_export_as_a_per_state_invariants_block()
    {
        var ir = IrExporter.Export(Build());

        ir.Should().Contain("\"invariants\"").And.Contain("\"Done\"").And.Contain("\"length\"");
        // A shape-only machine emits no invariants block.
        DeclarativeTurnstileIrHasNoInvariants();
    }

    private static void DeclarativeTurnstileIrHasNoInvariants() =>
        IrExporter
            .Export(Fakes.DeclarativeTurnstile.Built)
            .Should()
            .NotContain("\"invariants\"", "turnstile has no .Requires policy");
}
