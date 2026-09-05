using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitlesTool;

public sealed class Configuration : BasePluginConfiguration;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<Configuration>(paths, serializer)
{
    public override string Name => "Subtitles Tool";
    public override Guid Id => Guid.Parse("c4b75732-8527-4f58-9cdf-18efca21a9e5");
    public override string Description => "在视频旁保存 CID、GCID，手动选择并下载外挂字幕。";
}

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<HashRecords>();
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Subtitles-Tool/0.1.0.0");
            return new ThunderSource(http);
        });
        services.AddTransient<IStartupFilter, WebIntegration>();
    }
}
