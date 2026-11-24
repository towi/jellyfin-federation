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

    // The host serves a plugin's image from the plugin directory when
    // meta.json carries an "imagePath" (PluginsController.GetPluginImage),
    // and marks the list entry HasImage from it. Both csprojs must therefore
    // copy the logo into the output so the deploy (DLL + logo.png + meta.json)
    // is reproducible, and meta.json must reference exactly that file.
    [Fact]
    public void Logo_IsCopiedToOutput()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "logo.png");
        var actual = Path.GetFullPath(expected);
        Assert.True(File.Exists(actual), $"logo.png not found in test output: {actual}");
    }

    [Fact]
    public void Logo_IsValidPng()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "logo.png");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public void PackagingMetaReferencesDeployedLogo()
    {
        // packaging/meta.json is the template deployed next to the DLL; its
        // imagePath is relative to the plugin directory and must name the file
        // both csprojs copy to the output.
        var path = Path.Combine(FindRepoRoot(), "packaging", "meta.json");
        var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        Assert.Equal("logo.png", manifest.GetProperty("imagePath").GetString());
        Assert.Equal("30c06888-1907-48cc-957a-6d83e86a39d7", manifest.GetProperty("guid").GetString());
        Assert.Equal("Jellyfin Federation", manifest.GetProperty("name").GetString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Federation.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
