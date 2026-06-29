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
        // headers before giving up. NZBDav can take a moment to mount/prepare a
        // file from usenet, so keep this generous.
        UpstreamTimeoutSeconds = 120;
    }

    /// <summary>
    /// Gets or sets the base URL of the internal NZBDav instance
    /// (for example <c>http://nzbdav:3000</c>). No trailing slash required.
    /// </summary>
    public string NzbDavBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the upstream request timeout, in seconds, used when waiting
    /// for NZBDav to start responding.
    /// </summary>
    public int UpstreamTimeoutSeconds { get; set; }
}
