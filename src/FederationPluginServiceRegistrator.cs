using System;
using System.IO;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Registers the federation channels in DI. Runs before the plugin instances are
/// created (PluginManager.RegisterServices precedes CreatePlugins), so it must NOT
/// use <see cref="Plugin.Instance"/> and loads the configuration itself.
/// </summary>
public class FederationPluginServiceRegistrator : IPluginServiceRegistrator
{
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

        // Per-instance views cache so channels survive a remote being down at
        // startup (spec phase 3). DataPath, not the purged image cache.
        var viewsCache = new FederationViewsCache(appPaths.DataPath, logger);

        // Unauthenticated reachability probe (invariant 11). The config is fixed
        // at startup (restart-on-change semantics), so a closure is correct.
        var status = new FederationStatusService(
            () => config,
            new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
            logger);

        // Register every instance so status rows carry ServerUrl/Username from
        // startup (the probe loop alone would only give it state, not the
        // identifying fields).
        foreach (var instance in config.Instances)
        {
            status.RegisterInstance(instance);
        }

        var factory = new FederationChannelFactory(
            () => new RemoteApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }),
            logger,
            viewsCache: viewsCache,
            clientSetup: (client, alias) => client.SetReporter(report => status.ReportOutcome(alias, report)),
            status: status);

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

        // The status service as singleton (the controller reads it) and as
        // IHostedService (the probe loop starts/stops with the host).
        serviceCollection.AddSingleton(status);
        serviceCollection.AddSingleton<IHostedService>(status);

        // The controller's test-credentials factory: a throwaway client per test
        // (fresh DeviceId via ConfigureAsync — invariant 2 is satisfied).
        serviceCollection.AddSingleton<Func<RemoteApiClient>>(
            sp => () => new RemoteApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
    }

    /// <summary>
    /// Loads the plugin configuration from the plugin configurations directory
    /// ({ProgramDataPath}/plugins/configurations) without touching <see cref="Plugin.Instance"/>
    /// or host services that are not available during registration.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger for configuration problems.</param>
    /// <returns>The deserialized configuration, or defaults when missing or broken.</returns>
    internal static PluginConfiguration LoadConfig(IApplicationPaths appPaths, ILogger logger)
    {
        var path = Path.Combine(appPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Federation.xml");
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
}
