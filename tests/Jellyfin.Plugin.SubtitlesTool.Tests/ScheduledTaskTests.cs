using System.Reflection;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class ScheduledTaskTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subtitles-tool-task-" + Guid.NewGuid().ToString("N"));
    private readonly IMediaSourceManager _originalSources = BaseItem.MediaSourceManager;
    public ScheduledTaskTests()
    {
        Directory.CreateDirectory(_directory);
        BaseItem.MediaSourceManager = Stub<IMediaSourceManager>((method, args) => method.Name == nameof(IMediaSourceManager.GetPathProtocol)
            ? ((string)args[0]!).StartsWith("https://", StringComparison.Ordinal) ? MediaProtocol.Http : MediaProtocol.File
            : throw new NotSupportedException(method.Name));
    }
    private async Task<Movie> Video(string name)
    {
        var path = Path.Combine(_directory, name + ".mp4");
        await File.WriteAllBytesAsync(path, new byte[600000]);
        return new Movie { Id = Guid.NewGuid(), Path = path };
    }

    [Fact]
    public async Task TaskGeneratesMissingRecordsReportsInvalidOnesAndKeepsTriggersEmpty()
    {
        var fresh = await Video("缺失");
        var existing = await Video("已存在");
        var broken = await Video("损坏");
        var duplicate = new Movie { Id = Guid.NewGuid(), Path = fresh.Path };
        var remote = new Movie { Id = Guid.NewGuid(), Path = "https://example.com/movie.mp4" };
        var shortcut = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(_directory, "远程.strm") };
        await File.WriteAllTextAsync(shortcut.Path, "https://example.com/movie.mp4");
        var pair = new HashPair(new string('A', 40), new string('B', 40));
        await File.WriteAllTextAsync(HashRecords.RecordPath(existing.Path), pair.Serialize());
        await File.WriteAllTextAsync(HashRecords.RecordPath(broken.Path), "需要修复的记录");
        var items = new[] { fresh, existing, broken, duplicate, remote, shortcut };
        using var hashes = new HashRecords();
        var task = new GenerateHashRecordsTask(Library(items), Sessions([]), hashes, NullLogger<GenerateHashRecordsTask>.Instance);
        Assert.Empty(task.GetDefaultTriggers());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(_ => { }), default));
        Assert.Contains("已生成 1 个，复用 1 个，跳过 3 个，失败 1 个", error.Message);
        Assert.NotNull(await HashRecords.ReadAsync(fresh.Path, default));
        Assert.Equal(pair, await HashRecords.ReadAsync(existing.Path, default));
        Assert.Equal("需要修复的记录", await File.ReadAllTextAsync(HashRecords.RecordPath(broken.Path)));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ActivePlaybackWaitsAndPausedPlaybackAllowsGeneration()
    {
        var video = await Video("播放状态");
        var session = new SessionInfo(null!, NullLogger.Instance) { NowPlayingItem = new BaseItemDto() };
        using var hashes = new HashRecords();
        var task = new GenerateHashRecordsTask(Library([video]), Sessions([session]), hashes, NullLogger<GenerateHashRecordsTask>.Instance);
        using var cancellation = new CancellationTokenSource();
        var running = task.ExecuteAsync(new InlineProgress(_ => { }), cancellation.Token);
        Assert.False(running.IsCompleted);
        Assert.False(File.Exists(HashRecords.RecordPath(video.Path)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        session.PlayState.IsPaused = true;
        double final = 0;
        await task.ExecuteAsync(new InlineProgress(value => final = value), default);
        Assert.Equal(100, final);
        Assert.NotNull(await HashRecords.ReadAsync(video.Path, default));
        await session.DisposeAsync();
    }

    private static ILibraryManager Library(IReadOnlyList<Movie> items) => Stub<ILibraryManager>((method, args) => method.Name switch
    {
        nameof(ILibraryManager.GetItemIds) => items.Select(item => item.Id).ToArray(),
        nameof(ILibraryManager.GetItemList) => items.Where(item => ((InternalItemsQuery)args[0]!).ItemIds.Contains(item.Id)).Cast<BaseItem>().ToArray(),
        _ => throw new NotSupportedException(method.Name)
    });
    private static ISessionManager Sessions(IReadOnlyList<SessionInfo> sessions) => Stub<ISessionManager>((method, _) => method.Name == "get_Sessions" ? sessions : throw new NotSupportedException(method.Name));
    private static T Stub<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)result).InvokeMethod = invoke;
        return result;
    }
    public class InterfaceStub : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> InvokeMethod { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args!);
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
    public void Dispose()
    {
        BaseItem.MediaSourceManager = _originalSources;
        Directory.Delete(_directory, true);
    }
}
