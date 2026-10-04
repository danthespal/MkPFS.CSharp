namespace MkPFS.Core.AMPR;

/// <summary>
/// Pack build or configuration error (Python <c>PackError</c>). The CLI prints <c>error: &lt;message&gt;</c> and exits 2,
/// as it does for <see cref="ArgumentException"/> and <see cref="InvalidDataException"/> (Python <c>ValueError</c>).
/// </summary>
public sealed class AMPRPackException : Exception
{
    /// <summary>Create an error.</summary>
    /// <param name="message">Message.</param>
    public AMPRPackException(string message)
        : base(message)
    {
    }

    /// <summary>Create an error with a cause.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public AMPRPackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
