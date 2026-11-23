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
        // The host's default policy only requires authentication; our endpoints
        // must pin RequiresElevation explicitly (verified host behavior).
        var authorize = typeof(FederationController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("RequiresElevation", authorize!.Policy);
    }
}
