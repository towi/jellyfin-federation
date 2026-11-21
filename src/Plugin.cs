using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Jellyfin Federation plugin: integrates remote Jellyfin servers as channels
/// and proxies their streams through this server.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="diagnostics">Registration-time log sink; receives the host logger here.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger, DiagnosticSink diagnostics)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Logger = logger;

        // Plugin instances are created after the service provider exists, so this
        // is the earliest point where the host logger can be handed to the
        // messages buffered during service registration (config load, remote logins).
        diagnostics.Attach(logger);
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the plugin logger.
    /// </summary>
    public ILogger<Plugin> Logger { get; }

    /// <inheritdoc/>
    public override string Name => "Jellyfin Federation";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("30c06888-1907-48cc-957a-6d83e86a39d7");

    /// <inheritdoc/>
    public override string Description => "Integrates remote Jellyfin servers as channels with stream proxying.";
}
