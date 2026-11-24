using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationControllerTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static FederationStatusService CreateStatus()
    {
        var handler = new FakeHttpMessageHandler();
        var config = new PluginConfiguration();
        var service = new FederationStatusService(() => config, new HttpClient(handler), Logger);
        return service;
    }

    [Fact]
    public void GetStatus_ReturnsSnapshotWithoutSecrets()
    {
        var status = CreateStatus();
        status.RegisterInstance(new FederationInstance { Alias = "paul", ServerUrl = "https://remote.example.com:8920", Username = "paul", Password = "secret-token" });
        var controller = new FederationController(status, () => throw new InvalidOperationException("not used"));

        var result = controller.GetStatus();

        var json = JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value!, FederationJsonOptions.Options);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.Ordinal);
    }

    // The main-menu status page is for every user of A: the public endpoint
    // must carry only the reduced, non-sensitive fields — never the server
    // URL or username (those reveal the setup of the federated link) and no
    // error text.
    [Fact]
    public void GetStatusPublic_ReturnsOnlyReducedFields()
    {
        var status = CreateStatus();
        status.RegisterInstance(new FederationInstance { Alias = "paul", ServerUrl = "https://remote.example.com:8920", Username = "paul", Password = "secret-token" });
        status.RegisterChannels("paul", new[] { "paul · Movies" });
        var controller = new FederationController(status, () => throw new InvalidOperationException("not used"));

        var result = controller.GetStatusPublic();

        var json = JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value!, FederationJsonOptions.Options);
        Assert.Contains("\"alias\":\"paul\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Unknown\"", json, StringComparison.Ordinal); // not yet probed
        // The middot (U+00B7) is escaped by the default encoder, uppercase.
        Assert.Contains("\"channels\":[\"paul \\u00B7 Movies\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("remote.example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lasterror", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverurl", json, StringComparison.OrdinalIgnoreCase);
    }

    // Authorization split: the public endpoint requires only authentication
    // (any user of A sees the reduced status); the full snapshot and the
    // credential test require elevation. A policy swap would either leak the
    // setup to every user or hide the status from regular users.
    [Fact]
    public void StatusEndpoints_CarryCorrectPolicies()
    {
        var type = typeof(FederationController);

        var publicAuth = type.GetMethod("GetStatusPublic")!.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single();
        Assert.Null(publicAuth.Policy);
        Assert.Null(publicAuth.Roles);

        var fullAuth = type.GetMethod("GetStatus")!.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single();
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, fullAuth.Policy);

        var testAuth = type.GetMethod("TestCredentials")!.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single();
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, testAuth.Policy);
    }

    // EnableInMainMenu = true is what makes the host list the page in every
    // user's main menu (DashboardController filters on it); the page JS then
    // degrades to the reduced status view for non-admins. GetPages uses no
    // instance state, so the uninitialized-object path is safe.
    [Fact]
    public void Plugin_PageEnabledInMainMenu()
    {
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

        var page = Assert.Single(plugin.GetPages());
        Assert.True(page.EnableInMainMenu);
        Assert.Equal("Jellyfin Federation", page.Name);
        Assert.Equal("Jellyfin.Plugin.Federation.Web.config.html", page.EmbeddedResourcePath);
    }

    [Fact]
    public async Task TestCredentials_Success_ReturnsLibraryCount()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal),
            File.ReadAllText("Data/views-response.json"));
        var controller = new FederationController(CreateStatus(), () => new RemoteApiClient(new HttpClient(handler)));

        var result = await controller.TestCredentials(new TestCredentialsRequest("paul", "https://remote.example.com:8920", "paul", "secret"));

        var value = Assert.IsType<OkObjectResult>(result.Result);
        var json = JsonSerializer.Serialize(value.Value!, FederationJsonOptions.Options);
        Assert.Contains("\"ok\":true", json.Replace("True", "true"), StringComparison.OrdinalIgnoreCase);
        // Exactly one login (lockout safety).
        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"));
    }

    [Fact]
    public async Task TestCredentials_401_ReturnsInvalidCredentials_SingleLogin()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            string.Empty,
            HttpStatusCode.Unauthorized);
        var status = CreateStatus();
        status.RegisterInstance(new FederationInstance { Alias = "paul", ServerUrl = "https://remote.example.com:8920", Username = "paul", Password = "x" });
        var controller = new FederationController(status, () => new RemoteApiClient(new HttpClient(handler)));

        var result = await controller.TestCredentials(new TestCredentialsRequest("paul", "https://remote.example.com:8920", "paul", "wrong"));

        var value = Assert.IsType<OkObjectResult>(result.Result);
        var json = JsonSerializer.Serialize(value.Value!, FederationJsonOptions.Options);
        Assert.Contains("InvalidCredentials", json, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // exactly one login attempt
        // The failed test also updates the status service (spec phase 2).
        var row = status.Snapshot().Single(s => s.Alias == "paul");
        Assert.Equal(FederationInstanceState.InvalidCredentials, row.State);
    }

    [Fact]
    public async Task TestCredentials_Unreachable_ReturnsUnreachable()
    {
        var controller = new FederationController(CreateStatus(), () => new RemoteApiClient(new HttpClient(new ThrowingHandler())));

        var result = await controller.TestCredentials(new TestCredentialsRequest("paul", "https://down.example.com", "paul", "x"));

        var value = Assert.IsType<OkObjectResult>(result.Result);
        var json = JsonSerializer.Serialize(value.Value!, FederationJsonOptions.Options);
        Assert.Contains("Unreachable", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCredentialsRequest_BindsPascalCaseBody()
    {
        // The page sends PascalCase (the host's plugin-config wire format,
        // JsonDefaults.Options); pin that the record binds it, otherwise every
        // Test click would be a 400 for reasons the dashboard can't show.
        var request = JsonSerializer.Deserialize<TestCredentialsRequest>(
            """{"Alias":"paul","ServerUrl":"https://remote.example.com:8920","Username":"paul","Password":"secret"}""",
            FederationJsonOptions.Options);

        Assert.NotNull(request);
        Assert.Equal("https://remote.example.com:8920", request!.ServerUrl);
    }

    [Fact]
    public void Endpoints_AreAdminOnly()
    {
        // The host's default policy only requires authentication; the
        // full-status and test-credentials endpoints must pin
        // RequiresElevation per action (verified host behavior). The
        // public status endpoint deliberately uses plain [Authorize] so
        // every authenticated user sees the reduced status. The class
        // itself requires authentication — it must never be
        // [AllowAnonymous] (that opts the whole controller out of
        // authorization and would leak the sensitive endpoints).
        var type = typeof(FederationController);

        // No [AllowAnonymous] anywhere on the controller: it would opt the
        // whole controller out of authorization and leak the sensitive
        // endpoints to unauthenticated users (caught in a live check).
        Assert.Empty(type.GetCustomAttributes().Where(a => a.GetType().Name == "AnonymousAuthorizeAttribute"));

        var classAuth = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(classAuth);
        Assert.Null(classAuth!.Policy);

        var fullStatus = type.GetMethod("GetStatus")!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(fullStatus);
        Assert.Equal("RequiresElevation", fullStatus!.Policy);

        var test = type.GetMethod("TestCredentials")!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(test);
        Assert.Equal("RequiresElevation", test!.Policy);

        var pub = type.GetMethod("GetStatusPublic")!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(pub);
        Assert.Null(pub!.Policy);
        Assert.Null(pub.Roles);
    }
}
