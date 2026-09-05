$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src/Jellyfin.Plugin.SubtitlesTool/Jellyfin.Plugin.SubtitlesTool.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw '插件版本格式无效' }
dotnet build $projectFile -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw '插件构建失败' }
$artifactDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$archivePath = Join-Path $artifactDirectory "Jellyfin.Plugin.SubtitlesTool_$version.zip"
$outputDirectory = Join-Path $projectRoot 'src/Jellyfin.Plugin.SubtitlesTool/bin/Release/net9.0'
$files = @((Join-Path $outputDirectory 'Jellyfin.Plugin.SubtitlesTool.dll'), (Join-Path $outputDirectory 'Jellyfin.Plugin.SubtitlesTool.deps.json'), (Join-Path $projectRoot 'LICENSE'))
Compress-Archive -LiteralPath $files -DestinationPath $archivePath -Force
$entry = @{
    guid = 'c4b75732-8527-4f58-9cdf-18efca21a9e5'
    name = 'Subtitles Tool'
    description = '在视频旁保存 CID、GCID，手动选择并下载外挂字幕。'
    overview = '详情页字幕面板，支持中文筛选与同名外挂字幕保存，视频文件保持不变。'
    owner = 'yexi-by'
    category = 'Metadata'
    versions = @(@{
        version = $version
        changelog = '提供 CID、GCID 记录、手动字幕搜索与外挂保存。'
        targetAbi = '10.11.11.0'
        sourceUrl = "https://github.com/yexi-by/jellyfin-subtitles-tool/releases/download/v$version/Jellyfin.Plugin.SubtitlesTool_$version.zip"
        checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm MD5).Hash.ToUpperInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    })
}
$manifest = ConvertTo-Json -InputObject @($entry) -Depth 8
[IO.File]::WriteAllText((Join-Path $artifactDirectory 'manifest.json'), $manifest + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "安装包：$archivePath"
