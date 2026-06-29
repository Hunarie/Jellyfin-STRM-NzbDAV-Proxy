using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.NzbDavProxy.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NzbDavProxy;

/// <summary>
/// The NZBDav Proxy plugin. Registers the configuration page and exposes a
/// singleton instance so the API controller can read the configured NZBDav URL.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance so non-DI components (such as the
    /// controller) can read configuration without constructor plumbing.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "NZBDav Proxy";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("19d56267-475a-47a4-b9f3-857d07630198");

    /// <inheritdoc />
    public override string Description =>
        "Proxies .strm media stored on an internal NZBDav instance through Jellyfin, " +
        "so remote players such as Infuse can stream and seek without exposing NZBDav " +
        "to the public internet.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
