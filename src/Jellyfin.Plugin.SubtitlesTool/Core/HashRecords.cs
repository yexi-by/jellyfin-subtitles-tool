using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed class ToolException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record HashPair(string Cid, string Gcid)
{
    public string Serialize() => $"CID={Cid}\nGCID={Gcid}\n";
}

public sealed record HashProgress(string Phase, long Read = 0, long Total = 0);

/// <summary>记录文件是唯一持久化身份来源；不会检查或修改视频容器。</summary>
public sealed class HashRecords : IDisposable
{
    private readonly SemaphoreSlim _calculation = new(1, 1);
    private static readonly Regex RecordPattern = new("\\ACID=([0-9A-F]{40})\\r?\\nGCID=([0-9A-F]{40})\\r?\\n\\z", RegexOptions.CultureInvariant);

    public static string RecordPath(string mediaPath) => mediaPath + ".cidgcid";

    public static async Task<HashPair?> ReadAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var path = RecordPath(mediaPath);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256) throw InvalidRecord();
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var match = RecordPattern.Match(text);
        if (!match.Success) throw InvalidRecord();
        return new HashPair(match.Groups[1].Value, match.Groups[2].Value);
    }

    private static ToolException InvalidRecord() => new("CID、GCID 记录文件格式不正确，请检查同名 .cidgcid 文件。现有记录未被覆盖。");

    public async Task<HashPair> GetOrCreateAsync(string mediaPath, Func<HashProgress, Task> progress, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(mediaPath, cancellationToken);
        if (existing is not null) return existing;
        await progress(new HashProgress("waiting"));
        await _calculation.WaitAsync(cancellationToken);
        try
        {
            existing = await ReadAsync(mediaPath, cancellationToken);
            if (existing is not null) return existing;
            var temp = RecordPath(mediaPath) + $".{Guid.NewGuid():N}.tmp";
            try
            {
                HashPair pair;
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    pair = await CalculateAsync(mediaPath, progress, cancellationToken);
                    await output.WriteAsync(Encoding.UTF8.GetBytes(pair.Serialize()), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, RecordPath(mediaPath));
                return pair;
            }
            finally { File.Delete(temp); }
        }
        finally { _calculation.Release(); }
    }

    public static async Task<HashPair> CalculateAsync(string mediaPath, Func<HashProgress, Task> progress, CancellationToken cancellationToken)
    {
        var before = new FileInfo(mediaPath);
        var size = before.Length;
        var modified = before.LastWriteTimeUtc;
        await using var input = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        // SHA-1 是迅雷协议的组成部分，不用于安全校验。
        using var cidHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        if (size < 0xF000)
        {
            var small = new byte[(int)size];
            await input.ReadExactlyAsync(small, cancellationToken);
            cidHash.AppendData(small);
        }
        else
        {
            var segment = new byte[0x5000];
            foreach (var offset in new[] { 0, size / 3, size - segment.Length })
            {
                input.Position = offset;
                await input.ReadExactlyAsync(segment, cancellationToken);
                cidHash.AppendData(segment);
            }
        }
        var cid = Convert.ToHexString(cidHash.GetHashAndReset());
        input.Position = 0;
        var pieceSize = 0x40000;
        while (size / pieceSize > 0x200 && pieceSize < 0x200000) pieceSize <<= 1;
        var piece = new byte[pieceSize];
        using var gcidHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long read = 0;
        var reportClock = Stopwatch.StartNew();
        await progress(new HashProgress("hashing", 0, size));
        while (true)
        {
            var count = await input.ReadAtLeastAsync(piece, piece.Length, false, cancellationToken);
            if (count == 0) break;
            gcidHash.AppendData(SHA1.HashData(piece.AsSpan(0, count)));
            read += count;
            if (reportClock.ElapsedMilliseconds >= 200 || read >= size)
            {
                await progress(new HashProgress("hashing", Math.Min(read, size), size));
                reportClock.Restart();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var after = new FileInfo(mediaPath);
        if (!after.Exists || after.Length != size || after.LastWriteTimeUtc != modified || read != size)
            throw new ToolException("计算期间视频文件发生变化，请等待文件写入完成后重试。");
        return new HashPair(cid, Convert.ToHexString(gcidHash.GetHashAndReset()));
    }

    public void Dispose() => _calculation.Dispose();
}
