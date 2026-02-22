using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ActorPlus.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ActorPlus;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "Actor Plus";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => PluginName;

    // IMPORTANT: keep stable; this is used by the dashboard config page.
    public override Guid Id => Guid.Parse("d47f75a1-2c4c-4aa1-a1cd-5da6e5e6ccfd");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "actorplusconfig",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
