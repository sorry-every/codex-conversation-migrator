$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $projectRoot 'src\CodexConversationMigrator.cs'
$sqlite = Join-Path $projectRoot 'third-party\sqlite3.dll'
$dist = Join-Path $projectRoot 'dist'
$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw '未找到 .NET Framework csc.exe。请安装 .NET Framework 4.x Developer Pack。' }
if (-not (Test-Path -LiteralPath $source)) { throw "找不到源文件：$source" }
if (-not (Test-Path -LiteralPath $sqlite)) { throw "找不到 SQLite 动态库：$sqlite" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$output = Join-Path $dist 'Codex会话选择迁移工具-v3.4.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$output" "/resource:$sqlite,sqlite3.dll" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll $source
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码：$LASTEXITCODE" }
Write-Host "构建完成：$output"
