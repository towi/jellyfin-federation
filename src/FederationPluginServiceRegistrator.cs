using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Registers the federation channels in DI. Runs before the plugin instances are
/// created (PluginManager.RegisterServices precedes CreatePlugins), so it must NOT
/// use <see cref="Plugin.Instance"/> and loads the configuration itself.
/// </summary>
public class FederationPluginServiceRegistrator : IPluginServiceRegistrator
{
    private const string ConfigFileName = "Jellyfin.Plugin.Federation.xml";
    private const string EnvConfigPath = "FEDERATION_CONFIG";
    private const string EnvInstances = "FEDERATION_INSTANCES";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // applicationHost.Resolve<T>() CANNOT be used here: it goes through
        // ApplicationHost.ServiceProvider, which is still null during plugin
        // service registration (it is assigned only after Host.Build()).
        // ApplicationHost.RegisterServices has already run, so the
        // IApplicationPaths singleton instance is present as a descriptor —
        // extract it directly instead of building a provider (the collection
        // is incomplete at this point; e.g. ILoggerFactory is not registered
        // yet, so BuildServiceProvider + GetRequiredService would throw).
        var appPaths = serviceCollection
            .FirstOrDefault(d => d.ServiceType == typeof(IApplicationPaths))
            ?.ImplementationInstance as IApplicationPaths
            ?? throw new InvalidOperationException("IApplicationPaths not registered yet");

        // The host logger is not resolvable yet (service provider not built).
        // DiagnosticSink buffers until Plugin.AttachDiagnostics replays into it.
        var logger = new DiagnosticSink();
        serviceCollection.AddSingleton(logger);

        var config = LoadConfig(appPaths, logger);
        var factory = new FederationChannelFactory(
            () => new RemoteApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }),
            logger);

        // Blocking is acceptable for V1: failed or slow instances are skipped
        // after a bounded number of login attempts (worst case per instance is
        // ~4 x (HttpClient timeout 15s + 3s delay) = ~69s, serialized across
        // instances) and the server starts with the remaining (or zero) channels.
        IReadOnlyList<FederationChannel> channels;
        try
        {
            channels = factory.CreateChannelsAsync(config, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Federation: channel creation failed");
            channels = Array.Empty<FederationChannel>();
        }

        foreach (var channel in channels)
        {
            // Channels must be registered as the non-generic IChannel service so
            // ChannelManager receives all of them through its IEnumerable<IChannel>.
            serviceCollection.AddSingleton(typeof(IChannel), channel);
        }
    }

    /// <summary>
    /// Loads the plugin configuration without touching <see cref="Plugin.Instance"/>
    /// or host services that are not available during registration.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger for configuration problems.</param>
    /// <returns>The deserialized configuration, or defaults when missing or broken.</returns>
    internal static PluginConfiguration LoadConfig(IApplicationPaths appPaths, ILogger logger)
    {
        var config = LoadConfigFile(appPaths, logger);
        var instancesFromEnv = Environment.GetEnvironmentVariable(EnvInstances);
        if (!string.IsNullOrWhiteSpace(instancesFromEnv))
        {
            // Environment variables take precedence over the file so a single
            // `docker run` flag can override a baked-in or mounted XML.
            config.Instances = ParseInstancesFromEnv(instancesFromEnv, logger);
        }
        return config;
    }

    private static PluginConfiguration LoadConfigFile(IApplicationPaths appPaths, ILogger logger)
    {
        // The file path can be overridden for container installs where the
        // data directory layout differs (FEDERATION_CONFIG=/path/to.xml).
        var path = Environment.GetEnvironmentVariable(EnvConfigPath)
            ?? Path.Combine(appPaths.PluginConfigurationsPath, ConfigFileName);
        if (!File.Exists(path))
        {
            logger.LogInformation("Federation: no configuration file at {Path}; no channels registered", path);
            return new PluginConfiguration();
        }

        try
        {
            // Standard XmlSerializer is compatible with the host's serializer
            // (the host's MyXmlSerializer wraps System.Xml.Serialization too).
            using var stream = File.OpenRead(path);
            return (PluginConfiguration?)new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Deserialize(stream)
                ?? new PluginConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Federation: cannot read configuration file {Path}; no channels registered", path);
            return new PluginConfiguration();
        }
    }

    /// <summary>
    /// Parses FEDERATION_INSTANCES, a JSON array of instance objects with the
    /// camelCase keys "alias", "serverUrl", "username", "password"
    /// (e.g. [{"alias":"paul","serverUrl":"https://b.example:8920","username":"paul","password":"secret"}]).
    /// </summary>
    /// <param name="raw">Raw environment variable value (non-blank).</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The parsed instances; empty list on broken input (the plugin then starts without channels, like a broken file).</returns>
    internal static List<FederationInstance> ParseInstancesFromEnv(string raw, ILogger logger)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<FederationInstance[]>(raw, FederationJsonOptions.Options) ?? [];
            var usable = parsed.Where(i => !string.IsNullOrWhiteSpace(i.Alias)
                && !string.IsNullOrWhiteSpace(i.ServerUrl)
                && !string.IsNullOrWhiteSpace(i.Username)
                && !string.IsNullOrWhiteSpace(i.Password)).ToList();
            if (usable.Count < parsed.Length)
            {
                logger.LogWarning("Federation: {Skipped} of {Total} FEDERATION_INSTANCES entries skipped (missing alias, serverUrl, username or password)",
                    parsed.Length - usable.Count, parsed.Length);
            }
            return usable;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Federation: FEDERATION_INSTANCES is not a valid JSON array of instances; no channels registered");
            return [];
        }
    }
}
