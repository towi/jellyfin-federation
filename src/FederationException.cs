using System;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Error communicating with a remote Jellyfin server.
/// </summary>
public class FederationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FederationException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public FederationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="statusCode">HTTP status code that caused the error, if any.</param>
    public FederationException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the HTTP status code that caused the error, or null for non-HTTP failures.
    /// </summary>
    public int? StatusCode { get; }
}
