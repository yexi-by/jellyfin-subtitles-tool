using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitlesTool;

public sealed class GenerateHashRecordsTask(ILibraryManager library, ISessionManager sessions, HashRecords hashes, ILogger<GenerateHashRecordsTask> logger) : IScheduledTask
{
    public string Name => "生成 CID、GCID 记录";
    public string Key => "SubtitlesToolGenerateHashRecords";
    public string Category => "Subtitles Tool";
    public string Description => "在 Jellyfin 空闲时为本地电影和剧集生成 .cidgcid 文件，有效记录直接复用。播放期间暂停，空闲后继续；可随时停止任务。";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    private bool IsIdle() => !sessions.Sessions.Any(session => session.IsActive && session.NowPlayingItem is not null && !session.PlayState.IsPaused);

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(0);
        // 先固定条目 ID，再分批读取路径，避免长任务期间库变化导致分页遗漏。
        var ids = library.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            IsFolder = false,
            GroupByPresentationUniqueKey = false,
            SourceTypes = [SourceType.Library],
            Recursive = true
        });
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var generated = 0; var reused = 0; var skipped = 0; var failed = 0;
        logger.LogInformation("开始检查 {Count} 个电影和剧集条目的 CID、GCID 记录", ids.Count);

        try
        {
            const int batchSize = 256;
            for (var offset = 0; offset < ids.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchIds = ids.Skip(offset).Take(batchSize).ToArray();
                var items = library.GetItemList(new InternalItemsQuery
                {
                    ItemIds = batchIds,
                    GroupByPresentationUniqueKey = false,
                    DtoOptions = new DtoOptions(false) { Fields = [ItemFields.Path], EnableImages = false, EnableUserData = false }
                });
                skipped += batchIds.Length - items.Count;
                var completed = offset;
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is not Video video || (video is not Movie && video is not Episode)
                        || !video.IsFileProtocol || video.IsShortcut || video.VideoType != VideoType.VideoFile
                        || string.IsNullOrWhiteSpace(video.Path)
                        || Path.GetExtension(video.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++; completed++; continue;
                    }

                    try
                    {
                        var link = File.ResolveLinkTarget(video.Path, returnFinalTarget: true);
                        var mediaPath = link is { Exists: true } ? link.FullName : video.Path;
                        if (!seen.Add(mediaPath)) { skipped++; completed++; continue; }
                        var paused = false;
                        var result = await hashes.GetOrCreateWhenIdleAsync(mediaPath, current =>
                        {
                            if (current.Phase == "paused" && !paused)
                            {
                                paused = true;
                                logger.LogInformation("等待空闲后继续检查 CID、GCID 记录：{Path}", video.Path);
                            }
                            else if (current.Phase == "hashing" && paused)
                            {
                                paused = false;
                                logger.LogInformation("继续生成记录：{Path}", video.Path);
                            }
                            var fraction = current.Total > 0 ? Math.Clamp((double)current.Read / current.Total, 0, 0.999) : 0;
                            progress.Report((completed + fraction) * 100 / ids.Count);
                            return Task.CompletedTask;
                        }, IsIdle, cancellationToken);
                        if (result.Created) generated++;
                        else reused++;
                    }
                    catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        logger.LogWarning(ex, "生成 CID、GCID 记录失败：{Path}。请检查记录格式、文件状态和目录权限", video.Path);
                    }
                    completed++;
                    progress.Report(completed * 100.0 / ids.Count);
                }
                progress.Report((offset + batchIds.Length) * 100.0 / ids.Count);
            }
            if (failed > 0) throw new InvalidOperationException($"记录检查完成：已生成 {generated} 个，复用 {reused} 个，跳过 {skipped} 个，失败 {failed} 个。请在 Jellyfin 日志中查看失败文件。");
            progress.Report(100);
        }
        finally
        {
            logger.LogInformation("CID、GCID 任务结束：已生成 {Generated} 个，复用 {Reused} 个，跳过 {Skipped} 个，失败 {Failed} 个", generated, reused, skipped, failed);
        }
    }
}
