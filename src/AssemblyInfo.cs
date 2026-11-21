using System.Runtime.CompilerServices;

// Exposes internal members (e.g. FederationPluginServiceRegistrator.LoadConfig)
// to the test project. Both build targets compile this file.
[assembly: InternalsVisibleTo("Jellyfin.Plugin.Federation.Tests")]
