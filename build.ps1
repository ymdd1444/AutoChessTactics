# ============================================================
# AutoChessTactics 构建脚本（Windows / PowerShell）
# 用途：编译 C# -> 生成 dll -> 安装到游戏 mods 目录 -> 打包 zip
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
# 前提：.NET 9 SDK（脚本会自动查找 DOTNET_ROOT / 用户目录 / PATH / Program Files）
# 注意：游戏运行时 dll 会被锁定，请先关闭游戏再构建
# ============================================================
$ErrorActionPreference = 'Stop'

# 游戏根目录（安装位置）
$GameDir  = 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
# 本脚本所在目录（即工程目录）
$ProjDir  = $PSScriptRoot
# 输出到游戏 mods 目录的文件夹名（= mod id）
$ModId    = 'AutoChessTactics'

function Resolve-DotnetWithSdk {
    # 不同电脑上的 SDK 位置可能不同：
    #   - Codex/临时环境常用 DOTNET_ROOT；
    #   - 用户手动安装常见于 %USERPROFILE%\.dotnet；
    #   - 系统安装常见于 Program Files 或 PATH。
    # 这里逐个尝试，并且只接受“确实能列出 SDK”的 dotnet，避免误用只有 runtime 的 3.1 dotnet。
    $candidates = @()
    if ($env:DOTNET_ROOT) {
        $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    }
    $candidates += "$env:USERPROFILE\.dotnet\dotnet.exe"
    $pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($pathDotnet) {
        $candidates += $pathDotnet.Source
    }
    $candidates += "$env:ProgramFiles\dotnet\dotnet.exe"
    ${env:ProgramFiles(x86)} | ForEach-Object {
        if ($_ ) {
            $candidates += (Join-Path $_ 'dotnet\dotnet.exe')
        }
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (!(Test-Path $candidate)) {
            continue
        }

        try {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and $sdks) {
                return $candidate
            }
        }
        catch {
            # 这个候选不可用就继续找下一个，不中断整个构建。
        }
    }

    throw '找不到可用的 .NET SDK。请安装 .NET 9 SDK，或设置 DOTNET_ROOT 指向 SDK 安装目录。'
}

# dotnet 可执行文件（必须带 SDK，只有 runtime 不够编译 mod）
$Dotnet = Resolve-DotnetWithSdk
Write-Host "==> 使用 dotnet: $Dotnet"

# 游戏正在运行时会锁定 mod 的 dll，导致无法覆盖安装，先检测并提醒
if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Warning '检测到游戏正在运行：mod 的 dll 会被锁定，无法覆盖安装。'
    Write-Warning '请先关闭游戏再运行本脚本，或按 Ctrl+C 取消。'
    Write-Host '（按任意键继续尝试……）' -NoNewline
    $null = [Console]::ReadKey($true)
}

Write-Host '==> 1/4 编译 dll ...'
& $Dotnet build "$ProjDir\AutoChessTactics.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

Write-Host '==> 2/4 复制到 mods 目录 ...'
$dest = Join-Path $GameDir "mods\$ModId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$ProjDir\.godot\mono\temp\bin\Release\AutoChessTactics.dll" -Destination $dest -Force
Copy-Item "$ProjDir\mod_manifest.json" -Destination $dest -Force

Write-Host '==> 3/4 生成 release zip ...'
$manifest = Get-Content "$ProjDir\mod_manifest.json" -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$releaseRoot = Join-Path $ProjDir 'release'
$releaseDir = Join-Path $releaseRoot "v$version"
$zipPath = Join-Path $releaseRoot "$ModId-v$version.zip"

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
Copy-Item "$ProjDir\.godot\mono\temp\bin\Release\AutoChessTactics.dll" -Destination $releaseDir -Force
Copy-Item "$ProjDir\mod_manifest.json" -Destination $releaseDir -Force
Copy-Item "$ProjDir\README.md" -Destination (Join-Path $releaseDir 'README.txt') -Force

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $zipPath -Force

Write-Host "==> 4/4 完成！已安装到 $dest"
Write-Host "      已打包 $zipPath"
Write-Host '      重启游戏后生效（设置 -> Mod 设置 可查看/开关）'
