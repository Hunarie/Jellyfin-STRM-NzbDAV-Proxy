using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NzbDavProxy.Configuration;

/// <summary>
/// Plugin configuration. These values are editable from the Jellyfin
/// Dashboard -> Plugins -> NZBDav Proxy settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // The internal Docker address of the NZBDav container. This is the value
        // that currently appears inside your .strm files and that Infuse cannot
        // reach from outside your LAN. The proxy controller resolves every request
        // against this base URL from *inside* the Docker network.
        NzbDavBaseUrl = "http://nzbdav:3000";

        // How long (in seconds) the proxy waits for NZBDav to send response
        // headers before giving up. This bounds the connect/handshake only - it
        // does NOT limit how long a video may stream. NZBDav can take a moment to
        // mount/prepare a file from usenet, so keep this generous.
        UpstreamTimeoutSeconds = 120;

        // Optional credentials. If NZBDav (or a reverse proxy in front of it) is
        // protected with HTTP Basic auth, set these and the proxy will send an
        // Authorization header upstream. Leave blank if NZBDav is open on the
        // internal network.
        NzbDavUsername = string.Empty;
        NzbDavPassword = string.Empty;

        // Optional comma-separated whitelist of path prefixes the proxy is allowed
        // to fetch (e.g. "content/,dav/"). When empty, any path beneath the base
        // URL is allowed. Path traversal ("..") is always rejected regardless.
        AllowedPathPrefixes = string.Empty;

        // When enabled, the proxy logs the upstream URL and status for every
        // request at Information level. Useful while setting things up; noisy for
        // day-to-day use.
        EnableVerboseLogging = false;
    }

    /// <summary>
    /// Gets or sets the base URL of the internal NZBDav instance
    /// (for example <c>http://nzbdav:3000</c>). No trailing slash required.
    /// </summary>
    public string NzbDavBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the upstream request timeout, in seconds, used when waiting
    /// for NZBDav to start responding. Does not limit streaming duration.
    /// </summary>
    public int UpstreamTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the optional HTTP Basic auth username sent to NZBDav.
    /// </summary>
    public string NzbDavUsername { get; set; }

    /// <summary>
    /// Gets or sets the optional HTTP Basic auth password sent to NZBDav.
    /// Stored in plain text in the plugin's configuration file (standard for
    /// Jellyfin plugins) - keep your config directory protected.
    /// </summary>
    public string NzbDavPassword { get; set; }

    /// <summary>
    /// Gets or sets an optional comma-separated whitelist of allowed path
    /// prefixes. When non-empty, requested paths must start with one of them.
    /// </summary>
    public string AllowedPathPrefixes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether per-request verbose logging is on.
    /// </summary>
    public bool EnableVerboseLogging { get; set; }
}
