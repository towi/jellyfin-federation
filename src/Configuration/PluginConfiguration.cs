using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Federation.Configuration;

/// <summary>
/// Plugin configuration: list of federation instances.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Instances = new List<FederationInstance>();
    }

    /// <summary>
    /// Gets or sets the federation instances.
    /// </summary>
    public List<FederationInstance> Instances { get; set; }
}

/// <summary>
/// One remote Jellyfin server.
/// </summary>
public class FederationInstance
{
    /// <summary>
    /// Gets or sets the short alias, e.g. "paul".
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote base URL, e.g. "https://paul.tail1234.ts.net:8920".
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote user name to log in as.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote user's password (stored in this config file; protect via filesystem permissions).
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
