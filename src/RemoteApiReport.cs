namespace Jellyfin.Plugin.Federation;

/// <summary>
/// One authenticated-call outcome (first line of the status service data).
/// </summary>
/// <param name="Succeeded">Whether an authenticated call to the remote succeeded.</param>
/// <param name="Error">Sanitized short error (no stack, no query strings), null on success.</param>
/// <param name="AuthFailed">True when the error is a definitive 401/403.</param>
public sealed record RemoteApiReport(bool Succeeded, string? Error, bool AuthFailed);
