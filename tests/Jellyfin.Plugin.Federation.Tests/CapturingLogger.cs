using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Logger that records every entry for assertions.
/// </summary>
public sealed class CapturingLogger : ILogger
{
    /// <summary>
    /// Gets the recorded entries as (level, message) tuples.
    /// </summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
