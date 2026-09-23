using FluentAssertions;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Exceptions;
using Trax.Core.Junction;
using Trax.Effect.Extensions;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Services.FailureClassifier;
using Trax.Effect.Services.ServiceTrain;
using Trax.Effect.Tests.Data.InMemory.Integration.Fixtures;

namespace Trax.Effect.Tests.Data.InMemory.Integration.IntegrationTests;

/// <summary>
/// A failed run records what kind of failure it was, so a consumer can decide between retrying,
/// rebuilding against current state, and giving up without matching exception messages at every
/// call site.
///
/// <para>
/// Classification happens where the failure happens, holding the real exception. These tests pin
/// that, because a future change that starts wrapping exceptions would otherwise quietly turn every
/// classification into <c>Unclassified</c> rather than failing.
/// </para>
/// </summary>
public class FailureClassificationTests : TestSetup
{
    private static readonly ConfigurableClassifier Classifier = new();

    public override ServiceProvider ConfigureServices(IServiceCollection services) =>
        services
            .AddSingleton<IFailureClassifier>(Classifier)
            .AddScopedTraxRoute<IClassifiedFailingTrain, ClassifiedFailingTrain>()
            .AddScopedTraxRoute<IClassifiedCancellingTrain, ClassifiedCancellingTrain>()
            .AddScopedTraxRoute<IClassifiedPassingTrain, ClassifiedPassingTrain>()
            .BuildServiceProvider();

    [SetUp]
    public void ResetClassifier() => Classifier.Reset();

    [Test]
    public async Task A_classified_failure_records_the_classification()
    {
        Classifier.Result = FailureClass.Conflict;
        var train = Resolve();

        await Run(train);

        train.SeenClass.Should().Be(FailureClass.Conflict);
    }

    [Test]
    public async Task A_classifier_returning_null_leaves_the_failure_unclassified()
    {
        Classifier.Result = null;
        var train = Resolve();

        await Run(train);

        train.SeenClass.Should().Be(FailureClass.Unclassified);
    }

    [Test]
    public async Task The_classifier_sees_the_original_exception_type()
    {
        Classifier.Result = FailureClass.Permanent;
        var train = Resolve();

        await Run(train);

        Classifier
            .Seen.Should()
            .BeOfType<BespokeFailure>(
                "junctions enrich the exception and return it rather than wrapping it, which is "
                    + "what lets a classifier type-check instead of parsing a message"
            );
    }

    [Test]
    public async Task A_classifier_that_throws_does_not_mask_the_original_failure()
    {
        Classifier.Throws = true;
        var train = Resolve();

        var act = async () => await train.Run(Unit.Default);

        await act.Should()
            .ThrowAsync<Exception>("the train's failure is what surfaces, not the classifier's");
        train
            .SeenClass.Should()
            .Be(
                FailureClass.Unclassified,
                "a classifier that fails leaves the failure unclassified"
            );
    }

    [Test]
    public async Task A_cancelled_run_is_not_classified()
    {
        Classifier.Result = FailureClass.Permanent;
        var train = (ClassifiedCancellingTrain)
            Scope.ServiceProvider.GetRequiredService<IClassifiedCancellingTrain>();

        var act = async () => await train.Run(Unit.Default);
        await act.Should().ThrowAsync<Exception>();

        Classifier
            .Seen.Should()
            .BeNull("cancellation is not a failure, so there is nothing to classify");
    }

    [Test]
    public async Task A_successful_run_is_not_classified()
    {
        Classifier.Result = FailureClass.Permanent;
        var train = (ClassifiedPassingTrain)
            Scope.ServiceProvider.GetRequiredService<IClassifiedPassingTrain>();

        await train.Run(Unit.Default);

        Classifier.Seen.Should().BeNull();
    }

    [Test]
    public async Task The_classification_is_written_onto_the_exception_data()
    {
        Classifier.Result = FailureClass.Transient;
        var train = Resolve();

        await Run(train);

        train
            .SeenOnExceptionData.Should()
            .Be(
                FailureClass.Transient,
                "the class rides on the exception data so a remotely-executed run can carry it home"
            );
    }

    private ClassifiedFailingTrain Resolve() =>
        (ClassifiedFailingTrain)Scope.ServiceProvider.GetRequiredService<IClassifiedFailingTrain>();

    private static async Task Run(ClassifiedFailingTrain train)
    {
        var act = async () => await train.Run(Unit.Default);
        await act.Should().ThrowAsync<Exception>();
    }

    // ── probes ──────────────────────────────────────────────────────

    public class ConfigurableClassifier : IFailureClassifier
    {
        public FailureClass? Result { get; set; }
        public bool Throws { get; set; }
        public Exception? Seen { get; private set; }

        public void Reset()
        {
            Result = null;
            Throws = false;
            Seen = null;
        }

        public FailureClass? Classify(Exception exception)
        {
            Seen = exception;
            if (Throws)
                throw new InvalidOperationException("classifier is broken");
            return Result;
        }
    }

    public class BespokeFailure(string message) : Exception(message);

    public class ThrowingJunction : Junction<Unit, Unit>
    {
        public override Task<Unit> Run(Unit input) =>
            throw new BespokeFailure("the junction could not do the thing");
    }

    public interface IClassifiedFailingTrain : IServiceTrain<Unit, Unit>;

    public class ClassifiedFailingTrain : ServiceTrain<Unit, Unit>, IClassifiedFailingTrain
    {
        public FailureClass? SeenClass { get; private set; }
        public FailureClass? SeenOnExceptionData { get; private set; }

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Chain<ThrowingJunction>().Resolve();

        protected override Task OnFailed(
            Metadata metadata,
            Exception exception,
            CancellationToken ct
        )
        {
            SeenClass = metadata.FailureClass;
            SeenOnExceptionData = (
                exception.Data["TrainExceptionData"] as TrainExceptionData
            )?.FailureClass;
            return Task.CompletedTask;
        }
    }

    public class CancellingJunction : Junction<Unit, Unit>
    {
        public override Task<Unit> Run(Unit input) => throw new OperationCanceledException();
    }

    public interface IClassifiedCancellingTrain : IServiceTrain<Unit, Unit>;

    public class ClassifiedCancellingTrain : ServiceTrain<Unit, Unit>, IClassifiedCancellingTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Chain<CancellingJunction>().Resolve();
    }

    public interface IClassifiedPassingTrain : IServiceTrain<Unit, Unit>;

    public class ClassifiedPassingTrain : ServiceTrain<Unit, Unit>, IClassifiedPassingTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }
}
