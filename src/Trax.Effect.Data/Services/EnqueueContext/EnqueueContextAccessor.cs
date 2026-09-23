using Trax.Effect.Data.Services.DataContext;

namespace Trax.Effect.Data.Services.EnqueueContext;

/// <inheritdoc />
public class EnqueueContextAccessor : IEnqueueContextAccessor
{
    private IDataContext? _current;

    /// <inheritdoc />
    public IDataContext? Current => _current;

    /// <inheritdoc />
    public IDisposable Enter(IDataContext context)
    {
        if (_current is not null)
            throw new InvalidOperationException(
                "An enqueue context is already active on this scope. OnQueue hooks do not nest."
            );

        _current = context;
        return new Scope(this);
    }

    private sealed class Scope(EnqueueContextAccessor owner) : IDisposable
    {
        public void Dispose() => owner._current = null;
    }
}
