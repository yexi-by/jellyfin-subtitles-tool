# Jellyfin Subtitles Tool

在视频旁保存 CID、GCID，搜索迅雷字幕，由你选择并下载外挂字幕。视频文件保持不变。

适配 Jellyfin **10.11.11** 的 Web 界面，已验证桌面、手机和平板尺寸的浏览器布局；官方 Jellyfin Android App 的 WebView 兼容性尚未实机验证。仅处理 Jellyfin 已入库的本地电影和剧集文件；需要视频目录可写以及服务器能够访问迅雷字幕接口。

## 安装

在 Jellyfin **控制台 → 插件 → 存储库** 添加：

```text
https://raw.githubusercontent.com/yexi-by/jellyfin-subtitles-tool/main/manifest.json
```

从插件目录安装 **Subtitles Tool**，重启 Jellyfin，重新加载网页或重启 Android App。管理字幕需要账号具有 Jellyfin 的字幕管理权限。

## 使用

1. 在电影或剧集详情页点击 **字幕**。有多个媒体版本时，先选择要处理的版本。
2. 面板自动搜索。首次搜索会读取视频并生成记录，显示实际计算进度；关闭面板可以取消。
3. 默认显示中文或双语，可切换 **全部** 查看其他语言及未标注语言的结果。
4. 选择候选并点击 **下载**。已有同名字幕时，确认替换后才覆盖。
5. 保存完成并刷新媒体信息后，在播放器字幕列表中选择使用。

支持 SRT、ASS、SSA、VTT，保留原格式、样式和编码。字幕源没有结果时不会按文件名回退，也不会自动下载候选。

中文筛选依据字幕源的语言信息和文件名中的明确标记（例如 `zh-CN`、`chs`、`中文`）；文件名推断会标为“中文（文件名）”。源站的语言、评分和匹配结果可能不准确，请自行核对候选名称。

## 文件约定

```text
电影.mkv
电影.mkv.cidgcid
电影.srt
```

记录为 UTF-8 文本，无 BOM，以换行结尾，只有两行：

```text
CID=E3908EE98B37C56925CB036554850D360AE2510D
GCID=DB1F8AB96B65E05022A6211DA4226D52F8E54B16
```

上述数值仅为测试向量。存在有效记录就直接使用；损坏记录会报错并保留。若自行用另一个视频替换同名文件，应自行删除对应记录，让下一次搜索生成新值。

不转换容器、不重编码、不内封字幕、不改写视频元数据，没有入库后台任务和自动下载。Web 面板通过服务器响应加载，插件不会改写磁盘上的 Jellyfin Web 文件。

旧项目已经退役，新插件不会读取旧 MKV 标签或旧插件缓存。旧数据的导出和标签清理属于独立维护操作。

## 开发和发布

需要 .NET 9 SDK、Node 24 LTS、pnpm 12。工具和依赖版本由仓库配置及锁文件固定。

```powershell
cd webui
pnpm install --frozen-lockfile
pnpm check
pnpm test
pnpm build
cd ..
dotnet test Jellyfin.SubtitlesTool.sln
pwsh -File scripts/package.ps1
```

推送与项目版本一致的 `v0.1.0.0` 形式标签，GitHub Actions 会验证、打包、发布 Release 并更新插件清单。发行包只包含插件 DLL、依赖说明和许可证。

项目采用 GPL-3.0。CID、GCID 算法与其已知测试向量来自同作者的已归档项目，并保留 GPL-3.0 授权。
