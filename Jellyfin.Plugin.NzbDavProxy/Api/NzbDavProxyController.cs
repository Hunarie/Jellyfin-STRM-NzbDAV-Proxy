using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.NzbDavProxy.Api;

/// <summary>
/// API controller that bridges Jellyfin to an internal NZBDav instance.
///
/// A rewritten .strm file points at:
///     https://&lt;your-jellyfin&gt;/NZBDavProxy/Stream/&lt;original-nzbdav-path&gt;?api_key=&lt;token&gt;
///
/// This controller reconstructs the original NZBDav URL from the configured base
/// URL plus the captured path, fetches it from *inside* the Docker network, and
/// pipes the bytes straight back to the client.
/// </summary>
[ApiController]
[Authorize(Policy = "DefaultAuthorization")]
[Route("NZBDavProxy")]
public class NzbDavProxyController : ControllerBase
{
    // Headers we copy verbatim from the NZBDav response back to the client.
    // These carry the content type and everything Infuse needs to seek.
    private static readonly string[] _passthroughResponseHeaders =
    {
        HeaderNames.ContentType,
        HeaderNames.ContentLength,
        HeaderNames.ContentRange,
        HeaderNames.AcceptRanges,
        HeaderNames.LastModified,
        HeaderNames.ETag,
        HeaderNames.ContentDisposition,
        HeaderNames.CacheControl
    };

    // Request headers we forward upstream so NZBDav can answer with a proper
    // 206 Partial Content response (this is what makes scrubbing work).
    private static readonly string[] _passthroughRequestHeaders =
    {
        HeaderNames.Range,
        HeaderNames.IfRange,
        HeaderNames.IfModifiedSince,
        HeaderNames.IfNoneMatch
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NzbDavProxyController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NzbDavProxyController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">
    /// Jellyfin already registers <see cref="IHttpClientFactory"/> in its DI
    /// container, so it is injected here with no extra wiring required.
    /// </param>
    /// <param name="logger">The logger.</param>
    public NzbDavProxyController(
        IHttpClientFactory httpClientFactory,
        ILogger<NzbDavProxyController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Streams a file from the internal NZBDav instance.
    /// </summary>
    /// <param name="path">
    /// Catch-all route segment holding the original NZBDav path (slashes preserved),
    /// e.g. <c>content/Movies/Example (2024)/Example.mkv</c>.
    /// </param>
    /// <param name="cancellationToken">Aborted automatically when the client disconnects.</param>
    /// <returns>The streamed media response.</returns>
    [HttpGet("Stream/{**path}")]
    [HttpHead("Stream/{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> Stream(
        [FromRoute] string path,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.NzbDavBaseUrl))
        {
            _logger.LogError("NZBDav Proxy is not configured with a base URL.");
            return StatusCode(StatusCodes.Status500InternalServerError, "NZBDav base URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("No upstream path supplied.");
        }

        // Rebuild the original NZBDav URL: <base>/<path>[?<preserved query>].
        // We preserve any query string the original URL carried, minus the
        // api_key/ApiKey we use for Jellyfin auth (it has no meaning to NZBDav).
        var baseUrl = config.NzbDavBaseUrl.TrimEnd('/');
        var query = BuildUpstreamQuery();
        var targetUrl = $"{baseUrl}/{path.TrimStart('/')}{query}";

        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        using var upstreamRequest = new HttpRequestMessage(
            HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
            targetUrl);

        // Forward Range / conditional headers so NZBDav can return 206 partial content.
        foreach (var header in _passthroughRequestHeaders)
        {
            if (Request.Headers.TryGetValue(header, out var value))
            {
                upstreamRequest.Headers.TryAddWithoutValidation(header, value.ToArray());
            }
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            // ResponseHeadersRead => we get control as soon as the headers arrive,
            // WITHOUT buffering the whole video into memory. This is the key to
            // streaming arbitrarily large files at constant, low memory usage.
            upstreamResponse = await client
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client (Infuse) went away mid-request; nothing to do.
            return new EmptyResult();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach NZBDav at {TargetUrl}", targetUrl);
            return StatusCode(StatusCodes.Status502BadGateway, "Could not reach the internal NZBDav instance.");
        }

        try
        {
            if (upstreamResponse.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("NZBDav returned 404 for {TargetUrl}", targetUrl);
                return NotFound();
            }

            // Relay the upstream status code verbatim (200 for full, 206 for ranges).
            Response.StatusCode = (int)upstreamResponse.StatusCode;

            // Copy the headers Infuse needs for content-typing and seeking.
            CopyResponseHeaders(upstreamResponse);

            // Always advertise range support even if NZBDav omitted the header.
            if (!Response.Headers.ContainsKey(HeaderNames.AcceptRanges))
            {
                Response.Headers[HeaderNames.AcceptRanges] = "bytes";
            }

            // HEAD requests must not carry a body.
            if (HttpMethods.IsHead(Request.Method))
            {
                return new EmptyResult();
            }

            // Pipe the body straight through. CopyToAsync streams in fixed-size
            // chunks, so memory stays flat regardless of file size.
            await using var upstreamStream = await upstreamResponse.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await upstreamStream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal when the player seeks (it cancels the in-flight request and
            // opens a new ranged one). Not an error.
            return new EmptyResult();
        }
        finally
        {
            upstreamResponse.Dispose();
        }
    }

    /// <summary>
    /// Builds the query string to send upstream, stripping the Jellyfin auth token.
    /// </summary>
    private string BuildUpstreamQuery()
    {
        if (Request.Query.Count > 0)
        {
            var builder = new QueryBuilder();
            foreach (var pair in Request.Query)
            {
                if (string.Equals(pair.Key, "api_key", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "ApiKey", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.Add(pair.Key, pair.Value.ToString());
            }

            return builder.ToString();
        }

        return string.Empty;
    }

    /// <summary>
    /// Copies the whitelisted headers from the NZBDav response onto our response.
    /// </summary>
    private void CopyResponseHeaders(HttpResponseMessage upstreamResponse)
    {
        foreach (var header in _passthroughResponseHeaders)
        {
            if (upstreamResponse.Headers.TryGetValues(header, out var values))
            {
                Response.Headers[header] = values.ToArray();
            }
            else if (upstreamResponse.Content.Headers.TryGetValues(header, out var contentValues))
            {
                Response.Headers[header] = contentValues.ToArray();
            }
        }
    }
}
