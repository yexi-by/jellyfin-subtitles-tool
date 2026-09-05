using Jellyfin.Plugin.SubtitlesTool.Core;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class IdleCalculationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subtitles-tool-idle-" + Guid.NewGuid().ToString("N"));
    public IdleCalculationTests() => Directory.CreateDirectory(_directory);
    private async Task<string> Video(string name)
    {
        var path = Path.Combine(_directory, name + ".mp4");
        await File.WriteAllBytesAsync(path, new byte[600000]);
        return path;
    }
    private static Task Progress(HashProgress _) => Task.CompletedTask;

    [Fact]
    public async Task BusyServerWaitsWithoutCreatingTemporaryFilesAndCanBeCanceled()
    {
        var path = await Video("等待");
        using var records = new HashRecords();
        using var cancellation = new CancellationTokenSource();
        var paused = new TaskCompletionSource();
        var task = records.GetOrCreateWhenIdleAsync(path, current => { if (current.Phase == "paused") paused.TrySetResult(); return Task.CompletedTask; }, () => false, cancellation.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(Directory.GetFiles(_directory));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await records.GetOrCreateAsync(path, Progress, default);
        Assert.NotNull(await HashRecords.ReadAsync(path, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedCalculationReleasesTheGateAndEitherResumesOrCleansUp(bool cancel)
    {
        var backgroundPath = await Video("后台");
        var foregroundPath = await Video("手动");
        using var records = new HashRecords();
        using var cancellation = new CancellationTokenSource();
        var idle = true;
        var starts = 0;
        var paused = new TaskCompletionSource();
        var task = records.GetOrCreateWhenIdleAsync(backgroundPath, current =>
        {
            if (current.Phase == "hashing" && current.Read == 0) { starts++; idle = false; }
            if (current.Phase == "paused") paused.TrySetResult();
            return Task.CompletedTask;
        }, () => idle, cancellation.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(HashRecords.RecordPath(backgroundPath)));
        await records.GetOrCreateAsync(foregroundPath, Progress, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(task.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(File.Exists(HashRecords.RecordPath(backgroundPath)));
        }
        else
        {
            idle = true;
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.Created);
            Assert.Equal(await HashRecords.CalculateAsync(backgroundPath, Progress, default), result.Pair);
            Assert.Equal(1, starts);
        }
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task InteractiveRequestPreemptsBackgroundWorkEvenWhileTheServerIsIdle()
    {
        var backgroundPath = await Video("后台");
        var foregroundPath = await Video("手动");
        using var records = new HashRecords();
        var foregroundStarted = new TaskCompletionSource();
        var finishForeground = new TaskCompletionSource();
        Task<HashPair>? foreground = null;
        var paused = false;
        var background = records.GetOrCreateWhenIdleAsync(backgroundPath, current =>
        {
            if (current.Phase == "hashing" && current.Read == 0)
                foreground = records.GetOrCreateAsync(foregroundPath, async update =>
                {
                    if (update.Phase == "hashing" && update.Read == 0) { foregroundStarted.TrySetResult(); await finishForeground.Task; }
                }, default);
            if (current.Phase == "paused") paused = true;
            return Task.CompletedTask;
        }, () => true, default);
        await foregroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(paused);
        Assert.False(background.IsCompleted);
        finishForeground.SetResult();
        await foreground!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await background.WaitAsync(TimeSpan.FromSeconds(3))).Created);
    }

    [Fact]
    public async Task RecordCreatedByAnInteractiveRequestDuringPauseIsReused()
    {
        var path = await Video("同一视频");
        using var records = new HashRecords();
        var idle = true;
        var paused = new TaskCompletionSource();
        var background = records.GetOrCreateWhenIdleAsync(path, current =>
        {
            if (current.Phase == "hashing" && current.Read == 0) idle = false;
            if (current.Phase == "paused") paused.TrySetResult();
            return Task.CompletedTask;
        }, () => idle, default);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var manual = await records.GetOrCreateAsync(path, Progress, default).WaitAsync(TimeSpan.FromSeconds(3));
        var modified = File.GetLastWriteTimeUtc(HashRecords.RecordPath(path));
        idle = true;
        var result = await background.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.Created);
        Assert.Equal(manual, result.Pair);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(HashRecords.RecordPath(path)));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
