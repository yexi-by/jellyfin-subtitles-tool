namespace Jellyfin.Plugin.SubtitlesTool.Core;

public static class SidecarWriter
{
    public static async Task<string> SaveAsync(string mediaPath, string format, bool overwrite, Func<Stream, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        if (!ThunderSource.Formats.Contains(format)) throw new ToolException("不支持此字幕格式。");
        var target = Path.ChangeExtension(mediaPath, format);
        if (!overwrite && File.Exists(target)) throw new ToolException($"同名字幕 {Path.GetFileName(target)} 已存在。替换吗？", 409);
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await download(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temp, target, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(target)) { throw new ToolException($"同名字幕 {Path.GetFileName(target)} 已存在。替换吗？", 409); }
            return target;
        }
        finally { File.Delete(temp); }
    }
}
