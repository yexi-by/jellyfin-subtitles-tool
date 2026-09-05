using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitlesTool.Core;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class FileWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subtitles-tool-tests-" + Guid.NewGuid().ToString("N"));
    public FileWorkflowTests() => Directory.CreateDirectory(_directory);
    private string Video(string name = "电影.mkv") => Path.Combine(_directory, name);
    private static Task Progress(HashProgress _) => Task.CompletedTask;

    [Theory]
    [InlineData(false, "E3908EE98B37C56925CB036554850D360AE2510D", "DB1F8AB96B65E05022A6211DA4226D52F8E54B16")]
    [InlineData(true, "587529BF8377CDAE7A469165BD6C730E1EE49848", "4C656359008640B38F105C554773B18B2D3A13D9")]
    public async Task KnownHashVectors(bool large, string cid, string gcid)
    {
        var data = large ? Enumerable.Range(0, 120000).Select(index => (byte)(index * 37 % 256)).ToArray() : Encoding.UTF8.GetBytes("hello-subtitles-tools");
        await File.WriteAllBytesAsync(Video(), data);
        Assert.Equal(new HashPair(cid, gcid), await HashRecords.CalculateAsync(Video(), Progress, default));
    }

    [Fact]
    public async Task RecordsAreAtomicAndReusedWithoutChangingVideo()
    {
        await File.WriteAllBytesAsync(Video(), new byte[600000]);
        var modified = File.GetLastWriteTimeUtc(Video());
        var digest = SHA256.HashData(await File.ReadAllBytesAsync(Video()));
        using var records = new HashRecords();
        var first = await records.GetOrCreateAsync(Video(), Progress, default);
        Assert.Equal(first.Serialize(), await File.ReadAllTextAsync(Video() + ".cidgcid"));
        var reused = await records.GetOrCreateAsync(Video(), _ => throw new Exception("不应重新计算"), default);
        Assert.Equal(first, reused);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Video()));
        Assert.Equal(digest, SHA256.HashData(await File.ReadAllBytesAsync(Video())));
        Assert.DoesNotContain(Directory.GetFiles(_directory), path => path.EndsWith(".tmp"));
        Assert.NotEqual(HashRecords.RecordPath(Video()), HashRecords.RecordPath(Video("电影.mp4")));
    }

    [Fact]
    public async Task InvalidRecordIsNotOverwritten()
    {
        await File.WriteAllTextAsync(Video(), "video");
        await File.WriteAllTextAsync(Video() + ".cidgcid", "broken");
        using var records = new HashRecords();
        await Assert.ThrowsAsync<ToolException>(() => records.GetOrCreateAsync(Video(), Progress, default));
        Assert.Equal("broken", await File.ReadAllTextAsync(Video() + ".cidgcid"));
    }

    [Fact]
    public async Task CancellationAndChangingInputLeaveNoRecord()
    {
        await File.WriteAllBytesAsync(Video(), new byte[1000000]);
        using var records = new HashRecords();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => records.GetOrCreateAsync(Video(), progress => { if (progress.Phase == "hashing") cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.False(File.Exists(Video() + ".cidgcid"));
        var changed = false;
        await Assert.ThrowsAsync<ToolException>(() => records.GetOrCreateAsync(Video(), async progress =>
        {
            if (progress.Phase == "hashing" && !changed) { changed = true; await File.AppendAllTextAsync(Video(), "changed"); }
        }, default));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ConcurrentRequestsReuseTheCompletedRecord()
    {
        await File.WriteAllBytesAsync(Video(), new byte[500000]);
        using var records = new HashRecords();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var first = records.GetOrCreateAsync(Video(), async progress => { if (progress.Phase == "hashing" && progress.Read == 0) { entered.SetResult(); await release.Task; } }, default);
        await entered.Task;
        var second = records.GetOrCreateAsync(Video(), progress => { Assert.NotEqual("hashing", progress.Phase); return Task.CompletedTask; }, default);
        release.SetResult();
        Assert.Equal(await first, await second);
    }

    [Theory]
    [InlineData("srt")][InlineData("ass")][InlineData("ssa")][InlineData("vtt")]
    public async Task ReplacementRequiresConfirmationAndPreservesBytes(string format)
    {
        await File.WriteAllTextAsync(Video(), "untouched video");
        var modified = File.GetLastWriteTimeUtc(Video());
        var target = Path.ChangeExtension(Video(), format);
        await File.WriteAllTextAsync(target, "old subtitle");
        var bytes = Encoding.Unicode.GetBytes("新字幕，保留原编码和格式");
        Task Write(Stream stream, CancellationToken token) => stream.WriteAsync(bytes, token).AsTask();
        var error = await Assert.ThrowsAsync<ToolException>(() => SidecarWriter.SaveAsync(Video(), format, false, Write, default));
        Assert.Equal(409, error.Status);
        await Assert.ThrowsAsync<IOException>(() => SidecarWriter.SaveAsync(Video(), format, true, async (stream, token) => { await Write(stream, token); throw new IOException("下载中断"); }, default));
        Assert.Equal("old subtitle", await File.ReadAllTextAsync(target));
        await SidecarWriter.SaveAsync(Video(), format, true, Write, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal("untouched video", await File.ReadAllTextAsync(Video()));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Video()));
        Assert.DoesNotContain(Directory.GetFiles(_directory), path => path.EndsWith(".tmp"));
    }

    [Fact]
    public async Task SearchUsesOnlyGcidAndBindsCandidatesToMedia()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("?gcid=" + new string('A', 40), request.RequestUri!.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"result\":\"ok\",\"data\":[{\"name\":\"测试字幕\",\"url\":\"https://example.com/sub.srt\",\"ext\":\"srt\",\"languages\":[\"zh\",\"en\"],\"score\":10}]}") };
        }));
        using var source = new ThunderSource(client);
        var candidate = Assert.Single(await source.SearchAsync(Video(), new string('A', 40), default));
        Assert.True(candidate.Chinese);
        Assert.Equal("srt", source.Resolve(candidate.Id, Video()).Format);
        Assert.Throws<ToolException>(() => source.Resolve(candidate.Id, Video("other.mp4")));
        Assert.Throws<ToolException>(() => source.Resolve("expired", Video()));
    }

    [Theory]
    [InlineData("movie.en-zh-CN.srt", true)]
    [InlineData("movie.chs3.ass", true)]
    [InlineData("movie[中文简体].srt", true)]
    [InlineData("movie.cht.srt", true)]
    [InlineData("电影.en.srt", false)]
    [InlineData("Chinatown.srt", false)]
    public async Task MissingLanguageUsesOnlyExplicitFilenameMarkers(string name, bool chinese)
    {
        var payload = JsonSerializer.Serialize(new { code = 0, result = "ok", data = new[] { new { name, url = "https://example.com/sub.srt", ext = "srt", languages = Array.Empty<string>() } } });
        using var source = new ThunderSource(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })));
        var candidate = Assert.Single(await source.SearchAsync(Video(), new string('A', 40), default));
        Assert.Equal(chinese, candidate.Chinese);
        Assert.Equal(chinese ? ["中文（文件名）"] : Array.Empty<string>(), candidate.Languages);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
