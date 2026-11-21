using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// HttpMessageHandler that returns pre-registered responses matched against requests.
/// </summary>
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Matcher, string Body, HttpStatusCode StatusCode)> _responses = new();

    /// <summary>
    /// Gets all request messages that were received.
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// Gets the bodies of all received request messages.
    /// </summary>
    public List<string> RequestBodies { get; set; } = new();

    /// <summary>
    /// Gets or sets the access token (from Data/authenticate-response.json).
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Registers a successful (200 OK) response body that is returned when the matcher matches.
    /// </summary>
    /// <param name="matcher">Request matcher.</param>
    /// <param name="body">JSON response body.</param>
    public void Add(Func<HttpRequestMessage, bool> matcher, string body)
        => Add(matcher, body, HttpStatusCode.OK);

    /// <summary>
    /// Registers a response body with an explicit status code that is returned when the matcher matches.
    /// </summary>
    /// <param name="matcher">Request matcher.</param>
    /// <param name="body">JSON response body.</param>
    /// <param name="statusCode">Response status code.</param>
    public void Add(Func<HttpRequestMessage, bool> matcher, string body, HttpStatusCode statusCode)
        => _responses.Add((matcher, body, statusCode));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        foreach (var (matcher, body, statusCode) in _responses)
        {
            if (matcher(request))
            {
                return new HttpResponseMessage
                {
                    StatusCode = statusCode,
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
        }

        throw new InvalidOperationException($"FakeHttpMessageHandler: no registered response for {request.Method} {request.RequestUri}");
    }
}
