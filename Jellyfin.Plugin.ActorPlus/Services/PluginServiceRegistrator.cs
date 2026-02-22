using Jellyfin.Plugin.ActorPlus.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ActorPlus;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<BirthDateCacheStore>();
        serviceCollection.AddSingleton<CountryCodeMapper>();
        serviceCollection.AddSingleton<TmdbPersonClient>();
        serviceCollection.AddSingleton<PersonAgeService>();
        serviceCollection.AddHostedService<IndexHtmlInjectorHostedService>();
    }
}
