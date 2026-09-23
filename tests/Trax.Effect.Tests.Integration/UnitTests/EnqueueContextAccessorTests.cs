using FluentAssertions;
using NSubstitute;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Data.Services.EnqueueContext;

namespace Trax.Effect.Tests.Integration.UnitTests;

/// <summary>
/// Covers the ambient enqueue context an <c>OnQueue</c> hook reads to join the enqueue's
/// transaction. The scoping rules matter: a hook that reads <c>Current</c> outside an enqueue would
/// otherwise silently write on a context nobody commits.
/// </summary>
[TestFixture]
public class EnqueueContextAccessorTests
{
    private static IDataContext AContext() => Substitute.For<IDataContext>();

    [Test]
    public void Current_is_null_before_any_enqueue()
    {
        new EnqueueContextAccessor().Current.Should().BeNull();
    }

    [Test]
    public void Entering_exposes_the_context()
    {
        var accessor = new EnqueueContextAccessor();
        var context = AContext();

        using (accessor.Enter(context))
            accessor.Current.Should().BeSameAs(context);
    }

    [Test]
    public void Leaving_the_scope_clears_the_context()
    {
        var accessor = new EnqueueContextAccessor();

        using (accessor.Enter(AContext())) { }

        accessor
            .Current.Should()
            .BeNull(
                "a hook reading Current after the enqueue would write on a context nobody commits"
            );
    }

    [Test]
    public void A_second_enqueue_can_reuse_the_accessor_once_the_first_has_left()
    {
        var accessor = new EnqueueContextAccessor();
        var second = AContext();

        using (accessor.Enter(AContext())) { }

        using (accessor.Enter(second))
            accessor.Current.Should().BeSameAs(second);
    }

    [Test]
    public void Entering_twice_without_leaving_throws()
    {
        var accessor = new EnqueueContextAccessor();
        using var _ = accessor.Enter(AContext());

        var act = () => accessor.Enter(AContext());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*do not nest*",
                "silently replacing the context would strand whatever the outer enqueue tracked"
            );
    }

    [Test]
    public void Disposing_the_scope_twice_is_harmless()
    {
        var accessor = new EnqueueContextAccessor();
        var scope = accessor.Enter(AContext());

        scope.Dispose();
        var act = () => scope.Dispose();

        act.Should().NotThrow();
        accessor.Current.Should().BeNull();
    }
}
