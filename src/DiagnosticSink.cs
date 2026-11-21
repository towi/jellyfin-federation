using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Logger used while plugin services are registered. The host's
/// <see cref="ILoggerFactory"/> is not resolvable at that point (the service
/// provider is not built yet), so entries are buffered and replayed once
/// <see cref="Attach"/> receives the real logger from the plugin instance.
/// </summary>
public sealed class DiagnosticSink : ILogger
{
    private const int MaxBufferedEntries = 1000;

    private readonly object _gate = new();
    private readonly Queue<BufferedEntry> _buffer = new();
    private ILogger? _inner;

    /// <summary>
    /// Attaches the real logger and replays all buffered entries.
    /// Subsequent calls replace the target logger.
    /// </summary>
    /// <param name="inner">Host logger.</param>
    public void Attach(ILogger inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        lock (_gate)
        {
            _inner = inner;
            while (_buffer.TryDequeue(out var entry))
            {
                inner.Log(entry.Level, entry.EventId, entry.Message, entry.Exception, static (state, _) => state);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner?.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner?.IsEnabled(logLevel) ?? true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            if (_inner is not null)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            if (_buffer.Count >= MaxBufferedEntries)
            {
                return;
            }

            _buffer.Enqueue(new BufferedEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private readonly record struct BufferedEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
}
