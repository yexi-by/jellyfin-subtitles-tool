using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record Candidate(string Id, string Name, string Format, string[] Languages, int Score, bool Chinese);
public sealed record CandidateDownload(string MediaPath, Uri Url, string Format);

public sealed class ThunderSource(HttpClient httpClient) : IDisposable
{
    private readonly MemoryCache _candidates = new(new MemoryCacheOptions { SizeLimit = 4096 });
    public static readonly string[] Formats = ["srt", "ass", "ssa", "vtt"];

    public static bool IsChinese(IEnumerable<string> languages) => languages.Any(language =>
    {
        var value = language.Trim().ToLowerInvariant();
        return value is "zh" or "chi" or "zho" or "chs" or "cht" or "chinese" or "简体" or "繁体"
            || value.StartsWith("zh-", StringComparison.Ordinal) || value.StartsWith("zh_", StringComparison.Ordinal)
            || value.Contains('中');
    });

    public async Task<Candidate[]> SearchAsync(string mediaPath, string gcid, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await httpClient.GetAsync("https://api-shoulei-ssl.xunlei.com/oracle/subtitle?gcid=" + Uri.EscapeDataString(gcid), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new ToolException("字幕源暂时不可用，请稍后重试。", 502);
            var payload = await response.Content.ReadFromJsonAsync<ThunderResponse>(timeout.Token);
            if (payload is null || payload.Code != 0 || payload.Result != "ok") throw new ToolException("字幕源返回了异常结果，请重试。", 502);
            return payload.Data.Where(item => Formats.Contains(item.Ext.TrimStart('.').ToLowerInvariant()) && ValidUrl(item.Url))
                .GroupBy(item => item.Url, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.Score).ThenByDescending(item => item.FingerprintScore).First())
                .OrderByDescending(item => item.Score).ThenByDescending(item => item.FingerprintScore)
                .Select(item =>
                {
                    var id = Guid.NewGuid().ToString("N");
                    var format = item.Ext.TrimStart('.').ToLowerInvariant();
                    _candidates.Set(id, new CandidateDownload(mediaPath, new Uri(item.Url), format), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15), Size = 1 });
                    return new Candidate(id, item.Name, format, item.Languages, item.Score, IsChinese(item.Languages));
                }).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ToolException("连接字幕源超时，请重试。", 504); }
        catch (HttpRequestException) { throw new ToolException("无法连接字幕源，请检查服务器网络后重试。", 502); }
        catch (System.Text.Json.JsonException) { throw new ToolException("字幕源返回的数据无法识别，请稍后重试。", 502); }
    }

    public CandidateDownload Resolve(string id, string mediaPath)
    {
        if (!_candidates.TryGetValue<CandidateDownload>(id, out var candidate) || candidate is null || candidate.MediaPath != mediaPath)
            throw new ToolException("字幕候选已过期，请重新搜索。", 410);
        return candidate;
    }

    public async Task DownloadAsync(CandidateDownload candidate, Stream output, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await httpClient.GetAsync(candidate.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new ToolException("字幕下载失败，原有字幕未被修改，请重新搜索或重试。", 502);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += count;
                if (total > 20 * 1024 * 1024) throw new ToolException("字幕文件异常大，下载已停止。", 502);
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
            if (total == 0) throw new ToolException("下载到的字幕为空，原有字幕未被修改。", 502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ToolException("字幕下载超时，原有字幕未被修改。", 504); }
        catch (HttpRequestException) { throw new ToolException("字幕下载连接失败，原有字幕未被修改。", 502); }
    }

    private static bool ValidUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
    public void Dispose() { _candidates.Dispose(); httpClient.Dispose(); }

    private sealed class ThunderResponse
    {
        public int Code { get; set; }
        public string Result { get; set; } = "";
        public ThunderItem[] Data { get; set; } = [];
    }

    private sealed class ThunderItem
    {
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ext { get; set; } = "";
        public string[] Languages { get; set; } = [];
        public int Score { get; set; }
        [JsonPropertyName("fingerprintf_score")]
        public double FingerprintScore { get; set; }
    }
}
