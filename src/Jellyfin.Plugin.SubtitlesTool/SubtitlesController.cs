using System.Text.Json;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitlesTool;

[ApiController]
[Route("SubtitlesTool/Items/{itemId:guid}")]
[Authorize(Policy = Policies.SubtitleManagement)]
public sealed class SubtitlesController(ILibraryManager library, IMediaSourceManager sources, IFileSystem fileSystem, HashRecords hashes, ThunderSource thunder, ILogger<SubtitlesController> logger, IAuthorizationContext authorization) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public sealed record SearchRequest(string? MediaSourceId);
    public sealed record DownloadRequest(string? MediaSourceId, string CandidateId, bool Overwrite = false);
    private sealed record Target(Video Item, string Path, string SourceId);

    [HttpGet]
    public async Task<ActionResult> Info(Guid itemId, [FromQuery] string? mediaSourceId)
    {
        try
        {
            var target = await ResolveAsync(itemId, mediaSourceId);
            return Ok(new { fileName = Path.GetFileName(target.Path), mediaSourceId = target.SourceId, hasRecord = System.IO.File.Exists(HashRecords.RecordPath(target.Path)), subtitles = ExistingSubtitles(target.Path) });
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("search")]
    public async Task Search(Guid itemId, [FromBody] SearchRequest body, CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveAsync(itemId, body.MediaSourceId);
            Response.ContentType = "application/x-ndjson; charset=utf-8";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            var pair = await hashes.GetOrCreateAsync(target.Path, progress => Emit(new { type = "progress", progress.Phase, progress.Read, progress.Total }, cancellationToken), cancellationToken);
            await Emit(new { type = "progress", phase = "searching" }, cancellationToken);
            var candidates = await thunder.SearchAsync(target.Path, pair.Gcid, cancellationToken);
            await Emit(new { type = "results", candidates }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (IsUserError(ex))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Response.ContentType = "application/x-ndjson; charset=utf-8";
                await Emit(new { type = "error", message = Describe(ex) }, cancellationToken);
            }
        }
    }

    [HttpPost("download")]
    public async Task<ActionResult> Download(Guid itemId, [FromBody] DownloadRequest body, CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveAsync(itemId, body.MediaSourceId);
            var candidate = thunder.Resolve(body.CandidateId, target.Path);
            var subtitlePath = await SidecarWriter.SaveAsync(target.Path, candidate.Format, body.Overwrite, (output, token) => thunder.DownloadAsync(candidate, output, token), cancellationToken);
            // 文件提交后即使客户端关闭，也完成 Jellyfin 的媒体刷新。
            try
            {
                await target.Item.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ImageRefreshMode = MetadataRefreshMode.None,
                    ForceSave = true,
                    RegenerateTrickplay = false
                }, CancellationToken.None);
                var visible = sources.GetStaticMediaSources(target.Item, false).SelectMany(source => source.MediaStreams)
                    .Any(stream => stream.Type == MediaStreamType.Subtitle && stream.IsExternal && string.Equals(stream.Path, subtitlePath, StringComparison.Ordinal));
                if (!visible) return Ok(new { fileName = Path.GetFileName(subtitlePath), refreshed = false, message = "字幕已保存，Jellyfin 尚未更新字幕列表，请稍后刷新媒体信息。" });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "外挂字幕已保存，但媒体 {ItemId} 的刷新失败", itemId);
                return Ok(new { fileName = Path.GetFileName(subtitlePath), refreshed = false, message = "字幕已保存，但 Jellyfin 刷新失败，请刷新媒体信息。" });
            }
            return Ok(new { fileName = Path.GetFileName(subtitlePath), refreshed = true, message = "字幕已保存，可以在播放器中选择。" });
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    private async Task<Target> ResolveAsync(Guid itemId, string? sourceId)
    {
        var auth = await authorization.GetAuthorizationInfo(HttpContext);
        if (!auth.IsAuthenticated || (!auth.IsApiKey && auth.User is null))
            throw new ToolException("登录状态无效，请重新登录。", 401);
        var video = library.GetItemById<Video>(itemId, auth.User);
        if (video is not Movie && video is not Episode) throw new ToolException("此条目不是可处理的本地电影或剧集。", 404);
        var list = sources.GetStaticMediaSources(video, false);
        var source = string.IsNullOrEmpty(sourceId) ? list.FirstOrDefault() : list.FirstOrDefault(item => item.Id == sourceId);
        if (source is null || string.IsNullOrWhiteSpace(source.Path) || !System.IO.File.Exists(source.Path) || source.Protocol != MediaProtocol.File || Path.GetExtension(source.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("此媒体源不是可读取的本地视频文件。", 404);
        var item = Guid.TryParse(source.Id, out var sourceGuid) ? library.GetItemById<Video>(sourceGuid, auth.User) ?? video : video;
        return new Target(item, source.Path, source.Id);
    }

    private static string[] ExistingSubtitles(string path) => ThunderSource.Formats.Select(format => Path.ChangeExtension(path, format)).Where(System.IO.File.Exists).Select(file => Path.GetFileName(file)).ToArray();
    private async Task Emit<T>(T value, CancellationToken token)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value, JsonOptions) + "\n", token);
        await Response.Body.FlushAsync(token);
    }
    private static bool IsUserError(Exception ex) => ex is ToolException or IOException or UnauthorizedAccessException;
    private static string Describe(Exception ex) => ex switch
    {
        ToolException => ex.Message,
        UnauthorizedAccessException => "无法读取视频或写入同目录文件，请检查 Jellyfin 的目录权限。",
        _ => "文件操作失败，请检查文件是否存在、磁盘空间和目录权限后重试。"
    };
    private ObjectResult ErrorResult(Exception ex) => StatusCode(ex is ToolException error ? error.Status : 500, new { message = Describe(ex) });
}
