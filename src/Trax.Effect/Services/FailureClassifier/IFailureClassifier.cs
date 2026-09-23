using Trax.Core.Exceptions;

namespace Trax.Effect.Services.FailureClassifier;

/// <summary>
/// Decides what kind of failure an exception represents. Register one to have Trax record a
/// <see cref="FailureClass"/> on every failed run.
/// </summary>
/// <remarks>
/// Optional: with none registered, failures record <see cref="FailureClass.Unclassified"/> and
/// nothing changes.
///
/// It is called where the failure happens, holding the original exception object rather than a
/// wrapper, so it can type-check and read structured error data instead of parsing a message.
///
/// Throwing is not fatal — the exception is logged and the failure records as unclassified. A
/// classifier must never be able to mask the failure it was asked about.
/// </remarks>
public interface IFailureClassifier
{
    /// <summary>
    /// Returns the class of this failure, or null to leave it unclassified.
    /// </summary>
    FailureClass? Classify(Exception exception);
}
