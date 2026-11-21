using System;
using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationPluginServiceRegistratorTests : IDisposable
{
    private static readonly FederationInstance Paul = new()
    {
        Alias = "paul",
        ServerUrl = "http://b.example:8920",
        Username = "paul",
        Password = "secret",
    };

    private readonly string _configDir;
    private readonly string _envXmlPath;
    private readonly string _fileXmlPath;
    private readonly FakeAppPaths _appPaths = new();

    public FederationPluginServiceRegistratorTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "jellyfin-federation-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_configDir);
        _fileXmlPath = Path.Combine(_configDir, "Jellyfin.Plugin.Federation.xml");
        _envXmlPath = Path.Combine(_configDir, "env-overridden.xml");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FEDERATION_CONFIG", null);
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES", null);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; temp files get cleaned by the OS.
        }
    }

    [Fact]
    public void LoadConfig_ReadsFileFromPluginConfigurationsPath()
    {
        WriteFile(_fileXmlPath);
        _appPaths.ConfigDir = _configDir;

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal(Paul.Alias, config.Instances[0].Alias);
    }

    [Fact]
    public void LoadConfig_MissingFileYieldsNoInstances()
    {
        _appPaths.ConfigDir = _configDir;

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Empty(config.Instances);
    }

    [Fact]
    public void LoadConfig_FederationConfigEnvOverridesFilePath()
    {
        WriteFile(_envXmlPath);
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_CONFIG", _envXmlPath);

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal(Paul.ServerUrl, config.Instances[0].ServerUrl);
    }

    [Fact]
    public void LoadConfig_FederationConfigPointingAtMissingFileYieldsNoInstances()
    {
        WriteFile(_fileXmlPath);
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_CONFIG", Path.Combine(_configDir, "does-not-exist.xml"));

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Empty(config.Instances);
    }

    [Fact]
    public void LoadConfig_BrokenFileYieldsNoInstances()
    {
        File.WriteAllText(_fileXmlPath, "this is not xml");
        _appPaths.ConfigDir = _configDir;

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Empty(config.Instances);
    }

    [Fact]
    public void LoadConfig_EnvInstancesReplaceFileInstances()
    {
        WriteFile(_fileXmlPath);
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES",
            "[{\"alias\":\"paul\",\"serverUrl\":\"http://env.example:8920\",\"username\":\"paul\",\"password\":\"env-secret\"}]");

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal("http://env.example:8920", config.Instances[0].ServerUrl);
        Assert.Equal("env-secret", config.Instances[0].Password);
    }

    [Fact]
    public void LoadConfig_EnvInstancesUsedWhenNoFileExists()
    {
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES",
            "[{\"alias\":\"paul\",\"serverUrl\":\"http://env.example:8920\",\"username\":\"paul\",\"password\":\"env-secret\"}]");

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal("paul", config.Instances[0].Alias);
    }

    [Fact]
    public void LoadConfig_EnvWithWhitespaceKeysAndMixedCaseStillParses()
    {
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES",
            "[{ \"ALIAS\" : \"paul\", \"ServerUrl\" : \"http://env.example:8920\", \"username\" : \"paul\", \"password\" : \"s\" }]");

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal("paul", config.Instances[0].Alias);
    }

    [Fact]
    public void LoadConfig_BrokenEnvJsonYieldsNoInstancesAndDoesNotCrash()
    {
        WriteFile(_fileXmlPath);
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES", "{ not json");

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Empty(config.Instances);
    }

    [Fact]
    public void LoadConfig_EnvEntriesMissingFieldsAreSkipped()
    {
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES",
            "[{\"alias\":\"paul\",\"serverUrl\":\"http://a.example:8920\",\"username\":\"paul\",\"password\":\"s\"},"
            + "{\"alias\":\"x\",\"serverUrl\":\"http://b.example:8920\"},"
            + "{\"alias\":\"y\",\"password\":\"p\"}]");

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal("paul", config.Instances[0].Alias);
    }

    [Fact]
    public void LoadConfig_BlankEnvIsIgnored()
    {
        WriteFile(_fileXmlPath);
        _appPaths.ConfigDir = _configDir;
        Environment.SetEnvironmentVariable("FEDERATION_INSTANCES", "   ");

        var config = FederationPluginServiceRegistrator.LoadConfig(_appPaths, new CapturingLogger());

        Assert.Single(config.Instances);
        Assert.Equal(Paul.Password, config.Instances[0].Password);
    }

    private void WriteFile(string path)
    {
        var config = new PluginConfiguration { Instances = { Paul } };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = File.Create(path);
        serializer.Serialize(stream, config);
    }

    private sealed class FakeAppPaths : IApplicationPaths
    {
        public string ConfigDir { get; set; } = string.Empty;

        public string PluginConfigurationsPath => ConfigDir;

        public string ProgramDataPath => throw new NotSupportedException();

        public string WebPath => throw new NotSupportedException();

        public string ProgramSystemPath => throw new NotSupportedException();

        public string DataPath => throw new NotSupportedException();

        public string ImageCachePath => throw new NotSupportedException();

        public string PluginsPath => throw new NotSupportedException();

        public string LogDirectoryPath => throw new NotSupportedException();

        public string ConfigurationDirectoryPath => throw new NotSupportedException();

        public string SystemConfigurationFilePath => throw new NotSupportedException();

        public string CachePath => throw new NotSupportedException();

        public string TempDirectory => throw new NotSupportedException();

        public string VirtualDataPath => throw new NotSupportedException();

        public string TrickplayPath => throw new NotSupportedException();

        public string BackupPath => throw new NotSupportedException();

        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) => throw new NotSupportedException();
    }
}
