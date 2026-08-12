using System.Net.Http.Headers;
using System.Text;

namespace AgentBell.Hook;

/// <summary>Posts validated Codex JSON to the loopback-only AgentBell desktop ingestion endpoint.</summary>
public sealed class HttpEventForwarder : IEventForwarder
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly bool _endpointAvailable;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes a loopback event forwarder.</summary>
    /// <param name="httpClient">The HTTP client used for the request.</param>
    /// <param name="timeout">An optional total request timeout; defaults to 500 ms.</param>
    public HttpEventForwarder(HttpClient httpClient, TimeSpan? timeout = null)
        : this(httpClient, HookEndpointResolver.ResolveWithAvailability(), timeout)
    {
    }

    /// <summary>Initializes a forwarder with an explicit loopback endpoint.</summary>
    public HttpEventForwarder(HttpClient httpClient, Uri endpoint, TimeSpan? timeout = null)
        : this(httpClient, endpoint, endpointAvailable: true, timeout)
    {
    }

    /// <summary>Initializes a forwarder from an endpoint resolution that may fail closed.</summary>
    public HttpEventForwarder(
        HttpClient httpClient,
        HookEndpointResolution endpointResolution,
        TimeSpan? timeout)
        : this(
            httpClient,
            endpointResolution.Endpoint,
            endpointResolution.IsAvailable,
            timeout)
    {
    }

    private HttpEventForwarder(
        HttpClient httpClient,
        Uri endpoint,
        bool endpointAvailable,
        TimeSpan? timeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal)
            || !string.Equals(endpoint.AbsolutePath, "/api/v1/events/codex", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("The Hook endpoint must be the isolated loopback ingestion path.", nameof(endpoint));
        }

        _endpoint = endpoint;
        _endpointAvailable = endpointAvailable;
        _timeout = timeout ?? DefaultTimeout;

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    /// <inheritdoc />
    public async Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        if (!_endpointAvailable)
        {
            return ForwardResult.Failed(HookErrorCodes.ForwardUnavailable);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(rawJson, Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? ForwardResult.Accepted(statusCode)
                : ForwardResult.Failed(HookErrorCodes.ForwardRejected, statusCode);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            return ForwardResult.Failed(HookErrorCodes.ForwardTimeout);
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested)
        {
            return ForwardResult.Failed(HookErrorCodes.ForwardTimeout);
        }
        catch (HttpRequestException)
        {
            return ForwardResult.Failed(HookErrorCodes.ForwardUnavailable);
        }
    }
}
