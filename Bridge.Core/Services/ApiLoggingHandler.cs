using Microsoft.Extensions.Logging;
using System.Text;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class ApiLoggingHandler : DelegatingHandler
{
    private readonly ApiTraceStore _trace;
    private readonly ILogger<ApiLoggingHandler> _logger;

    public ApiLoggingHandler(
        ApiTraceStore trace,
        ILogger<ApiLoggingHandler> logger)
    {
        _trace = trace;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? reqBody = null;
        if (request.Content != null)
        {
            reqBody = await request.Content.ReadAsStringAsync(cancellationToken);
            // Replace content so downstream can still read it (safe even if it was stream-based)
            var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
            request.Content = new StringContent(reqBody, Encoding.UTF8, mediaType);
        }

        _trace.Add("REQUEST", $"{request.Method} {request.RequestUri}\n\n{reqBody}");
        _logger.LogInformation("API Request: {Method} {Uri}", request.Method, request.RequestUri);

        var response = await base.SendAsync(request, cancellationToken);

        string? resBody = null;
        if (response.Content != null)
        {
            resBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            response.Content = new StringContent(resBody, Encoding.UTF8, mediaType);
        }

        _trace.Add("RESPONSE", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n\n{resBody}");
        _logger.LogInformation("API Response: {StatusCode}", (int)response.StatusCode);

        return response;
    }
}