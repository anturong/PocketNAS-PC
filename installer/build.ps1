<#
.SYNOPSIS
    打包 PocketNAS PC 端安装包（Inno Setup）。

.DESCRIPTION
    流程：构建自包含产物 → 汇总到带时间戳的新暂存目录 → ISCC 编译 → 产出 dist\PocketNAS-PC-Setup-<版本>.exe

    刻意不做的事：
      - 不删除任何已有目录，每次用新的 stage-<时间戳>，失败也不会把上一次的产物弄没
      - 不覆盖 dist 里旧的 Setup exe，Inno 会自己按版本号命名

.NOTES
    Inno Setup 安装在用户目录时，ISCC 路径如下；装在 Program Files 里请改 $IsccCandidates。
#>

[CmdletBinding()]
param(
    [string]$Version = "1.0.5",
    [string]$Configuration = "Release",
    # 手机端 APK：默认自动取 OneDrive 成品目录里版本号最高那个，
    # 别再写死具体版本——上一个 1.17 写死导致出 1.0.1 时差点塞旧包
    [string]$AndroidApk = ""
)

# 未显式指定 APK 时，按版本号从高到低挑最新的 PocketNAS-*.apk
if (-not $AndroidApk) {
    $apkDir = "C:\Users\41456\OneDrive\我编写的常用工具"
    $latest = Get-ChildItem -Path $apkDir -Filter "PocketNAS-*.apk" -ErrorAction SilentlyContinue |
        Sort-Object { try { [version]($_.BaseName -replace '^PocketNAS-', '') } catch { [version]"0.0" } } -Descending |
        Select-Object -First 1
    if ($latest) { $AndroidApk = $latest.FullName }
}

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Find-Iscc {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    throw "没找到 ISCC.exe，先装 Inno Setup 6（https://jrsoftware.org/isdl.php）"
}

$iscc = Find-Iscc
Write-Host "ISCC: $iscc" -ForegroundColor Cyan

# 1. 构建。RID 需要先还原，否则 FtpsService 的 assets 里没有 net8.0/win-x64 目标
Push-Location $repo
try {
    Write-Host "== restore ==" -ForegroundColor Cyan
    dotnet restore src\PocketNAS.PC\PocketNAS.PC.csproj -r win-x64 | Out-Null
    dotnet restore src\PocketNAS.FtpsService\PocketNAS.FtpsService.csproj -r win-x64 | Out-Null

    Write-Host "== build ==" -ForegroundColor Cyan
    dotnet build src\PocketNAS.PC\PocketNAS.PC.csproj -c $Configuration -r win-x64 `
        --self-contained true --no-restore -v m
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }

    # 2. 暂存目录（新目录，不删旧的）
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stage = Join-Path $repo "dist\stage-$stamp"
    $app = Join-Path $stage "app"
    New-Item -ItemType Directory -Path $app -Force | Out-Null

    $built = Join-Path $repo "src\PocketNAS.PC\bin\$Configuration\net8.0-windows\win-x64"
    Write-Host "== stage ==" -ForegroundColor Cyan
    Copy-Item -Path "$built\*" -Destination $app -Recurse -Force

    # 嵌套 Publish 目标在 PublishDir 被外部覆盖时不会带 Content，web 页面要单独补
    $webSrc = Join-Path $repo "easy_webdav\src\WebDAVService\web"
    $webDst = Join-Path $app "service\web"
    New-Item -ItemType Directory -Path $webDst -Force | Out-Null
    Copy-Item -Path "$webSrc\*" -Destination $webDst -Force

    # 安卓端一起带上，装完就能扫码下载手机版
    if (Test-Path $AndroidApk) {
        New-Item -ItemType Directory -Path (Join-Path $app "android") -Force | Out-Null
        Copy-Item $AndroidApk (Join-Path $app "android") -Force
    } else {
        Write-Warning "没找到手机端 APK：$AndroidApk（安装包里将不含 android 目录）"
    }

    $files = (Get-ChildItem $app -Recurse -File).Count
    $size = [math]::Round((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "暂存：$app （$files 个文件，$size MB）" -ForegroundColor Green

    # 3. 编译安装包
    Write-Host "== iscc ==" -ForegroundColor Cyan
    & $iscc "/DStageApp=$app" "/DMyAppVersion=$Version" "installer\PocketNAS.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }

    $out = Join-Path $repo "dist\PocketNAS-PC-Setup-$Version.exe"
    if (Test-Path $out) {
        $mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
        Write-Host "`n产出：$out （$mb MB）" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
