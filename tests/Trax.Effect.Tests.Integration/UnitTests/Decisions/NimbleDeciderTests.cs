using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Decisions;
using Trax.Effect.Decisions.SystemOne;
using Trax.Effect.Decisions.SystemOne.Extensions;
using Trax.Effect.Extensions;

namespace Trax.Effect.Tests.Integration.UnitTests.Decisions;

/// <summary>
/// <c>AddNimbleDecider</c> is the first-class way to give trains a decider: Nimble on a local
/// Ollama by default, Bespoke's hosted API with a key. These pin what each default resolves to.
/// </summary>
public class NimbleDeciderTests
{
    [Test]
    public void ByDefault_ItReachesNimbleOnALocalOllama()
    {
        var options = new NimbleOptions().ToSystemOne();

        options.Endpoint.Should().Be(new Uri("http://localhost:11434/v1/systemone"));
        options.Model.Should().Be("nimble:9b");
        options.ApiKey.Should().BeNull();
        options.MaxConcurrentRequests.Should().BeNull("a local model is not rate-limited");
        options.MaxQuestions.Should().Be(64);
        options.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void WithAnApiKey_ItReachesBespokesHostedApiWithinItsLimits()
    {
        var options = new NimbleOptions { ApiKey = "bsk-test" }.ToSystemOne();

        options.Endpoint.Should().Be(new Uri("https://api.bespokelabs.ai/v1/systemone"));
        options.Model.Should().Be("nimble-v3");
        options.ApiKey.Should().Be("bsk-test");
        options.MaxConcurrentRequests.Should().Be(8, "Bespoke allows 8 requests at once");
    }

    [Test]
    public void AnythingSetExplicitly_OverridesTheDefaults()
    {
        var options = new NimbleOptions
        {
            Endpoint = new Uri("https://nimble.internal.example/v1/systemone"),
            Model = "nimble:9b-q4",
            MaxConcurrentRequests = 3,
        }.ToSystemOne();

        options.Endpoint!.Host.Should().Be("nimble.internal.example");
        options.Model.Should().Be("nimble:9b-q4");
        options.MaxConcurrentRequests.Should().Be(3);
    }

    [Test]
    public void AddNimbleDecider_RegistersTheDeciderTrainsFind()
    {
        using var provider = new ServiceCollection()
            .AddTrax(trax => trax.AddEffects(effects => effects.AddNimbleDecider()))
            .BuildServiceProvider();

        provider.GetRequiredService<IDecider>().Should().BeOfType<SystemOneDecider>();
        provider
            .GetRequiredService<IDecider>()
            .Should()
            .BeSameAs(provider.GetRequiredService<SystemOneDecider>());
    }

    [TestCase("nimble-latest")]
    [TestCase("nimble:latest")]
    [TestCase("nimble")]
    public void AnUnpinnedModel_StopsTheHostFromStarting(string model)
    {
        var register = () =>
            new ServiceCollection().AddTrax(trax =>
                trax.AddEffects(effects => effects.AddNimbleDecider(o => o.Model = model))
            );

        register.Should().Throw<ArgumentException>().WithMessage("*floating alias*");
    }

    [Test]
    public void PlainHttpToAnotherMachine_StopsTheHostFromStarting()
    {
        var register = () =>
            new ServiceCollection().AddTrax(trax =>
                trax.AddEffects(effects =>
                    effects.AddNimbleDecider(o =>
                        o.Endpoint = new Uri("http://gpu-box.internal:11434/v1/systemone")
                    )
                )
            );

        register.Should().Throw<ArgumentException>().WithMessage("*is not HTTPS*");
    }
}
