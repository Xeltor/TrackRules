using Jellyfin.Plugin.TrackRules.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TrackRules;

/// <summary>
/// Registers plugin services with the host container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ILanguageNormalizer, LanguageNormalizer>();
        serviceCollection.AddSingleton<IRuleStore, RuleStore>();
        serviceCollection.AddSingleton<ITrackRuleResolver, TrackRuleResolver>();
        serviceCollection.AddSingleton<ITranscodeGuard, TranscodeGuard>();
        serviceCollection.AddHostedService<SessionHook>();
    }
}
