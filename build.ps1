# ============================================================
# AutoChessTactics 构建脚本（Windows / PowerShell）
# 用途：编译 C# -> 生成 dll -> 安装到游戏 mods 目录
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
# 前提：.NET 9 SDK（本机装在 C:\Users\HP\.dotnet）
# 注意：游戏运行时 dll 会被锁定，请先关闭游戏再构建
# ============================================================
$ErrorActionPreference = 'Stop'

# 游戏根目录（安装位置）
$GameDir  = 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
# dotnet 可执行文件（本机装到用户目录）
$Dotnet   = "$env:USERPROFILE\.dotnet\dotnet.exe"
# 本脚本所在目录（即工程目录）
$ProjDir  = $PSScriptRoot
# 输出到游戏 mods 目录的文件夹名（= mod id）
$ModId    = 'AutoChessTactics'

# 游戏正在运行时会锁定 mod 的 dll，导致无法覆盖安装，先检测并提醒
if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Warning '检测到游戏正在运行：mod 的 dll 会被锁定，无法覆盖安装。'
    Write-Warning '请先关闭游戏再运行本脚本，或按 Ctrl+C 取消。'
    Write-Host '（按任意键继续尝试……）' -NoNewline
    $null = [Console]::ReadKey($true)
}

Write-Host '==> 1/3 编译 dll ...'
& $Dotnet build "$ProjDir\AutoChessTactics.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

Write-Host '==> 2/3 复制到 mods 目录 ...'
$dest = Join-Path $GameDir "mods\$ModId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$ProjDir\.godot\mono\temp\bin\Release\AutoChessTactics.dll" -Destination $dest -Force
Copy-Item "$ProjDir\mod_manifest.json" -Destination $dest -Force

Write-Host "==> 3/3 完成！已安装到 $dest"
Write-Host '      重启游戏后生效（设置 -> Mod 设置 可查看/开关）'
