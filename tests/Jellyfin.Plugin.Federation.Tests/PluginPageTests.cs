using System.IO;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PluginPageTests
{
    [Fact]
    public void Plugin_ImplementsIHasWebPages()
    {
        Assert.Contains(typeof(MediaBrowser.Model.Plugins.IHasWebPages), typeof(Plugin).GetInterfaces());
    }

    [Fact]
    public void EmbeddedResource_ExistsInAssembly()
    {
        var assembly = typeof(Plugin).Assembly;
        var names = assembly.GetManifestResourceNames();

        Assert.Contains("Jellyfin.Plugin.Federation.Web.config.html", names);
    }

    [Fact]
    public void EmbeddedResource_ContainsNoPluginIdPlaceholder()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Federation.Web.config.html");
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("30c06888-1907-48cc-957a-6d83e86a39d7", html); // uniquePluginId matches Plugin.Id
        Assert.DoesNotContain("TBD", html, StringComparison.Ordinal);
    }

    // Spec "Testing" table: config round-trip. The host's plugin-config endpoints
    // (PluginsController) serialize with JsonDefaults.Options — PascalCase
    // (PropertyNamingPolicy = null). The page JS must use exactly those keys;
    // a rename in either direction would silently produce an empty form or drop
    // fields on save, so pin the contract here.
    [Fact]
    public void PluginConfiguration_RoundTripsThroughHostJsonContract()
    {
        var config = new PluginConfiguration();
        config.Instances.Add(new FederationInstance
        {
            Alias = "paul",
            ServerUrl = "https://remote.example.com:8920",
            Username = "paul",
            Password = "secret"
        });

        var json = JsonSerializer.Serialize(config, Jellyfin.Extensions.Json.JsonDefaults.Options);

        // The keys the page JS reads and writes (config.Instances / i.Alias / ...).
        Assert.Contains("\"Instances\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Alias\":\"paul\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ServerUrl\":\"https://remote.example.com:8920\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Username\":\"paul\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Password\":\"secret\"", json, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<PluginConfiguration>(json, Jellyfin.Extensions.Json.JsonDefaults.Options);
        Assert.NotNull(roundTripped);
        var inst = Assert.Single(roundTripped!.Instances);
        Assert.Equal("paul", inst.Alias);
        Assert.Equal("https://remote.example.com:8920", inst.ServerUrl);
        Assert.Equal("paul", inst.Username);
        Assert.Equal("secret", inst.Password);
    }
}
