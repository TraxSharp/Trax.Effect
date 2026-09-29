using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Trax.Effect.StateMachine.Tests.UnitTests;

/// <summary>
/// Declarative authoring promises that a context record yields its schema for free and that a
/// <see cref="Rule.Custom"/> / <see cref="Reduction.Custom"/> is the escape hatch "hand-written per runtime".
/// These pin where that promise does not hold on the C# side.
/// </summary>
public class DeclarativeAuthoringDefectTests
{
    public enum Kind
    {
        Standard,
        Express,
    }

    public sealed record GuidContext(Guid OrderId);

    public sealed record TimeContext(DateTimeOffset PlacedAt);

    public sealed record EnumContext(Kind Kind);

    public sealed record UnsignedContext(uint Quantity);

    public sealed record MapContext(Dictionary<string, int> Counts);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string? ValidateOwnJson<TContext>(TContext value) =>
        SchemaValidator.Validate(
            SchemaReflection.For<TContext>(),
            (JsonObject)JsonSerializer.SerializeToNode(value, CamelCase)!
        );

    [Test]
    public void A_context_record_must_accept_its_own_serialized_form()
    {
        // Guid and DateTimeOffset serialize as strings, an enum as a string (or a number without the
        // converter), uint as a number and a dictionary as an object. The reflected schema calls the first
        // four "object" and the dictionary an "array" of strings, so each record's own JSON is rejected.
        using (new AssertionScope())
        {
            ValidateOwnJson(new GuidContext(Guid.NewGuid())).Should().BeNull("Guid");
            ValidateOwnJson(new TimeContext(DateTimeOffset.UnixEpoch))
                .Should()
                .BeNull("DateTimeOffset");
            ValidateOwnJson(new EnumContext(Kind.Express)).Should().BeNull("enum");
            ValidateOwnJson(new UnsignedContext(3)).Should().BeNull("uint");
            ValidateOwnJson(new MapContext(new() { ["a"] = 1 }))
                .Should()
                .BeNull("Dictionary<string,int>");
        }
    }

    public enum S
    {
        A,
        B,
    }

    public enum T
    {
        Go,
    }

    [Test]
    public void A_custom_guard_with_no_CSharp_handler_must_be_refused_at_build()
    {
        var builder = new MachineBuilder<S, T>();
        builder.Id("custom").StartsAt(S.A, () => new JsonObject());
        builder.In(S.A).Context().On(T.Go).When(new Rule.Custom("always")).To(S.B);

        // There is no C# surface to bind the "always" handler, so the compiled guard evaluates the rule with
        // no handler map and Go is refused forever, while a TypeScript twin given its handler takes the edge.
        Action build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>().WithMessage("*always*");
    }

    [Test]
    public void A_custom_reducer_with_no_CSharp_handler_must_be_refused_at_build()
    {
        var builder = new MachineBuilder<S, T>();
        builder.Id("custom").StartsAt(S.A, () => new JsonObject());
        builder.In(S.A).Context().On(T.Go).Reduce(new Reduction.Custom("fresh")).To(S.B);

        // Today the reducer silently carries the context forward instead of running "fresh".
        Action build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>().WithMessage("*fresh*");
    }
}
