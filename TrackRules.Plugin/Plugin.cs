using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.TrackRules.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TrackRules;

/// <summary>
/// The main Track Rules plugin class.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Track Rules";

    /// <inheritdoc />
    public override Guid Id { get; } = Guid.Parse("f4903c07-0d28-4183-9960-f870d61d07a3");

    /// <inheritdoc />
    public override string Description => "Enforce per-user audio and subtitle defaults with series/library/global scopes.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "TrackRulesConfig",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Ui.dashboard.html",
                    GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "trackrules-series-widget.js",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Ui.series-widget.js",
                    GetType().Namespace)
            }
        ];
    }
}
