using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Federation status and connection-test endpoints. Admin-only: instance URLs and
/// usernames are not for regular users (the host's default policy would allow any
/// authenticated user — elevation is explicit).
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("[controller]")] // → /Federation
public class FederationController : ControllerBase
{
    private readonly FederationStatusService _status;
    private readonly Func<RemoteApiClient> _testClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationController"/> class.
    /// </summary>
    /// <param name="status">Status service singleton.</param>
    /// <param name="testClientFactory">Creates a throwaway client for connection tests.</param>
    public FederationController(FederationStatusService status, Func<RemoteApiClient> testClientFactory)
    {
        _status = status;
        _testClientFactory = testClientFactory;
    }

    /// <summary>
    /// Gets the per-instance status snapshot.
    /// </summary>
    /// <returns>Status rows; never fails on remote errors (they are data). Never includes the password or an access token.</returns>
    [HttpGet("Status")]
    public ActionResult<object> GetStatus()
        => Ok(_status.Snapshot()
            .Select(s => new
            {
                s.Alias,
                s.ServerUrl,
                s.Username,
                s.State,
                s.LastError,
                s.LastCheckedUtc,
                s.LastAuthenticatedUtc,
                s.RemoteServerName,
                s.RemoteVersion,
                s.Channels
            })
            .ToList());

    /// <summary>
    /// Tests credentials against one remote: exactly one login and views fetch with
    /// a throwaway client (never the persistent one — invariant 2). The outcome is
    /// also fed to the status service (a failed test sets InvalidCredentials; a
    /// successful test clears it).
    /// </summary>
    /// <param name="request">Instance data (alias/server/username/password as entered, not necessarily saved).</param>
    /// <returns>ok + libraryCount, or a reason; wrong credentials count exactly one failed login on the remote.</returns>
    [HttpPost("TestCredentials")]
    public async Task<ActionResult<object>> TestCredentials([FromBody] TestCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ServerUrl)
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { ok = false, reason = "ServerError" });
        }

        using var client = _testClientFactory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // bounded: a stalled remote must not hang the dashboard
        try
        {
            var userId = await client.ConfigureAsync(request.Alias ?? "federation", request.ServerUrl.TrimEnd('/'), request.Username, request.Password, cts.Token).ConfigureAwait(false);
            var views = await client.GetViewsAsync(userId, cts.Token).ConfigureAwait(false);
            var libraryCount = views.Items.Count(v => v.Type == BaseItemKind.CollectionFolder);
            ReportTestOutcome(request.Alias, true, null);
            return Ok(new { ok = true, libraryCount });
        }
        catch (FederationException ex) when (ex.StatusCode is 401 or 403)
        {
            ReportTestOutcome(request.Alias, false, "InvalidCredentials");
            return Ok(new { ok = false, reason = "InvalidCredentials" });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ReportTestOutcome(request.Alias, false, "Unreachable");
            return Ok(new { ok = false, reason = "Unreachable" });
        }
        catch (Exception)
        {
            // Catch-all: any other failure (e.g. FederationException with null
            // StatusCode — "Login ... returned no access token", malformed JSON,
            // invalid Uri) becomes a clean JSON error, not a raw 500.
            ReportTestOutcome(request.Alias, false, "ServerError");
            return Ok(new { ok = false, reason = "ServerError" });
        }
    }

    private void ReportTestOutcome(string? alias, bool ok, string? reason)
    {
        // TestCredentials may run for an unsaved (new) alias — report only for
        // aliases the status service already tracks, so a typo or not-yet-saved
        // instance cannot create a ghost status row.
        if (!_status.IsTracked(alias))
        {
            return;
        }

        _status.ReportOutcome(
            alias,
            ok
                ? new RemoteApiReport(true, null, false)
                : new RemoteApiReport(false, reason, reason == "InvalidCredentials"));
    }
}

/// <summary>
/// Connection-test request payload.
/// </summary>
/// <param name="Alias">Instance alias as entered in the form (may be unsaved).</param>
/// <param name="ServerUrl">Remote base URL as entered.</param>
/// <param name="Username">Remote user name as entered.</param>
/// <param name="Password">Remote password as entered.</param>
public sealed record TestCredentialsRequest(string? Alias, string ServerUrl, string Username, string Password);
