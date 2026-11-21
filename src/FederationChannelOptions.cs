namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Immutable options of one remote library channel.
/// The access token is deliberately not part of the options: it can change
/// mid-run (401 re-login) and is read from the client at mapping time.
/// </summary>
/// <param name="Alias">Instance alias, e.g. "paul".</param>
/// <param name="ChannelName">Display name, "{Alias} · {Library}" — the host keys channel
/// entities on the name, so the factory guarantees uniqueness across all channels.</param>
/// <param name="RemoteLibraryId">Remote library item id (hex string, N-format).</param>
/// <param name="ServerUrl">Remote base URL without trailing slash.</param>
public sealed record FederationChannelOptions(string Alias, string ChannelName, string RemoteLibraryId, string ServerUrl);
